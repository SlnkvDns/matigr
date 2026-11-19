using System.Text.Json;
using Matigr.Checking.Application;
using Matigr.Checking.Application.Identity;
using Matigr.Checking.Domain;
using Matigr.Checking.Infrastructure.Fixtures;
using Matigr.Checking.Infrastructure.Storage;
using Matigr.Checking.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Checking.Persistence.Tests;

public partial class StorageTests
{
    private sealed class FailReceipt : SaveChangesInterceptor
    {
        public bool Triggered { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<HttpIdempotencyRow>().Any(x => x.State == EntityState.Added))
            { Triggered = true; throw new InvalidOperationException("Simulated failure before receipt persistence."); }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class MaterialDuringTransition(Func<Task> transition) : IReadOnlyMaterialStore
    {
        public MemoryStream Bytes { get; } = new([1, 2, 3], writable: false);
        public async Task<Stream> OpenAsync(SolutionReference solution, CancellationToken ct)
        { await transition(); return Bytes; }
    }
    private CheckingWork Work(CheckingDbContext db, IReadOnlyMaterialStore? material = null, int pageSize = 2) =>
        new(db, new StorageProtection(fixture.KeyPath), material ?? new UnavailableMaterial(), TimeProvider.System, new(pageSize));
    private static AuthenticatedSubject Actor(Guid id, params Guid[] tournaments) =>
        new(id.ToString(), "test", ["Jury", "Senior"], tournaments.Select(x => x.ToString()), null);
    private sealed class UnavailableMaterial : IReadOnlyMaterialStore
    {
        public Task<Stream> OpenAsync(SolutionReference solution, CancellationToken ct) => Task.FromException<Stream>(new MaterialUnavailableException());
    }

    [Fact]
    public async Task Two_reviewers_claim_one_check_and_only_one_receives_ownership()
    {
        var check = await Seed();
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        await SeedScope(first, check.TournamentId); await SeedScope(second, check.TournamentId);
        await using var one = fixture.Open(); await using var two = fixture.Open();
        var replies = await Task.WhenAll(
            Work(one).TakeNextAsync(Actor(first, check.TournamentId), check.TournamentId, "general", "claim", "one", CancellationToken.None),
            Work(two).TakeNextAsync(Actor(second, check.TournamentId), check.TournamentId, "general", "claim", "two", CancellationToken.None));
        Assert.Equal(new[] { 200, 204 }, replies.Select(x => x.Status).Order().ToArray());
        await using var verify = fixture.Open();
        Assert.Single(await verify.Assignments.Where(x => x.CheckId == check.CheckId).ToListAsync());
        Assert.Single(await verify.CheckHistory.Where(x => x.CheckId == check.CheckId && x.TransitionType == "Assigned").ToListAsync());
        Assert.Equal(2, await verify.HttpIdempotency.CountAsync(x => x.SubjectId == first || x.SubjectId == second));
    }

    [Fact]
    public async Task Concurrent_tabs_share_one_assignment_and_same_key_replays_original_encrypted_response()
    {
        var first = await Seed(); var second = await Seed(first.TournamentId);
        var reviewer = Guid.NewGuid(); await SeedScope(reviewer, first.TournamentId);
        var actor = Actor(reviewer, first.TournamentId);
        await using var one = fixture.Open(); await using var two = fixture.Open();
        var replies = await Task.WhenAll(
            Work(one).TakeNextAsync(actor, first.TournamentId, "general", "same", "one", CancellationToken.None),
            Work(two).TakeNextAsync(actor, first.TournamentId, "general", "same", "two", CancellationToken.None));
        Assert.All(replies, r => Assert.Equal(200, r.Status));
        Assert.Equal(replies[0].Assignment, replies[1].Assignment);
        await using var verify = fixture.Open();
        Assert.Single(await verify.Assignments.Where(x => x.ReviewerId == reviewer && x.State == AssignmentState.Active).ToListAsync());
        var receipt = Assert.Single(await verify.HttpIdempotency.Where(x => x.SubjectId == reviewer).ToListAsync());
        Assert.DoesNotContain(replies[0].Assignment!.Capability, receipt.ResponseCiphertext);
        Assert.Equal("\"2\"", receipt.ETag);
        var busy = await Work(verify).TakeNextAsync(actor, first.TournamentId, "general", "new-key", "three", CancellationToken.None);
        Assert.Equal("ReviewerBusy", busy.ErrorCode);
        Assert.Equal(replies[0].Assignment, await Work(verify).MyAssignmentAsync(actor, CancellationToken.None));
    }

    [Fact]
    public async Task Occupancy_is_global_across_tournaments_and_general_and_senior_queues()
    {
        var first = await Seed(); var second = await Seed();
        await using (var db = fixture.Open())
        {
            var row = await db.Checks.SingleAsync(x => x.CheckId == second.CheckId);
            row.State = CheckState.SeniorQueued; row.EscalationId = Guid.NewGuid(); await db.SaveChangesAsync();
        }
        var reviewer = Guid.NewGuid();
        await SeedScope(reviewer, first.TournamentId); await SeedScope(reviewer, second.TournamentId, ReviewerRole.Senior);
        var actor = Actor(reviewer, first.TournamentId, second.TournamentId);
        await using var one = fixture.Open(); await using var two = fixture.Open();
        var replies = await Task.WhenAll(
            Work(one).TakeNextAsync(actor, first.TournamentId, "general", "a", "one", CancellationToken.None),
            Work(two).TakeNextAsync(actor, second.TournamentId, "senior", "b", "two", CancellationToken.None));
        Assert.Equal(new[] { 200, 409 }, replies.Select(x => x.Status).Order().ToArray());
        Assert.Equal("ReviewerBusy", replies.Single(x => x.Status == 409).ErrorCode);
        await using var verify = fixture.Open();
        var current = await Work(verify).MyAssignmentAsync(actor, CancellationToken.None);
        Assert.Equal(replies.Single(x => x.Status == 200).Assignment, current);
        var hiddenScope = Actor(reviewer, Guid.NewGuid());
        var denied = await Assert.ThrowsAsync<CheckingOperationException>(() => Work(verify).MyAssignmentAsync(hiddenScope, CancellationToken.None));
        Assert.Equal(403, denied.Status); // never hides occupied work as 204
    }

    [Fact]
    public async Task Empty_and_busy_receipts_are_stable_and_conflicting_payload_does_not_take_work()
    {
        var reviewer = Guid.NewGuid(); var tournament = Guid.NewGuid();
        await SeedScope(reviewer, tournament); await SeedScope(reviewer, tournament, ReviewerRole.Senior);
        var actor = Actor(reviewer, tournament);
        await using var db = fixture.Open(); var work = Work(db);
        Assert.Equal(204, (await work.TakeNextAsync(actor, tournament, "general", "empty", "one", CancellationToken.None)).Status);
        var prepared = await Seed(tournament);
        Assert.Equal(204, (await work.TakeNextAsync(actor, tournament, "general", "empty", "two", CancellationToken.None)).Status);
        Assert.Equal("IdempotencyConflict", (await work.TakeNextAsync(actor, tournament, "senior", "empty", "three", CancellationToken.None)).ErrorCode);
        var assigned = await work.TakeNextAsync(actor, tournament, "general", "new", "four", CancellationToken.None);
        Assert.Equal(prepared.CheckId.ToString(), assigned.Assignment!.Check.CheckId);
        var busy = await work.TakeNextAsync(actor, tournament, "senior", "busy", "five", CancellationToken.None);
        Assert.Equal("ReviewerBusy", busy.ErrorCode);
        var row = await db.Checks.SingleAsync(x => x.CheckId == prepared.CheckId);
        var assignment = await db.Assignments.SingleAsync(x => x.CheckId == prepared.CheckId);
        row.State = CheckState.Completed; row.Outcome = CheckOutcome.Correct; row.Origin = DecisionOrigin.OrdinaryReview;
        row.CompletedAt = assignment.StartedAt.AddSeconds(1); assignment.State = AssignmentState.Completed; assignment.EndedAt = row.CompletedAt;
        await db.SaveChangesAsync();
        Assert.Equal(busy, await work.TakeNextAsync(actor, tournament, "senior", "busy", "six", CancellationToken.None));
        Assert.Equal(assigned, await work.TakeNextAsync(actor, tournament, "general", "new", "seven", CancellationToken.None));
        Assert.Null(await work.MyAssignmentAsync(actor, CancellationToken.None));
        var denied = await Assert.ThrowsAsync<CheckingOperationException>(() => work.MaterialAsync(actor, prepared.CheckId, CancellationToken.None));
        Assert.Equal(403, denied.Status);
    }

    [Fact]
    public async Task Queue_pagination_is_deterministic_bound_to_scope_and_contains_no_capability()
    {
        var tournament = Guid.NewGuid(); var reviewer = Guid.NewGuid();
        await SeedScope(reviewer, tournament);
        var expected = new List<Guid>();
        for (var i = 0; i < 5; i++) expected.Add((await Seed(tournament)).CheckId);
        await using var db = fixture.Open(); var work = Work(db); var actor = Actor(reviewer, tournament);
        var first = await work.QueueAsync(actor, tournament, "general", null, CancellationToken.None);
        Assert.Equal(2, first.Items.Count); Assert.NotNull(first.NextCursor);
        var all = first.Items.Select(x => x.CheckId).ToList(); var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = await work.QueueAsync(actor, tournament, "general", cursor, CancellationToken.None);
            all.AddRange(page.Items.Select(x => x.CheckId)); cursor = page.NextCursor;
        }
        Assert.Equal(expected.Select(x => x.ToString()).Order(StringComparer.Ordinal), all);
        Assert.DoesNotContain("capability", JsonSerializer.Serialize(first, CheckingJson.Options));
        await Assert.ThrowsAsync<CheckingOperationException>(() => work.QueueAsync(actor, tournament, "general", "bad", CancellationToken.None));
        await SeedScope(reviewer, tournament, ReviewerRole.Senior);
        await Assert.ThrowsAsync<CheckingOperationException>(() => work.QueueAsync(actor, tournament, "senior", first.NextCursor, CancellationToken.None));
    }

    [Fact]
    public async Task Material_is_for_current_owner_only_and_failures_do_not_change_the_check()
    {
        using var files = new PreparedFixtureFiles(); var catalog = new PreparedFixtureCatalog(files.DirectoryPath);
        var tournament = catalog.Solutions[0].Snapshot.TournamentId;
        var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        await SeedScope(owner, tournament); await SeedScope(other, tournament);
        await using var db = fixture.Open();
        var bootstrap = new AcceptedReferenceBootstrap(db, TimeProvider.System);
        foreach (var solution in catalog.Solutions) await bootstrap.EnsureAsync(solution.Snapshot, CancellationToken.None);
        var work = Work(db, new PreparedMaterialStore(catalog)); var actor = Actor(owner, tournament);
        var reply = await work.TakeNextAsync(actor, tournament, "general", "claim", "test", CancellationToken.None);
        var checkId = Guid.Parse(reply.Assignment!.Check.CheckId);
        await using (var stream = await work.MaterialAsync(actor, checkId, CancellationToken.None)) Assert.True(stream.Length > 0);
        var denied = await Assert.ThrowsAsync<CheckingOperationException>(() => work.MaterialAsync(Actor(other, tournament), checkId, CancellationToken.None));
        Assert.Equal(403, denied.Status);
        var view = await work.CheckAsync(Actor(other, tournament), checkId, CancellationToken.None);
        Assert.DoesNotContain("capability", JsonSerializer.Serialize(view, CheckingJson.Options));
        var hidden = await Assert.ThrowsAsync<CheckingOperationException>(() => work.CheckAsync(Actor(other, Guid.NewGuid()), checkId, CancellationToken.None));
        Assert.Equal(404, hidden.Status);
        var source = catalog.Solutions.Single(x => x.Snapshot.SolutionId.ToString() == reply.Assignment.Check.SolutionId);
        File.Delete(Path.Combine(files.DirectoryPath, source.RelativeFile));
        await Assert.ThrowsAsync<MaterialUnavailableException>(() => work.MaterialAsync(actor, checkId, CancellationToken.None));
        Assert.Equal(view, await work.CheckAsync(actor, checkId, CancellationToken.None));
    }

    [Fact]
    public async Task Senior_appeal_claim_preserves_basis_and_atomically_queues_assigned_fact_without_capability()
    {
        var original = await Seed(); var senior = Guid.NewGuid(); var selected = Guid.NewGuid();
        await SeedScope(senior, original.TournamentId, ReviewerRole.Senior);
        await SeedScope(selected, original.TournamentId);
        await using (var setup = fixture.Open())
        {
            var basis = await setup.Checks.SingleAsync(x => x.CheckId == original.CheckId);
            var firstAssignment = NewAssignment(basis, selected);
            await Assign(setup, basis, firstAssignment);
            basis.State = CheckState.Completed; basis.Outcome = CheckOutcome.Incorrect; basis.Origin = DecisionOrigin.OrdinaryReview;
            basis.CompletedAt = Now.AddSeconds(1); firstAssignment.State = AssignmentState.Completed; firstAssignment.EndedAt = basis.CompletedAt;
            await setup.SaveChangesAsync();
        }
        var appeal = new CheckRow { CheckId = Guid.NewGuid(), SolutionId = original.SolutionId, TournamentId = original.TournamentId,
            Kind = CheckKind.Appeal, State = CheckState.SeniorQueued, ReviewRevision = 2, Version = 3, CreatedAt = Now,
            AppealId = Guid.NewGuid(), RecheckRequestId = Guid.NewGuid(), ResultId = Guid.NewGuid(), ExpectedResultVersion = 1,
            BaseCheckId = original.CheckId, BasePublicationId = Guid.NewGuid(), SelectedReviewerId = selected,
            BaseOutcome = CheckOutcome.Incorrect, EscalationId = Guid.NewGuid() };
        await using var db = fixture.Open(); db.Add(appeal); await db.SaveChangesAsync();
        var reply = await Work(db).TakeNextAsync(Actor(senior, original.TournamentId), original.TournamentId, "senior", "claim", "command-id", CancellationToken.None);
        Assert.Equal(appeal.CheckId.ToString(), reply.Assignment!.Check.CheckId);
        Assert.Equal("SeniorAssigned", reply.Assignment.Check.State); Assert.Equal("4", reply.Assignment.Check.Version);
        var outbox = await db.Outbox.SingleAsync(x => x.CorrelationId == appeal.AppealId);
        Assert.DoesNotContain(reply.Assignment.Capability, outbox.Envelope);
        using var json = JsonDocument.Parse(outbox.Envelope); var payload = json.RootElement.GetProperty("payload");
        Assert.Equal(selected.ToString(), payload.GetProperty("selectedReviewerId").GetString());
        Assert.Equal(senior.ToString(), payload.GetProperty("reviewerId").GetString());
        Assert.Equal("SeniorEscalation", payload.GetProperty("assignmentKind").GetString());
        Assert.Equal(appeal.BasePublicationId.ToString(), payload.GetProperty("publicationId").GetString());
        var repeat = await Work(db).TakeNextAsync(Actor(senior, original.TournamentId), original.TournamentId, "senior", "claim", "another", CancellationToken.None);
        Assert.Equal(reply, repeat);
        Assert.Equal(1, await db.Outbox.CountAsync(x => x.CorrelationId == appeal.AppealId));
    }

    [Fact]
    public async Task Permissions_precede_replay_and_long_idempotency_keys_have_bounded_storage_representation()
    {
        var check = await Seed(); var reviewer = Guid.NewGuid(); await SeedScope(reviewer, check.TournamentId);
        await using var db = fixture.Open(); var work = Work(db); var actor = Actor(reviewer, check.TournamentId);
        var key = new string('k', 6000);
        var reply = await work.TakeNextAsync(actor, check.TournamentId, "general", key, "one", CancellationToken.None);
        Assert.Equal(200, reply.Status);
        Assert.Equal(reply, await work.TakeNextAsync(actor, check.TournamentId, "general", key, "two", CancellationToken.None));
        Assert.Equal(64, (await db.HttpIdempotency.SingleAsync(x => x.SubjectId == reviewer)).Key.Length);
        var revoked = new AuthenticatedSubject(reviewer.ToString(), "test", ["Jury"], [], null);
        Assert.Equal(403, (await Assert.ThrowsAsync<CheckingOperationException>(() => work.TakeNextAsync(revoked,
            check.TournamentId, "general", key, "three", CancellationToken.None))).Status);
        var ordinary = new AuthenticatedSubject(reviewer.ToString(), "test", ["Jury"], [check.TournamentId.ToString()], null);
        Assert.Equal(403, (await Assert.ThrowsAsync<CheckingOperationException>(() => work.QueueAsync(ordinary,
            check.TournamentId, "senior", null, CancellationToken.None))).Status);
    }

    [Fact]
    public async Task Failure_before_receipt_rolls_back_assignment_and_history_then_same_key_can_retry()
    {
        var check = await Seed(); var reviewer = Guid.NewGuid(); await SeedScope(reviewer, check.TournamentId);
        var actor = Actor(reviewer, check.TournamentId); var fault = new FailReceipt();
        await using (var crashing = new CheckingDbContext(new DbContextOptionsBuilder<CheckingDbContext>()
            .UseNpgsql(fixture.ConnectionString).AddInterceptors(fault).Options))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Work(crashing).TakeNextAsync(actor,
                check.TournamentId, "general", "lost", "one", CancellationToken.None));
        Assert.True(fault.Triggered);
        await using var verify = fixture.Open();
        var unchanged = await verify.Checks.SingleAsync(x => x.CheckId == check.CheckId);
        Assert.Equal(CheckState.Queued, unchanged.State); Assert.Equal(1, unchanged.Version);
        Assert.False(await verify.Assignments.AnyAsync(x => x.CheckId == check.CheckId));
        Assert.False(await verify.CheckHistory.AnyAsync(x => x.CheckId == check.CheckId));
        Assert.False(await verify.HttpIdempotency.AnyAsync(x => x.SubjectId == reviewer));
        Assert.Equal(200, (await Work(verify).TakeNextAsync(actor, check.TournamentId, "general", "lost", "two", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Material_read_rechecks_ownership_after_IO_and_disposes_bytes_if_assignment_was_released()
    {
        var check = await Seed(); var reviewer = Guid.NewGuid(); await SeedScope(reviewer, check.TournamentId);
        var actor = Actor(reviewer, check.TournamentId);
        await using var db = fixture.Open();
        await Work(db).TakeNextAsync(actor, check.TournamentId, "general", "claim", "one", CancellationToken.None);
        var material = new MaterialDuringTransition(async () =>
        {
            await using var writer = fixture.Open();
            var row = await writer.Checks.SingleAsync(x => x.CheckId == check.CheckId);
            var assignment = await writer.Assignments.SingleAsync(x => x.CheckId == check.CheckId);
            row.State = CheckState.SeniorQueued; row.CurrentAssignmentId = null; row.EscalationId = Guid.NewGuid();
            assignment.State = AssignmentState.Refused; assignment.EndedAt = assignment.StartedAt.AddSeconds(1);
            await writer.SaveChangesAsync();
        });
        Assert.Equal(403, (await Assert.ThrowsAsync<CheckingOperationException>(() => Work(db, material)
            .MaterialAsync(actor, check.CheckId, CancellationToken.None))).Status);
        Assert.False(material.Bytes.CanRead);
    }
}
