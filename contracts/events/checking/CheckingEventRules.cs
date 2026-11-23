using System.Text.Json;
using Matigr.Transport;

namespace Matigr.Checking.Contracts;

internal static class CheckingEventRules
{
    public static void Validate(JsonElement envelope, string route)
    {
        if (!route.StartsWith("checking.", StringComparison.Ordinal)) return;
        var payload = envelope.GetProperty("payload");
        if (route is "checking.check-completed.initial.v1" or "checking.check-completed.appeal.v1"
            && payload.GetProperty("kind").GetString() != (route.EndsWith(".initial.v1", StringComparison.Ordinal) ? "Initial" : "Appeal"))
            throw new InvalidMessageException("InvalidRoutingKind");
        if ((payload.TryGetProperty("assignmentKind", out var kind) && kind.GetString() == "Selected"
            || payload.TryGetProperty("decisionOrigin", out var origin) && origin.GetString() == "AppealReview")
            && payload.GetProperty("reviewerId").GetString() != payload.GetProperty("selectedReviewerId").GetString())
            throw new InvalidMessageException("InvalidSelectedReviewer");
        if (payload.TryGetProperty("checkId", out var check) && envelope.GetProperty("aggregateType").GetString() == "Check"
            && check.GetString() != envelope.GetProperty("aggregateId").GetString()) throw new InvalidMessageException("InvalidLineage");
    }
}
