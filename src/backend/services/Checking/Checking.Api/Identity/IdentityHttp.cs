using System.Globalization;
using System.Text.Json;
using Matigr.Checking.Infrastructure.Identity;

namespace Matigr.Checking.Api.Identity;

public static class IdentityHttp
{
    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services) =>
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Extensions.Remove("traceId");
            context.ProblemDetails.Extensions.TryAdd("code", "UnexpectedError");
            context.ProblemDetails.Extensions.TryAdd("correlationId", context.HttpContext.TraceIdentifier);
        });

    public static IResult Problem(HttpContext context, int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?>
        {
            ["code"] = code, ["correlationId"] = context.TraceIdentifier
        });

    public static IApplicationBuilder UsePrivateIdentityResponses(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/v1/checking")
                || context.Request.Path.StartsWithSegments("/dev/v1/session"))
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.CacheControl = "private, no-store";
                    return Task.CompletedTask;
                });
            await next(context);
        });

    public static void MapDevIdentity(this WebApplication app)
    {
        if (!app.Environment.HasDevIdentity()) return;
        // Fail startup on a broken fixture/key setup instead of rotating keys or issuing partial identities.
        _ = app.Services.GetRequiredService<DevSubjectDirectory>();
        _ = app.Services.GetRequiredService<DevSessionIssuer>();
        app.MapPost("/dev/v1/session/select", Select).AllowAnonymous();
    }

    private static async Task<IResult> Select(HttpContext context, DevSubjectDirectory subjects,
        DevSessionIssuer issuer, CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType())
            return Problem(context, 400, "InvalidRequest", "A JSON fixture selection is required");
        try
        {
            using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
            var root = json.RootElement;
            // Count also rejects duplicate keys; unknown fields cannot smuggle browser roles/team/scope.
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("testSubjectId", out var selected) || selected.ValueKind != JsonValueKind.String
                || selected.GetString()!.Length == 0)
                return Problem(context, 400, "InvalidRequest", "Select a server fixture subject");
            var subject = subjects.Find(selected.GetString()!);
            if (subject is null) return Problem(context, 404, "DevSubjectNotFound", "Fixture subject unavailable");
            var session = issuer.Issue(subject);
            return Results.Ok(new
            {
                session.AccessToken, tokenType = "Bearer",
                expiresAt = session.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                profile = new DevProfile(subject.SubjectId, subject.DisplayName,
                    subject.Roles.Order(StringComparer.Ordinal).ToArray(),
                    subject.TournamentIds.Order(StringComparer.Ordinal).ToArray(), subject.TeamId)
            });
        }
        catch (JsonException)
        {
            return Problem(context, 400, "InvalidRequest", "Invalid JSON fixture selection");
        }
    }
}

public sealed record DevProfile(string SubjectId, string DisplayName, string[] Roles, string[] TournamentIds,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? TeamId);
