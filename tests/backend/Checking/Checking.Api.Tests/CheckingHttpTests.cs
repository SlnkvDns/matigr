using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Matigr.Checking.Application;
using Matigr.Checking.Application.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Matigr.Checking.Api.Tests;

public class CheckingHttpTests(IdentityTestFiles files) : IClassFixture<IdentityTestFiles>
{
    private sealed class WorkProbe : ICheckingWork
    {
        public Task<CheckView> TransitionAsync(AuthenticatedSubject subject, Guid checkId, Guid assignmentId, string capability,
            string? outcome, long expectedVersion, string key, string correlationId, CancellationToken ct)
        { Subject(subject); return Task.FromResult(View); }
        public string? ActorId { get; private set; }
        public bool Unavailable { get; set; }
        public bool Empty { get; set; }
        public static CheckView View => new("check", "solution", "material-version", IdentityTestFiles.TournamentId,
            "team", "task", "Initial", "Assigned", "9007199254740993", "1", "2026-10-23T03:00:00Z");
        public static AssignmentView Assignment => new(View, "assignment", IdentityTestFiles.JuryId, "2026-10-23T03:01:00Z", "owner-only-secret");
        private void Subject(AuthenticatedSubject subject)
        {
            ActorId = subject.SubjectId;
            if (Unavailable) throw new CheckingOperationException(503, "DependencyUnavailable");
        }
        public Task<QueuePage> QueueAsync(AuthenticatedSubject subject, Guid t, string q, string? cursor, CancellationToken ct)
        { Subject(subject); return Task.FromResult(new QueuePage(t.ToString(), q, [], null)); }
        public Task<TakeNextReply> TakeNextAsync(AuthenticatedSubject subject, Guid t, string q, string key, string correlation, CancellationToken ct)
        { Subject(subject); return Task.FromResult(new TakeNextReply(Empty ? null : Assignment, null, correlation)); }
        public Task<AssignmentView?> MyAssignmentAsync(AuthenticatedSubject subject, CancellationToken ct)
        { Subject(subject); return Task.FromResult<AssignmentView?>(Empty ? null : Assignment); }
        public Task<CheckView> CheckAsync(AuthenticatedSubject subject, Guid checkId, CancellationToken ct)
        { Subject(subject); return Task.FromResult(View); }
        public Task<Stream> MaterialAsync(AuthenticatedSubject subject, Guid checkId, CancellationToken ct)
        { Subject(subject); return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("prepared content"))); }
    }

    private static async Task SignIn(HttpClient client, string key = "jury-a")
    {
        using var response = await client.PostAsJsonAsync("/dev/v1/session/select", new { testSubjectId = key });
        using var session = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.RootElement.GetProperty("accessToken").GetString());
    }

    [Theory]
    [InlineData(null, 428)]
    [InlineData("W/\"2\"", 400)]
    [InlineData("\"2\"", 200)]
    public async Task Verdict_routes_require_strong_version_and_trusted_owner_adapter(string? etag, int status)
    {
        await using var factory = files.Factory().WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ICheckingWork>(new WorkProbe())));
        using var client = factory.CreateClient(); await SignIn(client);
        foreach (var op in new[] { "complete", "refuse" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checking/checks/" + Guid.NewGuid() + "/" + op);
            request.Headers.Add("Idempotency-Key", "test");
            if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
            request.Content = op == "complete"
                ? JsonContent.Create(new { assignmentId = Guid.NewGuid(), capability = "secret", outcome = "Correct" })
                : JsonContent.Create(new { assignmentId = Guid.NewGuid(), capability = "secret" });
            using var response = await client.SendAsync(request);
            Assert.Equal(status, (int)response.StatusCode);
            Assert.True(response.Headers.CacheControl!.Private && response.Headers.CacheControl.NoStore);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"tournamentId\":\"0344e06b-4ea1-582e-bd91-e832719773f2\",\"queue\":\"unknown\"}")]
    [InlineData("{\"tournamentId\":\"0344e06b-4ea1-582e-bd91-e832719773f2\",\"queue\":\"general\",\"reviewerId\":\"other\"}")]
    [InlineData("{\"tournamentId\":\"0344e06b-4ea1-582e-bd91-e832719773f2\",\"queue\":\"general\",\"queue\":\"general\"}")]
    public async Task Take_rejects_invalid_or_browser_selected_actor_without_calling_work(string body)
    {
        var probe = new WorkProbe();
        await using var factory = files.Factory().WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ICheckingWork>(probe)));
        using var client = factory.CreateClient(); await SignIn(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checking/me/assignments/next")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", "retry");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Null(probe.ActorId);
        Assert.True(response.Headers.CacheControl!.Private && response.Headers.CacheControl.NoStore);
    }

    [Fact]
    public async Task Wire_views_are_exact_private_and_version_strings_do_not_lose_precision()
    {
        var probe = new WorkProbe();
        await using var factory = files.Factory().WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ICheckingWork>(probe)));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/checking/me/assignment")).StatusCode);
        await SignIn(client, "team-alpha");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/checking/me/assignment")).StatusCode);
        Assert.Null(probe.ActorId);
        await SignIn(client);
        using var missingKey = await client.PostAsJsonAsync("/api/v1/checking/me/assignments/next", new { tournamentId = IdentityTestFiles.TournamentId, queue = "general" });
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        using var response = await client.GetAsync("/api/v1/checking/me/assignment?tournamentId=ignored");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"9007199254740993\"", response.Headers.ETag!.Tag);
        Assert.Equal(IdentityTestFiles.JuryId, probe.ActorId);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("9007199254740993", json.GetProperty("check").GetProperty("version").GetString());
        Assert.False(json.GetProperty("check").TryGetProperty("outcome", out _));
        using var check = await client.GetAsync("/api/v1/checking/checks/" + Guid.NewGuid());
        Assert.DoesNotContain("capability", await check.Content.ReadAsStringAsync());
        using var material = await client.GetAsync("/api/v1/checking/checks/" + Guid.NewGuid() + "/material");
        Assert.Equal("text/plain", material.Content.Headers.ContentType!.MediaType);
        Assert.True(material.Headers.CacheControl!.Private && material.Headers.CacheControl.NoStore);
        Assert.Equal("nosniff", material.Headers.GetValues("X-Content-Type-Options").Single());
        probe.Empty = true;
        using var empty = await client.GetAsync("/api/v1/checking/me/assignment");
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode); Assert.Equal("", await empty.Content.ReadAsStringAsync());
        probe.Unavailable = true;
        using var unavailable = await client.GetAsync("/api/v1/checking/me/assignment");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        var problem = await unavailable.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DependencyUnavailable", problem.GetProperty("code").GetString());
        Assert.False(problem.TryGetProperty("traceId", out _));
    }
}
