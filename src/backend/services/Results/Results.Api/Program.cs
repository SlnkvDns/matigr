using HttpResults = Microsoft.AspNetCore.Http.Results;
using Matigr.Results.Application;
using Matigr.Results.Infrastructure;
using Matigr.Results.Api;
using Microsoft.EntityFrameworkCore;
using Matigr.Transport;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddResultsInfrastructure(builder.Configuration);

var app = builder.Build();
if (app.Environment.IsEnvironment("Local") && app.Configuration.GetValue<bool?>("Storage:ApplyMigrations") != false)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ResultsDbContext>();
    await db.Database.MigrateAsync();
    await ResultFixtures.SeedAsync(db, app.Configuration["Fixtures:Directory"] ?? throw new InvalidOperationException("Fixtures:Directory required."));
}
if (await TransportOperations.RunAsync(app.Services, app.Configuration)) return;
app.MapResultReads();
app.MapStandingsEvents();
app.MapGet("/health/live", () => HttpResults.Ok(new { status = "Alive" })).AllowAnonymous();
app.MapGet("/health/ready", async (IResultsReadiness readiness, CancellationToken cancellationToken) =>
{
    var state = await readiness.ProbeAsync(cancellationToken);
    if (state.Broker is { } broker) return HttpResults.Json(new { status = state.Ready ? "Ready" : "Unavailable", state.Database, broker }, statusCode: state.Ready ? 200 : 503);
    return HttpResults.Json(new { status = state.Ready ? "Ready" : "Unavailable", state.Database }, statusCode: state.Ready ? 200 : 503);
}).AllowAnonymous();
app.MapFallback(() => HttpResults.NotFound());
app.Run();

public partial class Program;
