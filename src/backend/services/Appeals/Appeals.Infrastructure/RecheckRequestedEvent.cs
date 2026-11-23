using System.Text.Json;
using Matigr.Appeals.Domain;

namespace Matigr.Appeals.Infrastructure;

internal static class RecheckRequestedEvent
{
    public static (OutboxRow Message, string PayloadHash) Create(Appeal appeal, string causationId)
    {
        var basis = appeal.Basis;
        var attempt = appeal.Attempt ?? throw new InvalidOperationException("A recheck request requires a selection.");
        var payload = JsonSerializer.SerializeToElement(new {
            solutionId = basis.SolutionId.ToString("D"), materialVersionId = basis.MaterialVersionId.ToString("D"),
            tournamentId = basis.TournamentId.ToString("D"), teamId = basis.TeamId.ToString("D"), taskId = basis.TaskId.ToString("D"),
            appealId = appeal.AppealId.ToString("D"), recheckRequestId = attempt.RequestId.ToString("D"),
            resultId = basis.ResultId.ToString("D"), expectedResultVersion = AppealStorage.Number(basis.ResultVersion),
            publicationId = basis.PublicationId.ToString("D"), baseCheckId = basis.BaseCheckId.ToString("D"),
            requestedReviewerId = attempt.ReviewerId.ToString("D"), baseOutcome = basis.Outcome.ToString(),
            submittedAt = AppealStorage.Stamp(appeal.SubmittedAt), acceptedAt = AppealStorage.Stamp(attempt.AcceptedAt)
        }, AppealStorage.Json);
        var message = Guid.NewGuid();
        var envelope = JsonSerializer.Serialize(new {
            messageId = message.ToString("D"), messageType = "matigr.appeals.recheck-requested", schemaVersion = 1,
            producer = "Appeals", occurredAt = AppealStorage.Stamp(attempt.AcceptedAt), aggregateType = "Appeal", aggregateId = appeal.AppealId.ToString("D"),
            aggregateVersion = AppealStorage.Number(appeal.Version), correlationId = appeal.AppealId.ToString("D"), causationId, payload
        }, AppealStorage.Json);
        return (new() { MessageId = message, Envelope = envelope, RoutingKey = "appeals.recheck-requested.v1",
            CreatedAt = attempt.AcceptedAt, NextAttemptAt = attempt.AcceptedAt }, AppealStorage.Hash(payload.GetRawText()));
    }
}
