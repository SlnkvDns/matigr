using System.Text.Json;
using Matigr.Appeals.Application;
using Npgsql;

namespace Matigr.Appeals.Api;

internal static class AppealEndpoints
{
    internal static IResult Error(HttpContext ctx, int status, string code) => Results.Problem(statusCode: status, title: "Appeal request failed",
        extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = ctx.TraceIdentifier });
    internal static IResult Reply(HttpContext ctx, AppealReply reply)
    {
        if (reply.ETag is { } etag) ctx.Response.Headers.ETag = etag;
        if (reply.Location is { } location) ctx.Response.Headers.Location = location;
        return Results.Content(reply.Json, "application/json", statusCode: reply.Status);
    }
    internal static string Key(HttpContext ctx)
    {
        var keys = ctx.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || string.IsNullOrWhiteSpace(keys[0])) throw new AppealOperationException(400, "IdempotencyKeyRequired");
        return keys[0]!;
    }
    internal static Guid Id(JsonElement body, string field)
    {
        if (!body.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var id) || id == Guid.Empty)
            throw new AppealOperationException(400, "InvalidRequest");
        return id;
    }
    internal static async Task<JsonElement> Body(HttpContext ctx, params string[] fields)
    {
        using var json = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Any(p => !fields.Contains(p.Name))
            || json.RootElement.EnumerateObject().Select(p => p.Name).Distinct().Count() != json.RootElement.EnumerateObject().Count()) throw new JsonException();
        return json.RootElement.Clone();
    }
    public static void MapAppealCommands(this WebApplication app)
    {
        app.Use(async (context, next) => {
            context.Response.Headers.CacheControl = "private, no-store";
            try { await next(context); }
            catch (AppealOperationException e) { await Error(context, e.Status, e.Code).ExecuteAsync(context); }
            catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException) { await Error(context, 400, "InvalidRequest").ExecuteAsync(context); }
            catch (NpgsqlException) { await Error(context, 503, "DependencyUnavailable").ExecuteAsync(context); }
        });
        if (app.Environment.IsEnvironment("Local") || app.Environment.IsEnvironment("Test"))
            app.MapPost("/dev/v1/tournaments/{tournamentId}/end-facts", async (string tournamentId, HttpContext ctx, IManualEnd end) => {
                var actor = await AppealsIdentity.Verify(ctx, app.Configuration);
                if (actor is null) return Error(ctx, 401, "Unauthorized");
                if (!Guid.TryParse(tournamentId, out var tournament)) return Error(ctx, 404, "TournamentNotFound");
                if (!actor.Has("Organizer", tournament)) return Error(ctx, 403, "Forbidden");
                var body = await Body(ctx, "factId");
                return Reply(ctx, await end.RecordAsync(actor, tournament, Id(body, "factId"), Key(ctx), ctx.RequestAborted));
            });
    }
}
