using Matigr.Transport;
using Matigr.Checking.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Checking.Persistence.Tests;

public partial class StorageTests
{
    private TransportOptions TransportConfig(int max = 2) => new(true, "checking.recheck-requests.v1", "test-" + Guid.NewGuid(), fixture.ConnectionString, "raw_envelope",
        PollMilliseconds: 10, LeaseSeconds: 1, MaxAttempts: max, RetryBaseSeconds: 1, RetryMaxSeconds: 2);
    [Fact]
    public async Task Outbox_claim_is_exclusive_confirm_mark_is_lease_bound_and_last_crash_is_recoverable()
    {
        var options = TransportConfig(1); var store = new PostgresTransportStore(options);
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using (var db = fixture.Open()) { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE outbox SET next_attempt_at = {now.AddYears(100)} WHERE status = 'Pending'"); db.Outbox.Add(new() { MessageId = id, RoutingKey = "test", Envelope = "{}", CorrelationId = Guid.NewGuid(), CreatedAt = now, NextAttemptAt = now }); await db.SaveChangesAsync(); }
        var first = await store.ClaimAsync(default); Assert.NotNull(first);
        Assert.Null(await store.ClaimAsync(default));
        await using (var db = fixture.Open()) { var row = await db.Outbox.SingleAsync(x => x.MessageId == first.MessageId); row.LockedUntil = now.AddMinutes(-1); await db.SaveChangesAsync(); }
        var recovered = await store.ClaimAsync(default); Assert.NotNull(recovered); Assert.Equal(first.MessageId, recovered.MessageId);
        await store.SentAsync(first, default);
        await using (var db = fixture.Open()) Assert.Equal("Pending", (await db.Outbox.SingleAsync(x => x.MessageId == recovered.MessageId)).Status);
        await store.SentAsync(recovered, default);
        await using (var db = fixture.Open()) Assert.Equal("Sent", (await db.Outbox.SingleAsync(x => x.MessageId == recovered.MessageId)).Status);
    }
    [Fact]
    public async Task Poison_and_deferred_are_durable_bounded_and_redrive_preserves_identity_with_audit()
    {
        var options = TransportConfig(); var store = new PostgresTransportStore(options);
        var messageId = Guid.NewGuid(); var raw = "{\"messageId\":\"" + messageId + "\",\"broken\":true}";
        await store.RecordFailureAsync(raw, "appeals.recheck-requested.v1", "DependencyUnavailable", false, default);
        await store.RecordFailureAsync(raw, "appeals.recheck-requested.v1", "DependencyUnavailable", false, default);
        Guid failure;
        await using (var db = fixture.Open()) {
            var row = await db.ProcessingFailures.SingleAsync(x => x.Consumer == options.Consumer);
            failure = row.FailureId; Assert.Equal("Quarantined", row.State); Assert.Equal(raw, row.RawEnvelope); Assert.Equal(messageId, row.MessageId);
        }
        Assert.True(await store.RedriveAsync(failure, default)); Assert.False(await store.RedriveAsync(failure, default));
        await using (var db = fixture.Open()) {
            var row = await db.ProcessingFailures.SingleAsync(x => x.FailureId == failure);
            Assert.Equal("Deferred", row.State); Assert.Equal(1, row.RedriveCount); Assert.Equal(raw, row.RawEnvelope); Assert.Equal(messageId, row.MessageId);
            Assert.Single(await db.Set<TransportRedriveRow>().Where(x => x.FailureId == failure).ToListAsync());
        }
        var deferred = await store.NextDeferredAsync(default); Assert.NotNull(deferred); Assert.Equal(raw, deferred.RawEnvelope);
        await store.ResolveAsync(deferred.FailureId, default); Assert.Null(await store.NextDeferredAsync(default));
        await store.RecordFailureAsync("not json", "bad", "InvalidSchema", true, default);
        await using (var db = fixture.Open()) Assert.Contains(await db.ProcessingFailures.Where(x => x.Consumer == options.Consumer).ToListAsync(), x => x.RawEnvelope == "not json" && x.State == "Quarantined");
    }
}
