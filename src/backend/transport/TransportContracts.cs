using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Matigr.Transport;

// Linked infrastructure sample, compiled separately into each service. No domain or ORM entities are shared.
public sealed record TransportOptions(bool Enabled, string Queue, string Consumer, string ConnectionString,
    string RawColumn, int PollMilliseconds = 500, int LeaseSeconds = 30, int MaxAttempts = 8,
    int RetryBaseSeconds = 2, int RetryMaxSeconds = 60)
{
    public static TransportOptions Read(IConfiguration c, string service, string queue, string consumer, string rawColumn) =>
        new(c.GetValue<bool>("Transport:Enabled"), queue, consumer,
            c.GetConnectionString(service) ?? throw new InvalidOperationException("Service database is required."), rawColumn,
            Positive(c, "PollMilliseconds", 500), Positive(c, "LeaseSeconds", 30), Positive(c, "MaxAttempts", 8),
            Positive(c, "RetryBaseSeconds", 2), Positive(c, "RetryMaxSeconds", 60));
    private static int Positive(IConfiguration c, string key, int fallback) => c.GetValue<int?>("Transport:" + key) is { } n
        ? n > 0 ? n : throw new InvalidOperationException("Invalid transport setting.") : fallback;
    public TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(RetryMaxSeconds, RetryBaseSeconds * Math.Pow(2, Math.Min(30, Math.Max(0, attempt - 1)))));
}
public sealed record Outgoing(Guid MessageId, string RoutingKey, string Envelope, DateTimeOffset Lease, int Attempts);
public sealed record Deferred(Guid FailureId, string RawEnvelope, string RoutingKey, int Attempts);
public sealed record InboundResult(bool Applied, string ReasonCode = "HandlerUnavailable");
public interface IInboundHandler
{
    void Validate(JsonElement envelope);
    Task<InboundResult> HandleAsync(JsonElement envelope, CancellationToken ct);
}
public sealed class InvalidMessageException(string code) : Exception(code);
public sealed class TransportHealth
{
    public long Pending;
    public long Deferred;
    public long Quarantined;
    public TransportHealth()
    {
        Meter.CreateObservableGauge("matigr.transport.outbox.pending", () => Volatile.Read(ref Pending));
        Meter.CreateObservableGauge("matigr.transport.deferred", () => Volatile.Read(ref Deferred));
        Meter.CreateObservableGauge("matigr.transport.quarantined", () => Volatile.Read(ref Quarantined));
    }
    public volatile bool Connected;
    public static readonly Meter Meter = new("Matigr.Transport", "1.0");
    public static readonly Counter<long> Published = Meter.CreateCounter<long>("matigr.transport.published");
    public static readonly Counter<long> Replayed = Meter.CreateCounter<long>("matigr.transport.redriven");
    public static readonly Counter<long> Failures = Meter.CreateCounter<long>("matigr.transport.failures");
    public static readonly Counter<long> Acknowledged = Meter.CreateCounter<long>("matigr.transport.acknowledged");
    public static readonly Histogram<double> Lag = Meter.CreateHistogram<double>("matigr.transport.outbox.age", "s");
}
