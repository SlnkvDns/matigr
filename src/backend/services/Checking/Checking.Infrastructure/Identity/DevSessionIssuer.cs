using System.Security.Cryptography;
using Matigr.Checking.Application.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Matigr.Checking.Infrastructure.Identity;

public sealed record IssuedDevSession(string AccessToken, DateTimeOffset ExpiresAt, AuthenticatedSubject Subject);

public sealed class DevSessionIssuer : IDisposable
{
    private readonly RSA key = RSA.Create();
    private readonly SigningCredentials signingCredentials;
    private readonly string issuer;
    private readonly string audience;
    private readonly TimeSpan lifetime;
    private readonly TimeProvider clock;

    public DevSessionIssuer(string privateKeyPath, string publicKeyPath, string issuer, string audience,
        TimeSpan lifetime, TimeProvider clock)
    {
        this.issuer = issuer;
        this.audience = audience;
        this.lifetime = lifetime;
        this.clock = clock;
        if (lifetime <= TimeSpan.Zero) throw new InvalidOperationException("Token lifetime must be positive.");
        key.ImportFromPem(File.ReadAllText(privateKeyPath));
        _ = key.ExportParameters(true); // Fail startup if a public-only PEM was supplied for issuance.
        using var publicKey = RSA.Create();
        publicKey.ImportFromPem(File.ReadAllText(publicKeyPath));
        if (key.KeySize < 3072 || !key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey.ExportSubjectPublicKeyInfo()))
            throw new InvalidOperationException("Invalid or mismatched issuer key pair; refusing key rotation.");
        // Providers must not outlive this issuer's RSA instance (e.g. multiple hosts in one process).
        var securityKey = new RsaSecurityKey(key)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };
        signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
    }

    public IssuedDevSession Issue(AuthenticatedSubject subject)
    {
        var now = clock.GetUtcNow();
        var expires = now.Add(lifetime);
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject.SubjectId, ["name"] = subject.DisplayName,
            ["role"] = subject.Roles.Order(StringComparer.Ordinal).ToArray(),
            ["tournament_id"] = subject.TournamentIds.Order(StringComparer.Ordinal).ToArray(),
            ["token_use"] = "user", ["jti"] = Guid.NewGuid().ToString()
        };
        if (subject.TeamId is not null) claims["team_id"] = subject.TeamId;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = audience, Claims = claims,
            IssuedAt = now.UtcDateTime, NotBefore = now.UtcDateTime, Expires = expires.UtcDateTime,
            SigningCredentials = signingCredentials, TokenType = "at+jwt"
        });
        return new IssuedDevSession(token, expires, subject);
    }

    public void Dispose() => key.Dispose();
}
