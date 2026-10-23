using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Matigr.Transport;

public sealed record TransportSchemas(IReadOnlyDictionary<string, WireSchema> Incoming, IReadOnlyDictionary<string, WireSchema> Outgoing)
{
    public void Validate(JsonElement envelope, string route, bool outgoing)
    {
        if (!(outgoing ? Outgoing : Incoming).TryGetValue(route, out var schema)) throw new InvalidMessageException("InvalidRoutingKey");
        schema.Validate(envelope);
        var payload = envelope.GetProperty("payload");
        if (route is "checking.check-completed.initial.v1" or "checking.check-completed.appeal.v1"
            && payload.GetProperty("kind").GetString() != (route.EndsWith(".initial.v1", StringComparison.Ordinal) ? "Initial" : "Appeal"))
            throw new InvalidMessageException("InvalidRoutingKind");
        if ((payload.TryGetProperty("assignmentKind", out var kind) && kind.GetString() == "Selected"
            || payload.TryGetProperty("decisionOrigin", out var origin) && origin.GetString() == "AppealReview")
            && payload.GetProperty("reviewerId").GetString() != payload.GetProperty("selectedReviewerId").GetString())
            throw new InvalidMessageException("InvalidSelectedReviewer");
        if (payload.TryGetProperty("checkId", out var check) && envelope.GetProperty("aggregateType").GetString() == "Check"
            && check.GetString() != envelope.GetProperty("aggregateId").GetString()) throw new InvalidMessageException("InvalidLineage");
    }
}

public sealed class TransportWorker(IServiceScopeFactory scopes, TransportOptions options, PostgresTransportStore store,
    TransportSchemas schemas, ConnectionFactory broker, TransportHealth health, ILogger<TransportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await broker.CreateConnectionAsync(cancellationToken: stoppingToken);
                await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), stoppingToken);
                health.Connected = true;
                while (!stoppingToken.IsCancellationRequested && channel.IsOpen)
                {
                    using var call = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    call.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.LeaseSeconds / 2)));
                    var ct = call.Token;
                    await store.ObserveAsync(health, ct);
                    var outgoing = await store.ClaimAsync(ct);
                    if (outgoing is not null)
                    {
                        try
                        {
                            using var envelope = JsonDocument.Parse(outgoing.Envelope);
                            schemas.Validate(envelope.RootElement, outgoing.RoutingKey, true);
                            if (envelope.RootElement.GetProperty("messageId").GetString() != outgoing.MessageId.ToString("D")) throw new InvalidMessageException("InvalidMessageIdentity");
                            var properties = new BasicProperties { Persistent = true, ContentType = "application/json",
                                MessageId = outgoing.MessageId.ToString("D"), AppId = envelope.RootElement.GetProperty("producer").GetString(),
                                Type = envelope.RootElement.GetProperty("messageType").GetString(), CorrelationId = envelope.RootElement.GetProperty("correlationId").GetString() };
                            // Tracking-enabled BasicPublish awaits confirm and throws on mandatory return/nack.
                            await channel.BasicPublishAsync("matigr.v1", outgoing.RoutingKey, mandatory: true, properties,
                                Encoding.UTF8.GetBytes(outgoing.Envelope), ct);
                            await store.SentAsync(outgoing, ct); TransportHealth.Published.Add(1);
                        }
                        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                        {
                            await store.RetryOutboxAsync(outgoing, e is InvalidMessageException or JsonException ? "InvalidOutgoingSchema" : "PublishUnavailable", stoppingToken);
                            TransportHealth.Failures.Add(1);
                            if (e is not (InvalidMessageException or JsonException)) throw;
                        }
                    }
                    var delivery = await channel.BasicGetAsync(options.Queue, autoAck: false, ct);
                    if (delivery is not null)
                    {
                        var raw = Encoding.UTF8.GetString(delivery.Body.Span);
                        await Receive(raw, delivery.RoutingKey, delivery.BasicProperties.MessageId, delivery.BasicProperties.AppId, ct);
                        // Receive returns only after durable business commit or durable deferred/quarantine storage.
                        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct); TransportHealth.Acknowledged.Add(1);
                    }
                    var deferred = await store.NextDeferredAsync(ct);
                    if (deferred is not null)
                    {
                        if (await Receive(deferred.RawEnvelope, deferred.RoutingKey, null, null, ct)) await store.ResolveAsync(deferred.FailureId, ct);
                    }
                    if (outgoing is null && delivery is null && deferred is null) await Task.Delay(options.PollMilliseconds, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                // Never log exception bodies, raw messages, secrets or tokens.
                logger.LogWarning("Transport unavailable for {Consumer}; retry scheduled", options.Consumer);
                TransportHealth.Failures.Add(1); health.Connected = false;
                await Task.Delay(options.Backoff(1), stoppingToken);
            }
            finally { health.Connected = false; }
        }
    }
    private async Task<bool> Receive(string raw, string route, string? propertyId, string? producer, CancellationToken ct)
    {
        try
        {
            using var envelope = JsonDocument.Parse(raw);
            schemas.Validate(envelope.RootElement, route, false);
            if (propertyId is not null && propertyId != envelope.RootElement.GetProperty("messageId").GetString()) throw new InvalidMessageException("InvalidMessageIdentity");
            if (producer is not null && producer != envelope.RootElement.GetProperty("producer").GetString()) throw new InvalidMessageException("InvalidProducer");
            using var scope = scopes.CreateScope(); var handler = scope.ServiceProvider.GetRequiredService<IInboundHandler>();
            handler.Validate(envelope.RootElement);
            var result = await handler.HandleAsync(envelope.RootElement, ct);
            if (!result.Applied) { await store.RecordFailureAsync(raw, route, result.ReasonCode, false, ct); return false; }
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidMessageException or FormatException or OverflowException)
        {
            await store.RecordFailureAsync(raw, route, e is InvalidMessageException ? e.Message : "InvalidPayload", true, ct);
            TransportHealth.Failures.Add(1); return false;
        }
    }
}
