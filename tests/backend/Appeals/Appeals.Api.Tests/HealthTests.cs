using System.Net;
using Matigr.Appeals.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Matigr.Appeals.Api.Tests;

public class HealthTests
{
    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    public async Task Readiness_reports_the_database_without_exposing_appeal_operations(bool database, HttpStatusCode expected)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IAppealsReadiness>(new Probe(database))));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(expected, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/appeals")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dev/v1/tournaments/test/end-facts")).StatusCode);
    }

    private sealed class Probe(bool database) : IAppealsReadiness
    {
        public Task<AppealsReadinessState> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AppealsReadinessState(database));
    }
}
