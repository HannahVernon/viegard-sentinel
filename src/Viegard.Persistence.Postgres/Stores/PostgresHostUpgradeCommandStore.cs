using System.Data;
using Npgsql;
using Viegard.Application.Configuration;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresHostUpgradeCommandStore(
    NpgsqlDataSource dataSource,
    TimeProvider? timeProvider = null) : IHostUpgradeCommandStore
{
    private const string UniqueViolation = "23505";
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<HostUpgradeCommand> RequestAsync(
        string target,
        string requestedBy,
        CancellationToken cancellationToken = default)
    {
        var normalizedTarget = HostUpgradeCommandPolicy.NormalizeTarget(target);
        var normalizedRequestedBy = HostUpgradeCommandPolicy.NormalizeRequestedBy(requestedBy);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        await ThrowIfBlockedAsync(connection, transaction, normalizedTarget, now, cancellationToken).ConfigureAwait(false);

        var command = HostUpgradeCommandPolicy.NewRequest(normalizedTarget, normalizedRequestedBy, now);
        try
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO host_upgrade_commands (
                    id,
                    target,
                    status,
                    requested_at,
                    requested_by,
                    started_at,
                    finished_at,
                    detail
                )
                VALUES (
                    @id,
                    @target,
                    @status,
                    @requested_at,
                    @requested_by,
                    NULL,
                    NULL,
                    NULL
                );
                """;
            insert.Parameters.AddWithValue("id", command.Id);
            insert.Parameters.AddWithValue("target", command.Target);
            insert.Parameters.AddWithValue("status", (int)command.Status);
            insert.Parameters.AddWithValue("requested_at", command.RequestedAt);
            insert.Parameters.AddWithValue("requested_by", command.RequestedBy);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return command;
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
        {
            throw new HostUpgradeCommandRejectedException(
                HostUpgradeCommandRejectionReason.SingleFlight,
                normalizedTarget);
        }
    }

    public async ValueTask<IReadOnlyList<HostUpgradeCommand>> ListRecentAsync(
        string? target = null,
        int limit = HostUpgradeCommandPolicy.DefaultRecentLimit,
        CancellationToken cancellationToken = default)
    {
        var normalizedTarget = string.IsNullOrWhiteSpace(target)
            ? null
            : HostUpgradeCommandPolicy.NormalizeTarget(target);
        var safeLimit = Math.Clamp(limit, 1, HostUpgradeCommandPolicy.MaxRecentLimit);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (normalizedTarget is null)
        {
            command.CommandText = """
                SELECT id, target, status, requested_at, requested_by, started_at, finished_at, detail
                FROM host_upgrade_commands
                ORDER BY requested_at DESC, id DESC
                LIMIT @limit;
                """;
        }
        else
        {
            command.CommandText = """
                SELECT id, target, status, requested_at, requested_by, started_at, finished_at, detail
                FROM host_upgrade_commands
                WHERE target = @target
                ORDER BY requested_at DESC, id DESC
                LIMIT @limit;
                """;
            command.Parameters.AddWithValue("target", normalizedTarget);
        }

        command.Parameters.AddWithValue("limit", safeLimit);
        var commands = new List<HostUpgradeCommand>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            commands.Add(ReadCommand(reader));
        }

        return commands;
    }

    public async ValueTask<IReadOnlyList<string>> ListTargetsAsync(
        int limit = HostUpgradeCommandPolicy.DefaultRecentLimit,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, HostUpgradeCommandPolicy.MaxRecentLimit);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT known.target
            FROM (
                SELECT @default_target::text AS target, NULL::timestamp with time zone AS latest_requested_at, 0 AS sort_key
                UNION ALL
                SELECT target, MAX(requested_at) AS latest_requested_at, CASE WHEN target = @default_target THEN 0 ELSE 1 END AS sort_key
                FROM host_upgrade_commands
                GROUP BY target
            ) AS known
            GROUP BY known.target
            ORDER BY MIN(known.sort_key), MAX(known.latest_requested_at) DESC NULLS LAST, known.target
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("default_target", HostUpgradeCommandPolicy.DefaultTarget);
        command.Parameters.AddWithValue("limit", safeLimit);

        var targets = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            targets.Add(reader.GetString(0));
        }

        return targets;
    }

    public async ValueTask<HostUpgradeCommand?> ClaimNextPendingAsync(
        string target,
        CancellationToken cancellationToken = default)
    {
        var normalizedTarget = HostUpgradeCommandPolicy.NormalizeTarget(target);
        var startedAt = _timeProvider.GetUtcNow().ToUniversalTime();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE host_upgrade_commands AS command
            SET status = @running_status,
                started_at = @started_at,
                detail = NULL
            WHERE command.id = (
                SELECT pending.id
                FROM host_upgrade_commands AS pending
                WHERE pending.target = @target
                  AND pending.status = @pending_status
                ORDER BY pending.requested_at, pending.id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            RETURNING command.id, command.target, command.status, command.requested_at, command.requested_by, command.started_at, command.finished_at, command.detail;
            """;
        command.Parameters.AddWithValue("target", normalizedTarget);
        command.Parameters.AddWithValue("pending_status", (int)HostUpgradeCommandStatus.Pending);
        command.Parameters.AddWithValue("running_status", (int)HostUpgradeCommandStatus.Running);
        command.Parameters.AddWithValue("started_at", startedAt);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadCommand(reader)
            : null;
    }

    public async ValueTask<HostUpgradeCommand?> CompleteAsync(
        Guid id,
        bool succeeded,
        string? detail,
        CancellationToken cancellationToken = default)
    {
        var finishedAt = _timeProvider.GetUtcNow().ToUniversalTime();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE host_upgrade_commands AS command
            SET status = @completed_status,
                finished_at = @finished_at,
                detail = @detail
            WHERE command.id = @id
              AND command.status = @running_status
            RETURNING command.id, command.target, command.status, command.requested_at, command.requested_by, command.started_at, command.finished_at, command.detail;
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("completed_status", succeeded
            ? (int)HostUpgradeCommandStatus.Succeeded
            : (int)HostUpgradeCommandStatus.Failed);
        command.Parameters.AddWithValue("finished_at", finishedAt);
        command.Parameters.AddWithValue("detail", string.IsNullOrEmpty(detail) ? DBNull.Value : detail);
        command.Parameters.AddWithValue("running_status", (int)HostUpgradeCommandStatus.Running);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadCommand(reader)
            : null;
    }

    private static async ValueTask ThrowIfBlockedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string target,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var inFlight = connection.CreateCommand())
        {
            inFlight.Transaction = transaction;
            inFlight.CommandText = """
                SELECT 1
                FROM host_upgrade_commands
                WHERE target = @target
                  AND status IN (@pending_status, @running_status)
                LIMIT 1;
                """;
            inFlight.Parameters.AddWithValue("target", target);
            inFlight.Parameters.AddWithValue("pending_status", (int)HostUpgradeCommandStatus.Pending);
            inFlight.Parameters.AddWithValue("running_status", (int)HostUpgradeCommandStatus.Running);
            if (await inFlight.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new HostUpgradeCommandRejectedException(
                    HostUpgradeCommandRejectionReason.SingleFlight,
                    target);
            }
        }

        await using var cooldown = connection.CreateCommand();
        cooldown.Transaction = transaction;
        cooldown.CommandText = """
            SELECT finished_at
            FROM host_upgrade_commands
            WHERE target = @target
              AND finished_at IS NOT NULL
            ORDER BY finished_at DESC
            LIMIT 1;
            """;
        cooldown.Parameters.AddWithValue("target", target);
        var result = await cooldown.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            return;
        }

        var finishedAt = ReadTimestamp(result);
        var retryAt = finishedAt.Add(HostUpgradeCommandPolicy.Cooldown);
        if (retryAt > now)
        {
            throw new HostUpgradeCommandRejectedException(
                HostUpgradeCommandRejectionReason.Cooldown,
                target,
                retryAt);
        }
    }

    private static HostUpgradeCommand ReadCommand(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        Target = reader.GetString(1),
        Status = (HostUpgradeCommandStatus)reader.GetInt32(2),
        RequestedAt = reader.GetFieldValue<DateTimeOffset>(3),
        RequestedBy = reader.GetString(4),
        StartedAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        FinishedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        Detail = reader.IsDBNull(7) ? null : reader.GetString(7),
    };

    private static DateTimeOffset ReadTimestamp(object value) => value switch
    {
        DateTimeOffset timestamp => timestamp,
        DateTime timestamp => new(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
        _ => throw new InvalidOperationException(
            $"Unexpected timestamp type from PostgreSQL: {value.GetType().FullName}."),
    };
}
