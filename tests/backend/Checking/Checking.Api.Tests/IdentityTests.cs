using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matigr.Checking.Api.Identity;
using Matigr.Checking.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Matigr.Checking.Api.Tests;

public class IdentityTests(IdentityTestFiles files) : IClassFixture<IdentityTestFiles>
{
    [Theory]
    [InlineData("jury-a")]
    [InlineData("jury-b")]
    [InlineData("senior")]
    [InlineData("organizer")]
    [InlineData("team-alpha")]
    [InlineData("team-beta")]
    [InlineData("team-gamma")]
    public async Task Selection_issues_exact_server_profile_and_publicly_verifiable_token(string key)
    {
        await using var factory = files.Factory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/dev/v1/session/select", new { testSubjectId = key });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertPrivate(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = json.RootElement;
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        var profile = body.GetProperty("profile");
        var expected = new DevSubjectDirectory(files.FixturesDirectory).Find(key)!;
        Assert.Equal(expected.SubjectId, profile.GetProperty("subjectId").GetString());
        Assert.Equal(expected.DisplayName, profile.GetProperty("displayName").GetString());
        Assert.Equal(expected.Roles.Order(), profile.GetProperty("roles").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(expected.TournamentIds.Order(), profile.GetProperty("tournamentIds").EnumerateArray().Select(item => item.GetString()));
        if (expected.TeamId is null) Assert.False(profile.TryGetProperty("teamId", out _));
        else Assert.Equal(expected.TeamId, profile.GetProperty("teamId").GetString());
        using var verifier = new UserTokenVerifier(new ConfigurationBuilder().AddInMemoryCollection(files.Settings).Build());
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(body.GetProperty("accessToken").GetString(), verifier.Parameters);
        Assert.True(validation.IsValid, validation.Exception?.ToString());
        Assert.Equal(expected.SubjectId, validation.ClaimsIdentity.FindFirst("sub")?.Value);
        Assert.Equal("user", validation.ClaimsIdentity.FindFirst("token_use")?.Value);
        var token = new JsonWebToken(body.GetProperty("accessToken").GetString());
        Assert.Equal("RS256", token.Alg);
        Assert.Equal("at+jwt", token.Typ);
        Assert.Equal(token.ValidTo, body.GetProperty("expiresAt").GetDateTime());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"testSubjectId\":null}")]
    [InlineData("{\"testSubjectId\":123}")]
    [InlineData("{\"testSubjectId\":\"\"}")]
    [InlineData("{\"testSubjectId\":\"jury-a\",\"role\":\"Senior\"}")]
    [InlineData("{\"testSubjectId\":\"jury-a\",\"roles\":[\"Senior\"]}")]
    [InlineData("{\"testSubjectId\":\"jury-a\",\"teamId\":\"other\"}")]
    [InlineData("{\"testSubjectId\":\"jury-a\",\"subjectId\":\"other\"}")]
    [InlineData("{\"testSubjectId\":\"jury-a\",\"tournamentIds\":[\"other\"]}")]
    [InlineData("{\"testSubjectId\":\"jury-a\",\"testSubjectId\":\"senior\"}")]
    [InlineData("{")]
    public async Task Selection_rejects_invalid_shape_and_browser_authority(string body)
    {
        await using var factory = files.Factory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/dev/v1/session/select", new StringContent(body, Encoding.UTF8, "application/json"));
        await AssertProblem(response, HttpStatusCode.BadRequest, "InvalidRequest");
    }

    [Fact]
    public async Task Unknown_fixture_and_wrong_content_type_are_safe_errors()
    {
        await using var factory = files.Factory();
        using var client = factory.CreateClient();
        await AssertProblem(await client.PostAsJsonAsync("/dev/v1/session/select", new { testSubjectId = "unknown" }),
            HttpStatusCode.NotFound, "DevSubjectNotFound");
        await AssertProblem(await client.PostAsync("/dev/v1/session/select", new StringContent("jury-a")),
            HttpStatusCode.BadRequest, "InvalidRequest");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Development")]
    public async Task Dev_route_and_issuer_services_are_absent_outside_explicit_Local_Test(string environment)
    {
        await using var factory = files.Factory(environment, new()
        {
            ["Fixtures:Directory"] = "does-not-exist", ["DevIdentity:PrivateKeyPath"] = "does-not-exist"
        });
        using var client = factory.CreateClient();
        await AssertProblem(await client.PostAsJsonAsync("/dev/v1/session/select", new { testSubjectId = "jury-a" }),
            HttpStatusCode.NotFound, "NotFound");
        Assert.Null(factory.Services.GetService<DevSessionIssuer>());
        Assert.Null(factory.Services.GetService<DevSubjectDirectory>());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Restart_preserves_subject_key_and_validity_of_previous_token()
    {
        var keyHash = SHA256.HashData(await File.ReadAllBytesAsync(files.PrivateKeyPath));
        string token;
        await using (var first = files.Factory("Local")) token = await SelectToken(first.CreateClient(), "jury-a");
        await using (var second = files.Factory("Local"))
        {
            var next = await SelectToken(second.CreateClient(), "jury-a");
            Assert.Equal(IdentityTestFiles.JuryId, new JsonWebToken(next).Subject);
        }
        // Independent API instance receives only verification configuration/public key.
        await using var host = await ProbeHost();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.GetAsync($"/api/v1/checking/probe/{IdentityTestFiles.TournamentId}/general");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(IdentityTestFiles.JuryId, await response.Content.ReadAsStringAsync());
        Assert.Equal(keyHash, SHA256.HashData(await File.ReadAllBytesAsync(files.PrivateKeyPath)));
    }

    [Theory]
    [InlineData("jury-a", "general", false, HttpStatusCode.OK)]
    [InlineData("jury-a", "senior", false, HttpStatusCode.Forbidden)]
    [InlineData("senior", "senior", false, HttpStatusCode.OK)]
    [InlineData("team-alpha", "general", false, HttpStatusCode.Forbidden)]
    [InlineData("organizer", "general", false, HttpStatusCode.Forbidden)]
    [InlineData("jury-a", "general", true, HttpStatusCode.Forbidden)]
    [InlineData("senior", "senior", true, HttpStatusCode.Forbidden)]
    public async Task Server_checks_review_role_tournament_and_senior_scope(string key, string queue, bool foreignTournament, HttpStatusCode status)
    {
        await using var factory = files.Factory();
        var token = await SelectToken(factory.CreateClient(), key);
        await using var host = await ProbeHost();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Role", "Senior");
        client.DefaultRequestHeaders.Add("X-Team-Id", "foreign-team");
        var tournament = foreignTournament ? "foreign-tournament" : IdentityTestFiles.TournamentId;
        using var response = await client.GetAsync($"/api/v1/checking/probe/{tournament}/{queue}");
        if (status == HttpStatusCode.Forbidden) await AssertProblem(response, status, "Forbidden");
        else Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public async Task Team_identity_comes_from_signed_server_claim_and_anonymous_headers_do_not_authenticate()
    {
        await using var factory = files.Factory();
        var token = await SelectToken(factory.CreateClient(), "team-alpha");
        await using var host = await ProbeHost();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Role", "Jury");
        client.DefaultRequestHeaders.Add("X-Team-Id", "foreign-team");
        await AssertProblem(await client.GetAsync("/api/v1/checking/identity-probe"), HttpStatusCode.Unauthorized, "Unauthorized");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(IdentityTestFiles.TeamId, await client.GetStringAsync("/api/v1/checking/identity-probe"));
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("audience-slash")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("signature")]
    [InlineData("unsigned")]
    [InlineData("algorithm")]
    [InlineData("service")]
    [InlineData("subject")]
    [InlineData("team")]
    [InlineData("unknown-role")]
    [InlineData("type")]
    [InlineData("tampered-role")]
    public async Task Invalid_or_non_user_credentials_are_rejected_without_leaking_details(string mutation)
    {
        await using var host = await ProbeHost();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", InvalidToken(mutation));
        await AssertProblem(await client.GetAsync($"/api/v1/checking/probe/{IdentityTestFiles.TournamentId}/general"),
            HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    public void Verifier_rejects_private_key_configuration()
    {
        var settings = files.Settings;
        settings["Identity:PublicKeyPath"] = files.PrivateKeyPath;
        Assert.Throws<InvalidOperationException>(() => new UserTokenVerifier(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
    }

    [Fact]
    public void Issuer_requires_matching_persisted_pair_and_private_signing_material()
    {
        using var foreignFiles = new IdentityTestFiles();
        Assert.Throws<InvalidOperationException>(() => new DevSessionIssuer(files.PrivateKeyPath, foreignFiles.PublicKeyPath,
            "matigr-local", "matigr-user-api", TimeSpan.FromHours(1), TimeProvider.System));
        Assert.Throws<CryptographicException>(() => new DevSessionIssuer(files.PublicKeyPath, files.PublicKeyPath,
            "matigr-local", "matigr-user-api", TimeSpan.FromHours(1), TimeProvider.System));
    }

    private async Task<WebApplication> ProbeHost()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        var settings = files.Settings;
        settings.Remove("DevIdentity:PrivateKeyPath");
        settings.Remove("Fixtures:Directory");
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddApiProblemDetails();
        builder.Services.AddCheckingIdentity(builder.Configuration, builder.Environment);
        var app = builder.Build();
        app.UsePrivateIdentityResponses();
        app.UseAuthentication();
        app.UseAuthorization();
        // Test-only probes exercise the actual API registration/group/policies with no fake business handlers.
        app.MapCheckingApi().MapGet("/probe/{tournamentId}/{queue}", async (HttpContext context,
            IAuthorizationService authorization, string tournamentId, string queue) =>
        {
            var permitted = await authorization.AuthorizeAsync(context.User,
                new ReviewScope(tournamentId, queue == "senior"), CheckingAuthorization.ReviewScopePolicy);
            return permitted.Succeeded ? Results.Text(context.CurrentSubject().SubjectId) : Results.Forbid();
        });
        app.MapGet("/api/v1/checking/identity-probe", (HttpContext context) => Results.Text(context.CurrentSubject().TeamId ?? ""))
            .RequireAuthorization();
        await app.StartAsync();
        return app;
    }

    private string InvalidToken(string mutation)
    {
        using var rsa = RSA.Create();
        if (mutation == "signature") rsa.KeySize = 3072;
        else rsa.ImportFromPem(File.ReadAllText(files.PrivateKeyPath));
        var claims = new Dictionary<string, object>
        {
            ["sub"] = IdentityTestFiles.JuryId, ["name"] = "jury-a", ["role"] = new[] { "Jury" },
            ["tournament_id"] = new[] { IdentityTestFiles.TournamentId }, ["token_use"] = "user"
        };
        if (mutation == "service") claims["token_use"] = "service";
        if (mutation == "subject") claims.Remove("sub");
        if (mutation == "team") claims["team_id"] = "browser-team";
        if (mutation == "unknown-role") claims["role"] = new[] { "Administrator" };
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = mutation == "issuer" ? "untrusted" : "matigr-local",
            Audience = mutation == "audience" ? "matigr-service-api" : mutation == "audience-slash" ? "matigr-user-api/" : "matigr-user-api",
            Claims = claims, IssuedAt = now.AddHours(-2),
            NotBefore = mutation == "future" ? now.AddHours(1) : now.AddHours(-2),
            Expires = mutation == "expired" ? now.AddHours(-1) : now.AddHours(2),
            TokenType = mutation == "type" ? "JWT" : "at+jwt",
            SigningCredentials = mutation == "unsigned" ? null : mutation == "algorithm"
                ? new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64)), SecurityAlgorithms.HmacSha256)
                : new SigningCredentials(new RsaSecurityKey(rsa)
                {
                    CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
                }, SecurityAlgorithms.RsaSha256)
        };
        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        if (mutation != "tampered-role") return token;
        var parts = token.Split('.');
        parts[1] = Base64UrlEncoder.Encode(Base64UrlEncoder.Decode(parts[1]).Replace("Jury", "Senior", StringComparison.Ordinal));
        return string.Join('.', parts);
    }

    private static async Task<string> SelectToken(HttpClient client, string key)
    {
        using var response = await client.PostAsJsonAsync("/dev/v1/session/select", new { testSubjectId = key });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static void AssertPrivate(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        AssertPrivate(response);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        if (status == HttpStatusCode.Unauthorized) Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)status, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("correlationId").GetString()));
        Assert.False(json.RootElement.TryGetProperty("detail", out _));
        Assert.All(json.RootElement.EnumerateObject(), property =>
            Assert.Contains(property.Name, new[] { "type", "title", "status", "code", "correlationId" }));
    }
}
