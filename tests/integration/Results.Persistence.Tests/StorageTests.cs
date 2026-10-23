using Matigr.Results.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Results.Persistence.Tests;

public sealed class DatabaseFixture : IAsyncLifetime
{
    public string Connection => Environment.GetEnvironmentVariable("ConnectionStrings__ResultsStorageTest")
        ?? throw new InvalidOperationException("Use isolated results-storage-tests Compose.");
    public ResultsDbContext Open() => new(new DbContextOptionsBuilder<ResultsDbContext>().UseNpgsql(Connection).Options);
    public string Fixtures => "/repo/infra/fixtures";
    public async Task InitializeAsync()
    {
        if (new NpgsqlConnectionStringBuilder(Connection).Database != "results_storage_test") throw new InvalidOperationException("Unsafe test database.");
        await using var db = Open(); await db.Database.EnsureDeletedAsync(); await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        await db.GetService<IMigrator>().MigrateAsync("0"); Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
        await ResultFixtures.SeedAsync(db, Fixtures);
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

public partial class StorageTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task Seed_repeat_is_safe_rules_are_exact_and_runtime_mutation_is_rejected()
    {
        await using var db = fixture.Open(); await ResultFixtures.SeedAsync(db, fixture.Fixtures);
        Assert.Equal(3, await db.Teams.CountAsync()); Assert.Equal(2, await db.Tasks.CountAsync());
        Assert.Equal(new decimal[] { 10, 20 }, await db.Rules.OrderBy(x => x.Points).Select(x => x.Points).ToArrayAsync());
        var rule = await db.Rules.FirstAsync(); rule.Points = 55;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
    [Fact]
    public async Task Changed_manifest_even_with_updated_checksum_cannot_overwrite_cost()
    {
        var directory = Path.Combine(Path.GetTempPath(), "results-fixture-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes((await File.ReadAllTextAsync(Path.Combine(fixture.Fixtures, "manifest.json"))).Replace("\"10\"", "\"11\""));
            await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), bytes);
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.sha256"), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
            await using var db = fixture.Open();
            await Assert.ThrowsAsync<InvalidOperationException>(() => ResultFixtures.SeedAsync(db, directory));
            Assert.Contains(10m, await db.Rules.Select(x => x.Points).ToListAsync());
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task Local_tuple_publication_evaluation_and_database_constraints_are_real()
    {
        await using var db = fixture.Open();
        var team = await db.Teams.FirstAsync(); var task = await db.Tasks.FirstAsync();
        var id = Guid.NewGuid();
        await using var tx = await db.Database.BeginTransactionAsync();
        db.Results.Add(new() { ResultId = id, TournamentId = team.TournamentId, TeamId = team.TeamId, TaskId = task.TaskId,
            SolutionId = Guid.NewGuid(), MaterialVersionId = Guid.NewGuid(), ResultVersion = 1, Outcome = "Incorrect", Score = 0, PublicationId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        db.Results.Add(new() { ResultId = Guid.NewGuid(), TournamentId = team.TournamentId, TeamId = team.TeamId, TaskId = task.TaskId,
            SolutionId = Guid.NewGuid(), MaterialVersionId = Guid.NewGuid(), ResultVersion = 1, Outcome = "Correct", Score = 10, PublicationId = Guid.NewGuid() });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        await tx.RollbackAsync();
        await using var verify = fixture.Open(); Assert.False(await verify.Results.AnyAsync(x => x.ResultId == id));
    }
}
