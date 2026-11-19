using System.Text.Json;
using System.Text.Json.Nodes;
using Matigr.Appeals.Infrastructure;
using Matigr.Transport;
using Microsoft.EntityFrameworkCore;

namespace Appeals.Persistence.Tests;

public partial class StorageTests
{
    private static JsonElement Applied(AppealRow row, Guid check, Guid assignment, bool changed = false)
    {
        var a = AppealStorage.Read(row); var attempt = a.Attempt!;
        return JsonSerializer.SerializeToElement(new { messageId = Guid.NewGuid(), messageType = "matigr.results.result-evaluation-applied", schemaVersion = 1,
            producer = "Results", occurredAt = DateTime.UtcNow, aggregateType = "EvaluationReceipt", aggregateId = check, aggregateVersion = "1", correlationId = a.AppealId, causationId = Guid.NewGuid(),
            payload = new { evaluationId = check, kind = "Appeal", reviewerId = attempt.ReviewerId, assignmentId = assignment, evaluationDecisionOrigin = "AppealReview",
                solutionId = row.SolutionId, materialVersionId = row.MaterialVersionId, tournamentId = row.TournamentId, teamId = row.TeamId, taskId = row.TaskId,
                resultId = row.ResultId, expectedResultVersion = "1", resultVersion = changed ? "2" : "1", previousOutcome = "Correct", outcome = changed ? "Incorrect" : "Correct",
                previousScore = "10", score = changed ? "0" : "10", scoreRuleVersion = "1", changed, appliedAt = DateTime.UtcNow, publicationState = "Published",
                basePublicationId = row.BasePublicationId, publicationId = changed ? Guid.NewGuid() : row.BasePublicationId, appealId = row.AppealId,
                recheckRequestId = attempt.RequestId, baseCheckId = row.BaseCheckId, selectedReviewerId = attempt.ReviewerId } });
    }
    private static JsonElement Completed(AppealRow row, JsonElement assigned, bool changed = false)
    {
        var node = JsonNode.Parse(assigned.GetRawText())!; node["messageId"] = Guid.NewGuid(); node["messageType"] = "matigr.checking.check-completed"; node["aggregateVersion"] = "2";
        var p = node["payload"]!.AsObject(); p.Remove("assignmentKind"); p.Remove("createdAt"); p.Remove("assignedAt");
        p["baseCheckId"] = row.BaseCheckId; p["kind"] = "Appeal"; p["decisionOrigin"] = "AppealReview"; p["outcome"] = changed ? "Incorrect" : "Correct"; p["completedAt"] = DateTime.UtcNow;
        return JsonSerializer.SerializeToElement(node);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Early_Published_receipt_closes_without_intermediate_facts_and_preserves_base_and_final_publications(bool changed)
    {
        var row = await ClaimedAppeal(); var check = Guid.NewGuid(); var assigned = Assigned(row, check);
        var applied = Applied(row, check, assigned.GetProperty("payload").GetProperty("assignmentId").GetGuid(), changed);
        var completed = Completed(row, assigned, changed);
        new WireSchema(typeof(AppealsTransport).Assembly, "result-evaluation-applied.v1.schema.json").Validate(applied);
        new WireSchema(typeof(AppealsTransport).Assembly, "check-completed.v1.schema.json").Validate(completed);
        await using var db = fixture.Open(); var process = new AppealProcess(db);
        Assert.True((await process.HandleAsync(applied, default)).Applied);
        Assert.True((await process.HandleAsync(assigned, default)).Applied); Assert.True((await process.HandleAsync(completed, default)).Applied);
        Assert.True((await process.HandleAsync(applied, default)).Applied);
        var saved = await db.Appeals.AsNoTracking().SingleAsync(x => x.AppealId == row.AppealId);
        Assert.Equal("Closed", saved.State); Assert.Equal(3, saved.Version); Assert.Equal(row.BasePublicationId, saved.BasePublicationId);
        Assert.Equal(applied.GetProperty("payload").GetProperty("publicationId").GetGuid(), saved.FinalPublicationId);
        Assert.Equal(changed, saved.BasePublicationId != saved.FinalPublicationId); Assert.Equal(3, await db.History.CountAsync(x => x.AppealId == row.AppealId));
    }
    [Fact]
    public async Task CheckCompleted_waits_for_Results_and_foreign_receipt_does_not_close()
    {
        var row = await ClaimedAppeal(); var check = Guid.NewGuid(); var assigned = Assigned(row, check);
        await using var db = fixture.Open(); var process = new AppealProcess(db);
        await process.HandleAsync(Completed(row, assigned), default);
        Assert.Equal("AwaitingResultApplication", (await db.Appeals.AsNoTracking().SingleAsync(x => x.AppealId == row.AppealId)).State);
        var applied = Applied(row, check, assigned.GetProperty("payload").GetProperty("assignmentId").GetGuid());
        var bad = JsonNode.Parse(applied.GetRawText())!; bad["payload"]!["materialVersionId"] = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidMessageException>(() => process.HandleAsync(JsonSerializer.SerializeToElement(bad), default));
        Assert.Null((await db.Appeals.AsNoTracking().SingleAsync(x => x.AppealId == row.AppealId)).FinalPublicationId);
        await process.HandleAsync(applied, default);
        Assert.Equal("Closed", (await db.Appeals.AsNoTracking().SingleAsync(x => x.AppealId == row.AppealId)).State);
    }
    [Fact]
    public async Task Early_Rejected_retains_evaluation_lineage_and_conflict_without_rebasing_or_reopening()
    {
        var row = await ClaimedAppeal(); var a = AppealStorage.Read(row); var check = Guid.NewGuid(); var assignment = Guid.NewGuid();
        var rejected = JsonSerializer.SerializeToElement(new { messageId = Guid.NewGuid(), messageType = "matigr.results.result-evaluation-rejected", schemaVersion = 1,
            producer = "Results", occurredAt = DateTime.UtcNow, aggregateType = "EvaluationReceipt", aggregateId = check, aggregateVersion = "1", correlationId = a.AppealId, causationId = Guid.NewGuid(),
            payload = new { evaluationId = check, kind = "Appeal", reviewerId = a.Attempt!.ReviewerId, assignmentId = assignment, evaluationDecisionOrigin = "AppealReview",
                solutionId = row.SolutionId, tournamentId = row.TournamentId, teamId = row.TeamId, taskId = row.TaskId, resultId = row.ResultId,
                expectedResultVersion = "1", actualResultVersion = "2", reasonCode = "ResultVersionConflict", rejectedAt = DateTime.UtcNow,
                appealId = row.AppealId, recheckRequestId = a.Attempt.RequestId, baseCheckId = row.BaseCheckId, selectedReviewerId = a.Attempt.ReviewerId } });
        new WireSchema(typeof(AppealsTransport).Assembly, "result-evaluation-rejected.v1.schema.json").Validate(rejected);
        await using var db = fixture.Open(); await new AppealProcess(db).HandleAsync(rejected, default);
        var saved = await db.Appeals.AsNoTracking().SingleAsync(x => x.AppealId == row.AppealId);
        Assert.Equal("NeedsResolution", saved.State); Assert.Equal(check, saved.CheckId); Assert.Null(saved.FinalPublicationId);
        var conflict = AppealStorage.Read(saved); Assert.Equal("ResultVersionConflict", conflict.Conflict); Assert.Equal(check, conflict.Rejection!.EvaluationId);
        Assert.Equal(row.BasePublicationId, saved.BasePublicationId); Assert.Equal(1, saved.ExpectedResultVersion);
    }
}
