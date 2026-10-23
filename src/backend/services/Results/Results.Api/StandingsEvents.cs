using System.Text;
using System.Text.Json;
using Matigr.Results.Application;
using Npgsql;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Matigr.Results.Api;

public static class StandingsEvents
{
    public static void MapStandingsEvents(this WebApplication app)
    {
        var interval = app.Configuration.GetValue<int?>("Snapshots:PollMilliseconds") ?? 1000;
        if (interval < 1) throw new InvalidOperationException("Snapshot polling interval must be positive.");
        app.MapGet("/api/v1/results/tournaments/{tournamentId}/standings/events", async (string tournamentId,
            HttpContext context, ISnapshotVersions versions) =>
        {
            if (!Guid.TryParse(tournamentId, out var tournament)) return Error(context, 404, "TournamentNotFound");
            // Enter the subscription before reading the handshake. Every connection observes its own DB,
            // so no competing broker queue or process-local notification can strand another replica.
            string initial;
            try { initial = await versions.CurrentAsync(tournament, context.RequestAborted); }
            catch (ResultReadException e) { return Error(context, e.Status, e.Code); }
            catch (NpgsqlException) { return Error(context, 503, "DependencyUnavailable"); }
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            return HttpResults.Stream(async stream =>
            {
                var ct = context.RequestAborted; var current = initial;
                async Task Emit(string version) {
                    var json = JsonSerializer.Serialize(new { tournamentId = tournament.ToString("D"), snapshotVersion = version });
                    await stream.WriteAsync(Encoding.UTF8.GetBytes($"event: snapshot\ndata: {json}\n\n"), ct);
                    await stream.FlushAsync(ct);
                }
                try
                {
                    await Emit(current);
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(interval, ct);
                        var next = await versions.CurrentAsync(tournament, ct);
                        if (next != current) { await Emit(next); current = next; }
                        else { await stream.WriteAsync(Encoding.UTF8.GetBytes(": keepalive\n\n"), ct); await stream.FlushAsync(ct); }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (NpgsqlException) { /* Close; the client reconnects and reconciles with GET. No private diagnostic payload. */ }
            }, contentType: "text/event-stream");
        });
    }
    private static IResult Error(HttpContext context, int status, string code) => HttpResults.Problem(statusCode: status,
        title: "Snapshot unavailable", extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = context.TraceIdentifier });
}
