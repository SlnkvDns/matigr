using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Matigr.Transport;

public sealed class PostgresTransportStore(TransportOptions options)
{
    private string RawColumn => options.RawColumn is "raw_envelope" or "envelope" ? options.RawColumn : throw new InvalidOperationException("Invalid local transport mapping.");
    private async Task<NpgsqlConnection> Open(CancellationToken ct)
    { var connection = new NpgsqlConnection(options.ConnectionString); await connection.OpenAsync(ct); return connection; }
    private static NpgsqlCommand Command(NpgsqlConnection c, string sql, params object[] args)
    { var cmd = new NpgsqlCommand(sql, c); foreach (var arg in args) cmd.Parameters.AddWithValue(arg); return cmd; }
    private static DateTimeOffset Stamp(DateTimeOffset value) => value.AddTicks(-(value.Ticks % 10));
    public async Task ObserveAsync(TransportHealth health, CancellationToken ct)
    {
        await using var c = await Open(ct);
        await using (var cmd = Command(c, "SELECT count(*), COALESCE(max(EXTRACT(epoch FROM now() - created_at)),0)::double precision FROM outbox WHERE status = 'Pending'"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) { health.Pending = reader.GetInt64(0); TransportHealth.Lag.Record(Math.Max(0, reader.GetDouble(1))); }
        await using var failures = Command(c, "SELECT state, count(*) FROM processing_failures WHERE state IN ('Deferred','Quarantined') GROUP BY state");
        health.Deferred = 0; health.Quarantined = 0;
        await using var rows = await failures.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct)) { if (rows.GetString(0) == "Deferred") health.Deferred = rows.GetInt64(1); else health.Quarantined = rows.GetInt64(1); }
    }

    public async Task<Outgoing?> ClaimAsync(CancellationToken ct)
    {
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow; var lease = Stamp(now.AddSeconds(options.LeaseSeconds));
        await using var cmd = Command(c, """
            SELECT message_id, routing_key, envelope::text, attempts FROM outbox
            WHERE status = 'Pending' AND next_attempt_at <= $1 AND (attempts < $2 OR locked_until IS NOT NULL)
              AND (locked_until IS NULL OR locked_until <= $1)
            ORDER BY created_at, message_id LIMIT 1 FOR UPDATE SKIP LOCKED
            """, now, options.MaxAttempts);
        Outgoing? item;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            item = await r.ReadAsync(ct) ? new(r.GetGuid(0), r.GetString(1), r.GetString(2), lease, r.GetInt32(3) + 1) : null;
        if (item is not null)
        {
            await using var update = Command(c, "UPDATE outbox SET attempts = attempts + 1, locked_until = $1 WHERE message_id = $2", lease, item.MessageId);
            await update.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct); return item;
    }
    public async Task SentAsync(Outgoing item, CancellationToken ct)
    {
        await using var c = await Open(ct);
        await using var cmd = Command(c, "UPDATE outbox SET status = 'Sent', sent_at = $1, locked_until = NULL WHERE message_id = $2 AND locked_until = $3 AND status = 'Pending'",
            DateTimeOffset.UtcNow, item.MessageId, item.Lease); await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task RetryOutboxAsync(Outgoing item, string code, CancellationToken ct)
    {
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await using var cmd = Command(c, "UPDATE outbox SET next_attempt_at = $1, locked_until = NULL WHERE message_id = $2 AND locked_until = $3 AND status = 'Pending'",
            DateTimeOffset.UtcNow + options.Backoff(item.Attempts), item.MessageId, item.Lease);
        if (await cmd.ExecuteNonQueryAsync(ct) == 1 && item.Attempts >= options.MaxAttempts)
            await Record(c, item.Envelope, item.RoutingKey, code, true, options.Consumer + ".outbox", ct);
        await tx.CommitAsync(ct);
    }
    public async Task RecordFailureAsync(string raw, string routingKey, string code, bool permanent, CancellationToken ct)
    {
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await Record(c, raw, routingKey, code, permanent, options.Consumer, ct); await tx.CommitAsync(ct);
    }
    private async Task Record(NpgsqlConnection c, string raw, string routingKey, string code, bool permanent, string consumer, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        // Technical identity for malformed data only; it never becomes a business message/Check ID.
        var message = new Guid(Convert.FromHexString(hash)[..16]);
        try { using var json = System.Text.Json.JsonDocument.Parse(raw); if (json.RootElement.TryGetProperty("messageId", out var id) && Guid.TryParse(id.GetString(), out var valid)) message = valid; }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { }
        var mutex = consumer + "/" + message.ToString("D");
        await using (var guard = Command(c, "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", mutex)) await guard.ExecuteNonQueryAsync(ct);
        await using var find = Command(c, "SELECT failure_id, attempts, state FROM processing_failures WHERE consumer = $1 AND message_id = $2 AND (payload_hash = $3 OR payload_hash = '') ORDER BY recorded_at LIMIT 1 FOR UPDATE",
            consumer, message, hash);
        Guid failure = Guid.NewGuid(); int attempt = 1; string? state = null; bool exists = false;
        await using (var r = await find.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct)) { exists = true; failure = r.GetGuid(0); attempt = r.GetInt32(1) + 1; state = r.GetString(2); }
        if (state == "Quarantined" || (state == "Resolved" && !consumer.EndsWith(".outbox", StringComparison.Ordinal))) return;
        var nextState = permanent || attempt >= options.MaxAttempts ? "Quarantined" : "Deferred";
        var next = DateTimeOffset.UtcNow + options.Backoff(attempt);
        if (exists)
        {
            await using var update = Command(c, $"UPDATE processing_failures SET payload_hash = $1, {RawColumn} = $2, routing_key = $3, reason_code = $4, state = $5, attempts = $6, next_attempt_at = $7 WHERE failure_id = $8",
                hash, raw, routingKey, code, nextState, attempt, next, failure); await update.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var insert = Command(c, $"INSERT INTO processing_failures (failure_id, consumer, message_id, payload_hash, {RawColumn}, routing_key, state, reason_code, attempts, recorded_at, next_attempt_at, redrive_count) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,0)",
                failure, consumer, message, hash, raw, routingKey, nextState, code, attempt, DateTimeOffset.UtcNow, next); await insert.ExecuteNonQueryAsync(ct);
        }
    }
    public async Task<Deferred?> NextDeferredAsync(CancellationToken ct)
    {
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await using var cmd = Command(c, $"SELECT failure_id, {RawColumn}, routing_key, attempts FROM processing_failures WHERE consumer = $1 AND state = 'Deferred' AND next_attempt_at <= $2 ORDER BY next_attempt_at, failure_id LIMIT 1 FOR UPDATE SKIP LOCKED",
            options.Consumer, DateTimeOffset.UtcNow);
        Deferred? item;
        await using (var r = await cmd.ExecuteReaderAsync(ct)) item = await r.ReadAsync(ct) ? new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt32(3)) : null;
        if (item is not null)
        { await using var update = Command(c, "UPDATE processing_failures SET next_attempt_at = $1 WHERE failure_id = $2", DateTimeOffset.UtcNow.AddSeconds(options.LeaseSeconds), item.FailureId); await update.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct); return item;
    }
    public async Task ResolveAsync(Guid failureId, CancellationToken ct)
    { await using var c = await Open(ct); await using var cmd = Command(c, "UPDATE processing_failures SET state = 'Resolved' WHERE failure_id = $1 AND state = 'Deferred'", failureId); await cmd.ExecuteNonQueryAsync(ct); }

    public async Task<bool> RedriveAsync(Guid failureId, CancellationToken ct)
    {
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await using var read = Command(c, "SELECT message_id, consumer FROM processing_failures WHERE failure_id = $1 AND state = 'Quarantined' FOR UPDATE", failureId);
        Guid message; string consumer;
        await using (var r = await read.ExecuteReaderAsync(ct)) { if (!await r.ReadAsync(ct)) return false; message = r.GetGuid(0); consumer = r.GetString(1); }
        if (consumer != options.Consumer && consumer != options.Consumer + ".outbox") return false;
        await using (var audit = Command(c, "INSERT INTO transport_redrives (redrive_id, failure_id, message_id, payload_hash, redriven_at) SELECT $1, failure_id, message_id, payload_hash, $2 FROM processing_failures WHERE failure_id = $3",
            Guid.NewGuid(), DateTimeOffset.UtcNow, failureId)) await audit.ExecuteNonQueryAsync(ct);
        if (consumer.EndsWith(".outbox", StringComparison.Ordinal))
        {
            await using var reset = Command(c, "UPDATE outbox SET attempts = 0, next_attempt_at = $1, locked_until = NULL WHERE message_id = $2 AND status = 'Pending'", DateTimeOffset.UtcNow, message);
            await reset.ExecuteNonQueryAsync(ct);
        }
        await using var update = Command(c, "UPDATE processing_failures SET state = $1, attempts = 0, next_attempt_at = $2, redrive_count = redrive_count + 1, redriven_at = $2 WHERE failure_id = $3",
            consumer.EndsWith(".outbox", StringComparison.Ordinal) ? "Resolved" : "Deferred", DateTimeOffset.UtcNow, failureId);
        await update.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); TransportHealth.Replayed.Add(1); return true;
    }
}
