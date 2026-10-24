using System.Text.Json;
using Matigr.Checking.Application;
using Matigr.Checking.Domain;
using Matigr.Checking.Infrastructure.Storage;
using Matigr.Transport;
using Microsoft.EntityFrameworkCore;

namespace Checking.Persistence.Tests;

public partial class StorageTests
{
    private async Task<RecheckRequest> RecheckSeed()
    {
        var seed = await Seed(); var reviewer = Guid.NewGuid(); await SeedScope(reviewer, seed.TournamentId);
        var actor = Actor(reviewer, seed.TournamentId);
        await using var db = fixture.Open();
        var a = (await Work(db).TakeNextAsync(actor, seed.TournamentId, "general", "base", "base", default)).Assignment!;
        await Work(db).TransitionAsync(actor, seed.CheckId, Guid.Parse(a.AssignmentId), a.Capability, "Correct", 2, "base-complete", "base", default);
        var s = await db.SolutionReferences.AsNoTracking().SingleAsync(x => x.SolutionId == seed.SolutionId);
        return new(Guid.NewGuid(), Guid.NewGuid(), s.SolutionId, s.MaterialVersionId, s.TournamentId, s.TeamId, s.TaskId,
            Guid.NewGuid(), 1, Guid.NewGuid(), seed.CheckId, reviewer, CheckOutcome.Correct, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }
    private RecheckRequests Rechecks(CheckingDbContext db) => new(db, new(fixture.KeyPath), TimeProvider.System);
    [Fact]
    public async Task Request_assigns_original_reviewer_once_and_replays_before_busy_or_later_state()
    {
        var request = await RecheckSeed(); await using var db = fixture.Open(); var service = Rechecks(db);
        Assert.True((await service.HandleAsync(request, Guid.NewGuid(), "request")).Applied);
        var row = await db.Checks.SingleAsync(x => x.AppealId == request.AppealId);
        var a = await db.Assignments.SingleAsync(x => x.CheckId == row.CheckId);
        Assert.Equal(request.RequestedReviewerId, a.ReviewerId); Assert.Equal(CheckState.Assigned, row.State);
        var first = await db.RecheckRequestReceipts.SingleAsync(x => x.RequestId == request.RequestId);
        using var envelope = JsonDocument.Parse(first.ResponseEnvelope);
        new WireSchema(typeof(Matigr.Checking.Infrastructure.CheckingTransport).Assembly, "recheck-assigned.v1.schema.json").Validate(envelope.RootElement);
        Assert.True((await service.HandleAsync(request, Guid.NewGuid(), "retry")).Applied);
        Assert.True((await service.HandleAsync(request with { RequestId = Guid.NewGuid() }, Guid.NewGuid(), "alias")).Applied);
        Assert.Single(await db.Checks.Where(x => x.AppealId == request.AppealId).ToListAsync());
        var bad = request with { MaterialVersionId = Guid.NewGuid() };
        await Assert.ThrowsAsync<InvalidMessageException>(() => service.HandleAsync(bad, Guid.NewGuid(), "conflict"));
    }
    [Fact]
    public async Task Busy_receipt_remains_busy_after_release_new_manual_attempt_creates_one_check()
    {
        var request = await RecheckSeed(); await Seed(request.TournamentId);
        var actor = Actor(request.RequestedReviewerId, request.TournamentId);
        await using var db = fixture.Open();
        var busy = (await Work(db).TakeNextAsync(actor, request.TournamentId, "general", "busy", "busy", default)).Assignment!;
        var handler = Rechecks(db); Assert.True((await handler.HandleAsync(request, Guid.NewGuid(), "request")).Applied);
        Assert.False(await db.Checks.AnyAsync(x => x.AppealId == request.AppealId));
        var receipt = await db.RecheckRequestReceipts.SingleAsync(x => x.RequestId == request.RequestId);
        Assert.Equal("ReviewerBusy", receipt.ReasonCode);
        using (var wire = JsonDocument.Parse(receipt.ResponseEnvelope)) new WireSchema(typeof(Matigr.Checking.Infrastructure.CheckingTransport).Assembly, "recheck-request-rejected.v1.schema.json").Validate(wire.RootElement);
        await Work(db).TransitionAsync(actor, Guid.Parse(busy.Check.CheckId), Guid.Parse(busy.AssignmentId), busy.Capability, "Incorrect", 2, "free", "free", default);
        await handler.HandleAsync(request, Guid.NewGuid(), "old-busy");
        Assert.False(await db.Checks.AnyAsync(x => x.AppealId == request.AppealId));
        await handler.HandleAsync(request with { RequestId = Guid.NewGuid(), AcceptedAt = DateTimeOffset.UtcNow }, Guid.NewGuid(), "new-manual");
        Assert.Single(await db.Checks.Where(x => x.AppealId == request.AppealId).ToListAsync());
    }
    [Fact]
    public async Task Appeal_refusal_and_senior_assignment_preserve_check_request_selected_and_revoke_old_capability()
    {
        var request = await RecheckSeed(); var senior = Guid.NewGuid(); await SeedScope(senior, request.TournamentId, ReviewerRole.Senior);
        await using var db = fixture.Open(); await Rechecks(db).HandleAsync(request, Guid.NewGuid(), "claim");
        var actor = Actor(request.RequestedReviewerId, request.TournamentId); var work = Work(db);
        var current = (await work.MyAssignmentAsync(actor, default))!;
        var refused = await work.TransitionAsync(actor, Guid.Parse(current.Check.CheckId), Guid.Parse(current.AssignmentId), current.Capability,
            null, 1, "refuse", "refuse", default);
        Assert.Equal("SeniorQueued", refused.State); Assert.Null(await work.MyAssignmentAsync(actor, default));
        var final = (await work.TakeNextAsync(Actor(senior, request.TournamentId), request.TournamentId, "senior", "senior", "senior", default)).Assignment!;
        Assert.Equal(current.Check.CheckId, final.Check.CheckId); Assert.NotEqual(current.AssignmentId, final.AssignmentId);
        var row = await db.Checks.SingleAsync(x => x.AppealId == request.AppealId);
        Assert.Equal(request.RequestId, row.RecheckRequestId); Assert.Equal(request.RequestedReviewerId, row.SelectedReviewerId);
        Assert.Equal("SeniorEscalation", (await work.TransitionAsync(Actor(senior, request.TournamentId), row.CheckId,
            Guid.Parse(final.AssignmentId), final.Capability, "Correct", long.Parse(final.Check.Version), "complete", "complete", default)).DecisionOrigin);
        await Assert.ThrowsAsync<CheckingOperationException>(() => work.MaterialAsync(actor, row.CheckId, default));
    }
}
