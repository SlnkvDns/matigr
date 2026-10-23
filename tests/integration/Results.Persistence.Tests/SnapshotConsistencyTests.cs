using System.Data.Common;
using Matigr.Results.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace Results.Persistence.Tests;
public partial class StorageTests
{
    private sealed class ReadBarrier : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("FROM tournament_tasks", StringComparison.Ordinal)) {
                Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            }
            return result;
        }
    }
    [Fact]
    public async Task Snapshot_read_cannot_mix_version_before_commit_with_scores_after_concurrent_commit()
    {
        var first = await NewEvaluation(); var second = await SecondTask(first);
        await using (var db = fixture.Open()) await new ApplyEvaluation(db).InitialAsync(first, Guid.NewGuid(), "first");
        var barrier = new ReadBarrier();
        var options = new DbContextOptionsBuilder<ResultsDbContext>().UseNpgsql(fixture.Connection).AddInterceptors(barrier).Options;
        await using var reader = new ResultsDbContext(options);
        var pending = new ResultReads(reader).StandingsAsync(first.Key.TournamentId, default);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try { await using var writer = fixture.Open(); await new ApplyEvaluation(writer).InitialAsync(second, Guid.NewGuid(), "second"); }
        finally { barrier.Release.TrySetResult(); }
        var old = await pending;
        Assert.Equal("1", old.SnapshotVersion); Assert.Equal("10", Assert.Single(old.Teams).Total);
        Assert.Equal("Pending", old.Teams[0].Cells[1].Status);
        await using var verify = fixture.Open(); var latest = await new ResultReads(verify).StandingsAsync(first.Key.TournamentId, default);
        Assert.Equal("2", latest.SnapshotVersion); Assert.Equal("30", Assert.Single(latest.Teams).Total);
        var mine = await new ResultReads(verify).TeamAsync(first.Key.TournamentId, first.Key.TeamId, default);
        Assert.Equal(latest.Teams[0].Cells.Select(x => x.Score).Order(), mine.Results.Select(x => x.Score).Order());
    }
}
