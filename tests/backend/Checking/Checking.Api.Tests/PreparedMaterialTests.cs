using Matigr.Checking.Application;
using Matigr.Checking.Domain;
using Matigr.Checking.Infrastructure.Fixtures;
using Matigr.Checking.TestSupport;

namespace Matigr.Checking.Api.Tests;

public class PreparedMaterialTests
{
    [Fact]
    public async Task Reads_only_verified_bytes_and_returns_a_read_only_snapshot()
    {
        using var files = new PreparedFixtureFiles();
        var catalog = new PreparedFixtureCatalog(files.DirectoryPath);
        var entry = catalog.Solutions[0];
        var store = new PreparedMaterialStore(catalog);
        await using var stream = await store.OpenAsync(entry.Snapshot, CancellationToken.None);
        Assert.False(stream.CanWrite);
        var original = await File.ReadAllBytesAsync(Path.Combine(files.DirectoryPath, entry.RelativeFile));
        await File.WriteAllTextAsync(Path.Combine(files.DirectoryPath, entry.RelativeFile), "changed after open");
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        Assert.Equal(original, output.ToArray());
        await Assert.ThrowsAsync<MaterialUnavailableException>(() => store.OpenAsync(entry.Snapshot, CancellationToken.None));
    }

    [Theory]
    [InlineData("../../secret.txt")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://example.test/material")]
    [InlineData("C:\\secret.txt")]
    public async Task Supplied_path_or_URL_cannot_be_used_as_a_material_reference(string reference)
    {
        using var files = new PreparedFixtureFiles();
        var catalog = new PreparedFixtureCatalog(files.DirectoryPath);
        var source = catalog.Solutions[0].Snapshot;
        var forged = new SolutionReference(source.SolutionId, source.TournamentId, source.TeamId, source.TaskId,
            source.MaterialVersionId, reference, source.AcceptedAt, source.SourceVersion);
        await Assert.ThrowsAsync<MaterialNotAllowedException>(() => new PreparedMaterialStore(catalog).OpenAsync(forged, CancellationToken.None));
        files.Rewrite(root => root["solutions"]![0]!["materialFile"] = reference);
        Assert.Throws<InvalidOperationException>(() => new PreparedFixtureCatalog(files.DirectoryPath));
    }

    [Fact]
    public async Task Every_snapshot_field_is_bound_to_the_allowlist_and_missing_file_is_observable()
    {
        using var files = new PreparedFixtureFiles();
        var catalog = new PreparedFixtureCatalog(files.DirectoryPath);
        var entry = catalog.Solutions[0];
        var source = entry.Snapshot;
        var store = new PreparedMaterialStore(catalog);
        SolutionReference Copy(Guid? solution = null, Guid? tournament = null, Guid? team = null, Guid? task = null,
            Guid? version = null, DateTimeOffset? accepted = null, long? sourceVersion = null) =>
            new(solution ?? source.SolutionId, tournament ?? source.TournamentId, team ?? source.TeamId, task ?? source.TaskId,
                version ?? source.MaterialVersionId, source.MaterialRef, accepted ?? source.AcceptedAt, sourceVersion ?? source.SourceVersion);
        foreach (var forged in new[] { Copy(solution: Guid.NewGuid()), Copy(tournament: Guid.NewGuid()), Copy(team: Guid.NewGuid()),
            Copy(task: Guid.NewGuid()), Copy(version: Guid.NewGuid()), Copy(accepted: source.AcceptedAt.AddSeconds(1)), Copy(sourceVersion: 2) })
            await Assert.ThrowsAsync<MaterialNotAllowedException>(() => store.OpenAsync(forged, CancellationToken.None));
        File.Delete(Path.Combine(files.DirectoryPath, entry.RelativeFile));
        await Assert.ThrowsAsync<MaterialUnavailableException>(() => store.OpenAsync(source, CancellationToken.None));
    }

    [Fact]
    public void Manifest_checksum_duplicates_and_unknown_keys_are_rejected_before_import()
    {
        using var files = new PreparedFixtureFiles();
        File.AppendAllText(Path.Combine(files.DirectoryPath, "manifest.json"), " ");
        Assert.Throws<InvalidOperationException>(() => new PreparedFixtureCatalog(files.DirectoryPath));
        files.Rewrite(root => root["solutions"]![0]!["teamId"] = Guid.NewGuid().ToString());
        Assert.Throws<InvalidOperationException>(() => new PreparedFixtureCatalog(files.DirectoryPath));
        using var duplicates = new PreparedFixtureFiles();
        duplicates.Rewrite(root => root["solutions"]![1] = root["solutions"]![0]!.DeepClone());
        Assert.Throws<InvalidOperationException>(() => new PreparedFixtureCatalog(duplicates.DirectoryPath));
    }

    [Fact]
    public async Task Cancellation_propagates_without_turning_into_an_incorrect_verdict()
    {
        using var files = new PreparedFixtureFiles();
        var catalog = new PreparedFixtureCatalog(files.DirectoryPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PreparedMaterialStore(catalog)
            .OpenAsync(catalog.Solutions[0].Snapshot, cancellation.Token));
    }
}
