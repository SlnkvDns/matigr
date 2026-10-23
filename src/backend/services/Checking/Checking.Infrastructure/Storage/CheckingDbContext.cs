using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Matigr.Checking.Infrastructure.Storage;

public sealed class CheckingDbContext(DbContextOptions<CheckingDbContext> options) : DbContext(options)
{
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void PrepareWrites()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries<SolutionReferenceRow>())
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Accepted solution snapshots are immutable.");
        foreach (var entry in ChangeTracker.Entries<CheckRow>().Where(x => x.State == EntityState.Modified))
        {
            if (entry.OriginalValues.GetValue<Domain.CheckState>(nameof(CheckRow.State)) == Domain.CheckState.Completed)
                throw new InvalidOperationException("Completed checks are immutable.");
            string[] immutable = [nameof(CheckRow.SolutionId), nameof(CheckRow.TournamentId), nameof(CheckRow.Kind),
                nameof(CheckRow.ReviewRevision), nameof(CheckRow.CreatedAt), nameof(CheckRow.AppealId),
                nameof(CheckRow.RecheckRequestId), nameof(CheckRow.ResultId), nameof(CheckRow.ExpectedResultVersion),
                nameof(CheckRow.BaseCheckId), nameof(CheckRow.BasePublicationId), nameof(CheckRow.SelectedReviewerId), nameof(CheckRow.BaseOutcome)];
            if (immutable.Any(name => entry.Property(name).IsModified))
                throw new InvalidOperationException("A check's solution and appeal basis are immutable.");
            entry.Entity.Version = checked(entry.OriginalValues.GetValue<long>(nameof(CheckRow.Version)) + 1);
        }
    }

    internal DbSet<SolutionReferenceRow> SolutionReferences => Set<SolutionReferenceRow>();
    internal DbSet<CheckRow> Checks => Set<CheckRow>();
    internal DbSet<AssignmentRow> Assignments => Set<AssignmentRow>();
    internal DbSet<ReviewerScopeRow> ReviewerScopes => Set<ReviewerScopeRow>();
    internal DbSet<CheckHistoryRow> CheckHistory => Set<CheckHistoryRow>();
    internal DbSet<RecheckRequestReceiptRow> RecheckRequestReceipts => Set<RecheckRequestReceiptRow>();
    internal DbSet<HttpIdempotencyRow> HttpIdempotency => Set<HttpIdempotencyRow>();
    internal DbSet<InboxRow> Inbox => Set<InboxRow>();
    internal DbSet<OutboxRow> Outbox => Set<OutboxRow>();
    internal DbSet<ProcessingFailureRow> ProcessingFailures => Set<ProcessingFailureRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<SolutionReferenceRow>(e =>
        {
            e.ToTable("solution_references", t => t.HasCheckConstraint("ck_solution_source_version", "source_version > 0"));
            e.HasKey(x => x.SolutionId);
            e.HasAlternateKey(x => new { x.SolutionId, x.TournamentId });
            e.HasIndex(x => new { x.TournamentId, x.TeamId, x.TaskId }).IsUnique();
            e.Property(x => x.MaterialRef).IsRequired();
        });
        model.Entity<CheckRow>(e =>
        {
            e.ToTable("checks", t =>
            {
                t.HasCheckConstraint("ck_check_version", "version > 0 AND review_revision > 0");
                t.HasCheckConstraint("ck_check_kind_basis", """
                    (kind = 'Initial' AND appeal_id IS NULL AND recheck_request_id IS NULL AND result_id IS NULL
                      AND expected_result_version IS NULL AND base_check_id IS NULL AND base_publication_id IS NULL
                      AND selected_reviewer_id IS NULL AND base_outcome IS NULL)
                    OR (kind = 'Appeal' AND appeal_id IS NOT NULL AND recheck_request_id IS NOT NULL AND result_id IS NOT NULL
                      AND expected_result_version IS NOT NULL AND expected_result_version > 0 AND base_check_id IS NOT NULL
                      AND base_check_id <> check_id AND base_publication_id IS NOT NULL AND selected_reviewer_id IS NOT NULL
                      AND base_outcome IS NOT NULL AND base_outcome IN ('Correct', 'Incorrect') AND state <> 'Queued')
                    """);
                t.HasCheckConstraint("ck_check_state", "state IN ('Queued','Assigned','SeniorQueued','SeniorAssigned','Completed')");
                t.HasCheckConstraint("ck_check_assignment", "(state IN ('Assigned','SeniorAssigned','Completed')) = (current_assignment_id IS NOT NULL)");
                t.HasCheckConstraint("ck_check_completion", """
                    (state = 'Completed' AND outcome IS NOT NULL AND outcome IN ('Correct','Incorrect')
                     AND origin IS NOT NULL AND completed_at IS NOT NULL AND completed_at >= created_at
                     AND ((origin = 'OrdinaryReview' AND kind = 'Initial' AND escalation_id IS NULL)
                       OR (origin = 'AppealReview' AND kind = 'Appeal' AND escalation_id IS NULL)
                       OR (origin = 'SeniorEscalation' AND escalation_id IS NOT NULL)))
                    OR (state <> 'Completed' AND outcome IS NULL AND origin IS NULL AND completed_at IS NULL)
                    """);
                t.HasCheckConstraint("ck_check_escalation", "state NOT IN ('SeniorQueued','SeniorAssigned') OR escalation_id IS NOT NULL");
                t.HasCheckConstraint("ck_check_ordinary", "state NOT IN ('Queued','Assigned') OR escalation_id IS NULL");
            });
            e.HasKey(x => x.CheckId);
            e.HasAlternateKey(x => new { x.CheckId, x.TournamentId });
            e.HasOne<SolutionReferenceRow>().WithMany().HasForeignKey(x => new { x.SolutionId, x.TournamentId })
                .HasPrincipalKey(x => new { x.SolutionId, x.TournamentId })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CheckRow>().WithMany().HasForeignKey(x => x.BaseCheckId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SolutionId, x.ReviewRevision }).IsUnique();
            e.HasIndex(x => x.SolutionId).IsUnique().HasFilter("kind = 'Initial'");
            e.HasIndex(x => x.AppealId).IsUnique().HasFilter("kind = 'Appeal'");
            e.HasIndex(x => new { x.State, x.TournamentId, x.CreatedAt, x.CheckId });
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.Kind).HasConversion<string>();
            e.Property(x => x.State).HasConversion<string>();
            e.Property(x => x.Outcome).HasConversion<string>();
            e.Property(x => x.Origin).HasConversion<string>();
            e.Property(x => x.BaseOutcome).HasConversion<string>();
        });
        model.Entity<ReviewerScopeRow>(e =>
        {
            e.ToTable("reviewer_scopes", t => t.HasCheckConstraint("ck_reviewer_scope_role", "role IN ('Reviewer','Senior')"));
            e.HasKey(x => new { x.ReviewerId, x.TournamentId, x.Role });
            e.Property(x => x.Role).HasConversion<string>();
        });
        model.Entity<AssignmentRow>(e =>
        {
            e.ToTable("assignments", t =>
            {
                t.HasCheckConstraint("ck_assignment_state_time", """
                    (state = 'Active' AND ended_at IS NULL)
                    OR (state IN ('Completed','Refused') AND ended_at IS NOT NULL AND ended_at >= started_at)
                    """);
                t.HasCheckConstraint("ck_assignment_capability", "capability_hash ~ '^[a-f0-9]{64}$' AND length(capability_ciphertext) > 0");
            });
            e.HasKey(x => x.AssignmentId);
            e.HasAlternateKey(x => new { x.CheckId, x.AssignmentId });
            e.HasOne<CheckRow>().WithMany().HasForeignKey(x => new { x.CheckId, x.TournamentId })
                .HasPrincipalKey(x => new { x.CheckId, x.TournamentId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ReviewerScopeRow>().WithMany().HasForeignKey(x => new { x.ReviewerId, x.TournamentId, x.Role }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ReviewerId).IsUnique().HasFilter("state = 'Active'");
            e.HasIndex(x => x.CheckId).IsUnique().HasFilter("state = 'Active'");
            e.Property(x => x.State).HasConversion<string>();
            e.Property(x => x.Role).HasConversion<string>();
            e.Property(x => x.CapabilityHash).IsRequired();
            e.Property(x => x.CapabilityCiphertext).IsRequired();
        });
        model.Entity<CheckHistoryRow>(e =>
        {
            e.ToTable("check_history", t => t.HasCheckConstraint("ck_history_version", "version > 0"));
            e.HasKey(x => x.HistoryId);
            e.HasOne<CheckRow>().WithMany().HasForeignKey(x => x.CheckId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.CheckId, x.Version, x.TransitionType }).IsUnique();
            e.Property(x => x.FromState).HasConversion<string>();
            e.Property(x => x.ToState).HasConversion<string>();
            e.Property(x => x.Outcome).HasConversion<string>();
        });
        model.Entity<RecheckRequestReceiptRow>(e =>
        {
            e.ToTable("recheck_request_receipts", t => t.HasCheckConstraint("ck_recheck_receipt_outcome", """
                (outcome = 'Assigned' AND check_id IS NOT NULL AND reason_code IS NULL)
                OR (outcome = 'Rejected' AND check_id IS NULL AND reason_code IS NOT NULL)
                """));
            e.HasKey(x => x.RequestId);
            e.HasOne<CheckRow>().WithMany().HasForeignKey(x => x.CheckId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.AppealId);
            e.Property(x => x.ResponseEnvelope).HasColumnType("jsonb");
        });
        model.Entity<HttpIdempotencyRow>(e =>
        {
            e.ToTable("http_idempotency", t => t.HasCheckConstraint("ck_http_status", "status_code BETWEEN 200 AND 599"));
            e.HasKey(x => new { x.SubjectId, x.Operation, x.Key });
        });
        model.Entity<InboxRow>(e =>
        {
            e.ToTable("inbox");
            e.HasKey(x => new { x.Consumer, x.MessageId });
        });
        model.Entity<OutboxRow>(e =>
        {
            e.ToTable("outbox", t =>
            {
                t.HasCheckConstraint("ck_outbox_delivery", "attempts >= 0 AND ((status = 'Pending' AND sent_at IS NULL) OR (status = 'Sent' AND sent_at IS NOT NULL))");
            });
            e.HasKey(x => x.MessageId);
            e.Property(x => x.Envelope).HasColumnType("jsonb");
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
        });
        model.Entity<ProcessingFailureRow>(e =>
        {
            e.ToTable("processing_failures", t => t.HasCheckConstraint("ck_failure_state", "state IN ('Deferred','Quarantined','Resolved') AND attempts >= 0"));
            e.HasKey(x => x.FailureId);
            e.HasIndex(x => new { x.State, x.NextAttemptAt });
            e.HasIndex(x => new { x.Consumer, x.MessageId });
        });

        // Explicit local naming, without a global/provider convention dependency.
        foreach (var entity in model.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
            property.SetColumnName(string.Concat(property.Name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString())));
        model.Entity<HttpIdempotencyRow>().Property(x => x.ETag).HasColumnName("etag");
    }
}
