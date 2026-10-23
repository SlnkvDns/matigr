using Matigr.Appeals.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Matigr.Appeals.Infrastructure;

public static class AppealsInfrastructureRegistration
{
    public static IServiceCollection AddAppealsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppealsDbContext>(options => options.UseNpgsql(
            configuration.GetConnectionString("Appeals")
                ?? throw new InvalidOperationException("Appeals connection string is required.")));
        services.AddSingleton<IAppealsReadiness>(_ => new AppealsReadiness(
            configuration.GetConnectionString("Appeals")
                ?? throw new InvalidOperationException("Appeals connection string is required.")));
        services.AddScoped<AppealsFixtureWindowSeeder>();
        return services;
    }
}
