using Matigr.Appeals.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Appeals.Persistence.Tests;

public sealed class DatabaseFixture : IAsyncLifetime
{
    public string Connection => Environment.GetEnvironmentVariable("ConnectionStrings__AppealsStorageTest")
        ?? throw new InvalidOperationException("Use isolated appeals-storage-tests Compose.");
    public AppealsDbContext Open() => new(new DbContextOptionsBuilder<AppealsDbContext>().UseNpgsql(Connection).Options);
    public async Task InitializeAsync()
    {
        if (new NpgsqlConnectionStringBuilder(Connection).Database != "appeals_storage_test") throw new InvalidOperationException("Unsafe test database.");
        await using var db = Open(); await db.Database.EnsureDeletedAsync(); await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
        await db.GetService<IMigrator>().MigrateAsync("0"); Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask;
}
public partial class StorageTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private async Task<AppealRow> NewAppeal()
    {
        await using var db = fixture.Open(); var tournament = Guid.NewGuid();
        db.SubmissionWindows.Add(new() { TournamentId = tournament }); await db.SaveChangesAsync();
        return new() { AppealId = Guid.NewGuid(), TournamentId = tournament, TeamId = Guid.NewGuid(), TaskId = Guid.NewGuid(), ResultId = Guid.NewGuid(),
            ExpectedResultVersion = 1, SolutionId = Guid.NewGuid(), MaterialVersionId = Guid.NewGuid(), BaseCheckId = Guid.NewGuid(),
            BasePublicationId = Guid.NewGuid(), WindowVersion = 1, SubmittedAt = DateTimeOffset.UtcNow };
    }
    [Fact]
    public async Task Permanent_tuple_UNIQUE_survives_Closed_and_new_result_version_and_preserves_original_publication()
    {
        var a = await NewAppeal(); await using var db = fixture.Open(); db.Appeals.Add(a); await db.SaveChangesAsync();
        a.State = "Closed"; a.FinalPublicationId = Guid.NewGuid(); a.Version = 2; await db.SaveChangesAsync();
        Assert.NotEqual(a.BasePublicationId, a.FinalPublicationId);
        await using var other = fixture.Open();
        var copy = await NewAppeal(); copy.TournamentId = a.TournamentId; copy.TeamId = a.TeamId; copy.TaskId = a.TaskId; copy.ExpectedResultVersion = 2;
        other.Appeals.Add(copy);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        Assert.Single(await db.Appeals.Where(x => x.TournamentId == a.TournamentId).ToListAsync());
        db.Appeals.Remove(a); await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
    [Fact]
    public async Task Pending_attempt_UNIQUE_and_receipt_history_and_basis_guards_are_local_and_transactional()
    {
        var a = await NewAppeal(); await using var db = fixture.Open(); db.Appeals.Add(a); await db.SaveChangesAsync();
        await using (var tx = await db.Database.BeginTransactionAsync()) {
            db.Attempts.Add(new() { RequestId = Guid.NewGuid(), AppealId = a.AppealId, ReviewerId = Guid.NewGuid(), AcceptedAt = DateTimeOffset.UtcNow, PayloadHash = "a" });
            await db.SaveChangesAsync(); await tx.RollbackAsync();
        }
        db.ChangeTracker.Clear(); Assert.False(await db.Attempts.AnyAsync(x => x.AppealId == a.AppealId));
        db.Attempts.Add(new() { RequestId = Guid.NewGuid(), AppealId = a.AppealId, ReviewerId = Guid.NewGuid(), AcceptedAt = DateTimeOffset.UtcNow, PayloadHash = "a" });
        await db.SaveChangesAsync();
        await using (var other = fixture.Open()) {
            other.Attempts.Add(new() { RequestId = Guid.NewGuid(), AppealId = a.AppealId, ReviewerId = Guid.NewGuid(), AcceptedAt = DateTimeOffset.UtcNow, PayloadHash = "b" });
            await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
        }
        var existing = await db.Appeals.SingleAsync(x => x.AppealId == a.AppealId); existing.BasePublicationId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
    [Fact]
    public async Task Closed_window_cannot_reopen_and_audit_and_HTTP_receipts_cannot_be_rewritten()
    {
        var a = await NewAppeal(); await using var db = fixture.Open();
        var window = await db.SubmissionWindows.SingleAsync(x => x.TournamentId == a.TournamentId);
        window.State = "Closed"; window.Version = 2; window.EndFactId = Guid.NewGuid(); window.EndedBy = Guid.NewGuid(); window.EndedAt = DateTimeOffset.UtcNow;
        db.EndFacts.Add(new() { FactId = window.EndFactId.Value, TournamentId = window.TournamentId, ActorId = window.EndedBy.Value,
            RecordedAt = DateTimeOffset.UtcNow, PayloadHash = "fact", Response = "{}" });
        var http = new HttpReceiptRow { SubjectId = Guid.NewGuid(), Operation = "Submit", Key = "hash", RequestHash = "request", StatusCode = 201, RecordedAt = DateTimeOffset.UtcNow };
        db.HttpReceipts.Add(http); await db.SaveChangesAsync();
        http.Response = "{\"changed\":true}"; await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear(); window = await db.SubmissionWindows.SingleAsync(x => x.TournamentId == a.TournamentId);
        window.State = "Open"; await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
