using System.Text.Json;
using Matigr.Appeals.Domain;
using Matigr.Transport;

namespace Matigr.Appeals.Infrastructure;

internal sealed partial class AppealProcess
{
    private static Appeal ApplyFinal(string? name, JsonElement p, JsonElement envelope, Appeal before, out bool handled)
    {
        handled = name is "matigr.checking.check-completed" or "matigr.results.result-evaluation-applied" or "matigr.results.result-evaluation-rejected";
        if (!handled) return before;
        var b = before.Basis; var request = before.Attempt!;
        if (Id(p, "selectedReviewerId") != request.ReviewerId || Id(p, "baseCheckId") != b.BaseCheckId || p.GetProperty("kind").GetString() != "Appeal")
            throw new InvalidMessageException("FinalReceiptCorrelationConflict");
        var escalation = p.TryGetProperty("escalationId", out _) ? Id(p, "escalationId") : (Guid?)null;
        if (name == "matigr.checking.check-completed") {
            CheckBasis(p, before);
            var origin = Enum.Parse<DecisionOrigin>(p.GetProperty("decisionOrigin").GetString()!, false);
            if ((origin == DecisionOrigin.SeniorEscalation) != escalation.HasValue || origin == DecisionOrigin.OrdinaryReview)
                throw new InvalidMessageException("FinalEvaluationLineageConflict");
            return before.Observe(new(request.RequestId, Id(p, "checkId"), request.ReviewerId, Version(envelope, "aggregateVersion"),
                Id(p, "assignmentId"), Id(p, "reviewerId"), escalation), AppealState.AwaitingResultApplication);
        }
        var evaluation = Id(p, "evaluationId");
        if (envelope.GetProperty("aggregateId").GetString() != evaluation.ToString("D") || (before.Progress is { } progress && progress.CheckId != evaluation)
            || (before.Receipt is { } terminal && terminal.EvaluationId != evaluation)) throw new InvalidMessageException("FinalEvaluationIdentityConflict");
        var evaluationOrigin = Enum.Parse<DecisionOrigin>(p.GetProperty("evaluationDecisionOrigin").GetString()!, false);
        if ((evaluationOrigin == DecisionOrigin.SeniorEscalation) != escalation.HasValue || evaluationOrigin == DecisionOrigin.OrdinaryReview
            || (evaluationOrigin == DecisionOrigin.AppealReview && Id(p, "reviewerId") != request.ReviewerId)) throw new InvalidMessageException("FinalEvaluationLineageConflict");
        if (name == "matigr.results.result-evaluation-rejected") {
            CheckTuple(p, before, false);
            return before.Rejected(new(evaluation, request.RequestId, request.ReviewerId, Id(p, "reviewerId"), Id(p, "assignmentId"), evaluationOrigin,
                escalation, p.GetProperty("reasonCode").GetString()!));
        }
        CheckTuple(p, before);
        if (Id(p, "resultId") != b.ResultId || Id(p, "basePublicationId") != b.PublicationId || p.GetProperty("previousOutcome").GetString() != b.Outcome.ToString()
            || p.GetProperty("scoreRuleVersion").GetString() != b.ScoreRuleVersion || p.GetProperty("publicationState").GetString() != "Published")
            throw new InvalidMessageException("PublishedReceiptBasisConflict");
        return before.Applied(new(evaluation, request.RequestId, request.ReviewerId, Id(p, "reviewerId"), Id(p, "assignmentId"), evaluationOrigin,
            escalation, b, Version(p, "resultVersion"), Id(p, "publicationId"), Enum.Parse<Outcome>(p.GetProperty("outcome").GetString()!, false), p.GetProperty("changed").GetBoolean()));
    }
}
