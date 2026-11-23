using Matigr.Checking.Application;
using Matigr.Checking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Checking.Persistence.Tests;

public partial class StorageTests
{
    [Theory]
    [InlineData("Correct")]
    [InlineData("Incorrect")]
    [InlineData(null)]
    public async Task Transition_is_atomic_replayable_and_releases_reviewer_before_Results(string? outcome)
    {
        var seeded = await Seed(); await Seed(seeded.TournamentId);
        var reviewer = Guid.NewGuid(); await SeedScope(reviewer, seeded.TournamentId);
        var actor = Actor(reviewer, seeded.TournamentId);
        await using var db = fixture.Open();
        var assignment = (await Work(db).TakeNextAsync(actor, seeded.TournamentId, "general", "claim", "claim", default)).Assignment!;
        var checkId = Guid.Parse(assignment.Check.CheckId); var aid = Guid.Parse(assignment.AssignmentId);
        var result = await Work(db).TransitionAsync(actor, checkId, aid, assignment.Capability, outcome, 2, "transition", "trace", default);
        Assert.Equal(outcome is null ? "SeniorQueued" : "Completed", result.State);
        Assert.Equal("3", result.Version);
        Assert.Null(await Work(db).MyAssignmentAsync(actor, default));
        Assert.Equal(result, await Work(db).TransitionAsync(actor, checkId, aid, assignment.Capability, outcome, 1, "transition", "other", default));
        var stale = await Assert.ThrowsAsync<CheckingOperationException>(() => Work(db).TransitionAsync(actor, checkId, aid, assignment.Capability, outcome, 2, "fresh", "trace", default));
        Assert.Equal(412, stale.Status);
        var bad = await Assert.ThrowsAsync<CheckingOperationException>(() => Work(db).TransitionAsync(actor, checkId, aid, "wrong", outcome, 2, "transition", "trace", default));
        Assert.Equal(403, bad.Status);
        Assert.Equal(200, (await Work(db).TakeNextAsync(actor, seeded.TournamentId, "general", "next", "trace", default)).Status);
        await using var verify = fixture.Open();
        Assert.Equal(outcome is null ? 0 : 1, await verify.Outbox.CountAsync(x => x.CorrelationId == checkId));
        if (outcome is not null)
        {
            var fact = await verify.Outbox.SingleAsync(x => x.CorrelationId == checkId);
            Assert.DoesNotContain(assignment.Capability, fact.Envelope);
            Assert.Contains("\"outcome\":\"" + outcome + "\"", fact.Envelope.Replace(" ", ""));
        }
    }

    [Fact]
    public async Task Racing_complete_and_refuse_have_one_winner()
    {
        var seed = await Seed(); var reviewer = Guid.NewGuid(); await SeedScope(reviewer, seed.TournamentId);
        var actor = Actor(reviewer, seed.TournamentId);
        await using var claim = fixture.Open();
        var a = (await Work(claim).TakeNextAsync(actor, seed.TournamentId, "general", "claim", "trace", default)).Assignment!;
        async Task<int> Run(string? outcome)
        {
            await using var db = fixture.Open();
            try { await Work(db).TransitionAsync(actor, Guid.Parse(a.Check.CheckId), Guid.Parse(a.AssignmentId), a.Capability, outcome, 2, "race", "trace", default); return 200; }
            catch (CheckingOperationException e) { return e.Status; }
        }
        Assert.Equal(new[] { 200, 412 }, (await Task.WhenAll(Run("Correct"), Run(null))).Order().ToArray());
        await using var verify = fixture.Open();
        Assert.Equal(1, await verify.CheckHistory.CountAsync(x => x.CheckId == Guid.Parse(a.Check.CheckId) && x.Version == 3));
    }

    [Fact]
    public async Task Failure_persisting_transition_receipt_rolls_back_outbox_history_and_release()
    {
        var seed = await Seed(); var reviewer = Guid.NewGuid(); await SeedScope(reviewer, seed.TournamentId);
        var actor = Actor(reviewer, seed.TournamentId);
        await using var claim = fixture.Open();
        var a = (await Work(claim).TakeNextAsync(actor, seed.TournamentId, "general", "claim", "trace", default)).Assignment!;
        var options = new DbContextOptionsBuilder<Matigr.Checking.Infrastructure.Storage.CheckingDbContext>()
            .UseNpgsql(fixture.ConnectionString).AddInterceptors(new FailReceipt()).Options;
        await using (var failing = new Matigr.Checking.Infrastructure.Storage.CheckingDbContext(options))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Work(failing).TransitionAsync(actor, Guid.Parse(a.Check.CheckId),
                Guid.Parse(a.AssignmentId), a.Capability, "Correct", 2, "complete", "trace", default));
        await using var verify = fixture.Open();
        Assert.Equal(a, await Work(verify).MyAssignmentAsync(actor, default));
        Assert.Empty(await verify.Outbox.Where(x => x.CorrelationId == Guid.Parse(a.Check.CheckId)).ToListAsync());
        Assert.Equal(1, await verify.CheckHistory.CountAsync(x => x.CheckId == Guid.Parse(a.Check.CheckId)));
    }
}
