using Matigr.Results.Application;
using Microsoft.EntityFrameworkCore;

namespace Matigr.Results.Infrastructure;

internal sealed class SnapshotVersions(ResultsDbContext db) : ISnapshotVersions
{
    public async Task<string> CurrentAsync(Guid tournament, CancellationToken ct)
    {
        var version = await db.Snapshots.AsNoTracking().Where(x => x.TournamentId == tournament).Select(x => (long?)x.Version).SingleOrDefaultAsync(ct)
            ?? throw new ResultReadException(404, "TournamentNotFound");
        return ApplyEvaluation.Version(version);
    }
}
