using System.Text.Json;
using Matigr.Appeals.Application;
using Matigr.Appeals.Infrastructure;
using Matigr.Transport;
using Microsoft.EntityFrameworkCore;

namespace Appeals.Persistence.Tests;

public partial class StorageTests
{
    private async Task<AppealRow> ClaimedAppeal()
    {
        var row = await RegisteredAppeal(); await using var db = fixture.Open();
        await new AppealWork(db).ClaimAsync(new(Guid.NewGuid(), ["Jury"], [row.TournamentId], null), row.AppealId, 1, "claim", default);
        return await db.Appeals.AsNoTracking().SingleAsync(x => x.AppealId == row.AppealId);
    }
    private static JsonElement Assigned(AppealRow row, Guid check, Guid? message = null)
    {
        var a = AppealStorage.Read(row); var attempt = a.Attempt!;
        return JsonSerializer.SerializeToElement(new { messageId = message ?? Guid.NewGuid(), messageType = "matigr.checking.recheck-assigned", schemaVersion = 1,
            producer = "Checking", occurredAt = DateTime.UtcNow, aggregateType = "Check", aggregateId = check, aggregateVersion = "1", correlationId = a.AppealId, causationId = Guid.NewGuid(),
            payload = new { appealId = a.AppealId, recheckRequestId = attempt.RequestId, checkId = check, selectedReviewerId = attempt.ReviewerId, reviewerId = attempt.ReviewerId,
                assignmentId = Guid.NewGuid(), assignmentKind = "Selected", solutionId = row.SolutionId, materialVersionId = row.MaterialVersionId, tournamentId = row.TournamentId,
                teamId = row.TeamId, taskId = row.TaskId, resultId = row.ResultId, publicationId = row.BasePublicationId, expectedResultVersion = "1", reviewRevision = "2",
                createdAt = DateTime.UtcNow, assignedAt = DateTime.UtcNow } });
    }
    private static JsonElement Busy(AppealRow row, Guid? message = null)
    {
        var a = AppealStorage.Read(row); var attempt = a.Attempt!;
        return JsonSerializer.SerializeToElement(new { messageId = message ?? Guid.NewGuid(), messageType = "matigr.checking.recheck-request-rejected", schemaVersion = 1,
            producer = "Checking", occurredAt = DateTime.UtcNow, aggregateType = "RecheckRequest", aggregateId = attempt.RequestId, aggregateVersion = "1", correlationId = a.AppealId, causationId = Guid.NewGuid(),
            payload = new { appealId = a.AppealId, recheckRequestId = attempt.RequestId, resultId = row.ResultId, expectedResultVersion = "1", requestedReviewerId = attempt.ReviewerId,
                solutionId = row.SolutionId, reasonCode = "ReviewerBusy", rejectedAt = DateTime.UtcNow } });
    }
    [Fact]
    public async Task Assigned_fact_confirms_exact_selected_reviewer_and_duplicate_is_idempotent()
    {
        var row = await ClaimedAppeal(); var fact = Assigned(row, Guid.NewGuid());
        new WireSchema(typeof(AppealsTransport).Assembly, "recheck-assigned.v1.schema.json").Validate(fact);
        await using var db = fixture.Open(); var process = new AppealProcess(db);
        Assert.True((await process.HandleAsync(fact, default)).Applied);
        Assert.True((await process.HandleAsync(fact, default)).Applied);
        var saved = await db.Appeals.AsNoTracking().SingleAsync(x => x.AppealId == row.AppealId);
        Assert.Equal("RecheckPending", saved.State); Assert.Equal(3, saved.Version);
        Assert.Equal(fact.GetProperty("payload").GetProperty("checkId").GetGuid(), saved.CheckId);
        Assert.Equal(3, await db.History.CountAsync(x => x.AppealId == row.AppealId));
    }
    [Fact]
    public async Task Confirmed_busy_allows_new_manual_attempt_old_busy_cannot_cancel_current_choice()
    {
        var row = await ClaimedAppeal(); var busy = Busy(row);
        new WireSchema(typeof(AppealsTransport).Assembly, "recheck-request-rejected.v1.schema.json").Validate(busy);
        await using var db = fixture.Open(); var process = new AppealProcess(db);
        Assert.True((await process.HandleAsync(busy, default)).Applied);
        var saved = await db.Appeals.AsNoTracking().SingleAsync(x => x.AppealId == row.AppealId); Assert.Equal("Registered", saved.State);
        Assert.Equal("RejectedBusy", (await db.Attempts.SingleAsync(x => x.AppealId == row.AppealId)).State);
        var actor = new AppealActor(Guid.NewGuid(), ["Jury"], [row.TournamentId], null);
        await new AppealWork(db).ClaimAsync(actor, row.AppealId, 3, "retry", default);
        Assert.True((await process.HandleAsync(Busy(row), default)).Applied);
        var current = await db.Appeals.AsNoTracking().SingleAsync(x => x.AppealId == row.AppealId);
        Assert.Equal("AcceptedAwaitingRecheck", current.State); Assert.Equal(4, current.Version);
        Assert.NotEqual(row.CurrentRequestId, current.CurrentRequestId);
    }
    [Fact]
    public async Task Conflicting_message_identity_and_foreign_basis_do_not_advance_process()
    {
        var row = await ClaimedAppeal(); var check = Guid.NewGuid(); var message = Guid.NewGuid(); var fact = Assigned(row, check, message);
        await using var db = fixture.Open(); var process = new AppealProcess(db); await process.HandleAsync(fact, default);
        await Assert.ThrowsAsync<InvalidMessageException>(() => process.HandleAsync(Assigned(row, Guid.NewGuid(), message), default));
        var foreign = Assigned(row, check); var node = System.Text.Json.Nodes.JsonNode.Parse(foreign.GetRawText())!;
        node["payload"]!["materialVersionId"] = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidMessageException>(() => process.HandleAsync(JsonSerializer.SerializeToElement(node), default));
        Assert.Equal(3, (await db.Appeals.AsNoTracking().SingleAsync(x => x.AppealId == row.AppealId)).Version);
    }
}
