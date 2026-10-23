using Matigr.Results.Application;
using Matigr.Results.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddResultsInfrastructure(builder.Configuration);

var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { status = "Alive" })).AllowAnonymous();
app.MapGet("/health/ready", async (IResultsReadiness readiness, CancellationToken cancellationToken) =>
{
    var state = await readiness.ProbeAsync(cancellationToken);
    return Results.Json(new { status = state.Ready ? "Ready" : "Unavailable", state.Database },
        statusCode: state.Ready ? 200 : 503);
}).AllowAnonymous();
app.MapFallback(() => Results.NotFound());
app.Run();

public partial class Program;
