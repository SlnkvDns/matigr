using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Matigr.Appeals.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Matigr.Appeals.Api.Tests;

public class ManualEndHttpTests
{
    private sealed class EndProbe : IManualEnd
    {
        public int Calls;
        public Task<AppealReply> RecordAsync(AppealActor actor, Guid tournament, Guid fact, string key, CancellationToken ct)
        {
            ++Calls; Assert.True(actor.Has("Organizer", tournament)); Assert.Equal("key", key);
            return Task.FromResult(new AppealReply(200, JsonSerializer.Serialize(new { tournamentId = tournament, state = "Closed", version = "2", factId = fact }), "\"2\""));
        }
    }
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Development")]
    public async Task Manual_end_route_does_not_exist_outside_Local_or_Test(string environment)
    {
        await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment(environment));
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/dev/v1/tournaments/{Guid.NewGuid()}/end-facts", new { factId = Guid.NewGuid() })).StatusCode);
    }
    [Theory]
    [InlineData("Organizer", "valid", 200)]
    [InlineData("Jury", "valid", 403)]
    [InlineData("Senior", "valid", 403)]
    [InlineData("Organizer", "scope", 403)]
    [InlineData("Organizer", "audience", 401)]
    [InlineData("Organizer", "service", 401)]
    [InlineData("Organizer", "anonymous", 401)]
    public async Task Only_verified_scoped_organizer_can_record_end(string role, string variant, int expected)
    {
        using var rsa = RSA.Create(3072); var path = Path.Combine(Path.GetTempPath(), "appeals-http-" + Guid.NewGuid() + ".pem");
        await File.WriteAllTextAsync(path, rsa.ExportSubjectPublicKeyInfoPem());
        var tournament = Guid.NewGuid(); var probe = new EndProbe();
        try {
            await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(b => {
                b.UseEnvironment("Test"); b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Identity:Issuer"] = "user-test", ["Identity:Audience"] = "matigr-user-api", ["Identity:PublicKeyPath"] = path }));
                b.ConfigureServices(s => s.AddSingleton<IManualEnd>(probe));
            });
            using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("Idempotency-Key", "key");
            if (variant != "anonymous") {
                var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor { Issuer = "user-test", Audience = variant == "audience" ? "wrong" : "matigr-user-api",
                    TokenType = "at+jwt", NotBefore = DateTime.UtcNow.AddMinutes(-1), Expires = DateTime.UtcNow.AddMinutes(5),
                    Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString("D"), ["name"] = "Test", ["token_use"] = variant == "service" ? "service" : "user",
                        ["role"] = new[] { role }, ["tournament_id"] = new[] { (variant == "scope" ? Guid.NewGuid() : tournament).ToString("D") } },
                    SigningCredentials = new(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256) });
                client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            }
            using var response = await client.PostAsJsonAsync($"/dev/v1/tournaments/{tournament}/end-facts", new { factId = Guid.NewGuid() });
            Assert.Equal(expected, (int)response.StatusCode); Assert.True(response.Headers.CacheControl!.Private && response.Headers.CacheControl.NoStore);
            Assert.Equal(expected == 200 ? 1 : 0, probe.Calls);
            if (expected != 200) {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.True(json.RootElement.TryGetProperty("code", out _)); Assert.True(json.RootElement.TryGetProperty("correlationId", out _));
                Assert.False(json.RootElement.TryGetProperty("traceId", out _));
            }
        } finally { File.Delete(path); }
    }
}
