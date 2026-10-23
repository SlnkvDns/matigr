using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Matigr.Checking.Api.Tests;

public sealed class IdentityTestFiles : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "matigr-auth-" + Guid.NewGuid());
    public string PublicKeyPath => Path.Combine(directory, "public.pem");
    public string PrivateKeyPath => Path.Combine(directory, "private.pem");
    public string FixturesDirectory { get; }
    public const string TournamentId = "0344e06b-4ea1-582e-bd91-e832719773f2";
    public const string JuryId = "6b8e3a04-ab14-5bdd-92cc-4a767e67f29b";
    public const string TeamId = "ce3eb69e-e2b8-5f58-a45c-40c9d71d0031";

    public IdentityTestFiles()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "infra/fixtures/manifest.json"))) root = root.Parent;
        FixturesDirectory = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Repository not found."), "infra/fixtures");
        Directory.CreateDirectory(directory);
        using var rsa = RSA.Create(3072);
        File.WriteAllText(PrivateKeyPath, rsa.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(PublicKeyPath, rsa.ExportSubjectPublicKeyInfoPem());
    }

    public Dictionary<string, string?> Settings => new()
    {
        ["Identity:Issuer"] = "matigr-local", ["Identity:Audience"] = "matigr-user-api",
        ["Identity:PublicKeyPath"] = PublicKeyPath,
        ["DevIdentity:PrivateKeyPath"] = PrivateKeyPath, ["DevIdentity:LifetimeSeconds"] = "3600",
        ["Fixtures:Directory"] = FixturesDirectory
    };

    public WebApplicationFactory<Program> Factory(string environment = "Test", Dictionary<string, string?>? overrides = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                Settings.Concat(overrides ?? []).GroupBy(pair => pair.Key).Select(group => group.Last())));
        });

    public void Dispose()
    {
        File.Delete(PrivateKeyPath);
        File.Delete(PublicKeyPath);
        Directory.Delete(directory);
    }
}
