using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Matigr.Transport;

public sealed class TransportObserver : BackgroundService
{
    private readonly TransportOptions options;
    private readonly PostgresTransportStore store;
    private readonly ILogger<TransportObserver> logger;
    private TransportStatistics statistics = new(0, 0, 0, 0);

    public TransportObserver(TransportOptions options, PostgresTransportStore store, ILogger<TransportObserver> logger)
    {
        this.options = options;
        this.store = store;
        this.logger = logger;
        TransportHealth.Meter.CreateObservableGauge("matigr.transport.outbox.pending", () => Volatile.Read(ref statistics).Pending);
        TransportHealth.Meter.CreateObservableGauge("matigr.transport.deferred", () => Volatile.Read(ref statistics).Deferred);
        TransportHealth.Meter.CreateObservableGauge("matigr.transport.quarantined", () => Volatile.Read(ref statistics).Quarantined);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(5, options.MetricsIntervalSeconds)));
            try
            {
                var sample = await store.ReadStatisticsAsync(timeout.Token);
                Volatile.Write(ref statistics, sample);
                TransportHealth.Lag.Record(sample.OldestAgeSeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) when (e is NpgsqlException or TimeoutException or OperationCanceledException or IOException)
            {
                logger.LogWarning("Transport statistics unavailable; retry scheduled.");
            }
            await Task.Delay(TimeSpan.FromSeconds(options.MetricsIntervalSeconds), stoppingToken);
        }
    }
}
