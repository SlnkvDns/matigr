using System.Data.Common;
using Matigr.Results.Domain;
using Matigr.Results.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Results.Persistence.Tests;

public partial class StorageTests
{
    private async Task<Evaluation> SecondTask(Evaluation first)
    {
        var task = Guid.NewGuid(); await using var db = fixture.Open();
        db.Tasks.Add(new() { TournamentId = first.Key.TournamentId, TaskId = task, Name = "Second", Order = 2 });
        db.Rules.Add(new() { TournamentId = first.Key.TournamentId, TaskId = task, RuleVersion = 1, Points = 20, ManifestHash = "test" });
        await db.SaveChangesAsync();
        return first with { CheckId = Guid.NewGuid(), SolutionId = Guid.NewGuid(), MaterialVersionId = Guid.NewGuid(), Key = first.Key with { TaskId = task } };
    }
    [Fact]
    public async Task Two_tasks_of_same_team_apply_concurrently_without_lost_sum_or_snapshot_increment()
    {
        var first = await NewEvaluation(); var second = await SecondTask(first);
        async Task Apply(Evaluation e) { await using var db = fixture.Open(); Assert.Equal("Applied", (await new ApplyEvaluation(db).InitialAsync(e, Guid.NewGuid(), "concurrent")).Status); }
        await Task.WhenAll(Apply(first), Apply(second));
        await using var verify = fixture.Open(); var table = await new ResultReads(verify).StandingsAsync(first.Key.TournamentId, default);
        Assert.Equal("2", table.SnapshotVersion); Assert.Equal("30", Assert.Single(table.Teams).Total);
        Assert.Equal(new[] { "10", "20" }, table.Teams[0].Cells.Select(x => x.Score).ToArray());
        var ids = await verify.Results.Where(x => x.TournamentId == first.Key.TournamentId).Select(x => x.ResultId).ToArrayAsync();
        Assert.Equal(2, await verify.Publications.CountAsync(x => ids.Contains(x.ResultId)));
        Assert.Equal(2, await verify.Receipts.CountAsync(x => x.EvaluationId == first.CheckId || x.EvaluationId == second.CheckId));
    }
}
