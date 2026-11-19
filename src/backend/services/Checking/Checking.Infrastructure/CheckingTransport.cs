using System.Text.Json;
using Matigr.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace Matigr.Checking.Infrastructure;

public static class CheckingTransport
{
    public static IServiceCollection AddCheckingTransport(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<TransportHealth>();
        if (!configuration.GetValue<bool>("Transport:Enabled")) return services;
        var options = TransportOptions.Read(configuration, "Checking", "checking.recheck-requests.v1", "Checking.RecheckRequested.v1", "raw_envelope");
        services.AddSingleton(options); services.AddSingleton<PostgresTransportStore>();
        var assembly = typeof(CheckingTransport).Assembly;
        services.AddSingleton(new TransportSchemas(
            new Dictionary<string, WireSchema> { ["appeals.recheck-requested.v1"] = new(assembly, "recheck-requested.v1.schema.json") },
            new Dictionary<string, WireSchema> {
                ["checking.check-completed.initial.v1"] = new(assembly, "check-completed.v1.schema.json"),
                ["checking.check-completed.appeal.v1"] = new(assembly, "check-completed.v1.schema.json"),
                ["checking.recheck-assigned.v1"] = new(assembly, "recheck-assigned.v1.schema.json"),
                ["checking.recheck-escalated.v1"] = new(assembly, "recheck-escalated.v1.schema.json"),
                ["checking.recheck-request-rejected.v1"] = new(assembly, "recheck-request-rejected.v1.schema.json") }));
        services.AddSingleton(new ConnectionFactory { HostName = configuration["Broker:Host"] ?? "localhost",
            UserName = configuration["Broker:Username"] ?? throw new InvalidOperationException("Broker username required."),
            Password = configuration["Broker:Password"] ?? throw new InvalidOperationException("Broker password required."),
            VirtualHost = configuration["Broker:VirtualHost"] ?? "matigr", AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5) });
        services.AddScoped<IInboundHandler, Storage.RecheckRequests>(); services.AddHostedService<TransportWorker>();
        return services;
    }
}
