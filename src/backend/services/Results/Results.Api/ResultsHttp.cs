using HttpResults = Microsoft.AspNetCore.Http.Results;
using Matigr.Results.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Matigr.Results.Api;

public static class ResultsHttp
{
    private static IResult Problem(HttpContext context, int status, string code) => HttpResults.Problem(statusCode: status,
        title: "Operation unavailable", extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = context.TraceIdentifier });
    public static void MapResultReads(this WebApplication app)
    {
        app.MapGet("/api/v1/results/tournaments/{tournamentId}/standings", (string tournamentId, HttpContext context, IResultReads reads, CancellationToken ct) => Guard(context, async () => {
            if (!Guid.TryParse(tournamentId, out var tournament)) return Problem(context, 404, "TournamentNotFound");
            var view = await reads.StandingsAsync(tournament, ct);
            context.Response.Headers.ETag = $"\"{view.SnapshotVersion}\""; context.Response.Headers.CacheControl = "public, max-age=0, must-revalidate";
            return HttpResults.Ok(view);
        }));
        app.MapGet("/api/v1/results/me/tournaments/{tournamentId}/results", (string tournamentId, HttpContext context, IResultReads reads, IConfiguration config, CancellationToken ct) => Guard(context, async () => {
            context.Response.Headers.CacheControl = "private, no-store";
            var user = await ResultsIdentity.Verify(context, config, false);
            if (user is null) { context.Response.Headers.WWWAuthenticate = "Bearer"; return Problem(context, 401, "Unauthorized"); }
            if (!Guid.TryParse(tournamentId, out var tournament) || !user.IsInRole("Team")
                || !user.HasClaim("tournament_id", tournament.ToString("D")) || !Guid.TryParse(user.FindFirst("team_id")?.Value, out var team))
                return Problem(context, 403, "Forbidden");
            return HttpResults.Ok(await reads.TeamAsync(tournament, team, ct));
        }));
        app.MapGet("/internal/v1/results/{resultId}/appeal-basis", (string resultId, HttpContext context, IResultReads reads, IConfiguration config, CancellationToken ct) => Guard(context, async () => {
            context.Response.Headers.CacheControl = "private, no-store";
            var service = await ResultsIdentity.Verify(context, config, true);
            if (service is null) { context.Response.Headers.WWWAuthenticate = "Bearer"; return Problem(context, 401, "Unauthorized"); }
            var query = context.Request.Query;
            if (!Guid.TryParse(resultId, out var result)) return Problem(context, 404, "PublishedResultNotFound");
            if (query.Count != 1 || query["expectedResultVersion"].Count != 1
                || !System.Text.RegularExpressions.Regex.IsMatch(query["expectedResultVersion"].ToString(), "^[1-9][0-9]*$")
                || !long.TryParse(query["expectedResultVersion"], out var version)) return Problem(context, 409, "ResultVersionConflict");
            return HttpResults.Ok(await reads.BasisAsync(result, version, ct));
        }));
    }
    private static async Task<IResult> Guard(HttpContext context, Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ResultReadException e) { return Problem(context, e.Status, e.Code); }
        catch (Exception e) when (e is NpgsqlException or DbUpdateException or IOException or System.Security.Cryptography.CryptographicException)
        { return Problem(context, 503, "DependencyUnavailable"); }
    }
}
