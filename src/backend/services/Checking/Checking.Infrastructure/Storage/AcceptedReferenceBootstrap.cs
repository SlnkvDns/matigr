using Matigr.Checking.Application;
using Matigr.Checking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Matigr.Checking.Infrastructure.Storage;

internal sealed class AcceptedReferenceBootstrap(CheckingDbContext db, TimeProvider clock) : IAcceptedReferenceBootstrap
{
    public async Task<BootstrappedCheck> EnsureAsync(SolutionReference snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // PostgreSQL timestamptz persists microseconds. Canonicalize before insert and comparison.
        var acceptedAt = snapshot.AcceptedAt.AddTicks(-(snapshot.AcceptedAt.Ticks % 10));
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO solution_references
              (solution_id, tournament_id, team_id, task_id, material_version_id, material_ref, accepted_at, source_version)
            VALUES ({snapshot.SolutionId}, {snapshot.TournamentId}, {snapshot.TeamId}, {snapshot.TaskId},
                    {snapshot.MaterialVersionId}, {snapshot.MaterialRef}, {acceptedAt}, {snapshot.SourceVersion})
            ON CONFLICT DO NOTHING
            """, cancellationToken);
        // Serialize both identical snapshots and conflicting business keys on the stored solution.
        var candidates = await db.SolutionReferences.FromSqlInterpolated($"""
            SELECT * FROM solution_references
            WHERE solution_id = {snapshot.SolutionId}
               OR (tournament_id = {snapshot.TournamentId} AND team_id = {snapshot.TeamId} AND task_id = {snapshot.TaskId})
            FOR UPDATE
            """).AsNoTracking().ToListAsync(cancellationToken);
        if (candidates.Count != 1 || !Matches(candidates[0], snapshot, acceptedAt))
            throw new AcceptedReferenceConflictException();
        var existing = await db.Checks.AsNoTracking().SingleOrDefaultAsync(
            check => check.SolutionId == snapshot.SolutionId && check.Kind == CheckKind.Initial, cancellationToken);
        BootstrappedCheck result;
        if (existing is not null) result = new(existing.CheckId, false);
        else
        {
            var check = Check.CreateInitial(Guid.NewGuid(), snapshot);
            var now = clock.GetUtcNow();
            db.Checks.Add(new CheckRow { CheckId = check.CheckId, SolutionId = snapshot.SolutionId,
                TournamentId = snapshot.TournamentId, Kind = check.Kind, ReviewRevision = check.ReviewRevision,
                State = check.State, Version = check.Version, CreatedAt = now });
            db.CheckHistory.Add(new CheckHistoryRow { HistoryId = Guid.NewGuid(), CheckId = check.CheckId,
                Version = check.Version, TransitionType = "InitialCheckCreated", ToState = check.State, OccurredAt = now });
            await db.SaveChangesAsync(cancellationToken);
            result = new(check.CheckId, true);
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static bool Matches(SolutionReferenceRow row, SolutionReference snapshot, DateTimeOffset acceptedAt) =>
        row.SolutionId == snapshot.SolutionId && row.TournamentId == snapshot.TournamentId && row.TeamId == snapshot.TeamId
        && row.TaskId == snapshot.TaskId && row.MaterialVersionId == snapshot.MaterialVersionId && row.MaterialRef == snapshot.MaterialRef
        && row.SourceVersion == snapshot.SourceVersion && row.AcceptedAt == acceptedAt;
}
