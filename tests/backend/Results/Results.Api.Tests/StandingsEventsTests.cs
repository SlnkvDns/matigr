using System.Net;
using Matigr.Results.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Matigr.Results.Api.Tests;

public class StandingsEventsTests
{
    private sealed class Versions : ISnapshotVersions
    {
        public string Current = "9007199254740993";
        public Task<string> CurrentAsync(Guid tournament, CancellationToken ct) => Task.FromResult(Current);
    }
    [Fact]
    public async Task Anonymous_handshake_and_two_replicas_observe_public_version_and_reconnect_reads_current()
    {
        var versions = new Versions(); var tournament = Guid.NewGuid();
        WebApplicationFactory<Program> Host() => new WebApplicationFactory<Program>().WithWebHostBuilder(b => {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Snapshots:PollMilliseconds"] = "10" }));
            b.ConfigureServices(s => s.AddSingleton<ISnapshotVersions>(versions));
        });
        await using var one = Host(); await using var two = Host();
        using var clientOne = one.CreateClient(); using var clientTwo = two.CreateClient(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var path = $"/api/v1/results/tournaments/{tournament}/standings/events";
        async Task<(HttpResponseMessage Response, StreamReader Reader)> Subscribe(HttpClient client)
        {
            var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, stop.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
            Assert.True(response.Headers.CacheControl!.NoCache);
            return (response, new StreamReader(await response.Content.ReadAsStreamAsync(stop.Token)));
        }
        async Task<string> Data(StreamReader reader)
        {
            while (await reader.ReadLineAsync(stop.Token) is { } line) if (line.StartsWith("data: ", StringComparison.Ordinal)) return line[6..];
            throw new InvalidOperationException("Stream closed before version.");
        }
        var first = await Subscribe(clientOne); var second = await Subscribe(clientTwo);
        using var r1 = first.Response; using var r2 = second.Response; using var s1 = first.Reader; using var s2 = second.Reader;
        var initial = await Data(s1); Assert.Contains("9007199254740993", initial); Assert.Equal(initial, await Data(s2));
        Assert.DoesNotContain("reviewer", initial); Assert.DoesNotContain("material", initial);
        Assert.Equal(2, System.Text.Json.JsonDocument.Parse(initial).RootElement.EnumerateObject().Count());
        versions.Current = "9007199254740994";
        Assert.Contains(versions.Current, await Data(s1)); Assert.Contains(versions.Current, await Data(s2));
        clientOne.DefaultRequestHeaders.Add("Last-Event-ID", "old");
        var third = await Subscribe(clientOne); using var r3 = third.Response; using var s3 = third.Reader;
        Assert.Contains(versions.Current, await Data(s3)); stop.Cancel();
    }
}
