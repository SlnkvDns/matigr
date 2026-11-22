using Matigr.Checking.Domain;

namespace Matigr.Checking.Application;

public sealed record ExistingRecheck(Guid CheckId, Guid SolutionId, AppealBasis Basis);
public sealed record RecheckAdmissionDecision(string? Rejection = null, ExistingRecheck? Existing = null);

public interface IRecheckEligibility
{
    Task<bool> HasReviewerScopeAsync(Guid reviewer, Guid tournament, CancellationToken ct);
    Task<ExistingRecheck?> FindExistingAsync(Guid appeal, CancellationToken ct);
    Task<bool> IsReviewerBusyAsync(Guid reviewer, CancellationToken ct);
}

public sealed class RecheckAdmission(IRecheckEligibility eligibility)
{
    public async Task<RecheckAdmissionDecision> DecideAsync(RecheckRequest request, SolutionReference source,
        Guid baseSolution, CheckKind baseKind, CheckOutcome? baseOutcome, CancellationToken ct)
    {
        if (source.TournamentId != request.TournamentId || source.TeamId != request.TeamId || source.TaskId != request.TaskId
            || source.MaterialVersionId != request.MaterialVersionId || baseSolution != request.SolutionId
            || baseKind != CheckKind.Initial || baseOutcome != request.BaseOutcome)
            return new("BasisMismatch");
        if (!await eligibility.HasReviewerScopeAsync(request.RequestedReviewerId, request.TournamentId, ct))
            return new("ReviewerScopeConflict");
        var existing = await eligibility.FindExistingAsync(request.AppealId, ct);
        if (existing is not null)
        {
            var basis = existing.Basis;
            return existing.SolutionId != request.SolutionId || basis.BaseCheckId != request.BaseCheckId || basis.ResultId != request.ResultId
                || basis.BasePublicationId != request.PublicationId || basis.ExpectedResultVersion != request.ExpectedResultVersion
                || basis.SelectedReviewerId != request.RequestedReviewerId || basis.BaseOutcome != request.BaseOutcome
                ? new("AppealBasisConflict") : new(Existing: existing);
        }
        return await eligibility.IsReviewerBusyAsync(request.RequestedReviewerId, ct) ? new("ReviewerBusy") : new();
    }

    public static Check Assign(RecheckRequest request, SolutionReference source, Check baseCheck, long revision, DateTimeOffset now)
    {
        var basis = new AppealBasis(request.AppealId, request.RequestId, request.ResultId, request.ExpectedResultVersion,
            request.BaseCheckId, request.PublicationId, request.RequestedReviewerId, request.BaseOutcome);
        return Check.CreateAppeal(Guid.NewGuid(), source, revision, basis, baseCheck, Guid.NewGuid(),
            new(request.RequestedReviewerId, request.TournamentId, ReviewerRole.Reviewer), now);
    }
}
