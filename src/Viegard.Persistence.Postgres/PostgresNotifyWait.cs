using Microsoft.Extensions.Logging;
using Npgsql;

namespace Viegard.Persistence.Postgres;

/// <summary>
/// Shared LISTEN/NOTIFY wait for the settings and signature change loops.
/// The whole open-listen-wait sequence runs under a linked cancellation that
/// fires shortly after the requested timeout, so no driver or socket state
/// can hold a refresh loop past its polling interval.  Motivated by a live
/// pipeline whose signature rules stayed 35+ minutes stale through four
/// notifications and dozens of would-be poll cycles: the poll fallback must
/// be unconditional, and a wedge must be visible in the logs.
/// </summary>
public static class PostgresNotifyWait
{
    /// <summary>Extra time past the caller's timeout before the wait is forcibly abandoned.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for a notification on
    /// <paramref name="channel"/>.  Returns true when a notification
    /// arrived; false when the wait timed out, was forcibly abandoned, or
    /// the connection failed (callers poll on false).  Caller cancellation
    /// propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async ValueTask<bool> WaitAsync(
        NpgsqlDataSource dataSource,
        string channel,
        TimeSpan timeout,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        grace.CancelAfter(timeout + Grace);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(grace.Token).ConfigureAwait(false);
            var notified = false;
            connection.Notification += (_, _) => notified = true;
            await using (var listen = connection.CreateCommand())
            {
                listen.CommandText = $"LISTEN {channel};";
                await listen.ExecuteNonQueryAsync(grace.Token).ConfigureAwait(false);
            }

            await connection.WaitAsync(timeout, grace.Token).ConfigureAwait(false);
            return notified;
        }
        catch (OperationCanceledException) when (grace.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning(
                "Notification wait on {Channel} exceeded its {Timeout} timeout plus grace and was abandoned "
                + "(possible wedged connection); forcing a poll cycle.",
                channel,
                timeout);
            return false;
        }
        catch (NpgsqlException ex)
        {
            logger?.LogWarning(
                ex,
                "Notification wait on {Channel} failed; falling back to a delayed poll.",
                channel);
            await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }
}
