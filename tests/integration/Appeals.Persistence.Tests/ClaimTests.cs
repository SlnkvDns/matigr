using System.Text.Json;
using Matigr.Appeals.Application;
using Matigr.Appeals.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Appeals.Persistence.Tests;

public partial class StorageTests
{
    private async Task<AppealRow> RegisteredAppeal()
    {
        var seed = await NewAppeal(); await using var db = fixture.Open();
        await new AppealSubmission(db, new BasisReader(db, Published(seed))).SubmitAsync(new(Guid.NewGuid(), ["Team"], [seed.TournamentId], seed.TeamId), seed.ResultId, 1, "submit", default);
        return await db.Appeals.AsNoTracking().SingleAsync(x => x.TournamentId == seed.TournamentId);
    }
    [Fact]
    public async Task Registered_is_visible_before_manual_claim_202_is_pending_and_replay_precedes_stale_ETag_after_end()
    {
        var row = await RegisteredAppeal(); var actor = new AppealActor(Guid.NewGuid(), ["Jury"], [row.TournamentId], null);
        await using var db = fixture.Open(); var work = new AppealWork(db);
        using (var queue = JsonDocument.Parse((await work.ListAsync(actor, row.TournamentId, false, default)).Json)) Assert.Single(queue.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(404, (await Assert.ThrowsAsync<AppealOperationException>(() => work.GetAsync(new(Guid.NewGuid(), ["Team"], [row.TournamentId], Guid.NewGuid()), row.AppealId, default))).Status);
        await new ManualEnd(db).RecordAsync(new(Guid.NewGuid(), ["Organizer"], [row.TournamentId], null), row.TournamentId, Guid.NewGuid(), "end", default);
        var first = await work.ClaimAsync(actor, row.AppealId, 1, "claim", default); Assert.Equal(202, first.Status); Assert.Equal("\"2\"", first.ETag);
        var replay = await work.ClaimAsync(actor, row.AppealId, 1, "claim", default);
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(first.Json).RootElement, JsonDocument.Parse(replay.Json).RootElement));
        Assert.Equal(412, (await Assert.ThrowsAsync<AppealOperationException>(() => work.ClaimAsync(actor, row.AppealId, 1, "stale", default))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<AppealOperationException>(() => work.ClaimAsync(actor, row.AppealId, 2, "duplicate", default))).Status);
        var saved = await db.Appeals.AsNoTracking().SingleAsync(x => x.AppealId == row.AppealId);
        Assert.Equal("AcceptedAwaitingRecheck", saved.State); Assert.Null(saved.CheckId);
        var attempt = await db.Attempts.SingleAsync(x => x.AppealId == row.AppealId); Assert.Equal(actor.SubjectId, attempt.ReviewerId);
        var messages = await db.Outbox.AsNoTracking().ToListAsync();
        var request = Assert.Single(messages, x => x.Envelope.Contains(row.AppealId.ToString(), StringComparison.Ordinal));
        using (var envelope = JsonDocument.Parse(request.Envelope)) {
            var p = envelope.RootElement.GetProperty("payload"); Assert.Equal(actor.SubjectId, p.GetProperty("requestedReviewerId").GetGuid());
            Assert.Equal(row.BasePublicationId, p.GetProperty("publicationId").GetGuid()); Assert.Equal(row.MaterialVersionId, p.GetProperty("materialVersionId").GetGuid());
        }
        using var after = JsonDocument.Parse((await work.ListAsync(actor, row.TournamentId, false, default)).Json);
        Assert.Empty(after.RootElement.GetProperty("items").EnumerateArray());
    }
    [Fact]
    public async Task Concurrent_two_jury_claims_have_one_attempt_history_transition_and_outbox()
    {
        var row = await RegisteredAppeal(); var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> Run() {
            await start.Task; await using var db = fixture.Open();
            try { return (await new AppealWork(db).ClaimAsync(new(Guid.NewGuid(), ["Jury"], [row.TournamentId], null), row.AppealId, 1, "claim", default)).Status; }
            catch (AppealOperationException e) { return e.Status; }
        }
        var a = Run(); var b = Run(); start.SetResult(); Assert.Equal(new[] { 202, 412 }, (await Task.WhenAll(a, b)).Order().ToArray());
        await using var verify = fixture.Open(); Assert.Single(await verify.Attempts.Where(x => x.AppealId == row.AppealId).ToListAsync());
        Assert.Equal(2, await verify.History.CountAsync(x => x.AppealId == row.AppealId));
        Assert.Single(await verify.Outbox.AsNoTracking().ToListAsync(), x => x.Envelope.Contains(row.AppealId.ToString(), StringComparison.Ordinal));
    }
    private sealed class FailClaimCommit : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default) => throw new InvalidOperationException("Claim SQL before commit fault");
    }
    [Fact]
    public async Task Claim_commit_fault_rolls_back_attempt_history_HTTP_outbox_and_Registered_state()
    {
        var row = await RegisteredAppeal(); var actor = new AppealActor(Guid.NewGuid(), ["Jury"], [row.TournamentId], null);
        var options = new DbContextOptionsBuilder<AppealsDbContext>().UseNpgsql(fixture.Connection).AddInterceptors(new FailClaimCommit()).Options;
        await using (var broken = new AppealsDbContext(options)) await Assert.ThrowsAsync<InvalidOperationException>(() => new AppealWork(broken).ClaimAsync(actor, row.AppealId, 1, "claim", default));
        await using var db = fixture.Open();
        Assert.Equal("Registered", (await db.Appeals.SingleAsync(x => x.AppealId == row.AppealId)).State);
        Assert.False(await db.Attempts.AnyAsync(x => x.AppealId == row.AppealId)); Assert.False(await db.HttpReceipts.AnyAsync(x => x.SubjectId == actor.SubjectId));
        Assert.Single(await db.History.Where(x => x.AppealId == row.AppealId).ToListAsync());
        Assert.DoesNotContain(await db.Outbox.AsNoTracking().ToListAsync(), x => x.Envelope.Contains(row.AppealId.ToString(), StringComparison.Ordinal));
        Assert.Equal(202, (await new AppealWork(db).ClaimAsync(actor, row.AppealId, 1, "claim", default)).Status);
    }
}
