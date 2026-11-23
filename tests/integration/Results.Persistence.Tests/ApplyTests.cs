using Matigr.Results.Domain;
using Matigr.Results.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Results.Persistence.Tests;

public partial class StorageTests
{
    private async Task<Evaluation> NewEvaluation(Outcome outcome = Outcome.Correct, decimal points = 10)
    {
        await using var db = fixture.Open();
        var t = Guid.NewGuid(); var team = Guid.NewGuid(); var task = Guid.NewGuid();
        db.Snapshots.Add(new() { TournamentId = t, FixtureVersion = 1, ManifestHash = "test", UpdatedAt = DateTimeOffset.UtcNow });
        db.Teams.Add(new() { TournamentId = t, TeamId = team, Name = "Test" });
        db.Tasks.Add(new() { TournamentId = t, TaskId = task, Name = "Task", Order = 1 });
        db.Rules.Add(new() { TournamentId = t, TaskId = task, RuleVersion = 1, Points = points, ManifestHash = "test" });
        await db.SaveChangesAsync();
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new(t, team, task), EvaluationKind.Initial, outcome,
            DecisionOrigin.OrdinaryReview, Guid.NewGuid(), Guid.NewGuid(), 0, 1, DateTimeOffset.UtcNow);
    }
    [Fact]
    public async Task Duplicate_new_message_replays_receipt_before_expected_zero_and_conflict_cannot_replace_source()
    {
        var e = await NewEvaluation();
        await using var db = fixture.Open(); var work = new ApplyEvaluation(db);
        var first = await work.InitialAsync(e, Guid.NewGuid(), "first");
        Assert.Equal("Applied", first.Status);
        var replay = await work.InitialAsync(e, Guid.NewGuid(), "repeat");
        Assert.Equal(first.Status, replay.Status);
        Assert.True(System.Text.Json.JsonElement.DeepEquals(System.Text.Json.JsonDocument.Parse(first.Response).RootElement, System.Text.Json.JsonDocument.Parse(replay.Response).RootElement));
        var other = e with { CheckId = Guid.NewGuid(), SolutionId = Guid.NewGuid() };
        Assert.Equal("Rejected", (await work.InitialAsync(other, Guid.NewGuid(), "other")).Status);
        Assert.Equal(1, await db.Results.CountAsync(x => x.TournamentId == e.Key.TournamentId));
        Assert.Equal(1, await db.Publications.CountAsync(x => x.ResultId == db.Results.Where(r => r.TournamentId == e.Key.TournamentId).Select(r => r.ResultId).First()));
        Assert.Equal(1, (await db.Snapshots.SingleAsync(x => x.TournamentId == e.Key.TournamentId)).Version);
        await Assert.ThrowsAsync<Matigr.Results.Application.EvaluationIdentityConflictException>(() => work.InitialAsync(e with { Outcome = Outcome.Incorrect }, Guid.NewGuid(), "bad"));
    }
    [Fact]
    public async Task Concurrent_initials_have_one_publication_and_two_tasks_both_contribute()
    {
        var e = await NewEvaluation();
        async Task<string> Run(Evaluation evaluation) { await using var db = fixture.Open(); return (await new ApplyEvaluation(db).InitialAsync(evaluation, Guid.NewGuid(), "race")).Status; }
        Assert.Equal(new[] { "Applied", "Rejected" }, (await Task.WhenAll(Run(e), Run(e with { CheckId = Guid.NewGuid() }))).Order().ToArray());
        var task = Guid.NewGuid();
        await using (var db = fixture.Open()) {
            db.Tasks.Add(new() { TournamentId = e.Key.TournamentId, TaskId = task, Name = "Second", Order = 2 });
            db.Rules.Add(new() { TournamentId = e.Key.TournamentId, TaskId = task, RuleVersion = 1, Points = 20, ManifestHash = "test" });
            await db.SaveChangesAsync();
        }
        Assert.Equal("Applied", await Run(e with { CheckId = Guid.NewGuid(), SolutionId = Guid.NewGuid(), MaterialVersionId = Guid.NewGuid(), Key = e.Key with { TaskId = task } }));
        await using var verify = fixture.Open();
        Assert.Equal(30, await verify.Results.Where(x => x.TournamentId == e.Key.TournamentId).SumAsync(x => x.Score));
        Assert.Equal(2, (await verify.Snapshots.SingleAsync(x => x.TournamentId == e.Key.TournamentId)).Version);
    }
    private sealed class FailCommit : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
        {
            throw new InvalidOperationException("Fault after SQL before commit");
        }
    }
    [Fact]
    public async Task Commit_fault_leaves_no_result_publication_receipt_or_inbox_and_retry_succeeds()
    {
        var e = await NewEvaluation(); var message = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ResultsDbContext>().UseNpgsql(fixture.Connection).AddInterceptors(new FailCommit()).Options;
        await using (var db = new ResultsDbContext(options))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ApplyEvaluation(db).InitialAsync(e, message, "fault"));
        await using var verify = fixture.Open();
        Assert.False(await verify.Results.AnyAsync(x => x.TournamentId == e.Key.TournamentId));
        Assert.False(await verify.Inbox.AnyAsync(x => x.MessageId == message));
        Assert.False(await verify.Receipts.AnyAsync(x => x.EvaluationId == e.CheckId));
        Assert.Equal(0, (await verify.Snapshots.SingleAsync(x => x.TournamentId == e.Key.TournamentId)).Version);
        Assert.Equal("Applied", (await new ApplyEvaluation(verify).InitialAsync(e, message, "retry")).Status);
    }
    [Fact]
    public async Task Missing_scoring_dependency_is_deferred_not_fake_zero_or_terminal_receipt()
    {
        var e = await NewEvaluation(); e = e with { Key = e.Key with { TaskId = Guid.NewGuid() } };
        var message = Guid.NewGuid(); await using var db = fixture.Open();
        Assert.Equal("Deferred", (await new ApplyEvaluation(db).InitialAsync(e, message, "missing")).Status);
        Assert.False(await db.Receipts.AnyAsync(x => x.EvaluationId == e.CheckId));
        Assert.False(await db.Inbox.AnyAsync(x => x.MessageId == message));
        Assert.Equal("Deferred", (await db.Failures.SingleAsync(x => x.MessageId == message)).State);
    }
}
