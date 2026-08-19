using System.Runtime.CompilerServices;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using Viegard.Application.Health;
using Viegard.Application.Secrets;
using Viegard.Application.Sources;
using Viegard.Application.Stores;
using Viegard.Domain.Events;
using Viegard.Domain.Health;

namespace Viegard.Sources.Imap;

/// <summary>
/// Monitors one IMAP account (D-0019..D-0022): implicit TLS, app-password
/// auth via <see cref="ISecretProvider"/>, folders opened read-only (never
/// alters flags), IDLE with polling fallback and a bounded IDLE cycle as the
/// safety poll, offsets persisted per folder so restarts resume cleanly.
/// Produces <see cref="MailFetchDto"/> JSON payloads.
/// </summary>
public sealed class ImapMailSource(
    ImapAccountOptions account,
    ISecretProvider secretProvider,
    ISourceOffsetStore offsetStore,
    ILogger<ImapMailSource> logger) : IDataSource, IHealthContributor
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private int _consecutiveIdleFailures;
    private string? _lastError;
    private bool _connected;

    public string SourceId => $"imap:{account.AccountId}";

    public string SourceType => ImapEventNormalizer.ImapSourceType;

    public string ComponentId => SourceId;

    public Task<ComponentHealth> CheckAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ComponentHealth
        {
            ComponentId = ComponentId,
            Status = _connected ? HealthStatus.Healthy : HealthStatus.Degraded,
            Detail = _connected ? "Connected." : _lastError ?? "Not connected yet.",
            CheckedAt = DateTimeOffset.UtcNow,
        });

    public async IAsyncEnumerable<ObservedItem> ObserveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = new ImapClient();
            var batches = RunSessionAsync(client, cancellationToken);

            await foreach (var batch in batches.ConfigureAwait(false))
            {
                foreach (var item in batch)
                {
                    yield return item;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            await Task.Delay(account.ReconnectDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One connection session: connect, then repeatedly sweep folders and
    /// wait (IDLE or poll).  Ends (without throwing) on any connection-level
    /// failure so the outer loop can delay and reconnect.
    /// </summary>
    private async IAsyncEnumerable<IReadOnlyList<ObservedItem>> RunSessionAsync(
        ImapClient client,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await ConnectAsync(client, cancellationToken).ConfigureAwait(false);
            _connected = true;
            _lastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordFailure("connect", ex);
            yield break;
        }

        while (!cancellationToken.IsCancellationRequested && client.IsConnected)
        {
            IReadOnlyList<ObservedItem> batch;
            try
            {
                batch = await SweepFoldersAsync(client, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordFailure("sweep", ex);
                yield break;
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }

            try
            {
                await WaitForNewMailAsync(client, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordFailure("wait", ex);
                yield break;
            }
        }
    }

    private async Task ConnectAsync(ImapClient client, CancellationToken cancellationToken)
    {
        var secret = await secretProvider.GetAsync(account.PasswordSecretName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Secret '{account.PasswordSecretName}' for IMAP account '{account.AccountId}' was not found.");

        await client.ConnectAsync(account.Host, account.Port, SecureSocketOptions.SslOnConnect, cancellationToken).ConfigureAwait(false);
        await client.AuthenticateAsync(account.Username, secret.Reveal(), cancellationToken).ConfigureAwait(false);

        logger.LogInformation("IMAP account {AccountId}: connected to {Host}:{Port}.", account.AccountId, account.Host, account.Port);
    }

    private async Task<IReadOnlyList<ObservedItem>> SweepFoldersAsync(ImapClient client, CancellationToken cancellationToken)
    {
        var items = new List<ObservedItem>();

        foreach (var folderName in account.Folders)
        {
            var folder = await client.GetFolderAsync(folderName, cancellationToken).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

            var lastUid = await ResolveLastUidAsync(folder, cancellationToken).ConfigureAwait(false);
            var newUids = await folder.SearchAsync(
                SearchQuery.Uids(new UniqueIdRange(new UniqueId(lastUid + 1), UniqueId.MaxValue)),
                cancellationToken).ConfigureAwait(false);

            foreach (var uid in newUids.Where(u => u.Id > lastUid).OrderBy(u => u.Id))
            {
                var item = await FetchMessageAsync(folder, uid, cancellationToken).ConfigureAwait(false);
                if (item is not null)
                {
                    items.Add(item);
                }

                await offsetStore.SetAsync(SourceId, LastUidKey(folderName), uid.Id.ToString(), cancellationToken).ConfigureAwait(false);
            }
        }

        return items;
    }

    /// <summary>
    /// Determines the resume point for a folder, handling first runs and
    /// UIDVALIDITY changes (which invalidate every stored UID).
    /// </summary>
    private async Task<uint> ResolveLastUidAsync(IMailFolder folder, CancellationToken cancellationToken)
    {
        var validityKey = $"uidvalidity:{folder.FullName}";
        var storedValidity = await offsetStore.GetAsync(SourceId, validityKey, cancellationToken).ConfigureAwait(false);
        var currentValidity = folder.UidValidity.ToString();

        var baselineNeeded = storedValidity != currentValidity;
        if (baselineNeeded)
        {
            if (storedValidity is not null)
            {
                logger.LogWarning(
                    "IMAP account {AccountId}: UIDVALIDITY changed for folder {Folder}; re-baselining.",
                    account.AccountId, folder.FullName);
            }

            await offsetStore.SetAsync(SourceId, validityKey, currentValidity, cancellationToken).ConfigureAwait(false);

            var baseline = account.IngestExistingOnFirstRun || folder.UidNext is not { } uidNext || uidNext.Id == 0
                ? 0u
                : uidNext.Id - 1;
            await offsetStore.SetAsync(SourceId, LastUidKey(folder.FullName), baseline.ToString(), cancellationToken).ConfigureAwait(false);
            return baseline;
        }

        var stored = await offsetStore.GetAsync(SourceId, LastUidKey(folder.FullName), cancellationToken).ConfigureAwait(false);
        return uint.TryParse(stored, out var lastUid) ? lastUid : 0u;
    }

    private async Task<ObservedItem?> FetchMessageAsync(IMailFolder folder, UniqueId uid, CancellationToken cancellationToken)
    {
        var summaries = await folder.FetchAsync(
            [uid],
            MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure,
            cancellationToken).ConfigureAwait(false);

        var summary = summaries.FirstOrDefault();
        if (summary is null)
        {
            logger.LogWarning("IMAP account {AccountId}: UID {Uid} vanished before fetch.", account.AccountId, uid.Id);
            return null;
        }

        var dto = new MailFetchDto
        {
            SchemaVersion = 1,
            AccountId = account.AccountId,
            Folder = folder.FullName,
            Uid = uid.Id,
            MessageId = summary.Envelope?.MessageId,
            From = MapAddresses(summary.Envelope?.From),
            ReplyTo = MapAddresses(summary.Envelope?.ReplyTo),
            To = MapAddresses(summary.Envelope?.To),
            Cc = MapAddresses(summary.Envelope?.Cc),
            Subject = summary.Envelope?.Subject,
            SentAt = summary.Envelope?.Date,
            TextBody = await GetTextPartAsync(folder, summary, summary.TextBody, cancellationToken).ConfigureAwait(false),
            HtmlBody = await GetTextPartAsync(folder, summary, summary.HtmlBody, cancellationToken).ConfigureAwait(false),
            Attachments = summary.Attachments
                .Select(a => new AttachmentDto
                {
                    FileName = a.FileName,
                    ContentType = a.ContentType?.MimeType,
                    SizeBytes = a.Octets,
                    IsInline = a.ContentDisposition?.Disposition?.Equals(ContentDisposition.Inline, StringComparison.OrdinalIgnoreCase) == true,
                })
                .ToList(),
        };

        var observation = new RawObservation
        {
            Id = Guid.NewGuid(),
            SourceId = SourceId,
            ObservedAt = DateTimeOffset.UtcNow,
            PayloadReference = $"imap/{account.AccountId}/{folder.FullName}/{folder.UidValidity}/{uid.Id}",
            IngestOffset = uid.Id.ToString(),
        };

        return new ObservedItem
        {
            Observation = observation,
            RawPayload = JsonSerializer.Serialize(dto, SerializerOptions),
        };
    }

    private static async Task<string?> GetTextPartAsync(
        IMailFolder folder,
        IMessageSummary summary,
        BodyPartText? part,
        CancellationToken cancellationToken)
    {
        if (part is null)
        {
            return null;
        }

        // Folder is opened read-only, so retrieval can never set \Seen (D-0022).
        var entity = await folder.GetBodyPartAsync(summary.UniqueId, part, cancellationToken).ConfigureAwait(false);
        return entity is TextPart textPart ? textPart.Text : null;
    }

    private async Task WaitForNewMailAsync(ImapClient client, CancellationToken cancellationToken)
    {
        var idleUsable = account.UseIdle
            && client.Capabilities.HasFlag(ImapCapabilities.Idle)
            && _consecutiveIdleFailures < account.IdleFailureThreshold;

        if (!idleUsable)
        {
            await Task.Delay(account.PollInterval, cancellationToken).ConfigureAwait(false);
            return;
        }

        // IDLE watches the currently open folder (the last one swept); the
        // bounded cycle doubles as the safety poll for all other folders.
        using var done = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        done.CancelAfter(account.IdleCycle);

        void OnCountChanged(object? sender, EventArgs e) => done.Cancel();

        var inbox = client.Inbox;
        inbox.CountChanged += OnCountChanged;
        try
        {
            await client.IdleAsync(done.Token, cancellationToken).ConfigureAwait(false);
            _consecutiveIdleFailures = 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _consecutiveIdleFailures++;
            logger.LogWarning(
                ex,
                "IMAP account {AccountId}: IDLE failed ({Count}/{Threshold}); will fall back to polling at threshold.",
                account.AccountId, _consecutiveIdleFailures, account.IdleFailureThreshold);
            throw;
        }
        finally
        {
            inbox.CountChanged -= OnCountChanged;
        }
    }

    private void RecordFailure(string stage, Exception ex)
    {
        _connected = false;
        _lastError = $"{stage}: {ex.Message}";
        logger.LogError(ex, "IMAP account {AccountId}: {Stage} failed; reconnecting after delay.", account.AccountId, stage);
    }

    private string LastUidKey(string folderName) => $"lastuid:{folderName}";

    private static IReadOnlyList<MailAddressDto> MapAddresses(InternetAddressList? addresses) =>
        addresses is null
            ? []
            : addresses
                .OfType<MailboxAddress>()
                .Select(m => new MailAddressDto { DisplayName = m.Name, Address = m.Address })
                .ToList();
}
