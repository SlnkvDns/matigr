using Matigr.Appeals.Application;
using Matigr.Appeals.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Matigr.Appeals.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAppealsInfrastructure(builder.Configuration);
var app = builder.Build();

if (app.Environment.IsEnvironment("Local"))
{
    var manifestPath = builder.Configuration["Fixtures:ManifestPath"]
        ?? throw new InvalidOperationException("Fixtures:ManifestPath is required in Local environment.");
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<AppealsDbContext>();
    await database.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<AppealsFixtureWindowSeeder>()
        .EnsureOpenFixtureAsync(manifestPath, CancellationToken.None);
}

app.MapGet("/health/live", () => Results.Ok(new { status = "Alive" })).AllowAnonymous();
app.MapAppealCommands();
app.MapGet("/health/ready", async (IAppealsReadiness readiness, CancellationToken cancellationToken) =>
{
    var state = await readiness.ProbeAsync(cancellationToken);
    return Results.Json(new { status = state.Ready ? "Ready" : "Unavailable", state.Database },
        statusCode: state.Ready ? 200 : 503);
}).AllowAnonymous();
app.MapFallback(() => Results.NotFound());
app.Run();

public partial class Program;
