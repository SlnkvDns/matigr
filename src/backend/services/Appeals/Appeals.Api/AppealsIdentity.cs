using System.Security.Cryptography;
using Matigr.Appeals.Application;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Matigr.Appeals.Api;

internal static class AppealsIdentity
{
    public static async Task<AppealActor?> Verify(HttpContext context, IConfiguration config)
    {
        var header = context.Request.Headers.Authorization;
        if (header.Count != 1 || !header.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var path = config["Identity:PublicKeyPath"]; var issuer = config["Identity:Issuer"]; var audience = config["Identity:Audience"];
        if (path is null || issuer is null || audience is null) return null;
        var pem = await File.ReadAllTextAsync(path, context.RequestAborted);
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal)) throw new InvalidOperationException("Public verification key required.");
        using var rsa = RSA.Create(); rsa.ImportFromPem(pem);
        if (rsa.KeySize < 3072) throw new InvalidOperationException("RSA 3072 required.");
        var result = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(header.ToString()[7..], new TokenValidationParameters {
            ValidateIssuer = true, ValidIssuer = issuer, ValidateAudience = true, ValidAudience = audience, IgnoreTrailingSlashWhenValidatingAudience = false,
            RequireSignedTokens = true, ValidateIssuerSigningKey = true, IssuerSigningKey = new RsaSecurityKey(rsa) { CryptoProviderFactory = new() { CacheSignatureProviders = false } },
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidTypes = ["at+jwt"], RequireExpirationTime = true, ValidateLifetime = true, ClockSkew = TimeSpan.Zero });
        if (!result.IsValid) return null;
        var claims = result.ClaimsIdentity.Claims.ToArray();
        string? Single(string name) { var values = claims.Where(c => c.Type == name).ToArray(); return values.Length == 1 ? values[0].Value : null; }
        if (Single("token_use") != "user" || !Guid.TryParse(Single("sub"), out var subject) || subject == Guid.Empty || Single("name") is null) return null;
        var roles = claims.Where(c => c.Type == "role").Select(c => c.Value).ToArray();
        var scope = claims.Where(c => c.Type == "tournament_id").Select(c => c.Value).ToArray();
        if (roles.Length == 0 || roles.Distinct().Count() != roles.Length || roles.Any(r => r is not ("Team" or "Jury" or "Senior" or "Organizer"))
            || scope.Distinct().Count() != scope.Length || scope.Any(t => !Guid.TryParse(t, out var id) || id == Guid.Empty)) return null;
        Guid? team = null;
        if (roles.Contains("Team")) { if (!Guid.TryParse(Single("team_id"), out var id) || id == Guid.Empty) return null; team = id; }
        else if (claims.Any(c => c.Type == "team_id")) return null;
        return new(subject, roles, scope.Select(Guid.Parse).ToArray(), team);
    }
}
