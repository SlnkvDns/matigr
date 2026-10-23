using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Matigr.Checking.Infrastructure.Storage;

public static class CheckingStorageRegistration
{
    public static IServiceCollection AddCheckingStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<CheckingDbContext>(options => options.UseNpgsql(
            configuration.GetConnectionString("Checking")
            ?? throw new InvalidOperationException("Checking connection string is required.")));
        services.AddSingleton(_ => new StorageProtection(
            configuration["StorageProtection:KeyPath"]
            ?? throw new InvalidOperationException("StorageProtection:KeyPath is required.")));
        return services;
    }
}
