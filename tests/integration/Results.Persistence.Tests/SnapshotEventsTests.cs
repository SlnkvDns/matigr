using System.Net.Http.Json;
using System.Text.Json;
using Matigr.Results.Application;
using Matigr.Results.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Results.Persistence.Tests;

public partial class StorageTests
{
    private sealed class HandshakeBarrier(DatabaseFixture database) : ISnapshotVersions
    {
        private int first;
        public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> CurrentAsync(Guid tournament, CancellationToken ct)
        {
            await using var db = database.Open();
            var version = await new SnapshotVersions(db).CurrentAsync(tournament, ct);
            if (Interlocked.Exchange(ref first, 1) == 0) {
                Captured.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            }
            return version;
        }
    }

    [Fact]
    public async Task Real_database_commit_between_subscription_handshake_and_GET_converges_on_two_API_replicas_and_after_missed_signal()
    {
        var first = await NewEvaluation(); var second = await SecondTask(first); var gate = new HandshakeBarrier(fixture);
        WebApplicationFactory<Program> Host(bool barrier) => new WebApplicationFactory<Program>().WithWebHostBuilder(b => {
            b.UseEnvironment("Test");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> {
                ["ConnectionStrings:Results"] = fixture.Connection, ["Storage:ApplyMigrations"] = "false", ["Snapshots:PollMilliseconds"] = "10" }));
            if (barrier) b.ConfigureServices(s => s.AddSingleton<ISnapshotVersions>(gate));
        });
        await using var one = Host(true); await using var two = Host(false);
        using var clientOne = one.CreateClient(); using var clientTwo = two.CreateClient();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var path = $"/api/v1/results/tournaments/{first.Key.TournamentId}/standings";
        async Task<string> Frame(StreamReader reader) {
            while (await reader.ReadLineAsync(stop.Token) is { } line) if (line.StartsWith("data: ", StringComparison.Ordinal)) {
                using var frame = JsonDocument.Parse(line[6..]);
                Assert.Equal(new[] { "snapshotVersion", "tournamentId" }, frame.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
                return frame.RootElement.GetProperty("snapshotVersion").GetString()!;
            }
            throw new InvalidOperationException("Unexpected SSE end.");
        }
        var pending = clientOne.GetAsync(path + "/events", HttpCompletionOption.ResponseHeadersRead, stop.Token);
        await gate.Captured.Task.WaitAsync(stop.Token);
        try { await using var db = fixture.Open(); await new ApplyEvaluation(db).InitialAsync(first, Guid.NewGuid(), "between-subscribe-handshake"); }
        finally { gate.Release.TrySetResult(); }
        using (var response = await pending)
        using (var reader = new StreamReader(await response.Content.ReadAsStreamAsync(stop.Token))) {
            Assert.Equal("0", await Frame(reader));
            var snapshot = await clientOne.GetFromJsonAsync<JsonElement>(path, stop.Token);
            Assert.Equal("1", snapshot.GetProperty("snapshotVersion").GetString());
            Assert.Equal("1", await Frame(reader));
            using var replica = await clientTwo.GetAsync(path + "/events", HttpCompletionOption.ResponseHeadersRead, stop.Token);
            using var replicaReader = new StreamReader(await replica.Content.ReadAsStreamAsync(stop.Token));
            Assert.Equal("1", await Frame(replicaReader));
            await using var db = fixture.Open(); await new ApplyEvaluation(db).InitialAsync(second, Guid.NewGuid(), "two-live-replicas");
            Assert.Equal("2", await Frame(reader)); Assert.Equal("2", await Frame(replicaReader));
        }
        // Both subscriptions are gone; commit with no listener, then reconnect with an old cursor.
        var offline = first with { CheckId = Guid.NewGuid(), SolutionId = Guid.NewGuid(), MaterialVersionId = Guid.NewGuid(), Key = first.Key with { TeamId = Guid.NewGuid() } };
        await using (var db = fixture.Open()) {
            db.Teams.Add(new() { TournamentId = first.Key.TournamentId, TeamId = offline.Key.TeamId, Name = "Offline commit" });
            await db.SaveChangesAsync();
            await new ApplyEvaluation(db).InitialAsync(offline, Guid.NewGuid(), "no-listeners");
        }
        clientTwo.DefaultRequestHeaders.Add("Last-Event-ID", "0");
        using var reconnected = await clientTwo.GetAsync(path + "/events", HttpCompletionOption.ResponseHeadersRead, stop.Token);
        using var reconnectedReader = new StreamReader(await reconnected.Content.ReadAsStreamAsync(stop.Token));
        Assert.Equal("3", await Frame(reconnectedReader));
        var final = await clientTwo.GetFromJsonAsync<JsonElement>(path, stop.Token);
        Assert.Equal("3", final.GetProperty("snapshotVersion").GetString());
        Assert.Equal("30", final.GetProperty("teams")[0].GetProperty("total").GetString());
        stop.Cancel();
    }
}
