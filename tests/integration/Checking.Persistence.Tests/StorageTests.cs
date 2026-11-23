using System.Security.Cryptography;
using Matigr.Checking.Domain;
using Matigr.Checking.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Checking.Persistence.Tests;

public sealed class DatabaseFixture : IAsyncLifetime
{
    public string ConnectionString { get; } = Environment.GetEnvironmentVariable("ConnectionStrings__CheckingStorageTest")
        ?? throw new InvalidOperationException("Use the isolated checking-storage-tests Compose project; a real PostgreSQL is required.");
    public string KeyPath { get; } = Environment.GetEnvironmentVariable("StorageProtection__KeyPath")
        ?? throw new InvalidOperationException("A persisted protection key is required.");
    public CheckingDbContext Open() => new(new DbContextOptionsBuilder<CheckingDbContext>().UseNpgsql(ConnectionString).Options);
    public async Task InitializeAsync()
    {
        // This credential/database belongs exclusively to the isolated test Compose project.
        if (new NpgsqlConnectionStringBuilder(ConnectionString).Database != "checking_storage_test")
            throw new InvalidOperationException("Refusing destructive migration tests outside checking_storage_test.");
        await using var db = Open();
        if (Environment.GetEnvironmentVariable("StorageTest__RestartRead") == "true")
        {
            Assert.Equal(3, (await db.Database.GetAppliedMigrationsAsync()).Count());
            return;
        }
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        Assert.Equal(3, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.False(db.Database.HasPendingModelChanges());
        await db.GetService<IMigrator>().MigrateAsync("0");
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync(); // repeat is harmless
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

// One fixture, no parallel migration reset; races below use separate connections explicitly.
public partial class StorageTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private async Task<CheckRow> Seed(Guid? tournament = null)
    {
        await using var db = fixture.Open();
        var solution = new SolutionReferenceRow
        {
            SolutionId = Guid.NewGuid(), TournamentId = tournament ?? Guid.NewGuid(), TeamId = Guid.NewGuid(),
            TaskId = Guid.NewGuid(), MaterialVersionId = Guid.NewGuid(), MaterialRef = "fixture://prepared-material",
            AcceptedAt = Now, SourceVersion = 1
        };
        var check = new CheckRow
        {
            CheckId = Guid.NewGuid(), SolutionId = solution.SolutionId, TournamentId = solution.TournamentId,
            Kind = CheckKind.Initial, ReviewRevision = 1, State = CheckState.Queued, CreatedAt = Now
        };
        db.AddRange(solution, check);
        await db.SaveChangesAsync();
        return check;
    }

    private async Task SeedScope(Guid reviewer, Guid tournament, ReviewerRole role = ReviewerRole.Reviewer)
    {
        await using var db = fixture.Open();
        db.Add(new ReviewerScopeRow { ReviewerId = reviewer, TournamentId = tournament, Role = role });
        await db.SaveChangesAsync();
    }

    private AssignmentRow NewAssignment(CheckRow check, Guid reviewer, ReviewerRole role = ReviewerRole.Reviewer)
    {
        var id = Guid.NewGuid();
        var protectedValue = new StorageProtection(fixture.KeyPath).IssueCapability(id, reviewer);
        return new AssignmentRow
        {
            AssignmentId = id, CheckId = check.CheckId, TournamentId = check.TournamentId, ReviewerId = reviewer,
            Role = role, State = AssignmentState.Active, StartedAt = Now,
            CapabilityHash = protectedValue.Hash, CapabilityCiphertext = protectedValue.Ciphertext
        };
    }

    private async Task Assign(CheckingDbContext db, CheckRow check, AssignmentRow assignment)
    {
        db.Add(assignment);
        check.CurrentAssignmentId = assignment.AssignmentId;
        check.State = assignment.Role == ReviewerRole.Senior ? CheckState.SeniorAssigned : CheckState.Assigned;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Partial_unique_constraints_reject_second_active_reviewer_and_second_active_check()
    {
        var first = await Seed();
        var other = await Seed(); // another tournament also shares the global reviewer limit
        var reviewer = Guid.NewGuid();
        var otherReviewer = Guid.NewGuid();
        await SeedScope(reviewer, first.TournamentId);
        await SeedScope(reviewer, other.TournamentId);
        await SeedScope(otherReviewer, first.TournamentId);
        await using (var db = fixture.Open())
        {
            var row = await db.Checks.SingleAsync(x => x.CheckId == first.CheckId);
            await Assign(db, row, NewAssignment(row, reviewer));
        }
        await using (var db = fixture.Open())
        {
            db.Add(NewAssignment(other, reviewer));
            await UniqueViolation(db, "IX_assignments_reviewer_id");
        }
        await using (var db = fixture.Open())
        {
            db.Add(NewAssignment(first, otherReviewer));
            await UniqueViolation(db, "IX_assignments_check_id");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_assignment_uses_actual_postgres_unique_wait_and_rollback(bool firstCommits)
    {
        var first = await Seed();
        var second = await Seed();
        var reviewer = Guid.NewGuid();
        await SeedScope(reviewer, first.TournamentId);
        await SeedScope(reviewer, second.TournamentId);
        await using var one = fixture.Open();
        await using var two = fixture.Open();
        await using var transaction = await one.Database.BeginTransactionAsync();
        one.Add(NewAssignment(first, reviewer));
        await one.SaveChangesAsync();
        await two.Database.OpenConnectionAsync();
        var pid = ((NpgsqlConnection)two.Database.GetDbConnection()).ProcessID;
        two.Add(NewAssignment(second, reviewer));
        var competing = two.SaveChangesAsync();
        // Synchronize on PostgreSQL's observed lock wait, not a fixed delay or mock barrier.
        await WaitForLock(pid);
        Assert.False(competing.IsCompleted);
        if (firstCommits)
        {
            await transaction.CommitAsync();
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => competing);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        else
        {
            await transaction.RollbackAsync();
            await competing;
        }
        await using var verify = fixture.Open();
        Assert.Single(await verify.Assignments.Where(x => x.ReviewerId == reviewer && x.State == AssignmentState.Active).ToListAsync());
    }

    private async Task WaitForLock(int pid)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT wait_event_type = 'Lock' FROM pg_stat_activity WHERE pid = $1", connection);
            command.Parameters.AddWithValue(pid);
            if (await command.ExecuteScalarAsync(deadline.Token) is true) return;
            await Task.Delay(10, deadline.Token);
        }
    }

    [Fact]
    public async Task Concurrent_state_writes_use_CAS_and_preserve_the_winning_version()
    {
        var check = await Seed();
        var reviewer = Guid.NewGuid();
        await SeedScope(reviewer, check.TournamentId);
        await using var one = fixture.Open();
        await using var two = fixture.Open();
        var winner = await one.Checks.SingleAsync(x => x.CheckId == check.CheckId);
        var stale = await two.Checks.SingleAsync(x => x.CheckId == check.CheckId);
        var assignment = NewAssignment(winner, reviewer);
        await Assign(one, winner, assignment);
        stale.State = CheckState.SeniorQueued;
        stale.EscalationId = Guid.NewGuid();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => two.SaveChangesAsync());
        await using var verify = fixture.Open();
        var saved = await verify.Checks.SingleAsync(x => x.CheckId == check.CheckId);
        Assert.Equal(2, saved.Version);
        Assert.Equal(CheckState.Assigned, saved.State);
        Assert.Equal(assignment.AssignmentId, saved.CurrentAssignmentId);
    }

    [Fact]
    public async Task Assignment_and_encrypted_HTTP_replay_survive_independent_context_and_key_instances()
    {
        var check = await Seed();
        var reviewer = Guid.NewGuid();
        await SeedScope(reviewer, check.TournamentId);
        var assignment = NewAssignment(check, reviewer);
        var protector = new StorageProtection(fixture.KeyPath);
        var token = protector.RestoreCapability(assignment.AssignmentId, reviewer, assignment.CapabilityHash, assignment.CapabilityCiphertext);
        await using (var db = fixture.Open())
        {
            var row = await db.Checks.SingleAsync(x => x.CheckId == check.CheckId);
            var response = "{\"capability\":\"" + token + "\"}";
            db.HttpIdempotency.Add(new HttpIdempotencyRow
            {
                SubjectId = reviewer, Operation = "TakeNext", Key = "lost-response", RequestHash = new string('a', 64),
                StatusCode = 200, ETag = "\"2\"", RecordedAt = Now,
                ResponseCiphertext = protector.ProtectHttpResponse(reviewer, "TakeNext", "lost-response", response)
            });
            await Assign(db, row, assignment);
        }
        await using var restarted = fixture.Open();
        var current = await restarted.Assignments.SingleAsync(x => x.ReviewerId == reviewer && x.State == AssignmentState.Active);
        var newProtector = new StorageProtection(fixture.KeyPath);
        Assert.Equal(token, newProtector.RestoreCapability(current.AssignmentId, reviewer, current.CapabilityHash, current.CapabilityCiphertext));
        Assert.Equal(check.CheckId, current.CheckId);
        var replay = await restarted.HttpIdempotency.SingleAsync(x => x.SubjectId == reviewer);
        Assert.DoesNotContain(token, replay.ResponseCiphertext);
        Assert.Contains(token, newProtector.RestoreHttpResponse(reviewer, "TakeNext", "lost-response", replay.ResponseCiphertext));
        Assert.Equal("\"2\"", replay.ETag);
    }

    [Fact]
    public async Task Rollback_does_not_leave_assignment_history_receipt_inbox_or_outbox()
    {
        var check = await Seed();
        var reviewer = Guid.NewGuid();
        var message = Guid.NewGuid();
        var request = Guid.NewGuid();
        await SeedScope(reviewer, check.TournamentId);
        await using (var db = fixture.Open())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var row = await db.Checks.SingleAsync(x => x.CheckId == check.CheckId);
            db.CheckHistory.Add(new CheckHistoryRow { HistoryId = Guid.NewGuid(), CheckId = check.CheckId,
                Version = 2, TransitionType = "Assigned", ToState = CheckState.Assigned, ActorId = reviewer, OccurredAt = Now });
            db.Inbox.Add(new InboxRow { Consumer = "test", MessageId = message, PayloadHash = new string('a', 64), ProcessedAt = Now });
            db.Outbox.Add(new OutboxRow { MessageId = message, Envelope = "{\"messageId\":\"" + message + "\"}",
                RoutingKey = "checking.test.v1", CorrelationId = request, CreatedAt = Now, NextAttemptAt = Now });
            db.RecheckRequestReceipts.Add(new RecheckRequestReceiptRow { RequestId = request, AppealId = Guid.NewGuid(),
                RequestHash = new string('a', 64), Outcome = "Assigned", CheckId = check.CheckId, RecordedAt = Now });
            db.HttpIdempotency.Add(new HttpIdempotencyRow { SubjectId = reviewer, Operation = "TakeNext", Key = "rollback",
                RequestHash = new string('a', 64), StatusCode = 200, RecordedAt = Now,
                ResponseCiphertext = new StorageProtection(fixture.KeyPath).ProtectHttpResponse(reviewer, "TakeNext", "rollback", "{}") });
            await Assign(db, row, NewAssignment(row, reviewer));
            await transaction.RollbackAsync();
        }
        await using var verify = fixture.Open();
        Assert.Equal(CheckState.Queued, (await verify.Checks.SingleAsync(x => x.CheckId == check.CheckId)).State);
        Assert.False(await verify.Assignments.AnyAsync(x => x.CheckId == check.CheckId));
        Assert.False(await verify.CheckHistory.AnyAsync(x => x.CheckId == check.CheckId));
        Assert.False(await verify.Inbox.AnyAsync(x => x.MessageId == message));
        Assert.False(await verify.Outbox.AnyAsync(x => x.MessageId == message));
        Assert.False(await verify.RecheckRequestReceipts.AnyAsync(x => x.RequestId == request));
        Assert.False(await verify.HttpIdempotency.AnyAsync(x => x.SubjectId == reviewer));
    }

    [Fact]
    public async Task Business_and_message_keys_reject_duplicates()
    {
        var check = await Seed();
        await using (var db = fixture.Open())
        {
            var solution = await db.SolutionReferences.AsNoTracking().SingleAsync(x => x.SolutionId == check.SolutionId);
            solution.SolutionId = Guid.NewGuid();
            db.Add(solution);
            await UniqueViolation(db, "IX_solution_references_tournament_id_team_id_task_id");
        }
        await using (var db = fixture.Open())
        {
            db.Add(new CheckRow { CheckId = Guid.NewGuid(), SolutionId = check.SolutionId, TournamentId = check.TournamentId,
                Kind = CheckKind.Initial, State = CheckState.Queued, ReviewRevision = 2, CreatedAt = Now });
            await UniqueViolation(db, "IX_checks_solution_id");
        }
        var message = Guid.NewGuid();
        await using (var db = fixture.Open())
        {
            db.Inbox.Add(new InboxRow { Consumer = "same-consumer", MessageId = message, PayloadHash = new string('a', 64), ProcessedAt = Now });
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.Open())
        {
            db.Inbox.Add(new InboxRow { Consumer = "same-consumer", MessageId = message, PayloadHash = new string('a', 64), ProcessedAt = Now });
            await UniqueViolation(db, "PK_inbox");
        }
        await using (var db = fixture.Open())
        {
            db.Inbox.Add(new InboxRow { Consumer = "different-consumer", MessageId = message, PayloadHash = new string('a', 64), ProcessedAt = Now });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Invalid_basis_state_and_cross_check_pointer_are_rejected_by_database()
    {
        var check = await Seed();
        await using (var db = fixture.Open())
        {
            db.Add(new CheckRow { CheckId = Guid.NewGuid(), SolutionId = check.SolutionId, TournamentId = check.TournamentId,
                Kind = CheckKind.Appeal, State = CheckState.Assigned, ReviewRevision = 2, CreatedAt = Now,
                CurrentAssignmentId = Guid.NewGuid() }); // missing immutable appeal basis
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        await using (var db = fixture.Open())
        {
            var row = await db.Checks.SingleAsync(x => x.CheckId == check.CheckId);
            row.State = CheckState.Assigned;
            row.CurrentAssignmentId = Guid.NewGuid();
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
    }

    [Fact]
    public async Task Refusal_releases_reviewer_but_retains_history_and_senior_waiting_check()
    {
        var original = await Seed();
        var next = await Seed();
        var reviewer = Guid.NewGuid();
        await SeedScope(reviewer, original.TournamentId);
        await SeedScope(reviewer, next.TournamentId);
        var assignment = NewAssignment(original, reviewer);
        await using (var db = fixture.Open())
            await Assign(db, await db.Checks.SingleAsync(x => x.CheckId == original.CheckId), assignment);
        await using (var db = fixture.Open())
        {
            var check = await db.Checks.SingleAsync(x => x.CheckId == original.CheckId);
            var active = await db.Assignments.SingleAsync(x => x.AssignmentId == assignment.AssignmentId);
            active.State = AssignmentState.Refused;
            active.EndedAt = Now.AddSeconds(1);
            check.State = CheckState.SeniorQueued;
            check.CurrentAssignmentId = null;
            check.EscalationId = Guid.NewGuid();
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.Open())
            await Assign(db, await db.Checks.SingleAsync(x => x.CheckId == next.CheckId), NewAssignment(next, reviewer));
        await using var verify = fixture.Open();
        var saved = await verify.Checks.SingleAsync(x => x.CheckId == original.CheckId);
        Assert.Equal(CheckState.SeniorQueued, saved.State);
        Assert.Null(saved.CurrentAssignmentId);
        Assert.Equal(AssignmentState.Refused, (await verify.Assignments.SingleAsync(x => x.AssignmentId == assignment.AssignmentId)).State);
        Assert.Single(await verify.Assignments.Where(x => x.ReviewerId == reviewer && x.State == AssignmentState.Active).ToListAsync());
    }

    [Theory]
    [InlineData("http", "PK_http_idempotency")]
    [InlineData("outbox", "PK_outbox")]
    [InlineData("request", "PK_recheck_request_receipts")]
    [InlineData("history", "IX_check_history_check_id_version_transition_type")]
    public async Task Durable_receipt_and_history_keys_are_unique(string kind, string constraint)
    {
        var check = await Seed();
        var id = Guid.NewGuid();
        object NewRow() => kind switch
        {
            "http" => new HttpIdempotencyRow { SubjectId = id, Operation = "TakeNext", Key = "same-key", RequestHash = new string('a', 64),
                StatusCode = 204, ResponseCiphertext = new StorageProtection(fixture.KeyPath).ProtectHttpResponse(id, "TakeNext", "same-key", ""), RecordedAt = Now },
            "outbox" => new OutboxRow { MessageId = id, Envelope = "{}", RoutingKey = "checking.test.v1", CorrelationId = id, CreatedAt = Now, NextAttemptAt = Now },
            "request" => new RecheckRequestReceiptRow { RequestId = id, AppealId = id, RequestHash = new string('a', 64),
                Outcome = "Rejected", ReasonCode = "ReviewerBusy", RecordedAt = Now },
            "history" => new CheckHistoryRow { HistoryId = Guid.NewGuid(), CheckId = check.CheckId, Version = 1,
                TransitionType = "Created", ToState = CheckState.Queued, OccurredAt = Now },
            _ => throw new InvalidOperationException()
        };
        await using (var db = fixture.Open())
        {
            db.Add(NewRow());
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.Open())
        {
            db.Add(NewRow());
            await UniqueViolation(db, constraint);
        }
    }

    [Fact]
    public async Task One_appeal_check_and_unique_review_revision_are_enforced()
    {
        var original = await Seed();
        var reviewer = Guid.NewGuid();
        await SeedScope(reviewer, original.TournamentId);
        await using (var db = fixture.Open())
        {
            var row = await db.Checks.SingleAsync(x => x.CheckId == original.CheckId);
            var initialAssignment = NewAssignment(row, reviewer);
            await Assign(db, row, initialAssignment);
            row.State = CheckState.Completed;
            row.Outcome = CheckOutcome.Incorrect;
            row.Origin = DecisionOrigin.OrdinaryReview;
            row.CompletedAt = Now.AddSeconds(1);
            initialAssignment.State = AssignmentState.Completed;
            initialAssignment.EndedAt = row.CompletedAt;
            await db.SaveChangesAsync();
        }
        var appealId = Guid.NewGuid();
        var appeal = new CheckRow
        {
            CheckId = Guid.NewGuid(), SolutionId = original.SolutionId, TournamentId = original.TournamentId,
            Kind = CheckKind.Appeal, ReviewRevision = 2, State = CheckState.Assigned, CreatedAt = Now,
            AppealId = appealId, RecheckRequestId = Guid.NewGuid(), SelectedReviewerId = reviewer,
            ResultId = Guid.NewGuid(), ExpectedResultVersion = 1, BaseCheckId = original.CheckId,
            BasePublicationId = Guid.NewGuid(), BaseOutcome = CheckOutcome.Incorrect
        };
        var assignment = NewAssignment(appeal, reviewer);
        appeal.CurrentAssignmentId = assignment.AssignmentId;
        await using (var db = fixture.Open())
        {
            db.AddRange(appeal, assignment);
            await db.SaveChangesAsync(); // deferrable pointer permits this insert ordering
        }
        await using (var db = fixture.Open())
        {
            var duplicate = await db.Checks.AsNoTracking().SingleAsync(x => x.CheckId == appeal.CheckId);
            duplicate.CheckId = Guid.NewGuid();
            duplicate.ReviewRevision = 3;
            duplicate.RecheckRequestId = Guid.NewGuid();
            db.Add(duplicate);
            await UniqueViolation(db, "IX_checks_appeal_id");
        }
        await using (var db = fixture.Open())
        {
            var duplicate = await db.Checks.AsNoTracking().SingleAsync(x => x.CheckId == appeal.CheckId);
            duplicate.CheckId = Guid.NewGuid();
            duplicate.AppealId = Guid.NewGuid();
            duplicate.RecheckRequestId = Guid.NewGuid();
            db.Add(duplicate);
            await UniqueViolation(db, "IX_checks_solution_id_review_revision");
        }
    }

    [Fact]
    public async Task Immutable_snapshots_completed_checks_and_appeal_basis_cannot_be_changed_by_EF_writes()
    {
        var check = await Seed();
        var reviewer = Guid.NewGuid();
        await SeedScope(reviewer, check.TournamentId);
        await using (var db = fixture.Open())
            await Assign(db, await db.Checks.SingleAsync(x => x.CheckId == check.CheckId), NewAssignment(check, reviewer));
        await using (var db = fixture.Open())
        {
            var row = await db.Checks.SingleAsync(x => x.CheckId == check.CheckId);
            var assignment = await db.Assignments.SingleAsync(x => x.CheckId == check.CheckId);
            row.State = CheckState.Completed;
            row.Outcome = CheckOutcome.Correct;
            row.Origin = DecisionOrigin.OrdinaryReview;
            row.CompletedAt = Now.AddSeconds(1);
            assignment.State = AssignmentState.Completed;
            assignment.EndedAt = row.CompletedAt;
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.Open())
        {
            var row = await db.Checks.SingleAsync(x => x.CheckId == check.CheckId);
            row.Outcome = CheckOutcome.Incorrect;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = fixture.Open())
        {
            var row = await db.SolutionReferences.SingleAsync(x => x.SolutionId == check.SolutionId);
            row.MaterialRef = "replaced";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        var queued = await Seed();
        await using (var db = fixture.Open())
        {
            var row = await db.Checks.SingleAsync(x => x.CheckId == queued.CheckId);
            row.SelectedReviewerId = reviewer;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
    }

    private static async Task UniqueViolation(CheckingDbContext db, string constraint)
    {
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal(constraint, postgres.ConstraintName);
    }

    [Fact]
    public async Task Capability_checkpoint_survives_process_restart()
    {
        var reviewer = Guid.Parse("7a39480a-c4f7-4cb9-90e8-b12e58561055");
        if (Environment.GetEnvironmentVariable("StorageTest__RestartRead") != "true")
        {
            var check = await Seed();
            await SeedScope(reviewer, check.TournamentId);
            var assignment = NewAssignment(check, reviewer);
            var protection = new StorageProtection(fixture.KeyPath);
            var original = protection.RestoreCapability(assignment.AssignmentId, reviewer, assignment.CapabilityHash, assignment.CapabilityCiphertext);
            await using var db = fixture.Open();
            db.HttpIdempotency.Add(new HttpIdempotencyRow { SubjectId = reviewer, Operation = "TakeNext", Key = "restart-checkpoint",
                RequestHash = new string('a', 64), StatusCode = 200, ETag = "\"2\"", RecordedAt = Now,
                ResponseCiphertext = protection.ProtectHttpResponse(reviewer, "TakeNext", "restart-checkpoint", original) });
            await Assign(db, await db.Checks.SingleAsync(x => x.CheckId == check.CheckId), assignment);
            return;
        }
        // The writer process has exited. Neither plaintext nor its protector survive in memory.
        await using var restarted = fixture.Open();
        var current = await restarted.Assignments.SingleAsync(x => x.ReviewerId == reviewer && x.State == AssignmentState.Active);
        var receipt = await restarted.HttpIdempotency.SingleAsync(x => x.SubjectId == reviewer);
        var restoredProtection = new StorageProtection(fixture.KeyPath);
        var token = restoredProtection.RestoreCapability(current.AssignmentId, reviewer, current.CapabilityHash, current.CapabilityCiphertext);
        Assert.Equal(token, restoredProtection.RestoreHttpResponse(reviewer, "TakeNext", "restart-checkpoint", receipt.ResponseCiphertext));
        var checkAfterRestart = await restarted.Checks.SingleAsync(x => x.CheckId == current.CheckId);
        Assert.Equal(CheckState.Assigned, checkAfterRestart.State);
        Assert.Equal(current.AssignmentId, checkAfterRestart.CurrentAssignmentId);
        Assert.Equal(2, checkAfterRestart.Version);
    }
}
