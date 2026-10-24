using System.Text.Json;
using Matigr.Appeals.Application;
using static Matigr.Appeals.Api.AppealEndpoints;
namespace Matigr.Appeals.Api;
internal static class SubmissionEndpoints
{
    public static void MapAppealSubmissions(this WebApplication app) =>
        app.MapPost("/api/v1/appeals", async (HttpContext ctx, IAppealSubmission submission) => {
            var actor = await AppealsIdentity.Verify(ctx, app.Configuration);
            if (actor is null) return Error(ctx, 401, "Unauthorized");
            if (!actor.Roles.Contains("Team") || actor.TeamId is null) return Error(ctx, 403, "Forbidden");
            var body = await Body(ctx, "resultId", "expectedResultVersion");
            if (!body.TryGetProperty("expectedResultVersion", out var field) || field.ValueKind != JsonValueKind.String
                || !long.TryParse(field.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version)
                || version < 1 || field.GetString() != version.ToString(System.Globalization.CultureInfo.InvariantCulture)) return Error(ctx, 400, "InvalidRequest");
            return Reply(ctx, await submission.SubmitAsync(actor, Id(body, "resultId"), version, Key(ctx), ctx.RequestAborted));
        });
}
