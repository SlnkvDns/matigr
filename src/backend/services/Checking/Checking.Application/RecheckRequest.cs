using System.Security.Cryptography;
using System.Text.Json;
using Matigr.Checking.Domain;

namespace Matigr.Checking.Application;

public sealed record RecheckRequest(Guid AppealId, Guid RequestId, Guid SolutionId, Guid MaterialVersionId, Guid TournamentId,
    Guid TeamId, Guid TaskId, Guid ResultId, long ExpectedResultVersion, Guid PublicationId, Guid BaseCheckId,
    Guid RequestedReviewerId, CheckOutcome BaseOutcome, DateTimeOffset SubmittedAt, DateTimeOffset AcceptedAt)
{
    public void Validate()
    {
        if (new[] { AppealId, RequestId, SolutionId, MaterialVersionId, TournamentId, TeamId, TaskId, ResultId, PublicationId, BaseCheckId, RequestedReviewerId }.Contains(Guid.Empty)
            || ExpectedResultVersion < 1 || !Enum.IsDefined(BaseOutcome) || AcceptedAt < SubmittedAt) throw new ArgumentException("Invalid recheck request.");
    }
    public string SemanticHash()
    {
        Validate(); return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
    }
}
