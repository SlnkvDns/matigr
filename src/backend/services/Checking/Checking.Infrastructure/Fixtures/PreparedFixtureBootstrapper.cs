using Matigr.Checking.Application;
using Matigr.Checking.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Matigr.Checking.Infrastructure.Fixtures;

internal sealed class PreparedFixtureBootstrapper(CheckingDbContext db, PreparedFixtureCatalog catalog,
    IAcceptedReferenceBootstrap bootstrap, TimeProvider clock)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        const string name = "checking-local";
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO fixture_sets (name, version, manifest_hash, imported_at)
            VALUES ({name}, {catalog.Version}, {catalog.ManifestHash}, {now}) ON CONFLICT DO NOTHING
            """, cancellationToken);
        var sets = await db.FixtureSets.FromSqlInterpolated($"SELECT * FROM fixture_sets WHERE name = {name} FOR UPDATE")
            .AsNoTracking().ToListAsync(cancellationToken);
        if (sets.Single().Version != catalog.Version || sets.Single().ManifestHash != catalog.ManifestHash)
            throw new InvalidOperationException("Prepared fixture set differs from the previously imported immutable manifest.");
        foreach (var scope in catalog.ReviewerScopes)
        {
            var role = scope.Role.ToString();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reviewer_scopes (reviewer_id, tournament_id, role)
                VALUES ({scope.ReviewerId}, {scope.TournamentId}, {role}) ON CONFLICT DO NOTHING
                """, cancellationToken);
        }
        foreach (var solution in catalog.Solutions)
            await bootstrap.EnsureAsync(solution.Snapshot, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

public static class PreparedFixtureRegistration
{
    // Called only from explicit Local/Test composition. No endpoint or broker subscription.
    public static IServiceCollection AddPreparedCheckingFixtures(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => new PreparedFixtureCatalog(configuration["Fixtures:Directory"]
            ?? throw new InvalidOperationException("Fixtures:Directory is required for prepared materials.")));
        services.AddSingleton<IReadOnlyMaterialStore, PreparedMaterialStore>();
        services.AddScoped<PreparedFixtureBootstrapper>();
        return services;
    }

    public static Task BootstrapPreparedCheckingFixturesAsync(this IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<PreparedFixtureBootstrapper>().InitializeAsync(cancellationToken);
}
