using Matigr.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Matigr.Appeals.Infrastructure;

public static class AppealsTransport
{
    public static IServiceCollection AddAppealsTransport(this IServiceCollection services, IConfiguration c)
    {
        if (!c.GetValue<bool>("Transport:Enabled")) return services;
        var options = TransportOptions.Read(c, "Appeals", "appeals.checking-facts.v1", AppealProcess.Consumer, "envelope");
        services.AddSingleton(options); services.AddSingleton<PostgresTransportStore>();
        var assembly = typeof(AppealsTransport).Assembly;
        var schemas = new TransportSchemas(new Dictionary<string, WireSchema> {
            ["checking.recheck-assigned.v1"] = new(assembly, "recheck-assigned.v1.schema.json"),
            ["checking.recheck-request-rejected.v1"] = new(assembly, "recheck-request-rejected.v1.schema.json"),
            ["checking.recheck-escalated.v1"] = new(assembly, "recheck-escalated.v1.schema.json"),
            ["checking.check-completed.appeal.v1"] = new(assembly, "check-completed.v1.schema.json"),
            ["results.result-evaluation-applied.appeal.v1"] = new(assembly, "result-evaluation-applied.v1.schema.json"),
            ["results.result-evaluation-rejected.appeal.v1"] = new(assembly, "result-evaluation-rejected.v1.schema.json")
        }, new Dictionary<string, WireSchema> { ["appeals.recheck-requested.v1"] = new(assembly, "recheck-requested.v1.schema.json") });
        services.AddSingleton(schemas);
        services.AddSingleton(new ConnectionFactory { HostName = c["Broker:Host"] ?? "localhost", UserName = c["Broker:Username"] ?? "appeals",
            Password = c["Broker:Password"] ?? throw new InvalidOperationException("Broker password required."), VirtualHost = c["Broker:VirtualHost"] ?? "matigr",
            AutomaticRecoveryEnabled = false, RequestedConnectionTimeout = TimeSpan.FromSeconds(5) });
        services.AddScoped<IInboundHandler, AppealProcess>();
        foreach (var queue in new[] { options.Queue, "appeals.result-receipts.v1" }) {
            var local = options with { Queue = queue }; var health = new TransportHealth(); services.AddSingleton(health);
            services.AddSingleton<IHostedService>(p => new TransportWorker(p.GetRequiredService<IServiceScopeFactory>(), local,
                new PostgresTransportStore(local), schemas, p.GetRequiredService<ConnectionFactory>(), health, p.GetRequiredService<ILogger<TransportWorker>>()));
        }
        return services;
    }
}
