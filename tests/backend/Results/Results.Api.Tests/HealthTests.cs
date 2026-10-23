using System.Net;
using Matigr.Results.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Matigr.Results.Api.Tests;

public class HealthTests
{
    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    public async Task Readiness_reports_the_database_without_claiming_business_success(bool database, HttpStatusCode expected)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IResultsReadiness>(new Probe(database))));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(expected, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/results/tournaments/test/standings")).StatusCode);
    }

    private sealed class Probe(bool database) : IResultsReadiness
    {
        public Task<ResultsReadinessState> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ResultsReadinessState(database));
    }
}
