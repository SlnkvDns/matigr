using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Matigr.Results.Api;

// Independent verifier of the agreed wire contract; no dependency on Checking internals or issuer.
internal static class ResultsIdentity
{
    public static async Task<ClaimsPrincipal?> Verify(HttpContext context, IConfiguration configuration, bool service)
    {
        var header = context.Request.Headers.Authorization;
        if (header.Count != 1 || !header.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var prefix = service ? "ServiceIdentity:" : "Identity:";
        var path = configuration[prefix + "PublicKeyPath"];
        var issuer = configuration[prefix + "Issuer"]; var audience = configuration[prefix + "Audience"];
        if (path is null || issuer is null || audience is null) return null;
        var pem = await File.ReadAllTextAsync(path, context.RequestAborted);
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal)) throw new InvalidOperationException("Verification requires a public key.");
        using var key = RSA.Create(); key.ImportFromPem(pem);
        if (key.KeySize < 3072) throw new InvalidOperationException("RSA 3072 required.");
        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        var result = await handler.ValidateTokenAsync(header.ToString()[7..], new TokenValidationParameters {
            ValidateIssuer = true, ValidIssuer = issuer, ValidateAudience = true, ValidAudience = audience,
            IgnoreTrailingSlashWhenValidatingAudience = false, RequireSignedTokens = true, ValidateIssuerSigningKey = true,
            IssuerSigningKey = new RsaSecurityKey(key) { CryptoProviderFactory = new() { CacheSignatureProviders = false } },
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidTypes = ["at+jwt"], RequireExpirationTime = true,
            ValidateLifetime = true, ClockSkew = TimeSpan.Zero, NameClaimType = "name", RoleClaimType = "role"
        });
        if (!result.IsValid) return null;
        var principal = new ClaimsPrincipal(result.ClaimsIdentity);
        string? Single(string name) { var claims = principal.FindAll(name).ToArray(); return claims.Length == 1 ? claims[0].Value : null; }
        if (string.IsNullOrWhiteSpace(Single("sub")) || Single("token_use") != (service ? "service" : "user")) return null;
        if (service) return Single("sub") == "Appeals" ? principal : null;
        var roles = principal.FindAll("role").Select(x => x.Value).ToArray();
        var tournaments = principal.FindAll("tournament_id").Select(x => x.Value).ToArray();
        if (Single("name") is null || roles.Length == 0 || roles.Distinct().Count() != roles.Length
            || roles.Any(x => x is not ("Team" or "Jury" or "Senior" or "Organizer"))
            || tournaments.Any(string.IsNullOrWhiteSpace) || tournaments.Distinct().Count() != tournaments.Length
            || (roles.Contains("Team") ? string.IsNullOrWhiteSpace(Single("team_id")) : principal.HasClaim(x => x.Type == "team_id"))) return null;
        return principal;
    }
}
