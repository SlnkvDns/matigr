using System.Security.Claims;
using System.Security.Cryptography;
using Matigr.Checking.Application.Identity;
using Microsoft.IdentityModel.Tokens;

namespace Matigr.Checking.Api.Identity;

public sealed class UserTokenVerifier : IDisposable
{
    private readonly RSA key = RSA.Create();
    public TokenValidationParameters Parameters { get; }

    public UserTokenVerifier(IConfiguration configuration)
    {
        var pem = File.ReadAllText(Required(configuration, "Identity:PublicKeyPath"));
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new InvalidOperationException("Verification accepts a public key only.");
        key.ImportFromPem(pem);
        if (key.ExportParameters(false).Modulus!.Length < 384)
            throw new InvalidOperationException("Verification requires RSA 3072 or stronger.");
        Parameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = Required(configuration, "Identity:Issuer"),
            ValidateAudience = true, ValidAudience = Required(configuration, "Identity:Audience"),
            IgnoreTrailingSlashWhenValidatingAudience = false,
            RequireSignedTokens = true, ValidateIssuerSigningKey = true,
            IssuerSigningKey = new RsaSecurityKey(key)
            {
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
            },
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RequireExpirationTime = true, ValidateLifetime = true, ClockSkew = TimeSpan.Zero,
            ValidTypes = ["at+jwt"], NameClaimType = "name", RoleClaimType = "role"
        };
    }

    public static string Required(IConfiguration configuration, string name) =>
        !string.IsNullOrWhiteSpace(configuration[name]) ? configuration[name]!
            : throw new InvalidOperationException($"{name} is required.");

    public static AuthenticatedSubject? ReadSubject(ClaimsPrincipal principal)
    {
        string? Single(string name)
        {
            var values = principal.FindAll(name).Select(claim => claim.Value).ToArray();
            return values.Length == 1 ? values[0] : null;
        }
        var subjectId = Single("sub");
        var displayName = Single("name");
        var roles = principal.FindAll("role").Select(claim => claim.Value).ToArray();
        var tournaments = principal.FindAll("tournament_id").Select(claim => claim.Value).ToArray();
        var teamClaims = principal.FindAll("team_id").ToArray();
        var teamId = Single("team_id");
        if (principal.Identity?.IsAuthenticated != true || Single("token_use") != "user"
            || string.IsNullOrWhiteSpace(subjectId) || displayName is null
            || roles.Length == 0 || roles.Distinct(StringComparer.Ordinal).Count() != roles.Length
            || roles.Any(role => role is not ("Jury" or "Senior" or "Team" or "Organizer"))
            || tournaments.Any(string.IsNullOrWhiteSpace)
            || tournaments.Distinct(StringComparer.Ordinal).Count() != tournaments.Length
            || (roles.Contains("Team") ? string.IsNullOrWhiteSpace(teamId) : teamClaims.Length != 0))
            return null;
        return new AuthenticatedSubject(subjectId, displayName, roles, tournaments, teamId);
    }

    public void Dispose() => key.Dispose();
}
