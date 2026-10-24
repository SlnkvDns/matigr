using System.Text.Json;
using Matigr.Results.Domain;
using Matigr.Results.Infrastructure;
using Matigr.Transport;
using Microsoft.EntityFrameworkCore;

namespace Results.Persistence.Tests;

public partial class StorageTests
{
    private async Task<Evaluation> AppealFor(Evaluation initial, Outcome outcome)
    {
        await using var db = fixture.Open();
        await new ApplyEvaluation(db).InitialAsync(initial, Guid.NewGuid(), "initial");
        var row = await db.Results.SingleAsync(x => x.TournamentId == initial.Key.TournamentId);
        return initial with { CheckId = Guid.NewGuid(), Kind = EvaluationKind.Appeal, Outcome = outcome, Origin = DecisionOrigin.AppealReview,
            ReviewRevision = 2, ExpectedResultVersion = 1, Appeal = new(Guid.NewGuid(), Guid.NewGuid(), initial.ReviewerId, row.ResultId, initial.CheckId, row.PublicationId) };
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Appeal_CAS_preserves_noop_source_or_replaces_contribution_and_emits_complete_stable_receipt(bool changed)
    {
        var initial = await NewEvaluation(); var e = await AppealFor(initial, changed ? Outcome.Incorrect : Outcome.Correct);
        await using var db = fixture.Open(); var before = await db.Results.AsNoTracking().SingleAsync(x => x.TournamentId == e.Key.TournamentId);
        var message = Guid.NewGuid(); var work = new ApplyEvaluation(db);
        var applied = await work.AppealAsync(e, message, "appeal"); Assert.Equal("Applied", applied.Status);
        using var envelope = JsonDocument.Parse(applied.Response);
        new WireSchema(typeof(ResultsTransport).Assembly, "result-evaluation-applied.v1.schema.json").Validate(envelope.RootElement);
        new WireSchema(typeof(StorageTests).Assembly, "Appeals.result-evaluation-applied.v1.schema.json").Validate(envelope.RootElement);
        var p = envelope.RootElement.GetProperty("payload"); Assert.Equal(changed, p.GetProperty("changed").GetBoolean());
        Assert.Equal(before.PublicationId.ToString("D"), p.GetProperty("basePublicationId").GetString());
        Assert.Equal(changed ? "2" : "1", p.GetProperty("resultVersion").GetString());
        var after = await db.Results.AsNoTracking().SingleAsync(x => x.ResultId == before.ResultId);
        Assert.Equal(changed ? 0 : 10, after.Score);
        Assert.Equal(changed ? 2 : 1, (await db.Snapshots.SingleAsync(x => x.TournamentId == e.Key.TournamentId)).Version);
        if (!changed) Assert.Equal(before.Publication, after.Publication);
        Assert.Equal(changed ? 2 : 1, await db.Publications.CountAsync(x => x.ResultId == before.ResultId));
        var replay = await work.AppealAsync(e, Guid.NewGuid(), "new-correlation");
        Assert.True(JsonElement.DeepEquals(envelope.RootElement, JsonDocument.Parse(replay.Response).RootElement));
        Assert.Single(await db.Outbox.AsNoTracking().ToListAsync(), x => x.Envelope.Contains(e.CheckId.ToString(), StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("stale")]
    [InlineData("material")]
    [InlineData("publication")]
    public async Task Invalid_appeal_basis_rejects_without_changing_result_or_creating_publication(string conflict)
    {
        var initial = await NewEvaluation(); var e = await AppealFor(initial, Outcome.Incorrect);
        e = conflict switch { "stale" => e with { ExpectedResultVersion = 2 }, "material" => e with { MaterialVersionId = Guid.NewGuid() },
            _ => e with { Appeal = e.Appeal! with { BasePublicationId = Guid.NewGuid() } } };
        await using var db = fixture.Open(); var r = await new ApplyEvaluation(db).AppealAsync(e, Guid.NewGuid(), "bad");
        Assert.Equal("Rejected", r.Status); using var json = JsonDocument.Parse(r.Response);
        new WireSchema(typeof(ResultsTransport).Assembly, "result-evaluation-rejected.v1.schema.json").Validate(json.RootElement);
        new WireSchema(typeof(StorageTests).Assembly, "Appeals.result-evaluation-rejected.v1.schema.json").Validate(json.RootElement);
        Assert.Equal(10, (await db.Results.SingleAsync(x => x.TournamentId == e.Key.TournamentId)).Score);
        Assert.Equal(1, (await db.Snapshots.SingleAsync(x => x.TournamentId == e.Key.TournamentId)).Version);
    }
    [Fact]
    public async Task Appeal_receipt_outbox_and_changed_publication_rollback_together_after_SQL_before_commit()
    {
        var e = await AppealFor(await NewEvaluation(), Outcome.Incorrect); var message = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ResultsDbContext>().UseNpgsql(fixture.Connection).AddInterceptors(new FailCommit()).Options;
        await using (var broken = new ResultsDbContext(options)) await Assert.ThrowsAsync<InvalidOperationException>(() => new ApplyEvaluation(broken).AppealAsync(e, message, "fault"));
        await using var db = fixture.Open();
        Assert.False(await db.Inbox.AnyAsync(x => x.MessageId == message)); Assert.False(await db.Receipts.AnyAsync(x => x.EvaluationId == e.CheckId));
        Assert.DoesNotContain(await db.Outbox.AsNoTracking().ToListAsync(), x => x.Envelope.Contains(e.CheckId.ToString(), StringComparison.Ordinal));
        Assert.Equal(1, (await db.Snapshots.SingleAsync(x => x.TournamentId == e.Key.TournamentId)).Version);
        Assert.Equal("Applied", (await new ApplyEvaluation(db).AppealAsync(e, message, "retry")).Status);
    }
}
