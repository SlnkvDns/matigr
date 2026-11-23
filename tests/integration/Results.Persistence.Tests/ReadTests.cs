using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Matigr.Results.Application;
using Matigr.Results.Domain;
using Matigr.Results.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Results.Persistence.Tests;

public partial class StorageTests
{
    [Fact]
    public async Task Published_reads_share_scores_and_internal_basis_is_exact_and_current()
    {
        var e = await NewEvaluation(Outcome.Incorrect); await using var db = fixture.Open();
        await new ApplyEvaluation(db).InitialAsync(e, Guid.NewGuid(), "read");
        var reads = new ResultReads(db); var table = await reads.StandingsAsync(e.Key.TournamentId, default);
        var row = Assert.Single(table.Teams); Assert.Equal("0", row.Total); Assert.Equal(1, row.Rank);
        Assert.Equal("Published", Assert.Single(row.Cells).Status); Assert.Equal("0", row.Cells[0].Score);
        var mine = await reads.TeamAsync(e.Key.TournamentId, e.Key.TeamId, default);
        var result = Assert.Single(mine.Results);
        var basis = await reads.BasisAsync(Guid.Parse(result.ResultId), 1, default);
        Assert.Equal(e.CheckId.ToString("D"), basis.SourceCheckId); Assert.Equal("Incorrect", basis.Outcome);
        Assert.Equal(result.PublicationId, basis.PublicationId); Assert.Equal("Published", basis.PublicationState);
        Assert.EndsWith("Z", basis.PublishedAt);
        Assert.Equal(409, (await Assert.ThrowsAsync<ResultReadException>(() => reads.BasisAsync(Guid.Parse(result.ResultId), 2, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ResultReadException>(() => reads.BasisAsync(Guid.NewGuid(), 1, default))).Status);
    }

    [Fact]
    public async Task Real_HTTP_checks_anonymous_allowlist_team_identity_and_dedicated_service_audience()
    {
        var e = await NewEvaluation(); await using var db = fixture.Open();
        await new ApplyEvaluation(db).InitialAsync(e, Guid.NewGuid(), "http");
        var result = await db.Results.SingleAsync(x => x.TournamentId == e.Key.TournamentId);
        using var userKey = RSA.Create(3072); using var serviceKey = RSA.Create(3072);
        var directory = Path.Combine(Path.GetTempPath(), "results-keys-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        var userPath = Path.Combine(directory, "user.pem"); var servicePath = Path.Combine(directory, "service.pem");
        await File.WriteAllTextAsync(userPath, userKey.ExportSubjectPublicKeyInfoPem());
        await File.WriteAllTextAsync(servicePath, serviceKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["ConnectionStrings:Results"] = fixture.Connection, ["Storage:ApplyMigrations"] = "false",
                    ["Identity:Issuer"] = "test-user", ["Identity:Audience"] = "matigr-user-api", ["Identity:PublicKeyPath"] = userPath,
                    ["ServiceIdentity:Issuer"] = "test-service", ["ServiceIdentity:Audience"] = "matigr-results-internal", ["ServiceIdentity:PublicKeyPath"] = servicePath
                }));
            });
            using var client = host.CreateClient();
            var publicPath = $"/api/v1/results/tournaments/{e.Key.TournamentId}/standings";
            using var anonymous = await client.GetAsync(publicPath);
            Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode); Assert.Equal("\"1\"", anonymous.Headers.ETag!.Tag);
            Assert.True(anonymous.Headers.CacheControl!.Public && anonymous.Headers.CacheControl.MustRevalidate);
            Assert.Equal(TimeSpan.Zero, anonymous.Headers.CacheControl.MaxAge);
            var json = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(new[] { "snapshotVersion", "tasks", "teams", "tournamentId" }, json.EnumerateObject().Select(x => x.Name).Order().ToArray());
            Assert.DoesNotContain("reviewer", json.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("solution", json.GetRawText(), StringComparison.OrdinalIgnoreCase);
            var mine = $"/api/v1/results/me/tournaments/{e.Key.TournamentId}/results";
            var basis = $"/internal/v1/results/{result.ResultId}/appeal-basis?expectedResultVersion=1";
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(mine)).StatusCode);
            string Token(bool service = false, Guid? team = null, string? audience = null)
            {
                var claims = new Dictionary<string, object> { ["sub"] = service ? "Appeals" : "subject", ["token_use"] = service ? "service" : "user" };
                if (!service) { claims["name"] = "Team"; claims["role"] = new[] { "Team" }; claims["team_id"] = (team ?? e.Key.TeamId).ToString("D"); claims["tournament_id"] = new[] { e.Key.TournamentId.ToString("D") }; }
                return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor { Issuer = service ? "test-service" : "test-user",
                    Audience = audience ?? (service ? "matigr-results-internal" : "matigr-user-api"), Claims = claims,
                    TokenType = "at+jwt", Expires = DateTime.UtcNow.AddMinutes(5), NotBefore = DateTime.UtcNow.AddMinutes(-1),
                    SigningCredentials = new(new RsaSecurityKey(service ? serviceKey : userKey), SecurityAlgorithms.RsaSha256) });
            }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
            using var own = await client.GetAsync(mine); Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            Assert.True(own.Headers.CacheControl!.Private && own.Headers.CacheControl.NoStore);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(basis)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token(team: Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(mine)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token(service: true));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(basis)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(mine)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(basis.Replace("Version=1", "Version=2"))).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token(service: true, audience: "matigr-user-api"));
            using var forbidden = await client.GetAsync(basis); Assert.Equal(HttpStatusCode.Unauthorized, forbidden.StatusCode);
            var problem = JsonDocument.Parse(await forbidden.Content.ReadAsStringAsync()).RootElement;
            Assert.False(problem.TryGetProperty("traceId", out _)); Assert.True(problem.TryGetProperty("correlationId", out _));
        }
        finally { Directory.Delete(directory, true); }
    }
}
