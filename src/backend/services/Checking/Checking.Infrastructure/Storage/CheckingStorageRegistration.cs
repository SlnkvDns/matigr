using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Matigr.Checking.Application;

namespace Matigr.Checking.Infrastructure.Storage;

public static class CheckingStorageRegistration
{
    public static IServiceCollection AddCheckingStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IAcceptedReferenceBootstrap, AcceptedReferenceBootstrap>();
        var configuredPageSize = configuration["Checking:QueuePageSize"];
        var pageSize = configuredPageSize is null ? 50 : int.Parse(configuredPageSize, System.Globalization.CultureInfo.InvariantCulture);
        if (pageSize < 1 || pageSize == int.MaxValue) throw new InvalidOperationException("Invalid technical queue page size.");
        services.AddSingleton(new QueueReadOptions(pageSize));
        services.AddScoped<ICheckingWork>(provider => new CheckingWork(provider.GetRequiredService<CheckingDbContext>(),
            provider.GetRequiredService<StorageProtection>(), provider.GetService<IReadOnlyMaterialStore>() ?? new UnconfiguredMaterialStore(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<QueueReadOptions>()));
        services.AddDbContext<CheckingDbContext>(options => options.UseNpgsql(
            configuration.GetConnectionString("Checking")
            ?? throw new InvalidOperationException("Checking connection string is required.")));
        services.AddSingleton(_ => new StorageProtection(
            configuration["StorageProtection:KeyPath"]
            ?? throw new InvalidOperationException("StorageProtection:KeyPath is required.")));
        return services;
    }

    private sealed class UnconfiguredMaterialStore : IReadOnlyMaterialStore
    {
        public Task<Stream> OpenAsync(Domain.SolutionReference solution, CancellationToken cancellationToken) =>
            Task.FromException<Stream>(new MaterialUnavailableException());
    }
}
