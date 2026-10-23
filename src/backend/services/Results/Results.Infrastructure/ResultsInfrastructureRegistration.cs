using Matigr.Results.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Matigr.Results.Infrastructure;

public static class ResultsInfrastructureRegistration
{
    public static IServiceCollection AddResultsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddResultsTransport(configuration);
        services.AddScoped<IApplyEvaluation, ApplyEvaluation>();
        services.AddScoped<IResultReads, ResultReads>();
        services.AddScoped<ISnapshotVersions, SnapshotVersions>();
        services.AddDbContext<ResultsDbContext>(options => options.UseNpgsql(
            configuration.GetConnectionString("Results")
                ?? throw new InvalidOperationException("Results connection string is required.")));
        services.AddSingleton<IResultsReadiness>(provider => new ResultsReadiness(
            configuration.GetConnectionString("Results")
                ?? throw new InvalidOperationException("Results connection string is required."),
            configuration.GetValue<bool>("Transport:Enabled") ? provider.GetRequiredService<Matigr.Transport.TransportHealth>() : null));
        return services;
    }
}
