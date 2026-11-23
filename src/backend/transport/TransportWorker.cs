using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Matigr.Transport;

public sealed record TransportSchemas(IReadOnlyDictionary<string, WireSchema> Incoming,
    IReadOnlyDictionary<string, WireSchema> Outgoing, Action<JsonElement, string>? ValidateContract = null)
{
    public void Validate(JsonElement envelope, string route, bool outgoing)
    {
        if (!(outgoing ? Outgoing : Incoming).TryGetValue(route, out var schema)) throw new InvalidMessageException("InvalidRoutingKey");
        schema.Validate(envelope);
        ValidateContract?.Invoke(envelope, route);
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
                await using var publisher = await connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), stoppingToken);
                await using var consumer = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                health.Connected = true;
                var stages = new Task[] {
                    PollAsync(ct => PublishAsync(publisher, ct, session.Token), session.Token),
                    PollAsync(ct => ConsumeAsync(consumer, ct), session.Token),
                    PollAsync(RetryAsync, session.Token)
                };
                try { await await Task.WhenAny(stages); }
                finally
                {
                    health.Connected = false;
                    await session.CancelAsync();
                    await Task.WhenAll(stages);
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

    private async Task PollAsync(Func<CancellationToken, Task<bool>> process, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            using var call = CancellationTokenSource.CreateLinkedTokenSource(stop);
            call.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.LeaseSeconds / 2)));
            if (!await process(call.Token)) await Task.Delay(options.PollMilliseconds, stop);
        }
    }

    private async Task<bool> PublishAsync(IChannel channel, CancellationToken ct, CancellationToken sessionToken)
    {
        if (!channel.IsOpen) throw new InvalidOperationException("Publisher channel closed.");
        var outgoing = await store.ClaimAsync(ct);
        if (outgoing is null) return false;
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
            await store.SentAsync(outgoing, ct);
            TransportHealth.Published.Add(1);
        }
        catch (Exception e) when (!sessionToken.IsCancellationRequested)
        {
            await store.RetryOutboxAsync(outgoing, e is InvalidMessageException or JsonException ? "InvalidOutgoingSchema" : "PublishUnavailable", sessionToken);
            TransportHealth.Failures.Add(1);
            if (e is not (InvalidMessageException or JsonException)) throw;
        }
        return true;
    }

    private async Task<bool> ConsumeAsync(IChannel channel, CancellationToken ct)
    {
        var delivery = await channel.BasicGetAsync(options.Queue, autoAck: false, ct);
        if (delivery is null) return false;
        var raw = Encoding.UTF8.GetString(delivery.Body.Span);
        await Receive(raw, delivery.RoutingKey, delivery.BasicProperties.MessageId, delivery.BasicProperties.AppId, ct);
        // Receive returns only after durable business commit or durable deferred/quarantine storage.
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
        TransportHealth.Acknowledged.Add(1);
        return true;
    }

    private async Task<bool> RetryAsync(CancellationToken ct)
    {
        var deferred = await store.NextDeferredAsync(ct);
        if (deferred is null) return false;
        if (await Receive(deferred.RawEnvelope, deferred.RoutingKey, null, null, ct)) await store.ResolveAsync(deferred.FailureId, ct);
        return true;
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
