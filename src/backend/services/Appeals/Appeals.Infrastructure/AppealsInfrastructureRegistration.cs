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
        services.AddHttpClient<IResultsBasis, ResultsBasisClient>(http => {
            if (configuration["Results:BaseUrl"] is { } url) http.BaseAddress = new Uri(url);
            var timeout = configuration.GetValue<int?>("Results:TimeoutSeconds") ?? 5;
            if (timeout < 1) throw new InvalidOperationException("Positive Results timeout required.");
            http.Timeout = TimeSpan.FromSeconds(timeout);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }
}
