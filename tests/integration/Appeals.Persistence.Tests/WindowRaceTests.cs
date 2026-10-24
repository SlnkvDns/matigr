using System.Data.Common;
using Matigr.Appeals.Application;
using Matigr.Appeals.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Appeals.Persistence.Tests;
public partial class StorageTests
{
    private sealed class WindowGate(bool hold) : DbCommandInterceptor
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static bool Window(DbCommand c) => c.CommandText.Contains("submission_windows", StringComparison.Ordinal) && c.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { if (!hold && Window(command)) Entered.TrySetResult(); return ValueTask.FromResult(result); }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken ct = default)
        { if (hold && Window(command)) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); } return result; }
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Submit_and_end_linearize_on_the_same_window_row_with_explicit_lock_barriers(bool submitWins)
    {
        var seed = await NewAppeal(); var firstGate = new WindowGate(true); var secondGate = new WindowGate(false);
        AppealsDbContext Open(WindowGate gate) => new(new DbContextOptionsBuilder<AppealsDbContext>().UseNpgsql(fixture.Connection).AddInterceptors(gate).Options);
        await using var first = Open(firstGate); await using var second = Open(secondGate);
        var team = new AppealActor(Guid.NewGuid(), ["Team"], [seed.TournamentId], seed.TeamId);
        var organizer = new AppealActor(Guid.NewGuid(), ["Organizer"], [seed.TournamentId], null); var fact = Guid.NewGuid();
        Task<AppealReply> Submit(AppealsDbContext db) => new AppealSubmission(db, new BasisReader(db, Published(seed))).SubmitAsync(team, seed.ResultId, 1, "submit", default);
        Task<AppealReply> End(AppealsDbContext db) => new ManualEnd(db).RecordAsync(organizer, seed.TournamentId, fact, "end", default);
        var winner = submitWins ? Submit(first) : End(first);
        await firstGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var loser = submitWins ? End(second) : Submit(second);
        await secondGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(loser.IsCompleted); firstGate.Release.TrySetResult();
        Assert.Equal(submitWins ? 201 : 200, (await winner).Status);
        if (submitWins) Assert.Equal(200, (await loser).Status);
        else Assert.Equal("WindowClosed", (await Assert.ThrowsAsync<AppealOperationException>(() => loser)).Code);
        await using var verify = fixture.Open();
        Assert.Equal("Closed", (await verify.SubmissionWindows.SingleAsync(x => x.TournamentId == seed.TournamentId)).State);
        Assert.Equal(submitWins ? 1 : 0, await verify.Appeals.CountAsync(x => x.TournamentId == seed.TournamentId));
        if (submitWins) Assert.Equal(201, (await Submit(verify)).Status);
        Assert.Equal(200, (await End(verify)).Status);
    }
}
