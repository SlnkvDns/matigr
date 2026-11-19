using System.Globalization;
using System.Text.Json;
using Matigr.Results.Application;
using Matigr.Results.Domain;
using Matigr.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace Matigr.Results.Infrastructure;

public static class ResultsTransport
{
    public static IServiceCollection AddResultsTransport(this IServiceCollection services, IConfiguration c)
    {
        services.AddSingleton<TransportHealth>();
        if (!c.GetValue<bool>("Transport:Enabled")) return services;
        var options = TransportOptions.Read(c, "Results", "results.check-completed.v1", "Results.CheckCompleted.v1", "envelope");
        services.AddSingleton(options); services.AddSingleton<PostgresTransportStore>();
        var assembly = typeof(ResultsTransport).Assembly;
        services.AddSingleton(new TransportSchemas(new Dictionary<string, WireSchema> {
            ["checking.check-completed.initial.v1"] = new(assembly, "check-completed.v1.schema.json"),
            ["checking.check-completed.appeal.v1"] = new(assembly, "check-completed.v1.schema.json") },
            new Dictionary<string, WireSchema> {
                ["results.result-evaluation-applied.appeal.v1"] = new(assembly, "result-evaluation-applied.v1.schema.json"),
                ["results.result-evaluation-rejected.appeal.v1"] = new(assembly, "result-evaluation-rejected.v1.schema.json") }));
        services.AddSingleton(new ConnectionFactory { HostName = c["Broker:Host"] ?? "localhost", UserName = c["Broker:Username"] ?? "results",
            Password = c["Broker:Password"] ?? throw new InvalidOperationException("Broker password required."), VirtualHost = c["Broker:VirtualHost"] ?? "matigr",
            AutomaticRecoveryEnabled = false, RequestedConnectionTimeout = TimeSpan.FromSeconds(5) });
        services.AddScoped<IInboundHandler, ResultMessageHandler>(); services.AddHostedService<TransportWorker>(); return services;
    }
}

internal sealed class ResultMessageHandler(IApplyEvaluation apply) : IInboundHandler
{
    internal static Evaluation Read(JsonElement envelope)
    {
        var p = envelope.GetProperty("payload");
        Guid Id(string name) => Guid.Parse(p.GetProperty(name).GetString()!);
        var kind = Enum.Parse<EvaluationKind>(p.GetProperty("kind").GetString()!, false);
        return new(Id("checkId"), Id("solutionId"), Id("materialVersionId"), new(Id("tournamentId"), Id("teamId"), Id("taskId")), kind,
            Enum.Parse<Outcome>(p.GetProperty("outcome").GetString()!, false), Enum.Parse<DecisionOrigin>(p.GetProperty("decisionOrigin").GetString()!, false),
            Id("reviewerId"), Id("assignmentId"), long.Parse(p.GetProperty("expectedResultVersion").GetString()!, CultureInfo.InvariantCulture),
            long.Parse(p.GetProperty("reviewRevision").GetString()!, CultureInfo.InvariantCulture), p.GetProperty("completedAt").GetDateTimeOffset(),
            kind == EvaluationKind.Appeal ? new(Id("appealId"), Id("recheckRequestId"), Id("selectedReviewerId"), Id("resultId"), Id("baseCheckId"), Id("publicationId")) : null,
            p.TryGetProperty("escalationId", out var escalation) ? Guid.Parse(escalation.GetString()!) : null);
    }
    public void Validate(JsonElement envelope)
    {
        try { Read(envelope).Validate(); if (!Guid.TryParse(envelope.GetProperty("messageId").GetString(), out var message) || message == Guid.Empty) throw new ArgumentException(); }
        catch (ArgumentException) { throw new InvalidMessageException("InvalidEvaluationLineage"); }
    }
    public async Task<InboundResult> HandleAsync(JsonElement envelope, CancellationToken ct)
    {
        var evaluation = Read(envelope);
        // The schema and kind-specific routing are checked by the worker; no browser or material/scoring fields are trusted.
        try
        {
            var message = envelope.GetProperty("messageId").GetGuid(); var correlation = envelope.GetProperty("correlationId").GetString()!;
            var result = evaluation.Kind == EvaluationKind.Initial ? await apply.InitialAsync(evaluation, message, correlation, ct)
                : await apply.AppealAsync(evaluation, message, correlation, ct);
            return new(result.Status is "Applied" or "Rejected", "ScoringDependencyUnavailable");
        }
        catch (EvaluationIdentityConflictException) { throw new InvalidMessageException("EvaluationIdentityConflict"); }
    }
}
