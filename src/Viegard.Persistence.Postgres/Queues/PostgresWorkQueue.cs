using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using Viegard.Application.Queues;

namespace Viegard.Persistence.Postgres.Queues;

/// <summary>
/// Durable work queue over PostgreSQL (D-0024): visibility-timeout leases via
/// <c>FOR UPDATE SKIP LOCKED</c>, dead-lettering by delivery count (enforced
/// at lease time so crashed consumers cannot cause infinite redelivery), and
/// <c>LISTEN/NOTIFY</c> wakeups with a fallback poll.  Safe for concurrent
/// consumers across processes and hosts.
/// </summary>
public sealed partial class PostgresWorkQueue<T> : IWorkQueue<T>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan FallbackPollInterval = TimeSpan.FromSeconds(5);

    private readonly NpgsqlDataSource _dataSource;
    private readonly int _maxDeliveryCount;
    private readonly TimeSpan _leaseDuration;
    private readonly string _channelName;

    public PostgresWorkQueue(NpgsqlDataSource dataSource, string queueName, int maxDeliveryCount = 5, TimeSpan? leaseDuration = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDeliveryCount, 1);

        _dataSource = dataSource;
        QueueName = queueName;
        _maxDeliveryCount = maxDeliveryCount;
        _leaseDuration = leaseDuration ?? TimeSpan.FromMinutes(5);
        _channelName = "viegard_q_" + ChannelSanitizer().Replace(queueName.ToLowerInvariant(), "_");
    }

    public string QueueName { get; }

    public async ValueTask EnqueueAsync(T message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = JsonSerializer.Serialize(message, Json);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO queue_messages (queue_name, payload_json, delivery_count, enqueued_at, dead_lettered)
            VALUES (@queue, CAST(@payload AS jsonb), 0, now(), false);
            INSERT INTO queue_counters (queue_name, enqueued, completed, abandoned)
            VALUES (@queue, 1, 0, 0)
            ON CONFLICT (queue_name) DO UPDATE SET enqueued = queue_counters.enqueued + 1;
            SELECT pg_notify(@channel, '');
            """;
        command.Parameters.AddWithValue("queue", QueueName);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("channel", _channelName);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IWorkLease<T>> LeaseAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lease = await TryLeaseOnceAsync(cancellationToken).ConfigureAwait(false);
            if (lease is not null)
            {
                return lease;
            }

            await WaitForSignalAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<WorkQueueStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT count(*) FROM queue_messages
                WHERE queue_name = @queue AND NOT dead_lettered
                  AND (leased_until IS NULL OR leased_until < now())) AS depth,
              (SELECT count(*) FROM queue_messages
                WHERE queue_name = @queue AND NOT dead_lettered
                  AND leased_until IS NOT NULL AND leased_until >= now()) AS in_flight,
              (SELECT min(enqueued_at) FROM queue_messages
                WHERE queue_name = @queue AND NOT dead_lettered
                  AND (leased_until IS NULL OR leased_until < now())) AS oldest,
              (SELECT count(*) FROM queue_messages
                WHERE queue_name = @queue AND dead_lettered) AS dead,
              COALESCE((SELECT enqueued FROM queue_counters WHERE queue_name = @queue), 0) AS enqueued,
              COALESCE((SELECT completed FROM queue_counters WHERE queue_name = @queue), 0) AS completed,
              COALESCE((SELECT abandoned FROM queue_counters WHERE queue_name = @queue), 0) AS abandoned;
            """;
        command.Parameters.AddWithValue("queue", QueueName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new WorkQueueStats
        {
            QueueName = QueueName,
            Depth = (int)reader.GetInt64(0),
            InFlight = (int)reader.GetInt64(1),
            OldestPendingEnqueuedAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            DeadLetterCount = (int)reader.GetInt64(3),
            TotalEnqueued = reader.GetInt64(4),
            TotalCompleted = reader.GetInt64(5),
            TotalAbandoned = reader.GetInt64(6),
        };
    }

    private async Task<IWorkLease<T>?> TryLeaseOnceAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Poison protection at lease time: expired leases from crashed
        // consumers count as deliveries, so over-limit messages are moved to
        // the dead-letter state here rather than redelivered forever.
        await using (var deadLetter = connection.CreateCommand())
        {
            deadLetter.CommandText = """
                UPDATE queue_messages
                SET dead_lettered = true, leased_until = NULL
                WHERE queue_name = @queue AND NOT dead_lettered
                  AND (leased_until IS NULL OR leased_until < now())
                  AND delivery_count >= @max;
                """;
            deadLetter.Parameters.AddWithValue("queue", QueueName);
            deadLetter.Parameters.AddWithValue("max", _maxDeliveryCount);
            await deadLetter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE queue_messages m
            SET leased_until = now() + @lease, delivery_count = m.delivery_count + 1
            WHERE m.id = (
                SELECT id FROM queue_messages
                WHERE queue_name = @queue AND NOT dead_lettered
                  AND (leased_until IS NULL OR leased_until < now())
                ORDER BY id
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING m.id, m.payload_json, m.delivery_count;
            """;
        command.Parameters.AddWithValue("queue", QueueName);
        command.Parameters.AddWithValue("lease", _leaseDuration);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var id = reader.GetInt64(0);
        var payloadJson = reader.GetString(1);
        var deliveryCount = reader.GetInt32(2);

        var message = JsonSerializer.Deserialize<T>(payloadJson, Json)
            ?? throw new InvalidOperationException($"Queue '{QueueName}' message {id} deserialized to null.");

        return new Lease(this, id, message, deliveryCount);
    }

    private async Task WaitForSignalAsync(CancellationToken cancellationToken)
    {
        // A dedicated short-lived connection per wait: sharing a listener
        // connection risks commands hitting it while it is in the Waiting
        // state.  Npgsql resets LISTEN state when the connection returns to
        // the pool.
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (var listen = connection.CreateCommand())
            {
                listen.CommandText = $"LISTEN {_channelName};";
                await listen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Wake on NOTIFY or fall back to a periodic poll (also re-checks
            // expired leases, which produce no NOTIFY).
            await connection.WaitAsync(FallbackPollInterval, cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // Connection failure degrades to polling cadence.
            await Task.Delay(FallbackPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CompleteAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM queue_messages WHERE id = @id;
            UPDATE queue_counters SET completed = completed + 1 WHERE queue_name = @queue;
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("queue", QueueName);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AbandonAsync(long id, int deliveryCount, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = deliveryCount >= _maxDeliveryCount
            ? """
              UPDATE queue_messages SET dead_lettered = true, leased_until = NULL WHERE id = @id;
              UPDATE queue_counters SET abandoned = abandoned + 1 WHERE queue_name = @queue;
              """
            : """
              UPDATE queue_messages SET leased_until = NULL WHERE id = @id;
              UPDATE queue_counters SET abandoned = abandoned + 1 WHERE queue_name = @queue;
              SELECT pg_notify(@channel, '');
              """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("queue", QueueName);
        if (deliveryCount < _maxDeliveryCount)
        {
            command.Parameters.AddWithValue("channel", _channelName);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex("[^a-z0-9_]")]
    private static partial Regex ChannelSanitizer();

    private sealed class Lease(PostgresWorkQueue<T> queue, long id, T message, int deliveryCount) : IWorkLease<T>
    {
        private int _settled;

        public T Message => message;

        public int DeliveryCount => deliveryCount;

        public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            EnsureUnsettled();
            await queue.CompleteAsync(id, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask AbandonAsync(CancellationToken cancellationToken = default)
        {
            EnsureUnsettled();
            await queue.AbandonAsync(id, deliveryCount, cancellationToken).ConfigureAwait(false);
        }

        private void EnsureUnsettled()
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0)
            {
                throw new InvalidOperationException("This lease has already been completed or abandoned.");
            }
        }
    }
}
