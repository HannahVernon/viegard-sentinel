using Microsoft.Extensions.Logging;
using Npgsql;
using Viegard.Application.Doh;

namespace Viegard.Persistence.Postgres.Stores;

/// <summary>
/// Cross-process probe trigger backed by Postgres LISTEN/NOTIFY, following the
/// same pattern as the settings change loops.  The Admin UI/API publishes a
/// request with <c>pg_notify</c>; the probe worker (a separate process) waits on
/// the channel.  The wait is sliced into short segments so a single LISTEN
/// connection is never held for the full multi-hour probe interval, matching the
/// 60-second cadence the settings refresh loop already uses.
/// </summary>
public sealed class PostgresDohProbeTrigger(
    NpgsqlDataSource dataSource,
    ILogger<PostgresDohProbeTrigger>? logger = null)
    : IDohProbeTrigger
{
    public const string NotifyChannel = "viegard_doh_probe_now";
    private static readonly TimeSpan WaitSlice = TimeSpan.FromSeconds(60);

    public async ValueTask RequestAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_notify('{NotifyChannel}', '');";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> WaitForRequestAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var remaining = timeout;
        while (remaining > TimeSpan.Zero)
        {
            var slice = remaining < WaitSlice ? remaining : WaitSlice;
            if (await PostgresNotifyWait.WaitAsync(dataSource, NotifyChannel, slice, logger, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            remaining -= slice;
        }

        return false;
    }
}
