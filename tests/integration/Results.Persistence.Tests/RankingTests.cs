using Matigr.Results.Domain;
using Matigr.Results.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Results.Persistence.Tests;

public partial class StorageTests
{
    public static IEnumerable<object[]> RankCases()
    {
        yield return [new[] { 20, 20, 10 }, new[] { 1, 1, 2 }, new[] { 20, 10, 10 }, new[] { 1, 2, 2 }];
        yield return [new[] { 30, 20, 10 }, new[] { 1, 2, 3 }, new[] { 10, 20, 30 }, new[] { 3, 2, 1 }];
        yield return [new[] { 0, 0, 0 }, new[] { 1, 1, 1 }, new[] { 10, 0, 0 }, new[] { 1, 2, 2 }];
        yield return [new[] { 10, 20, 20 }, new[] { 2, 1, 1 }, new[] { 20, 20, 20 }, new[] { 1, 1, 1 }];
    }
    [Theory]
    [MemberData(nameof(RankCases))]
    public async Task Authoritative_SQL_dense_ranks_merge_split_and_move_all_affected_teams(int[] scores, int[] expected, int[] changed, int[] expectedChanged)
    {
        var e = await NewEvaluation(); var teams = new[] { e.Key.TeamId, Guid.NewGuid(), Guid.NewGuid() };
        await using (var db = fixture.Open())
        {
            foreach (var team in teams.Skip(1)) db.Teams.Add(new() { TournamentId = e.Key.TournamentId, TeamId = team, Name = team.ToString() });
            await db.SaveChangesAsync();
        }
        foreach (var team in teams)
        {
            await using var db = fixture.Open();
            await new ApplyEvaluation(db).InitialAsync(e with { CheckId = Guid.NewGuid(), SolutionId = Guid.NewGuid(), Key = e.Key with { TeamId = team } }, Guid.NewGuid(), "rank");
        }
        async Task Check(int[] values, int[] places)
        {
            // Prepared read-side test data, not a second production scoring/ranking algorithm or runtime correction API.
            await using var db = fixture.Open();
            for (var i = 0; i < teams.Length; i++)
            {
                var score = (decimal)values[i]; var team = teams[i];
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE team_task_results SET score = {score} WHERE tournament_id = {e.Key.TournamentId} AND team_id = {team}");
            }
            var snapshot = await new ResultReads(db).StandingsAsync(e.Key.TournamentId, default);
            for (var i = 0; i < teams.Length; i++)
            {
                var team = snapshot.Teams.Single(x => x.TeamId == teams[i].ToString("D"));
                Assert.Equal(values[i].ToString(), team.Total); Assert.Equal(places[i], team.Rank);
                Assert.Equal(team.Total, Assert.Single(team.Cells).Score);
            }
        }
        await Check(scores, expected); await Check(changed, expectedChanged);
    }
    [Fact]
    public async Task Pending_has_no_score_but_empty_sum_is_zero_and_not_Incorrect()
    {
        var e = await NewEvaluation(); await using var db = fixture.Open();
        var table = await new ResultReads(db).StandingsAsync(e.Key.TournamentId, default);
        var team = Assert.Single(table.Teams); Assert.Equal("0", team.Total); Assert.Equal(1, team.Rank);
        var cell = Assert.Single(team.Cells); Assert.Equal("Pending", cell.Status); Assert.Null(cell.Score);
    }
}
