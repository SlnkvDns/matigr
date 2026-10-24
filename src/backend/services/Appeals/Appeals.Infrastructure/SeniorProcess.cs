using System.Text.Json;
using Matigr.Appeals.Domain;
using Matigr.Transport;
namespace Matigr.Appeals.Infrastructure;
internal sealed partial class AppealProcess
{
    private static Appeal ApplySenior(string? name, JsonElement p, JsonElement envelope, Appeal before)
    {
        if (name != "matigr.checking.recheck-escalated") throw new InvalidMessageException("UnknownProcessMessage");
        var b = before.Basis; var selected = before.Attempt!; var version = Version(envelope, "aggregateVersion");
        if (Id(p, "resultId") != b.ResultId || Version(p, "expectedResultVersion") != b.ResultVersion || Id(p, "tournamentId") != b.TournamentId
            || Id(p, "teamId") != b.TeamId || Id(p, "taskId") != b.TaskId || Id(p, "selectedReviewerId") != selected.ReviewerId
            || Id(p, "releasedReviewerId") != selected.ReviewerId) throw new InvalidMessageException("EscalationCorrelationConflict");
        if (before.Progress is { } known && version > known.CheckVersion && known.EscalationId is null
            && known.AssignmentId is { } assignment && Id(p, "releasedAssignmentId") != assignment) throw new InvalidMessageException("ReleasedAssignmentConflict");
        return before.Observe(new(selected.RequestId, Id(p, "checkId"), selected.ReviewerId, version, null, null, Id(p, "escalationId")), AppealState.AwaitingSeniorReview);
    }
}
