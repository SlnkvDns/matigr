using Matigr.Checking.Application;
using Matigr.Checking.Domain;
using Matigr.Checking.Infrastructure.Fixtures;
using Matigr.Checking.Infrastructure.Storage;
using Matigr.Checking.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Checking.Persistence.Tests;

public partial class StorageTests
{
    private static SolutionReference NewSnapshot() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), "prepared:test", new DateTimeOffset(2026, 10, 23, 3, 0, 0, TimeSpan.Zero), 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bootstrap_serializes_identical_input_and_recovers_after_competing_commit_or_rollback(bool commit)
    {
        var source = NewSnapshot();
        await using var one = fixture.Open();
        await using var two = fixture.Open();
        await using var transaction = await one.Database.BeginTransactionAsync();
        var first = await new AcceptedReferenceBootstrap(one, TimeProvider.System).EnsureAsync(source, CancellationToken.None);
        await two.Database.OpenConnectionAsync();
        var second = new AcceptedReferenceBootstrap(two, TimeProvider.System).EnsureAsync(source, CancellationToken.None);
        await WaitForLock(((NpgsqlConnection)two.Database.GetDbConnection()).ProcessID);
        Assert.False(second.IsCompleted);
        if (commit) await transaction.CommitAsync(); else await transaction.RollbackAsync();
        var result = await second;
        Assert.Equal(!commit, result.Created);
        if (commit) Assert.Equal(first.CheckId, result.CheckId);
        await using var verify = fixture.Open();
        Assert.Single(await verify.SolutionReferences.Where(x => x.SolutionId == source.SolutionId).ToListAsync());
        var check = Assert.Single(await verify.Checks.Where(x => x.SolutionId == source.SolutionId).ToListAsync());
        Assert.Equal(CheckKind.Initial, check.Kind);
        Assert.Equal(CheckState.Queued, check.State);
        Assert.Equal(1, check.Version);
        Assert.Null(check.Outcome);
        Assert.Single(await verify.CheckHistory.Where(x => x.CheckId == check.CheckId).ToListAsync());
    }

    [Fact]
    public async Task Conflicting_source_identity_tuple_material_or_version_cannot_replace_snapshot()
    {
        var source = NewSnapshot();
        await using (var db = fixture.Open())
            await new AcceptedReferenceBootstrap(db, TimeProvider.System).EnsureAsync(source, CancellationToken.None);
        SolutionReference Copy(Guid? solution = null, Guid? tournament = null, Guid? team = null, Guid? task = null,
            Guid? material = null, string? reference = null, DateTimeOffset? accepted = null, long? version = null) =>
            new(solution ?? source.SolutionId, tournament ?? source.TournamentId, team ?? source.TeamId, task ?? source.TaskId,
                material ?? source.MaterialVersionId, reference ?? source.MaterialRef, accepted ?? source.AcceptedAt, version ?? source.SourceVersion);
        foreach (var conflict in new[] { Copy(solution: Guid.NewGuid()), Copy(tournament: Guid.NewGuid()), Copy(team: Guid.NewGuid()),
            Copy(task: Guid.NewGuid()), Copy(material: Guid.NewGuid()), Copy(reference: "other"),
            Copy(accepted: source.AcceptedAt.AddSeconds(1)), Copy(version: 2) })
        {
            await using var db = fixture.Open();
            await Assert.ThrowsAsync<AcceptedReferenceConflictException>(() => new AcceptedReferenceBootstrap(db, TimeProvider.System)
                .EnsureAsync(conflict, CancellationToken.None));
        }
        await using var verify = fixture.Open();
        var saved = await verify.SolutionReferences.SingleAsync(x => x.SolutionId == source.SolutionId);
        Assert.Equal(source.MaterialRef, saved.MaterialRef);
        Assert.Equal(source.MaterialVersionId, saved.MaterialVersionId);
        Assert.Equal(1, await verify.Checks.CountAsync(x => x.SolutionId == source.SolutionId));
    }

    [Fact]
    public async Task Bootstrap_keeps_completed_check_and_does_not_publish_intake_or_verdict_events()
    {
        var source = NewSnapshot();
        Guid checkId;
        await using (var db = fixture.Open())
        {
            var result = await new AcceptedReferenceBootstrap(db, TimeProvider.System).EnsureAsync(source, CancellationToken.None);
            checkId = result.CheckId;
            var check = await db.Checks.SingleAsync(x => x.CheckId == checkId);
            var reviewer = Guid.NewGuid();
            await SeedScope(reviewer, source.TournamentId);
            var assignment = NewAssignment(check, reviewer);
            // Its fixture AcceptedAt is historical; assignment must not precede Check creation here.
            assignment.StartedAt = check.CreatedAt;
            await Assign(db, check, assignment);
            check.State = CheckState.Completed;
            check.Outcome = CheckOutcome.Correct;
            check.Origin = DecisionOrigin.OrdinaryReview;
            check.CompletedAt = check.CreatedAt.AddSeconds(1);
            assignment.State = AssignmentState.Completed;
            assignment.EndedAt = check.CompletedAt;
            await db.SaveChangesAsync();
        }
        await using var repeat = fixture.Open();
        var beforeOutbox = await repeat.Outbox.CountAsync();
        var replay = await new AcceptedReferenceBootstrap(repeat, TimeProvider.System).EnsureAsync(source, CancellationToken.None);
        Assert.Equal(new BootstrappedCheck(checkId, false), replay);
        var saved = await repeat.Checks.SingleAsync(x => x.CheckId == checkId);
        Assert.Equal(CheckState.Completed, saved.State);
        Assert.Equal(CheckOutcome.Correct, saved.Outcome);
        Assert.Equal(3, saved.Version);
        Assert.Equal(beforeOutbox, await repeat.Outbox.CountAsync());
        var history = Assert.Single(await repeat.CheckHistory.Where(x => x.CheckId == checkId).ToListAsync());
        Assert.Equal("InitialCheckCreated", history.TransitionType);
    }

    [Fact]
    public async Task Outer_transaction_rolls_back_earlier_bootstrap_when_later_snapshot_conflicts()
    {
        var existing = NewSnapshot();
        var fresh = NewSnapshot();
        await using (var db = fixture.Open())
            await new AcceptedReferenceBootstrap(db, TimeProvider.System).EnsureAsync(existing, CancellationToken.None);
        await using (var db = fixture.Open())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var service = new AcceptedReferenceBootstrap(db, TimeProvider.System);
            await service.EnsureAsync(fresh, CancellationToken.None);
            var conflict = new SolutionReference(Guid.NewGuid(), existing.TournamentId, existing.TeamId, existing.TaskId,
                existing.MaterialVersionId, existing.MaterialRef, existing.AcceptedAt, existing.SourceVersion);
            await Assert.ThrowsAsync<AcceptedReferenceConflictException>(() => service.EnsureAsync(conflict, CancellationToken.None));
            await transaction.RollbackAsync();
        }
        await using var verify = fixture.Open();
        Assert.False(await verify.SolutionReferences.AnyAsync(x => x.SolutionId == fresh.SolutionId));
        Assert.False(await verify.Checks.AnyAsync(x => x.SolutionId == fresh.SolutionId));
    }

    [Fact]
    public async Task Fixture_import_is_repeatable_and_changed_manifest_is_detected_even_with_updated_checksum()
    {
        using var files = new PreparedFixtureFiles();
        var catalog = new PreparedFixtureCatalog(files.DirectoryPath);
        async Task Import(PreparedFixtureCatalog input)
        {
            await using var db = fixture.Open();
            await new PreparedFixtureBootstrapper(db, input, new AcceptedReferenceBootstrap(db, TimeProvider.System), TimeProvider.System)
                .InitializeAsync(CancellationToken.None);
        }
        await Import(catalog);
        await using var verify = fixture.Open();
        var ids = catalog.Solutions.Select(x => x.Snapshot.SolutionId).ToArray();
        var before = await verify.Checks.AsNoTracking().Where(x => ids.Contains(x.SolutionId)).OrderBy(x => x.CheckId).Select(x => x.CheckId).ToListAsync();
        await Import(new PreparedFixtureCatalog(files.DirectoryPath));
        Assert.Equal(before, await verify.Checks.AsNoTracking().Where(x => ids.Contains(x.SolutionId)).OrderBy(x => x.CheckId).Select(x => x.CheckId).ToListAsync());
        Assert.Equal(6, before.Count);
        foreach (var scope in catalog.ReviewerScopes)
            Assert.True(await verify.ReviewerScopes.AnyAsync(x => x.ReviewerId == scope.ReviewerId && x.TournamentId == scope.TournamentId && x.Role == scope.Role));
        files.Rewrite(root => root["solutions"]![0]!["materialSha256"] = new string('a', 64));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Import(new PreparedFixtureCatalog(files.DirectoryPath)));
        Assert.Equal(catalog.ManifestHash, (await verify.FixtureSets.AsNoTracking().SingleAsync()).ManifestHash);
    }

    [Fact]
    public async Task Missing_tampered_or_symlinked_material_cannot_change_check_or_escape_allowlist()
    {
        using var files = new PreparedFixtureFiles();
        var catalog = new PreparedFixtureCatalog(files.DirectoryPath);
        var entry = catalog.Solutions[0];
        await using var db = fixture.Open();
        var check = await new AcceptedReferenceBootstrap(db, TimeProvider.System).EnsureAsync(entry.Snapshot, CancellationToken.None);
        var original = await db.Checks.AsNoTracking().SingleAsync(x => x.CheckId == check.CheckId);
        var store = new PreparedMaterialStore(catalog);
        var path = Path.Combine(files.DirectoryPath, entry.RelativeFile);
        File.Delete(path);
        await Assert.ThrowsAsync<MaterialUnavailableException>(() => store.OpenAsync(entry.Snapshot, CancellationToken.None));
        File.WriteAllText(path, "tampered");
        await Assert.ThrowsAsync<MaterialUnavailableException>(() => store.OpenAsync(entry.Snapshot, CancellationToken.None));
        File.Delete(path);
        var outside = Path.Combine(files.DirectoryPath, "private.txt");
        File.WriteAllText(outside, "private bytes that must never be returned");
        File.CreateSymbolicLink(path, outside); // These integration tests run on Linux PostgreSQL/Docker.
        await Assert.ThrowsAsync<MaterialUnavailableException>(() => store.OpenAsync(entry.Snapshot, CancellationToken.None));
        var saved = await db.Checks.AsNoTracking().SingleAsync(x => x.CheckId == check.CheckId);
        Assert.Equal(original.State, saved.State);
        Assert.Equal(original.Outcome, saved.Outcome);
        Assert.Equal(original.Version, saved.Version);
    }
}
