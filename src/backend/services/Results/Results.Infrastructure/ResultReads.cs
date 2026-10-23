using System.Data;
using System.Text.Json;
using Matigr.Results.Application;
using Matigr.Results.Domain;
using Microsoft.EntityFrameworkCore;

namespace Matigr.Results.Infrastructure;

internal sealed class ResultReads(ResultsDbContext db) : IResultReads
{
    private sealed class TotalRank { public Guid TeamId { get; set; } public decimal Total { get; set; } public long Rank { get; set; } }
    public async Task<StandingsSnapshot> StandingsAsync(Guid tournament, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var snapshot = await db.Snapshots.AsNoTracking().SingleOrDefaultAsync(x => x.TournamentId == tournament, ct)
            ?? throw new ResultReadException(404, "TournamentNotFound");
        var tasks = await db.Tasks.AsNoTracking().Where(x => x.TournamentId == tournament).OrderBy(x => x.Order).ToArrayAsync(ct);
        var teams = await db.Teams.AsNoTracking().Where(x => x.TournamentId == tournament).OrderBy(x => x.Name).ThenBy(x => x.TeamId).ToArrayAsync(ct);
        var results = await db.Results.AsNoTracking().Where(x => x.TournamentId == tournament).ToArrayAsync(ct);
        // One production ranking algorithm: exact PostgreSQL SUM and DENSE_RANK, no ID/time tie-break.
        var ranks = await db.Database.SqlQuery<TotalRank>($"""
            SELECT "TeamId", "Total", DENSE_RANK() OVER (ORDER BY "Total" DESC) AS "Rank"
            FROM (SELECT t.team_id AS "TeamId", COALESCE(SUM(r.score), 0) AS "Total"
                  FROM tournament_teams t LEFT JOIN team_task_results r
                  ON t.tournament_id = r.tournament_id AND t.team_id = r.team_id
                  WHERE t.tournament_id = {tournament} GROUP BY t.team_id) totals
            """).ToDictionaryAsync(x => x.TeamId, ct);
        var cells = results.ToDictionary(x => (x.TeamId, x.TaskId));
        var view = new StandingsSnapshot(tournament.ToString("D"), ApplyEvaluation.Version(snapshot.Version),
            tasks.Select(x => new StandingTask(x.TaskId.ToString("D"), x.Name)).ToArray(),
            teams.Select(t => new StandingTeam(t.TeamId.ToString("D"), t.Name,
                tasks.Select(task => cells.TryGetValue((t.TeamId, task.TaskId), out var result)
                    ? new StandingCell("Published", ApplyEvaluation.Exact(result.Score)) : new StandingCell("Pending")).ToArray(),
                ApplyEvaluation.Exact(ranks[t.TeamId].Total), checked((int)ranks[t.TeamId].Rank))).OrderBy(x => x.Rank).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray());
        await tx.CommitAsync(ct); return view;
    }
    public async Task<MyTournamentResults> TeamAsync(Guid tournament, Guid team, CancellationToken ct)
    {
        if (!await db.Teams.AnyAsync(x => x.TournamentId == tournament && x.TeamId == team, ct)) throw new ResultReadException(404, "TeamNotFound");
        var rows = await db.Results.AsNoTracking().Where(x => x.TournamentId == tournament && x.TeamId == team).OrderBy(x => x.TaskId).ToArrayAsync(ct);
        return new(tournament.ToString("D"), rows.Select(x => {
            var p = JsonSerializer.Deserialize<ResultPublication>(x.Publication, ApplyEvaluation.Json)!;
            return new MyTaskResult(x.TaskId.ToString("D"), x.ResultId.ToString("D"), ApplyEvaluation.Version(x.ResultVersion),
                x.PublicationId.ToString("D"), x.Outcome, ApplyEvaluation.Exact(x.Score),
                p.Source.Origin == DecisionOrigin.SeniorEscalation ? "SeniorFinal" : "CheckWithAppeals"); }).ToArray());
    }
    public async Task<PublishedAppealBasis> BasisAsync(Guid result, long expectedVersion, CancellationToken ct)
    {
        var row = await db.Results.AsNoTracking().SingleOrDefaultAsync(x => x.ResultId == result, ct)
            ?? throw new ResultReadException(404, "PublishedResultNotFound");
        if (row.ResultVersion != expectedVersion) throw new ResultReadException(409, "ResultVersionConflict");
        var p = JsonSerializer.Deserialize<ResultPublication>(row.Publication, ApplyEvaluation.Json)!;
        return new(row.ResultId.ToString("D"), ApplyEvaluation.Version(row.ResultVersion), row.TournamentId.ToString("D"),
            row.TeamId.ToString("D"), row.TaskId.ToString("D"), row.SolutionId.ToString("D"), row.MaterialVersionId.ToString("D"),
            p.Source.CheckId.ToString("D"), p.Source.Origin.ToString(), p.Source.Outcome.ToString(), ApplyEvaluation.Version(p.Rule.Version),
            p.PublicationId.ToString("D"), p.PublishedAt.UtcDateTime.ToString("O"));
    }
}
