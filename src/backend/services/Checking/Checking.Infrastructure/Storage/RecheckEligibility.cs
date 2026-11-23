using Matigr.Checking.Application;
using Matigr.Checking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Matigr.Checking.Infrastructure.Storage;

internal sealed class RecheckEligibility(CheckingDbContext db) : IRecheckEligibility
{
    public Task<bool> HasReviewerScopeAsync(Guid reviewer, Guid tournament, CancellationToken ct) =>
        db.ReviewerScopes.AnyAsync(x => x.ReviewerId == reviewer && x.TournamentId == tournament && x.Role == ReviewerRole.Reviewer, ct);

    public async Task<ExistingRecheck?> FindExistingAsync(Guid appeal, CancellationToken ct)
    {
        var row = await db.Checks.AsNoTracking().SingleOrDefaultAsync(x => x.AppealId == appeal, ct);
        return row is null ? null : new(row.CheckId, row.SolutionId, new(appeal, row.RecheckRequestId!.Value,
            row.ResultId!.Value, row.ExpectedResultVersion!.Value, row.BaseCheckId!.Value,
            row.BasePublicationId!.Value, row.SelectedReviewerId!.Value, row.BaseOutcome!.Value));
    }

    public Task<bool> IsReviewerBusyAsync(Guid reviewer, CancellationToken ct) =>
        db.Assignments.AnyAsync(x => x.ReviewerId == reviewer && x.State == AssignmentState.Active, ct);
}
