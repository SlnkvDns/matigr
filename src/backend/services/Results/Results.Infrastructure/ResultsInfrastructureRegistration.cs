using Matigr.Results.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Matigr.Results.Infrastructure;

public static class ResultsInfrastructureRegistration
{
    public static IServiceCollection AddResultsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ResultsDbContext>(options => options.UseNpgsql(
            configuration.GetConnectionString("Results")
                ?? throw new InvalidOperationException("Results connection string is required.")));
        services.AddSingleton<IResultsReadiness>(_ => new ResultsReadiness(
            configuration.GetConnectionString("Results")
                ?? throw new InvalidOperationException("Results connection string is required.")));
        return services;
    }
}
