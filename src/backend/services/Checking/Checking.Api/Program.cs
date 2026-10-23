using Matigr.Checking.Application;
using Matigr.Checking.Infrastructure;
using Matigr.Checking.Api.Identity;
using RabbitMQ.Client;
using Matigr.Checking.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Matigr.Checking.Infrastructure.Fixtures;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddApiProblemDetails();
builder.Services.AddCheckingIdentity(builder.Configuration, builder.Environment);
builder.Services.AddCheckingStorage(builder.Configuration);
if (builder.Environment.IsEnvironment("Local") || builder.Environment.IsEnvironment("Test"))
    builder.Services.AddPreparedCheckingFixtures(builder.Configuration);
builder.Services.AddSingleton<ICheckingReadiness>(_ => new CheckingReadiness(
    builder.Configuration.GetConnectionString("Checking")
        ?? throw new InvalidOperationException("Checking connection string is required."),
    new ConnectionFactory
    {
        HostName = builder.Configuration["Broker:Host"] ?? "localhost",
        UserName = builder.Configuration["Broker:Username"]
            ?? throw new InvalidOperationException("Broker username is required."),
        Password = builder.Configuration["Broker:Password"]
            ?? throw new InvalidOperationException("Broker password is required."),
        VirtualHost = "matigr",
        RequestedConnectionTimeout = TimeSpan.FromSeconds(2)
    }));
var app = builder.Build();
_ = app.Services.GetRequiredService<UserTokenVerifier>();
_ = app.Services.GetRequiredService<StorageProtection>();
if (app.Environment.IsEnvironment("Local") && builder.Configuration.GetValue("Storage:ApplyMigrations", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<CheckingDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.BootstrapPreparedCheckingFixturesAsync(CancellationToken.None);
}
app.UseExceptionHandler();
app.UsePrivateIdentityResponses();
app.UseAuthentication();
app.UseAuthorization();
app.MapDevIdentity();
app.MapCheckingApi(); // Business handlers join this protected group in CHK-BE-05/06.
app.MapGet("/health/live", () => Results.Ok(new { status = "Alive" })).AllowAnonymous();
app.MapGet("/health/ready", async (ICheckingReadiness readiness, CancellationToken cancellationToken) =>
{
    var state = await readiness.ProbeAsync(cancellationToken);
    return Results.Json(new { status = state.Ready ? "Ready" : "Unavailable", state.Database, state.Broker },
        statusCode: state.Ready ? 200 : 503);
}).AllowAnonymous();
// Unmapped routes, including disabled dev routes, remain 404 with no business surface.
app.MapFallback((HttpContext context) => IdentityHttp.Problem(context, 404, "NotFound", "Resource unavailable"))
    .AllowAnonymous();
app.Run();

public partial class Program;
