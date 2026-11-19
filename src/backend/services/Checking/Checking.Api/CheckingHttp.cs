using System.Security.Cryptography;
using System.Text.Json;
using Matigr.Checking.Api.Identity;
using Matigr.Checking.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Matigr.Checking.Api;

public static class CheckingHttp
{
    public static void MapCheckingWork(this RouteGroupBuilder group)
    {
        foreach (var operation in new[] { "complete", "refuse" })
        {
            var complete = operation == "complete";
            group.MapPost("/checks/{checkId}/" + operation, (string checkId, HttpContext context, ICheckingWork work, CancellationToken ct) => Guard(context, async () =>
            {
                if (!Guid.TryParse(checkId, out var id)) return IdentityHttp.Problem(context, 404, "CheckNotFound", "Resource unavailable");
                var key = context.Request.Headers["Idempotency-Key"];
                if (key.Count == 0 || string.IsNullOrEmpty(key.ToString())) return IdentityHttp.Problem(context, 400, "IdempotencyKeyRequired", "Idempotency key required");
                var etag = context.Request.Headers.IfMatch;
                if (etag.Count == 0) return IdentityHttp.Problem(context, 428, "PreconditionRequired", "If-Match required");
                if (etag.Count != 1 || !System.Text.RegularExpressions.Regex.IsMatch(etag.ToString(), "^\"[1-9][0-9]*\"$")
                    || !long.TryParse(etag.ToString().Trim('"'), out var version)
                    || key.Count != 1 || !context.Request.HasJsonContentType()) return IdentityHttp.Problem(context, 400, "InvalidRequest", "Invalid command");
                try
                {
                    using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
                    var body = json.RootElement;
                    if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Count() != (complete ? 3 : 2)
                        || !body.TryGetProperty("assignmentId", out var assignment) || assignment.ValueKind != JsonValueKind.String
                        || !Guid.TryParse(assignment.GetString(), out var assignmentId) || assignmentId == Guid.Empty
                        || !body.TryGetProperty("capability", out var cap) || cap.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(cap.GetString())
                        || (complete && (!body.TryGetProperty("outcome", out var o) || o.ValueKind != JsonValueKind.String || o.GetString() is not ("Correct" or "Incorrect"))))
                        return IdentityHttp.Problem(context, 400, "InvalidRequest", "Invalid command");
                    var view = await work.TransitionAsync(context.CurrentSubject(), id, assignmentId, cap.GetString()!,
                        complete ? body.GetProperty("outcome").GetString() : null, version, key.ToString(), context.TraceIdentifier, ct);
                    context.Response.Headers.ETag = $"\"{view.Version}\"";
                    return Results.Json(view, CheckingJson.Options);
                }
                catch (JsonException) { return IdentityHttp.Problem(context, 400, "InvalidRequest", "Invalid command"); }
            }));
        }
        group.MapGet("/queues", (HttpContext context, ICheckingWork work, CancellationToken ct) => Guard(context, async () =>
        {
            var query = context.Request.Query;
            if (query.Keys.Any(x => x is not ("tournamentId" or "queue" or "cursor")) || query.Any(x => x.Value.Count != 1)
                || !Guid.TryParse(query["tournamentId"], out var tournament) || tournament == Guid.Empty
                || query["queue"].ToString() is not ("general" or "senior")
                || (query.ContainsKey("cursor") && string.IsNullOrEmpty(query["cursor"])))
                return IdentityHttp.Problem(context, 400, "InvalidRequest", "Invalid queue request");
            return Results.Json(await work.QueueAsync(context.CurrentSubject(), tournament, query["queue"].ToString(),
                query.ContainsKey("cursor") ? query["cursor"].ToString() : null, ct), CheckingJson.Options);
        }));
        group.MapPost("/me/assignments/next", (HttpContext context, ICheckingWork work, CancellationToken ct) => Guard(context, async () =>
        {
            var key = context.Request.Headers["Idempotency-Key"];
            if (key.Count == 0 || string.IsNullOrEmpty(key.ToString()))
                return IdentityHttp.Problem(context, 400, "IdempotencyKeyRequired", "Idempotency key required");
            if (key.Count != 1 || !context.Request.HasJsonContentType())
                return IdentityHttp.Problem(context, 400, "InvalidRequest", "Invalid assignment request");
            JsonDocument json;
            try { json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct); }
            catch (JsonException) { return IdentityHttp.Problem(context, 400, "InvalidRequest", "Invalid assignment request"); }
            using (json)
            {
                var body = json.RootElement;
                if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Count() != 2
                    || !body.TryGetProperty("tournamentId", out var t) || t.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(t.GetString(), out var tournament) || tournament == Guid.Empty
                    || !body.TryGetProperty("queue", out var q) || q.ValueKind != JsonValueKind.String
                    || q.GetString() is not ("general" or "senior"))
                    return IdentityHttp.Problem(context, 400, "InvalidRequest", "Invalid assignment request");
                var reply = await work.TakeNextAsync(context.CurrentSubject(), tournament, q.GetString()!, key.ToString(), context.TraceIdentifier, ct);
                if (reply.ErrorCode is not null)
                    return Results.Problem(statusCode: 409, title: "Conflict", extensions: new Dictionary<string, object?>
                        { ["code"] = reply.ErrorCode, ["correlationId"] = reply.CorrelationId });
                if (reply.Assignment is null) return Results.NoContent();
                context.Response.Headers.ETag = reply.ETag;
                return Results.Json(reply.Assignment, CheckingJson.Options);
            }
        }));
        group.MapGet("/me/assignment", (HttpContext context, ICheckingWork work, CancellationToken ct) => Guard(context, async () =>
        {
            var assignment = await work.MyAssignmentAsync(context.CurrentSubject(), ct);
            if (assignment is null) return Results.NoContent();
            context.Response.Headers.ETag = $"\"{assignment.Check.Version}\"";
            return Results.Json(assignment, CheckingJson.Options);
        }));
        group.MapGet("/checks/{checkId}", (string checkId, HttpContext context, ICheckingWork work, CancellationToken ct) => Guard(context, async () =>
        {
            if (!Guid.TryParse(checkId, out var id)) return IdentityHttp.Problem(context, 404, "CheckNotFound", "Resource unavailable");
            var check = await work.CheckAsync(context.CurrentSubject(), id, ct);
            context.Response.Headers.ETag = $"\"{check.Version}\"";
            return Results.Json(check, CheckingJson.Options);
        }));
        group.MapGet("/checks/{checkId}/material", (string checkId, HttpContext context, ICheckingWork work, CancellationToken ct) => Guard(context, async () =>
        {
            if (!Guid.TryParse(checkId, out var id)) return IdentityHttp.Problem(context, 404, "CheckNotFound", "Resource unavailable");
            var stream = await work.MaterialAsync(context.CurrentSubject(), id, ct);
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Stream(stream, "text/plain; charset=utf-8", enableRangeProcessing: false);
        }));
    }

    private static async Task<IResult> Guard(HttpContext context, Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (CheckingOperationException error) { return IdentityHttp.Problem(context, error.Status, error.Code, "Operation unavailable"); }
        catch (Exception error) when (error is MaterialUnavailableException or MaterialNotAllowedException)
        { return IdentityHttp.Problem(context, 503, "MaterialUnavailable", "Material unavailable"); }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException or CryptographicException)
        { return IdentityHttp.Problem(context, 503, "DependencyUnavailable", "Dependency unavailable"); }
    }
}
