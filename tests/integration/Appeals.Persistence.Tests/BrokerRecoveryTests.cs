using System.Text;
using System.Text.Json;
using Matigr.Appeals.Infrastructure;
using Matigr.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;

namespace Appeals.Persistence.Tests;
public partial class StorageTests
{
    private sealed class InterruptedProcess(IInboundHandler inner, bool committed) : IInboundHandler
    {
        public TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Validate(JsonElement e) => inner.Validate(e);
        public async Task<InboundResult> HandleAsync(JsonElement e, CancellationToken ct)
        {
            if (committed) await inner.HandleAsync(e, ct);
            Reached.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException("Unreachable barrier");
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_Appeals_worker_restart_before_commit_or_after_commit_before_ack_closes_once(bool committed)
    {
        var row = await ClaimedAppeal(); var envelope = Applied(row, Guid.NewGuid(), Guid.NewGuid()); var message = envelope.GetProperty("messageId").GetGuid();
        await using (var isolate = fixture.Open()) await isolate.Database.ExecuteSqlRawAsync("UPDATE outbox SET next_attempt_at=now()+interval '1 day' WHERE status='Pending'");
        var factory = new ConnectionFactory { HostName = Environment.GetEnvironmentVariable("BrokerTest__Host") ?? throw new InvalidOperationException("Use isolated broker"), UserName = "isolated", Password = "isolated-test-only", AutomaticRecoveryEnabled = false };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        var queue = "appeals-fault-" + Guid.NewGuid(); const string route = "results.result-evaluation-applied.appeal.v1";
        await channel.ExchangeDeclareAsync("matigr.v1", ExchangeType.Topic, durable: true);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false); await channel.QueueBindAsync(queue, "matigr.v1", route);
        await channel.BasicPublishAsync("matigr.v1", route, true, new BasicProperties { Persistent = true, ContentType = "application/json", AppId = "Results", MessageId = message.ToString() }, Encoding.UTF8.GetBytes(envelope.GetRawText()));
        var options = new TransportOptions(true, queue, AppealProcess.Consumer, fixture.Connection, "envelope", PollMilliseconds: 10, LeaseSeconds: 20);
        var schemas = new TransportSchemas(new Dictionary<string, WireSchema> { [route] = new(typeof(AppealsTransport).Assembly, "result-evaluation-applied.v1.schema.json") }, new Dictionary<string, WireSchema>());
        async Task Run(IInboundHandler handler, Func<Task> body)
        {
            await using var provider = new ServiceCollection().AddSingleton(handler).BuildServiceProvider();
            using var worker = new TransportWorker(provider.GetRequiredService<IServiceScopeFactory>(), options, new(options), schemas, factory, new(), NullLogger<TransportWorker>.Instance);
            await worker.StartAsync(default);
            try { await body(); } finally { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await worker.StopAsync(deadline.Token); }
        }
        try {
            await using var db = fixture.Open(); var interrupted = new InterruptedProcess(new AppealProcess(db), committed);
            await Run(interrupted, async () => {
                await interrupted.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20)); await using var verify = fixture.Open();
                Assert.Equal(committed, await verify.Inbox.AnyAsync(x => x.MessageId == message));
                Assert.Equal(committed ? "Closed" : "AcceptedAwaitingRecheck", (await verify.Appeals.SingleAsync(x => x.AppealId == row.AppealId)).State);
            });
            Assert.Equal(1u, await channel.MessageCountAsync(queue)); await using var restarted = fixture.Open();
            await Run(new AppealProcess(restarted), async () => {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (true) { await using var verify = fixture.Open(); if (await verify.Inbox.AnyAsync(x => x.MessageId == message, deadline.Token) && await channel.MessageCountAsync(queue, deadline.Token) == 0) break; await Task.Delay(10, deadline.Token); }
            });
            await using var final = fixture.Open();
            Assert.Equal("Closed", (await final.Appeals.SingleAsync(x => x.AppealId == row.AppealId)).State);
            Assert.Single(await final.History.Where(x => x.AppealId == row.AppealId && x.State == "Closed").ToListAsync());
            Assert.Single(await final.Attempts.Where(x => x.AppealId == row.AppealId).ToListAsync());
            Assert.Single(await final.Inbox.Where(x => x.MessageId == message).ToListAsync());
        } finally { await channel.QueueDeleteAsync(queue); }
    }
}
