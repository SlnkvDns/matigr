using System.Text;
using System.Text.Json;
using Matigr.Results.Domain;
using Matigr.Results.Infrastructure;
using Matigr.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;

namespace Results.Persistence.Tests;

public partial class StorageTests
{
    private sealed class InterruptedHandler(IInboundHandler inner, bool afterCommit) : IInboundHandler
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Validate(JsonElement envelope) => inner.Validate(envelope);
        public async Task<InboundResult> HandleAsync(JsonElement envelope, CancellationToken ct)
        {
            if (afterCommit) await inner.HandleAsync(envelope, ct);
            Reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // host cancellation closes the real channel without ack
            throw new InvalidOperationException("Unreachable barrier.");
        }
    }

    private static string Wire(Evaluation e, Guid message) => JsonSerializer.Serialize(new {
        messageId = message, schemaVersion = 1, messageType = "matigr.checking.check-completed", producer = "Checking",
        occurredAt = e.CompletedAt.UtcDateTime, correlationId = Guid.NewGuid(), causationId = Guid.NewGuid(), aggregateType = "Check",
        aggregateId = e.CheckId, aggregateVersion = "3",
        payload = new { checkId = e.CheckId, solutionId = e.SolutionId, materialVersionId = e.MaterialVersionId,
            tournamentId = e.Key.TournamentId, teamId = e.Key.TeamId, taskId = e.Key.TaskId, kind = "Initial",
            outcome = e.Outcome.ToString(), decisionOrigin = "OrdinaryReview", reviewerId = e.ReviewerId,
            assignmentId = e.AssignmentId, expectedResultVersion = "0", reviewRevision = "1", completedAt = e.CompletedAt.UtcDateTime }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_worker_host_stops_before_commit_or_after_commit_before_ack_and_restarts_without_double_effect(bool afterCommit)
    {
        var e = await NewEvaluation(); var message = Guid.NewGuid(); var raw = Wire(e, message);
        var factory = new ConnectionFactory { HostName = Environment.GetEnvironmentVariable("BrokerTest__Host")
            ?? throw new InvalidOperationException("Use isolated Compose broker."), UserName = "isolated", Password = "isolated-test-only", AutomaticRecoveryEnabled = false };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        var queue = "results-fault-" + Guid.NewGuid(); const string route = "checking.check-completed.initial.v1";
        await channel.ExchangeDeclareAsync("matigr.v1", ExchangeType.Topic, durable: true);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(queue, "matigr.v1", route);
        await channel.BasicPublishAsync("matigr.v1", route, true,
            new BasicProperties { Persistent = true, ContentType = "application/json", MessageId = message.ToString("D"), AppId = "Checking" }, Encoding.UTF8.GetBytes(raw));
        var options = new TransportOptions(true, queue, "Results.CheckCompleted.v1", fixture.Connection, "envelope", PollMilliseconds: 10, LeaseSeconds: 20);
        var store = new PostgresTransportStore(options);
        var schemas = new TransportSchemas(new Dictionary<string, WireSchema> { [route] = new(typeof(ResultsTransport).Assembly, "check-completed.v1.schema.json") }, new Dictionary<string, WireSchema>());
        using (var wire = JsonDocument.Parse(raw)) schemas.Validate(wire.RootElement, route, false);
        await using var db = fixture.Open();
        var interrupted = new InterruptedHandler(new ResultMessageHandler(new ApplyEvaluation(db)), afterCommit);
        async Task Run(IInboundHandler handler, Func<Task> body)
        {
            await using var services = new ServiceCollection().AddSingleton(handler).BuildServiceProvider();
            using var worker = new TransportWorker(services.GetRequiredService<IServiceScopeFactory>(), options, store, schemas, factory, new TransportHealth(), NullLogger<TransportWorker>.Instance);
            await worker.StartAsync(default);
            try { await body(); }
            finally { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await worker.StopAsync(deadline.Token); }
        }
        try
        {
            await Run(interrupted, async () => {
                await interrupted.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await using var verify = fixture.Open();
                Assert.Equal(afterCommit, await verify.Receipts.AnyAsync(x => x.EvaluationId == e.CheckId));
                Assert.Equal(afterCommit ? 1 : 0, (await verify.Snapshots.SingleAsync(x => x.TournamentId == e.Key.TournamentId)).Version);
            });
            Assert.Equal(1u, await channel.MessageCountAsync(queue));
            await using var restarted = fixture.Open();
            await Run(new ResultMessageHandler(new ApplyEvaluation(restarted)), async () => {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (await channel.MessageCountAsync(queue, deadline.Token) != 0) await Task.Delay(10, deadline.Token);
                // BasicGet removes ready count before commit: also wait for the authoritative receipt.
                while (true) {
                    await using var verify = fixture.Open();
                    if (await verify.Receipts.AnyAsync(x => x.EvaluationId == e.CheckId, deadline.Token)) break;
                    await Task.Delay(10, deadline.Token);
                }
            });
            await using var final = fixture.Open();
            Assert.Single(await final.Results.Where(x => x.TournamentId == e.Key.TournamentId).ToListAsync());
            Assert.Single(await final.Publications.Where(x => x.ResultId == final.Results.Where(r => r.TournamentId == e.Key.TournamentId).Select(r => r.ResultId).First()).ToListAsync());
            Assert.Single(await final.Inbox.Where(x => x.MessageId == message).ToListAsync());
            Assert.Equal(1, (await final.Snapshots.SingleAsync(x => x.TournamentId == e.Key.TournamentId)).Version);
        }
        finally { await channel.QueueDeleteAsync(queue); }
    }
}
