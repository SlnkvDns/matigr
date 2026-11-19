using Microsoft.EntityFrameworkCore;

namespace Matigr.Appeals.Infrastructure;

public sealed class AppealsDbContext(DbContextOptions<AppealsDbContext> options) : DbContext(options)
{
    internal DbSet<SubmissionWindow> SubmissionWindows => Set<SubmissionWindow>();
    internal DbSet<AppealRow> Appeals => Set<AppealRow>();
    internal DbSet<AttemptRow> Attempts => Set<AttemptRow>();
    internal DbSet<HistoryRow> History => Set<HistoryRow>();
    internal DbSet<EndFactRow> EndFacts => Set<EndFactRow>();
    internal DbSet<HttpReceiptRow> HttpReceipts => Set<HttpReceiptRow>();
    internal DbSet<InboxRow> Inbox => Set<InboxRow>();
    internal DbSet<OutboxRow> Outbox => Set<OutboxRow>();
    internal DbSet<FailureRow> Failures => Set<FailureRow>();
    public override int SaveChanges(bool acceptAllChangesOnSuccess) { Guard(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    { Guard(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
    private void Guard()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries()) {
            if (entry.Entity is HistoryRow or EndFactRow or HttpReceiptRow or RedriveRow && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Appeal audit and command receipts are immutable.");
            if (entry.Entity is AppealRow && entry.State == EntityState.Deleted) throw new InvalidOperationException("Permanent appeal quota cannot be deleted.");
            if (entry.Entity is AppealRow && entry.State == EntityState.Modified) {
                string[] frozen = [nameof(AppealRow.TournamentId), nameof(AppealRow.TeamId), nameof(AppealRow.TaskId), nameof(AppealRow.ResultId),
                    nameof(AppealRow.ExpectedResultVersion), nameof(AppealRow.SolutionId), nameof(AppealRow.MaterialVersionId), nameof(AppealRow.BaseCheckId),
                    nameof(AppealRow.BasePublicationId), nameof(AppealRow.Basis), nameof(AppealRow.SubmittedAt), nameof(AppealRow.WindowVersion)];
                if (frozen.Any(x => entry.Property(x).IsModified)) throw new InvalidOperationException("Appeal admission and basis are immutable.");
            }
            if (entry.Entity is AttemptRow && entry.State == EntityState.Modified
                && new[] { nameof(AttemptRow.AppealId), nameof(AttemptRow.ReviewerId), nameof(AttemptRow.AcceptedAt), nameof(AttemptRow.PayloadHash) }.Any(x => entry.Property(x).IsModified))
                throw new InvalidOperationException("Selection identity is immutable.");
            if (entry.Entity is SubmissionWindow && entry.State == EntityState.Modified
                && entry.OriginalValues.GetValue<string>(nameof(SubmissionWindow.State)) == "Closed") throw new InvalidOperationException("Submission cannot reopen.");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SubmissionWindow>(entity =>
        {
            entity.ToTable("submission_windows", table => table.HasCheckConstraint(
                "ck_submission_windows_state", "state IN ('Open', 'Closed')"));
            entity.HasKey(window => window.TournamentId);
            entity.Property(window => window.TournamentId).HasColumnName("tournament_id");
            entity.Property(window => window.State).HasColumnName("state").HasMaxLength(16).IsRequired();
            entity.Property(window => window.Version).HasColumnName("version").IsRequired();
            entity.Property(window => window.EndFactId).HasColumnName("end_fact_id");
            entity.Property(window => window.EndedBy).HasColumnName("ended_by");
            entity.Property(window => window.EndedAt).HasColumnName("ended_at");
            entity.Property(window => window.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<AppealRow>(e => {
            e.ToTable("appeals", t => {
                t.HasCheckConstraint("ck_appeal_version", "version > 0 AND expected_result_version > 0 AND window_version > 0");
                t.HasCheckConstraint("ck_appeal_state", "state IN ('Registered','AcceptedAwaitingRecheck','RecheckPending','AwaitingSeniorReview','AwaitingResultApplication','Closed','NeedsResolution')");
                t.HasCheckConstraint("ck_appeal_final", "(state = 'Closed') = (final_publication_id IS NOT NULL)");
            });
            e.HasKey(x => x.AppealId); e.HasIndex(x => new { x.TournamentId, x.TeamId, x.TaskId }).IsUnique();
            e.HasIndex(x => new { x.TournamentId, x.State, x.SubmittedAt, x.AppealId }); e.HasIndex(x => new { x.TeamId, x.TournamentId });
            e.HasIndex(x => x.CurrentRequestId); e.HasIndex(x => x.CheckId);
            e.Property(x => x.Version).IsConcurrencyToken(); e.Property(x => x.Basis).HasColumnType("jsonb"); e.Property(x => x.Snapshot).HasColumnType("jsonb");
            e.HasOne<SubmissionWindow>().WithMany().HasForeignKey(x => x.TournamentId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AttemptRow>(e => {
            e.ToTable("selection_attempts", t => t.HasCheckConstraint("ck_attempt_state", "state IN ('Pending','Assigned','RejectedBusy','RejectedConflict')"));
            e.HasKey(x => x.RequestId); e.HasIndex(x => x.AppealId).IsUnique().HasFilter("state IN ('Pending','Assigned')");
            e.HasOne<AppealRow>().WithMany().HasForeignKey(x => x.AppealId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<HistoryRow>(e => { e.ToTable("appeal_history"); e.HasKey(x => x.HistoryId);
            e.HasIndex(x => new { x.AppealId, x.Version }).IsUnique(); e.Property(x => x.Snapshot).HasColumnType("jsonb");
            e.HasOne<AppealRow>().WithMany().HasForeignKey(x => x.AppealId).OnDelete(DeleteBehavior.Restrict); });
        modelBuilder.Entity<EndFactRow>(e => { e.ToTable("manual_end_facts"); e.HasKey(x => x.FactId); e.Property(x => x.Response).HasColumnType("jsonb");
            e.HasOne<SubmissionWindow>().WithMany().HasForeignKey(x => x.TournamentId).OnDelete(DeleteBehavior.Restrict); });
        modelBuilder.Entity<HttpReceiptRow>(e => { e.ToTable("http_idempotency"); e.HasKey(x => new { x.SubjectId, x.Operation, x.Key }); e.Property(x => x.Response).HasColumnType("jsonb"); });
        modelBuilder.Entity<InboxRow>(e => { e.ToTable("inbox"); e.HasKey(x => new { x.Consumer, x.MessageId }); });
        modelBuilder.Entity<OutboxRow>(e => { e.ToTable("outbox", t => t.HasCheckConstraint("ck_outbox_state", "attempts >= 0 AND ((status='Pending' AND sent_at IS NULL) OR (status='Sent' AND sent_at IS NOT NULL))"));
            e.HasKey(x => x.MessageId); e.Property(x => x.Envelope).HasColumnType("jsonb"); e.HasIndex(x => new { x.Status, x.NextAttemptAt }); });
        modelBuilder.Entity<FailureRow>(e => { e.ToTable("processing_failures", t => t.HasCheckConstraint("ck_failure_state", "state IN ('Deferred','Quarantined','Resolved') AND attempts >= 0"));
            e.HasKey(x => x.FailureId); e.Property(x => x.Envelope).HasColumnType("text"); e.HasIndex(x => new { x.Consumer, x.MessageId, x.PayloadHash }).IsUnique(); e.HasIndex(x => new { x.State, x.NextAttemptAt }); });
        modelBuilder.Entity<RedriveRow>(e => { e.ToTable("transport_redrives"); e.HasKey(x => x.RedriveId);
            e.HasOne<FailureRow>().WithMany().HasForeignKey(x => x.FailureId).OnDelete(DeleteBehavior.Restrict); });
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
            property.SetColumnName(string.Concat(property.Name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString())));
    }
}

internal sealed class SubmissionWindow
{
    public Guid TournamentId { get; set; }
    public string State { get; set; } = "Open";
    public long Version { get; set; } = 1;
    public Guid? EndFactId { get; set; }
    public Guid? EndedBy { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}
