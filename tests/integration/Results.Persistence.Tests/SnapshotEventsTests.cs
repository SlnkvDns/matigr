using System.Net.WebSockets;
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
            if (Interlocked.Exchange(ref first, 1) == 0)
            {
                Captured.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            }
            return version;
        }
    }

    [Fact]
    public async Task Real_database_commit_during_handshake_and_later_publications_reach_two_replicas()
    {
        var first = await NewEvaluation(); var second = await SecondTask(first); var gate = new HandshakeBarrier(fixture);
        WebApplicationFactory<Program> Host(bool barrier) => new WebApplicationFactory<Program>().WithWebHostBuilder(b => {
            b.UseEnvironment("Test");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> {
                ["ConnectionStrings:Results"] = fixture.Connection, ["Storage:ApplyMigrations"] = "false" }));
            if (barrier) b.ConfigureServices(s => s.AddSingleton<ISnapshotVersions>(gate));
        });
        await using var one = Host(true); await using var two = Host(false);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var path = new Uri($"ws://localhost/api/v1/results/tournaments/{first.Key.TournamentId}/standings/events");
        var clientOne = one.Server.CreateWebSocketClient(); var clientTwo = two.Server.CreateWebSocketClient();
        async Task<JsonDocument> Frame(WebSocket socket)
        {
            var buffer = new byte[2048];
            using var body = new MemoryStream();
            ValueWebSocketReceiveResult frame;
            do
            {
                frame = await socket.ReceiveAsync(buffer.AsMemory(), stop.Token);
                Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
                body.Write(buffer, 0, frame.Count);
            } while (!frame.EndOfMessage);
            return JsonDocument.Parse(body.ToArray());
        }
        async Task<string?> Version(WebSocket socket)
        {
            using var frame = await Frame(socket);
            Assert.Equal(new[] { "snapshotVersion", "tasks", "teams", "tournamentId" }, frame.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
            return frame.RootElement.GetProperty("snapshotVersion").GetString();
        }
        var pending = clientOne.ConnectAsync(path, stop.Token);
        await gate.Captured.Task.WaitAsync(stop.Token);
        try { await using var db = fixture.Open(); await new ApplyEvaluation(db).InitialAsync(first, Guid.NewGuid(), "during-handshake"); }
        finally { gate.Release.TrySetResult(); }
        using (var initial = await pending)
        using (var replica = await clientTwo.ConnectAsync(path, stop.Token))
        {
            Assert.Equal("1", await Version(initial));
            Assert.Equal("1", await Version(replica));
            // No HTTP read occurs between the write and either WebSocket message.
            await using var db = fixture.Open(); await new ApplyEvaluation(db).InitialAsync(second, Guid.NewGuid(), "two-live-replicas");
            Assert.Equal("2", await Version(initial)); Assert.Equal("2", await Version(replica));
        }
        var offline = first with { CheckId = Guid.NewGuid(), SolutionId = Guid.NewGuid(), MaterialVersionId = Guid.NewGuid(), Key = first.Key with { TeamId = Guid.NewGuid() } };
        await using (var db = fixture.Open())
        {
            db.Teams.Add(new() { TournamentId = first.Key.TournamentId, TeamId = offline.Key.TeamId, Name = "Offline commit" });
            await db.SaveChangesAsync();
            await new ApplyEvaluation(db).InitialAsync(offline, Guid.NewGuid(), "no-viewers");
        }
        using var reconnected = await clientTwo.ConnectAsync(path, stop.Token);
        using var latest = await Frame(reconnected);
        Assert.Equal("3", latest.RootElement.GetProperty("snapshotVersion").GetString());
        Assert.Equal("30", latest.RootElement.GetProperty("teams")[0].GetProperty("total").GetString());
    }
}
