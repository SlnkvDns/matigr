using System.Net;
using Matigr.Checking.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Matigr.Checking.Api.Tests;

public class HealthTests
{
    [Theory]
    [InlineData(true, true, HttpStatusCode.OK)]
    [InlineData(false, true, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, false, HttpStatusCode.ServiceUnavailable)]
    public async Task Readiness_reports_each_dependency_without_claiming_business_success(
        bool database, bool broker, HttpStatusCode expected)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<ICheckingReadiness>(new Probe(database, broker))));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(expected, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/checking/upload", null)).StatusCode);
    }

    private sealed class Probe(bool database, bool broker) : ICheckingReadiness
    {
        public Task<DependencyReadiness> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DependencyReadiness(database, broker));
    }
}
