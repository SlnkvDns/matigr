using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Matigr.Results.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Matigr.Results.Api.Tests;

public class StandingsEventsTests
{
    private sealed class Source : IResultReads, ISnapshotVersions
    {
        public long Version = 9007199254740993L;
        public int Builds;
        public TaskCompletionSource BuildStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBuild { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> CurrentAsync(Guid tournament, CancellationToken ct) => Task.FromResult(Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        public async Task<StandingsSnapshot> StandingsAsync(Guid tournament, CancellationToken ct)
        {
            Interlocked.Increment(ref Builds);
            BuildStarted.TrySetResult();
            await ReleaseBuild.Task.WaitAsync(ct);
            return new(tournament.ToString("D"), await CurrentAsync(tournament, ct), [], []);
        }
        public Task<MyTournamentResults> TeamAsync(Guid tournament, Guid team, CancellationToken ct) => throw new NotSupportedException();
        public Task<PublishedAppealBasis> BasisAsync(Guid result, long expectedVersion, CancellationToken ct) => throw new NotSupportedException();
    }

    private static WebApplicationFactory<Program> Host(Source source) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Snapshots:Listen"] = "false" }));
        builder.ConfigureServices(s => { s.AddSingleton<ISnapshotVersions>(source); s.AddSingleton<IResultReads>(source); });
    });

    [Fact]
    public async Task Concurrent_refreshes_share_one_build_and_conditional_reads_use_current_version()
    {
        var source = new Source();
        await using var host = Host(source);
        using var client = host.CreateClient();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var path = $"/api/v1/results/tournaments/{Guid.NewGuid()}/standings";
        var requests = Enumerable.Range(0, 100).Select(_ => client.GetAsync(path, stop.Token)).ToArray();
        try { await source.BuildStarted.Task.WaitAsync(stop.Token); }
        finally { source.ReleaseBuild.TrySetResult(); }
        var responses = await Task.WhenAll(requests);
        foreach (var response in responses)
        {
            using (response) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(1, source.Builds);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "\"0\", W/\"9007199254740993\"");
        using var unchanged = await client.SendAsync(conditional, stop.Token);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync(stop.Token));
        source.Version++;
        using var staleTag = new HttpRequestMessage(HttpMethod.Get, path);
        staleTag.Headers.TryAddWithoutValidation("If-None-Match", "\"9007199254740993\"");
        using var changed = await client.SendAsync(staleTag, stop.Token);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal("\"9007199254740994\"", changed.Headers.ETag!.Tag);
        Assert.Equal(2, source.Builds);
    }

    [Fact]
    public async Task Anonymous_websockets_share_the_cached_public_snapshot_and_reconnect_reads_current()
    {
        var source = new Source();
        source.ReleaseBuild.SetResult();
        await using var host = Host(source);
        using var client = host.CreateClient();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var path = $"/api/v1/results/tournaments/{Guid.NewGuid()}/standings";
        var socketUri = new Uri("ws://localhost" + path + "/events");
        var ws = host.Server.CreateWebSocketClient();
        using var one = await ws.ConnectAsync(socketUri, stop.Token);
        using var two = await ws.ConnectAsync(socketUri, stop.Token);
        Assert.Equal(await Frame(one, stop.Token), await Frame(two, stop.Token));
        Assert.Equal(1, source.Builds);
        source.Version++;
        using (var get = await client.GetAsync(path, stop.Token)) get.EnsureSuccessStatusCode();
        Assert.Equal("9007199254740994", await Frame(one, stop.Token));
        Assert.Equal("9007199254740994", await Frame(two, stop.Token));
        using var reconnected = await ws.ConnectAsync(socketUri, stop.Token);
        Assert.Equal("9007199254740994", await Frame(reconnected, stop.Token));
        Assert.Equal(2, source.Builds);
    }

    private static async Task<string> Frame(WebSocket socket, CancellationToken ct)
    {
        var bytes = new byte[4096];
        var message = await socket.ReceiveAsync(bytes.AsMemory(), ct);
        Assert.True(message.EndOfMessage);
        Assert.Equal(WebSocketMessageType.Text, message.MessageType);
        using var json = JsonDocument.Parse(bytes.AsMemory(0, message.Count));
        Assert.Equal(new[] { "snapshotVersion", "tasks", "teams", "tournamentId" }, json.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
        return json.RootElement.GetProperty("snapshotVersion").GetString()!;
    }
}
