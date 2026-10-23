using Matigr.Checking.Application;
using Matigr.Checking.Infrastructure;
using RabbitMQ.Client;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
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
app.UseExceptionHandler();
app.MapGet("/health/live", () => Results.Ok(new { status = "Alive" }));
app.MapGet("/health/ready", async (ICheckingReadiness readiness, CancellationToken cancellationToken) =>
{
    var state = await readiness.ProbeAsync(cancellationToken);
    return Results.Json(new { status = state.Ready ? "Ready" : "Unavailable", state.Database, state.Broker },
        statusCode: state.Ready ? 200 : 503);
});
app.Run();

public partial class Program;
