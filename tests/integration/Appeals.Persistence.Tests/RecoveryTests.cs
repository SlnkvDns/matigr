using Matigr.Appeals.Infrastructure;
using Matigr.Transport;
using Microsoft.EntityFrameworkCore;
namespace Appeals.Persistence.Tests;
public partial class StorageTests
{
    [Fact]
    public async Task Readiness_requires_both_independent_consumers_and_database()
    {
        var first = new TransportHealth { Connected = true }; var second = new TransportHealth { Connected = false };
        var probe = new AppealsReadiness(fixture.Connection, [first, second]);
        var down = await probe.ProbeAsync(default); Assert.True(down.Database); Assert.False(down.Ready); Assert.False(down.Broker);
        second.Connected = true; Assert.True((await probe.ProbeAsync(default)).Ready);
        first.Connected = false; Assert.False((await probe.ProbeAsync(default)).Ready);
    }
    [Fact]
    public async Task Deferred_and_poison_are_durable_redrive_is_audited_and_preserves_quota_and_message()
    {
        var row = await ClaimedAppeal(); var fact = Assigned(row, Guid.NewGuid());
        var options = new TransportOptions(true, "test", AppealProcess.Consumer, fixture.Connection, "envelope", MaxAttempts: 2, RetryBaseSeconds: 1, RetryMaxSeconds: 1);
        var store = new PostgresTransportStore(options); var raw = fact.GetRawText();
        await store.RecordFailureAsync(raw, "checking.recheck-assigned.v1", "DependencyUnavailable", false, default);
        await store.RecordFailureAsync(raw, "checking.recheck-assigned.v1", "DependencyUnavailable", false, default);
        await using var db = fixture.Open(); var message = fact.GetProperty("messageId").GetGuid();
        var failure = await db.Failures.AsNoTracking().SingleAsync(x => x.MessageId == message);
        Assert.Equal("Quarantined", failure.State); Assert.Equal(raw, failure.Envelope);
        Assert.True(await store.RedriveAsync(failure.FailureId, default)); Assert.False(await store.RedriveAsync(failure.FailureId, default));
        var next = await store.NextDeferredAsync(default); Assert.NotNull(next); Assert.Equal(raw, next.RawEnvelope);
        Assert.True((await new AppealProcess(db).HandleAsync(fact, default)).Applied); await store.ResolveAsync(failure.FailureId, default);
        Assert.Equal("Resolved", (await db.Failures.AsNoTracking().SingleAsync(x => x.FailureId == failure.FailureId)).State);
        Assert.Equal(1, await db.Set<RedriveRow>().CountAsync(x => x.FailureId == failure.FailureId && x.MessageId == message));
        Assert.Single(await db.Appeals.Where(x => x.AppealId == row.AppealId).ToListAsync());
        Assert.Single(await db.Attempts.Where(x => x.AppealId == row.AppealId).ToListAsync());
        await store.RecordFailureAsync("malformed wire", "bad.route", "InvalidSchema", true, default);
        Assert.Contains(await db.Failures.AsNoTracking().ToListAsync(), x => x.Envelope == "malformed wire" && x.State == "Quarantined");
    }
}
