using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Matigr.Appeals.Application;
using Matigr.Appeals.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Matigr.Appeals.Infrastructure;

internal sealed class ResultsBasisClient(HttpClient http, IConfiguration config) : IResultsBasis
{
    public async Task<PublishedBasis> GetAsync(Guid result, long expectedVersion, CancellationToken ct)
    {
        if (result == Guid.Empty || expectedVersion < 1) throw new AppealOperationException(400, "InvalidRequest");
        var privatePath = config["ResultsServiceIdentity:PrivateKeyPath"];
        var issuer = config["ResultsServiceIdentity:Issuer"]; var audience = config["ResultsServiceIdentity:Audience"];
        if (privatePath is null || issuer is null || audience is null || http.BaseAddress is null) throw new AppealOperationException(503, "ResultsUnavailable");
        try {
            using var key = RSA.Create(); key.ImportFromPem(await File.ReadAllTextAsync(privatePath, ct));
            if (key.KeySize < 3072) throw new InvalidOperationException("RSA 3072 required.");
            var now = DateTime.UtcNow;
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor {
                Issuer = issuer, Audience = audience, TokenType = "at+jwt", Claims = new Dictionary<string, object> { ["sub"] = "Appeals", ["token_use"] = "service" },
                NotBefore = now, IssuedAt = now, Expires = now.AddMinutes(1), SigningCredentials = new(new RsaSecurityKey(key) {
                    CryptoProviderFactory = new() { CacheSignatureProviders = false } }, SecurityAlgorithms.RsaSha256) });
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/v1/results/{result:D}/appeal-basis?expectedResultVersion={expectedVersion.ToString(CultureInfo.InvariantCulture)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new AppealOperationException(404, "PublishedResultNotFound");
            if (response.StatusCode == HttpStatusCode.Conflict) throw new AppealOperationException(409, "ResultVersionConflict");
            if (response.StatusCode != HttpStatusCode.OK) throw new AppealOperationException(503, "ResultsUnavailable");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var p = json.RootElement; Guid Id(string name) => p.GetProperty(name).GetGuid();
            var versionText = p.GetProperty("resultVersion").GetString();
            if (p.GetProperty("publicationState").GetString() != "Published" || Id("resultId") != result
                || versionText != expectedVersion.ToString(CultureInfo.InvariantCulture)) throw new JsonException("Untrusted basis.");
            var basis = new PublishedBasis(result, expectedVersion, Id("tournamentId"), Id("teamId"), Id("taskId"), Id("solutionId"), Id("materialVersionId"),
                Id("sourceCheckId"), Enum.Parse<DecisionOrigin>(p.GetProperty("decisionOrigin").GetString()!, false),
                Enum.Parse<Outcome>(p.GetProperty("outcome").GetString()!, false), p.GetProperty("scoreRuleVersion").GetString()!, Id("publicationId"),
                p.GetProperty("publishedAt").GetDateTimeOffset());
            basis.Validate(); return basis;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException
            or FormatException or ArgumentException or KeyNotFoundException or IOException or UnauthorizedAccessException or CryptographicException) {
            throw new AppealOperationException(503, "ResultsUnavailable");
        }
    }
}
