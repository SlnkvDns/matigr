using System.Net.WebSockets;
using Matigr.Results.Application;
using Matigr.Results.Infrastructure;
using Npgsql;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Matigr.Results.Api;

public static class StandingsEvents
{
    public static void MapStandingsEvents(this WebApplication app) =>
        app.MapMethods("/api/v1/results/tournaments/{tournamentId}/standings/events", ["GET", "CONNECT"], StreamAsync);

    private static async Task StreamAsync(string tournamentId, HttpContext context, StandingsCache cache, IHostApplicationLifetime lifetime,
        StandingsConnectionLimits limits)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(tournamentId, out var tournament))
        {
            await Error(context, 404, "TournamentNotFound");
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await Error(context, 400, "WebSocketRequired");
            return;
        }
        using var attempt = limits.AttemptConnection();
        using var connection = attempt.IsAcquired ? limits.AcquireConnection() : null;
        if (connection?.IsAcquired != true)
        {
            context.Response.Headers.RetryAfter = "1";
            await Error(context, 429, "StandingsConnectionLimit");
            return;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        try
        {
            using var subscription = await cache.SubscribeAsync(tournament, stop.Token);
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var send = SendAsync(socket, subscription, stop.Token);
            var receive = socket.ReceiveAsync(new byte[1].AsMemory(), stop.Token).AsTask();
            var finished = await Task.WhenAny(send, receive);
            if (finished == receive && !receive.IsCompletedSuccessfully) socket.Abort();
            stop.Cancel();
            try { await Task.WhenAll(send, receive); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (WebSocketException) { }
            if (socket.State == WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
            else socket.Abort();
        }
        catch (ResultReadException e) { await Error(context, e.Status, e.Code); }
        catch (NpgsqlException) { await Error(context, 503, "DependencyUnavailable"); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (WebSocketException) { }
    }

    private static async Task SendAsync(WebSocket socket, StandingsCache.Subscription subscription, CancellationToken ct)
    {
        await foreach (var snapshot in subscription.Snapshots.ReadAllAsync(ct))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.SendAsync(snapshot.Json.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
        }
    }

    private static Task Error(HttpContext context, int status, string code) => HttpResults.Problem(statusCode: status,
        title: "Snapshot unavailable", extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = context.TraceIdentifier })
        .ExecuteAsync(context);
}
