using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Matigr.Checking.Api.Tests;
using Matigr.Checking.Domain;
using Matigr.Checking.Infrastructure.Fixtures;
using Matigr.Checking.Infrastructure.Storage;
using Matigr.Checking.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Checking.Persistence.Tests;

public partial class StorageTests
{
    [Fact]
    public async Task Real_HTTP_races_restore_owner_replay_and_material_rights_on_PostgreSQL()
    {
        using var identity = new IdentityTestFiles(); using var materials = new PreparedFixtureFiles();
        var ids = new Dictionary<string, string>();
        materials.Rewrite(root => {
            void Rewrite(JsonNode node) {
                if (node is JsonObject obj) foreach (var pair in obj.ToArray()) {
                    if (pair.Value is JsonValue v && v.TryGetValue<string>(out var text) && Guid.TryParse(text, out _)) {
                        if (!ids.TryGetValue(text, out var value)) ids[text] = value = Guid.NewGuid().ToString("D"); obj[pair.Key] = value;
                    } else if (pair.Value is not null) Rewrite(pair.Value);
                }
                if (node is JsonArray array) for (var i = 0; i < array.Count; i++) {
                    if (array[i] is JsonValue v && v.TryGetValue<string>(out var text) && Guid.TryParse(text, out _)) {
                        if (!ids.TryGetValue(text, out var value)) ids[text] = value = Guid.NewGuid().ToString("D"); array[i] = value;
                    } else if (array[i] is not null) Rewrite(array[i]!);
                }
            }
            Rewrite(root);
        });
        var catalog = new PreparedFixtureCatalog(materials.DirectoryPath); var tournament = catalog.Solutions[0].Snapshot.TournamentId;
        await using (var db = fixture.Open()) {
            foreach (var scope in catalog.ReviewerScopes) db.ReviewerScopes.Add(new() { ReviewerId = scope.ReviewerId, TournamentId = scope.TournamentId, Role = scope.Role });
            await db.SaveChangesAsync();
            await new AcceptedReferenceBootstrap(db, TimeProvider.System).EnsureAsync(catalog.Solutions[0].Snapshot, default);
        }
        var settings = new Dictionary<string, string?> { ["ConnectionStrings:Checking"] = fixture.ConnectionString,
            ["StorageProtection:KeyPath"] = fixture.KeyPath, ["Fixtures:Directory"] = materials.DirectoryPath, ["Transport:Enabled"] = "false" };
        const string prefix = "/api/v1/checking";
        async Task<string> Token(HttpClient client, string name) {
            using var response = await client.PostAsJsonAsync("/dev/v1/session/select", new { testSubjectId = name });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        }
        async Task<HttpResponseMessage> Post(HttpClient client, string path, object body, string key, string? etag = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, prefix + path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", key); if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
            return await client.SendAsync(request);
        }
        string ownerToken; string otherToken; JsonElement assigned; string assignmentEtag;
        await using (var host = identity.Factory(overrides: settings)) {
            using var one = host.CreateClient(); using var two = host.CreateClient();
            var tokenOne = await Token(one, "jury-a"); var tokenTwo = await Token(two, "jury-b");
            one.DefaultRequestHeaders.Authorization = new("Bearer", tokenOne); two.DefaultRequestHeaders.Authorization = new("Bearer", tokenTwo);
            var replies = await Task.WhenAll(Post(one, "/me/assignments/next", new { tournamentId = tournament, queue = "general" }, "claim"),
                Post(two, "/me/assignments/next", new { tournamentId = tournament, queue = "general" }, "claim"));
            Assert.Equal(new[] { 200, 204 }, replies.Select(x => (int)x.StatusCode).Order().ToArray());
            var win = Array.FindIndex(replies, x => x.StatusCode == HttpStatusCode.OK);
            assigned = await replies[win].Content.ReadFromJsonAsync<JsonElement>(); assignmentEtag = replies[win].Headers.ETag!.Tag;
            ownerToken = win == 0 ? tokenOne : tokenTwo; otherToken = win == 0 ? tokenTwo : tokenOne;
            foreach (var reply in replies) reply.Dispose();
            one.DefaultRequestHeaders.Authorization = new("Bearer", ownerToken); two.DefaultRequestHeaders.Authorization = new("Bearer", otherToken);
            var checkId = assigned.GetProperty("check").GetProperty("checkId").GetString();
            Assert.Equal(HttpStatusCode.OK, (await one.GetAsync(prefix + $"/checks/{checkId}/material")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await two.GetAsync(prefix + $"/checks/{checkId}/material")).StatusCode);
        }
        // New HTTP host, same PostgreSQL/key and old credential. No lease/automatic release on host disposal.
        await using var restarted = identity.Factory(overrides: settings); using var owner = restarted.CreateClient(); using var other = restarted.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new("Bearer", ownerToken); other.DefaultRequestHeaders.Authorization = new("Bearer", otherToken);
        using var restored = await owner.GetAsync(prefix + "/me/assignment?tournamentId=does-not-hide-current");
        Assert.True(JsonElement.DeepEquals(assigned, await restored.Content.ReadFromJsonAsync<JsonElement>()));
        var check = assigned.GetProperty("check").GetProperty("checkId").GetString();
        var aid = assigned.GetProperty("assignmentId").GetString(); var cap = assigned.GetProperty("capability").GetString();
        var complete = new { assignmentId = aid, capability = cap, outcome = "Correct" }; var refuse = new { assignmentId = aid, capability = cap };
        var race = await Task.WhenAll(Post(owner, $"/checks/{check}/complete", complete, "complete", assignmentEtag), Post(owner, $"/checks/{check}/refuse", refuse, "refuse", assignmentEtag));
        Assert.Equal(new[] { 200, 412 }, race.Select(x => (int)x.StatusCode).Order().ToArray());
        var completedWon = race[0].StatusCode == HttpStatusCode.OK;
        using var replay = await Post(owner, $"/checks/{check}/{(completedWon ? "complete" : "refuse")}", completedWon ? (object)complete : refuse,
            completedWon ? "complete" : "refuse", "\"1\"");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(JsonElement.DeepEquals(await race[completedWon ? 0 : 1].Content.ReadFromJsonAsync<JsonElement>(), await replay.Content.ReadFromJsonAsync<JsonElement>()));
        foreach (var result in race) result.Dispose();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.GetAsync(prefix + "/me/assignment")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(prefix + $"/checks/{check}/material")).StatusCode);
        await using (var db = fixture.Open()) {
            await new AcceptedReferenceBootstrap(db, TimeProvider.System).EnsureAsync(catalog.Solutions[1].Snapshot, default);
            await new AcceptedReferenceBootstrap(db, TimeProvider.System).EnsureAsync(catalog.Solutions[2].Snapshot, default);
        }
        using var next = await Post(owner, "/me/assignments/next", new { tournamentId = tournament, queue = "general" }, "next"); Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        var active = await next.Content.ReadFromJsonAsync<JsonElement>(); var activeCheck = active.GetProperty("check").GetProperty("checkId").GetString();
        var sourceId = active.GetProperty("check").GetProperty("solutionId").GetGuid();
        var source = catalog.Solutions.Single(x => x.Snapshot.SolutionId == sourceId);
        var path = Path.Combine(materials.DirectoryPath, source.RelativeFile); var bytes = await File.ReadAllBytesAsync(path);
        try {
            await File.WriteAllTextAsync(path, "tampered");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await owner.GetAsync(prefix + $"/checks/{activeCheck}/material")).StatusCode);
            using var stillAssigned = await owner.GetAsync(prefix + "/me/assignment");
            Assert.Equal("Assigned", (await stillAssigned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("check").GetProperty("state").GetString());
        } finally { await File.WriteAllBytesAsync(path, bytes); }
        using var senior = restarted.CreateClient(); senior.DefaultRequestHeaders.Authorization = new("Bearer", await Token(senior, "senior"));
        if (completedWon) {
            var extra = await Seed(tournament); await using var db = fixture.Open(); var row = await db.Checks.SingleAsync(x => x.CheckId == extra.CheckId);
            row.State = CheckState.SeniorQueued; row.EscalationId = Guid.NewGuid(); await db.SaveChangesAsync();
        }
        var queues = await Task.WhenAll(Post(senior, "/me/assignments/next", new { tournamentId = tournament, queue = "general" }, "general"),
            Post(senior, "/me/assignments/next", new { tournamentId = tournament, queue = "senior" }, "senior"));
        Assert.Equal(new[] { 200, 409 }, queues.Select(x => (int)x.StatusCode).Order().ToArray());
        foreach (var response in queues) response.Dispose();
    }
}
