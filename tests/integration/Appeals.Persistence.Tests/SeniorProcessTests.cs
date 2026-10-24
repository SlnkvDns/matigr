using System.Text.Json;
using System.Text.Json.Nodes;
using Matigr.Appeals.Infrastructure;
using Matigr.Transport;
using Microsoft.EntityFrameworkCore;

namespace Appeals.Persistence.Tests;

public partial class StorageTests
{
    private static JsonElement Escalated(AppealRow row, JsonElement assigned, Guid escalation)
    {
        var a = AppealStorage.Read(row); var p = assigned.GetProperty("payload");
        return JsonSerializer.SerializeToElement(new { messageId = Guid.NewGuid(), messageType = "matigr.checking.recheck-escalated", schemaVersion = 1,
            producer = "Checking", occurredAt = DateTime.UtcNow, aggregateType = "Check", aggregateId = p.GetProperty("checkId").GetGuid(), aggregateVersion = "2", correlationId = a.AppealId, causationId = Guid.NewGuid(),
            payload = new { appealId = row.AppealId, recheckRequestId = a.Attempt!.RequestId, selectedReviewerId = a.Attempt.ReviewerId,
                resultId = row.ResultId, expectedResultVersion = "1", tournamentId = row.TournamentId, teamId = row.TeamId, taskId = row.TaskId,
                checkId = p.GetProperty("checkId").GetGuid(), releasedReviewerId = a.Attempt.ReviewerId,
                releasedAssignmentId = p.GetProperty("assignmentId").GetGuid(), escalationId = escalation, escalatedAt = DateTime.UtcNow } });
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Senior_receipt_is_sufficient_before_escalation_or_after_senior_progress_without_changing_selected(bool early)
    {
        var row = await ClaimedAppeal(); var check = Guid.NewGuid(); var selected = Assigned(row, check); var escalation = Guid.NewGuid(); var senior = Guid.NewGuid();
        var assignedNode = JsonNode.Parse(selected.GetRawText())!; assignedNode["messageId"] = Guid.NewGuid(); assignedNode["aggregateVersion"] = "3";
        assignedNode["payload"]!["reviewerId"] = senior; assignedNode["payload"]!["assignmentId"] = Guid.NewGuid();
        assignedNode["payload"]!["assignmentKind"] = "SeniorEscalation"; assignedNode["payload"]!["escalationId"] = escalation;
        var seniorAssigned = JsonSerializer.SerializeToElement(assignedNode);
        var appliedNode = JsonNode.Parse(Applied(row, check, assignedNode["payload"]!["assignmentId"]!.GetValue<Guid>()).GetRawText())!;
        appliedNode["payload"]!["reviewerId"] = senior; appliedNode["payload"]!["evaluationDecisionOrigin"] = "SeniorEscalation"; appliedNode["payload"]!["escalationId"] = escalation;
        var receipt = JsonSerializer.SerializeToElement(appliedNode); var escalated = Escalated(row, selected, escalation);
        new WireSchema(typeof(AppealsTransport).Assembly, "recheck-escalated.v1.schema.json").Validate(escalated);
        new WireSchema(typeof(AppealsTransport).Assembly, "recheck-assigned.v1.schema.json").Validate(seniorAssigned);
        new WireSchema(typeof(AppealsTransport).Assembly, "result-evaluation-applied.v1.schema.json").Validate(receipt);
        await using var db = fixture.Open(); var process = new AppealProcess(db);
        if (early) await process.HandleAsync(receipt, default);
        await process.HandleAsync(selected, default); await process.HandleAsync(escalated, default);
        if (!early) {
            var queued = AppealStorage.Read(await db.Appeals.AsNoTracking().SingleAsync(x => x.AppealId == row.AppealId));
            Assert.Equal("AwaitingSeniorReview", queued.State.ToString()); Assert.Null(queued.Progress!.AssignmentId);
        }
        await process.HandleAsync(seniorAssigned, default); await process.HandleAsync(receipt, default);
        var saved = AppealStorage.Read(await db.Appeals.AsNoTracking().SingleAsync(x => x.AppealId == row.AppealId));
        Assert.Equal("Closed", saved.State.ToString()); Assert.Equal(AppealStorage.Read(row).Attempt!.ReviewerId, saved.Receipt!.SelectedReviewerId);
        Assert.Equal(senior, saved.Receipt.ReviewerId); Assert.Equal(escalation, saved.Receipt.EscalationId); Assert.Equal(check, saved.Receipt.EvaluationId);
        Assert.Single(await db.Appeals.Where(x => x.AppealId == row.AppealId).ToListAsync());
    }
}
