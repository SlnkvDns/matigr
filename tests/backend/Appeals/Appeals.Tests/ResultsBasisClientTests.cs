using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matigr.Appeals.Application;
using Matigr.Appeals.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Matigr.Appeals.Tests;

public class ResultsBasisClientTests
{
    private sealed class Reply(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    [Theory]
    [InlineData(200, "valid", 200)]
    [InlineData(404, "valid", 404)]
    [InlineData(409, "valid", 409)]
    [InlineData(503, "valid", 503)]
    [InlineData(401, "valid", 503)]
    [InlineData(200, "wrongVersion", 503)]
    [InlineData(200, "wrongResult", 503)]
    [InlineData(200, "unpublished", 503)]
    [InlineData(200, "malformed", 503)]
    public async Task Adapter_uses_separate_signed_service_identity_and_requires_current_Published_basis(int status, string variant, int expected)
    {
        using var rsa = RSA.Create(3072); var file = Path.Combine(Path.GetTempPath(), "appeals-service-" + Guid.NewGuid() + ".pem");
        await File.WriteAllTextAsync(file, rsa.ExportPkcs8PrivateKeyPem());
        var basis = AppealTests.Basis() with { ResultVersion = 9007199254740993 };
        var data = new Dictionary<string, object> { ["resultId"] = variant == "wrongResult" ? Guid.NewGuid() : basis.ResultId,
            ["resultVersion"] = variant == "wrongVersion" ? "9007199254740994" : "9007199254740993", ["tournamentId"] = basis.TournamentId,
            ["teamId"] = basis.TeamId, ["taskId"] = basis.TaskId, ["solutionId"] = basis.SolutionId, ["materialVersionId"] = basis.MaterialVersionId,
            ["sourceCheckId"] = basis.BaseCheckId, ["decisionOrigin"] = "OrdinaryReview", ["outcome"] = "Correct", ["scoreRuleVersion"] = "1",
            ["publicationId"] = basis.PublicationId, ["publishedAt"] = basis.PublishedAt, ["publicationState"] = variant == "unpublished" ? "Pending" : "Published" };
        var calls = 0;
        try {
            using var http = new HttpClient(new Reply(async (request, _) => {
                ++calls; Assert.Equal($"/internal/v1/results/{basis.ResultId:D}/appeal-basis?expectedResultVersion=9007199254740993", request.RequestUri!.PathAndQuery);
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.True(request.Headers.CacheControl!.NoStore);
                var token = await new JsonWebTokenHandler().ValidateTokenAsync(request.Headers.Authorization.Parameter!, new TokenValidationParameters {
                    ValidateIssuer = true, ValidIssuer = "service-test", ValidateAudience = true, ValidAudience = "results-internal",
                    ValidateIssuerSigningKey = true, IssuerSigningKey = new RsaSecurityKey(rsa) { CryptoProviderFactory = new() { CacheSignatureProviders = false } },
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidTypes = ["at+jwt"], ValidateLifetime = true, ClockSkew = TimeSpan.Zero });
                Assert.True(token.IsValid); Assert.Equal("Appeals", token.Claims["sub"]); Assert.Equal("service", token.Claims["token_use"]);
                return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(variant == "malformed" ? "broken" : JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
            })) { BaseAddress = new Uri("http://results.invalid") };
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["ResultsServiceIdentity:PrivateKeyPath"] = file, ["ResultsServiceIdentity:Issuer"] = "service-test", ["ResultsServiceIdentity:Audience"] = "results-internal" }).Build();
            var client = new ResultsBasisClient(http, config);
            if (expected == 200) Assert.Equal(basis, await client.GetAsync(basis.ResultId, basis.ResultVersion, default));
            else Assert.Equal(expected, (await Assert.ThrowsAsync<AppealOperationException>(() => client.GetAsync(basis.ResultId, basis.ResultVersion, default))).Status);
            Assert.Equal(1, calls);
        } finally { File.Delete(file); }
    }
    [Fact]
    public async Task Missing_service_credentials_fail_closed_without_HTTP_or_cached_basis()
    {
        using var http = new HttpClient(new Reply((_, _) => throw new InvalidOperationException("Must not send"))) { BaseAddress = new Uri("http://results.invalid") };
        var client = new ResultsBasisClient(http, new ConfigurationBuilder().Build());
        Assert.Equal(503, (await Assert.ThrowsAsync<AppealOperationException>(() => client.GetAsync(Guid.NewGuid(), 1, default))).Status);
    }
    [Theory]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    public async Task Network_and_timeout_fail_closed_but_caller_cancellation_propagates(string failure)
    {
        using var rsa = RSA.Create(3072); var file = Path.Combine(Path.GetTempPath(), "appeals-cancel-" + Guid.NewGuid() + ".pem");
        await File.WriteAllTextAsync(file, rsa.ExportPkcs8PrivateKeyPem());
        try {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var http = new HttpClient(new Reply(async (_, ct) => {
                entered.TrySetResult();
                if (failure == "network") throw new HttpRequestException("test outage");
                await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException("Unreachable");
            })) { BaseAddress = new Uri("http://results.invalid"), Timeout = TimeSpan.FromMilliseconds(failure == "timeout" ? 100 : 5000) };
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["ResultsServiceIdentity:PrivateKeyPath"] = file, ["ResultsServiceIdentity:Issuer"] = "service-test", ["ResultsServiceIdentity:Audience"] = "results-internal" }).Build();
            using var cancel = new CancellationTokenSource();
            var pending = new ResultsBasisClient(http, config).GetAsync(Guid.NewGuid(), 1, cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (failure == "cancel") { cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); }
            else Assert.Equal(503, (await Assert.ThrowsAsync<AppealOperationException>(() => pending)).Status);
        } finally { File.Delete(file); }
    }
}
