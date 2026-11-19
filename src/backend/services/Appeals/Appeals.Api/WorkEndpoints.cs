using System.Globalization;
using Matigr.Appeals.Application;
using static Matigr.Appeals.Api.AppealEndpoints;
namespace Matigr.Appeals.Api;
internal static class WorkEndpoints
{
    public static void MapAppealWork(this WebApplication app)
    {
        app.MapGet("/api/v1/appeals/me", async (HttpContext ctx, IAppealWork work) => {
            var actor = await AppealsIdentity.Verify(ctx, app.Configuration); if (actor is null) return Error(ctx, 401, "Unauthorized");
            if (!Guid.TryParse(ctx.Request.Query["tournamentId"], out var t)) return Error(ctx, 400, "InvalidRequest");
            return Reply(ctx, await work.ListAsync(actor, t, true, ctx.RequestAborted));
        });
        app.MapGet("/api/v1/appeals/tournaments/{tournamentId}", async (string tournamentId, HttpContext ctx, IAppealWork work) => {
            var actor = await AppealsIdentity.Verify(ctx, app.Configuration); if (actor is null) return Error(ctx, 401, "Unauthorized");
            if (!Guid.TryParse(tournamentId, out var t) || (ctx.Request.Query.ContainsKey("state") && ctx.Request.Query["state"] != "Registered")) return Error(ctx, 400, "InvalidRequest");
            return Reply(ctx, await work.ListAsync(actor, t, false, ctx.RequestAborted));
        });
        app.MapGet("/api/v1/appeals/{appealId}", async (string appealId, HttpContext ctx, IAppealWork work) => {
            var actor = await AppealsIdentity.Verify(ctx, app.Configuration); if (actor is null) return Error(ctx, 401, "Unauthorized");
            if (!Guid.TryParse(appealId, out var id)) return Error(ctx, 404, "NotFound");
            return Reply(ctx, await work.GetAsync(actor, id, ctx.RequestAborted));
        });
        app.MapPost("/api/v1/appeals/{appealId}/claim", async (string appealId, HttpContext ctx, IAppealWork work) => {
            var actor = await AppealsIdentity.Verify(ctx, app.Configuration); if (actor is null) return Error(ctx, 401, "Unauthorized");
            if (!Guid.TryParse(appealId, out var id)) return Error(ctx, 404, "NotFound");
            var match = ctx.Request.Headers.IfMatch;
            if (match.Count == 0) return Error(ctx, 428, "IfMatchRequired");
            var text = match.ToString();
            if (match.Count != 1 || text.Length < 3 || text[0] != '"' || text[^1] != '"'
                || !long.TryParse(text[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1
                || text != $"\"{version.ToString(CultureInfo.InvariantCulture)}\"") return Error(ctx, 400, "InvalidRequest");
            if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding")) await Body(ctx); // no client-supplied reviewer or other business payload
            return Reply(ctx, await work.ClaimAsync(actor, id, version, Key(ctx), ctx.RequestAborted));
        });
    }
}
