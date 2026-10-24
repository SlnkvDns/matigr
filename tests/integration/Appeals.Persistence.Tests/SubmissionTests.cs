using System.Text.Json;
using Matigr.Appeals.Application;
using Matigr.Appeals.Domain;
using Matigr.Appeals.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Appeals.Persistence.Tests;

public partial class StorageTests
{
    private sealed class BasisReader(AppealsDbContext db, PublishedBasis basis) : IResultsBasis
    {
        public int Calls; public bool Down;
        public Task<PublishedBasis> GetAsync(Guid result, long version, CancellationToken ct) {
            ++Calls; Assert.Null(db.Database.CurrentTransaction);
            if (Down) throw new AppealOperationException(503, "ResultsUnavailable");
            Assert.Equal(basis.ResultId, result); Assert.Equal(basis.ResultVersion, version); return Task.FromResult(basis);
        }
    }
    private static PublishedBasis Published(AppealRow a) => new(a.ResultId, a.ExpectedResultVersion, a.TournamentId, a.TeamId, a.TaskId,
        a.SolutionId, a.MaterialVersionId, a.BaseCheckId, DecisionOrigin.OrdinaryReview, Outcome.Correct, "1", a.BasePublicationId, DateTimeOffset.UtcNow.AddMinutes(-1));
    [Fact]
    public async Task Submit_registers_without_recheck_and_successful_replay_survives_closed_window_and_Results_outage()
    {
        var seed = await NewAppeal(); await using var db = fixture.Open();
        var actor = new AppealActor(Guid.NewGuid(), ["Team"], [seed.TournamentId], seed.TeamId);
        var reader = new BasisReader(db, Published(seed)); var work = new AppealSubmission(db, reader);
        var response = await work.SubmitAsync(actor, seed.ResultId, 1, "lost-reply", default);
        Assert.Equal(201, response.Status); Assert.Equal("\"1\"", response.ETag); Assert.NotNull(response.Location);
        var row = await db.Appeals.SingleAsync(x => x.TournamentId == seed.TournamentId);
        Assert.Equal("Registered", row.State); Assert.Null(row.CurrentRequestId); Assert.Null(row.CheckId);
        Assert.DoesNotContain(await db.Outbox.AsNoTracking().ToListAsync(), x => x.Envelope.Contains(row.AppealId.ToString(), StringComparison.Ordinal));
        Assert.False(await db.Attempts.AnyAsync(x => x.AppealId == row.AppealId));
        Assert.Single(await db.History.Where(x => x.AppealId == row.AppealId).ToListAsync());
        await new ManualEnd(db).RecordAsync(new(Guid.NewGuid(), ["Organizer"], [seed.TournamentId], null), seed.TournamentId, Guid.NewGuid(), "end", default);
        reader.Down = true;
        var replay = await work.SubmitAsync(actor, seed.ResultId, 1, "lost-reply", default);
        Assert.Equal(response.Status, replay.Status); Assert.Equal(response.Location, replay.Location); Assert.Equal(response.ETag, replay.ETag);
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(response.Json).RootElement, JsonDocument.Parse(replay.Json).RootElement));
        Assert.Equal(1, reader.Calls);
        Assert.Equal("IdempotencyConflict", (await Assert.ThrowsAsync<AppealOperationException>(() => work.SubmitAsync(actor, seed.ResultId, 2, "lost-reply", default))).Code);
        Assert.Equal(1, reader.Calls);
    }
    [Theory]
    [InlineData("unavailable", 503)]
    [InlineData("foreign", 403)]
    [InlineData("senior", 409)]
    [InlineData("closed", 409)]
    public async Task Failed_admission_does_not_consume_quota_or_create_request(string reason, int expected)
    {
        var seed = await NewAppeal(); await using var db = fixture.Open(); var basis = Published(seed);
        if (reason == "senior") basis = basis with { Origin = DecisionOrigin.SeniorEscalation };
        if (reason == "closed") await new ManualEnd(db).RecordAsync(new(Guid.NewGuid(), ["Organizer"], [seed.TournamentId], null), seed.TournamentId, Guid.NewGuid(), "end", default);
        var reader = new BasisReader(db, basis) { Down = reason == "unavailable" };
        var actor = new AppealActor(Guid.NewGuid(), ["Team"], [seed.TournamentId], reason == "foreign" ? Guid.NewGuid() : seed.TeamId);
        Assert.Equal(expected, (await Assert.ThrowsAsync<AppealOperationException>(() => new AppealSubmission(db, reader).SubmitAsync(actor, seed.ResultId, 1, "submit", default))).Status);
        Assert.False(await db.Appeals.AnyAsync(x => x.TournamentId == seed.TournamentId));
        Assert.False(await db.HttpReceipts.AnyAsync(x => x.SubjectId == actor.SubjectId));
    }
    [Fact]
    public async Task Parallel_submissions_with_different_keys_obtain_one_permanent_tuple_and_one_success()
    {
        var seed = await NewAppeal(); var basis = Published(seed); var actor = new AppealActor(Guid.NewGuid(), ["Team"], [seed.TournamentId], seed.TeamId);
        async Task<int> Run() {
            await using var db = fixture.Open();
            try { return (await new AppealSubmission(db, new BasisReader(db, basis)).SubmitAsync(actor, seed.ResultId, 1, Guid.NewGuid().ToString(), default)).Status; }
            catch (AppealOperationException e) { Assert.Equal("AlreadyAppealed", e.Code); return e.Status; }
        }
        Assert.Equal(new[] { 201, 409 }, (await Task.WhenAll(Run(), Run())).Order().ToArray());
        await using var verify = fixture.Open(); Assert.Single(await verify.Appeals.Where(x => x.TournamentId == seed.TournamentId).ToListAsync());
        Assert.Single(await verify.HttpReceipts.Where(x => x.SubjectId == actor.SubjectId).ToListAsync());
    }
}
