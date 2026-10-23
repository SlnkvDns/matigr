using Microsoft.EntityFrameworkCore;

namespace Matigr.Results.Infrastructure;

public sealed class ResultsDbContext(DbContextOptions<ResultsDbContext> options) : DbContext(options)
{
    internal DbSet<ResultRow> Results => Set<ResultRow>();
    internal DbSet<PublicationRow> Publications => Set<PublicationRow>();
    internal DbSet<ReceiptRow> Receipts => Set<ReceiptRow>();
    internal DbSet<SnapshotRow> Snapshots => Set<SnapshotRow>();
    internal DbSet<TeamRow> Teams => Set<TeamRow>();
    internal DbSet<TaskRow> Tasks => Set<TaskRow>();
    internal DbSet<RuleRow> Rules => Set<RuleRow>();
    internal DbSet<InboxRow> Inbox => Set<InboxRow>();
    internal DbSet<OutboxRow> Outbox => Set<OutboxRow>();
    internal DbSet<FailureRow> Failures => Set<FailureRow>();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    { Guard(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    { Guard(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    private void Guard()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is RuleRow or TeamRow or TaskRow or PublicationRow or ReceiptRow
                && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Published history, receipts and fixture rules are immutable.");
            if (entry.Entity is ResultRow && entry.State == EntityState.Modified)
            {
                string[] frozen = [nameof(ResultRow.ResultId), nameof(ResultRow.TournamentId), nameof(ResultRow.TeamId),
                    nameof(ResultRow.TaskId), nameof(ResultRow.SolutionId), nameof(ResultRow.MaterialVersionId)];
                if (frozen.Any(x => entry.Property(x).IsModified)) throw new InvalidOperationException("Result identity is immutable.");
            }
        }
    }
    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<SnapshotRow>(e => {
            e.ToTable("tournament_snapshots", t => t.HasCheckConstraint("ck_snapshot_version", "version >= 0 AND fixture_version > 0"));
            e.HasKey(x => x.TournamentId); e.Property(x => x.Version).IsConcurrencyToken();
        });
        m.Entity<TeamRow>(e => { e.ToTable("tournament_teams"); e.HasKey(x => new { x.TournamentId, x.TeamId });
            e.HasOne<SnapshotRow>().WithMany().HasForeignKey(x => x.TournamentId).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<TaskRow>(e => { e.ToTable("tournament_tasks"); e.HasKey(x => new { x.TournamentId, x.TaskId });
            e.HasIndex(x => new { x.TournamentId, x.Order }).IsUnique();
            e.HasOne<SnapshotRow>().WithMany().HasForeignKey(x => x.TournamentId).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<RuleRow>(e => { e.ToTable("scoring_rules", t => t.HasCheckConstraint("ck_rule_points", "points >= 0 AND rule_version > 0"));
            e.HasKey(x => new { x.TournamentId, x.TaskId, x.RuleVersion }); e.Property(x => x.Points).HasPrecision(28, 0);
            e.HasOne<TaskRow>().WithMany().HasForeignKey(x => new { x.TournamentId, x.TaskId }).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<ResultRow>(e => {
            e.ToTable("team_task_results", t => t.HasCheckConstraint("ck_result_state", "result_version > 0 AND score >= 0 AND outcome IN ('Correct','Incorrect')"));
            e.HasKey(x => x.ResultId); e.HasIndex(x => new { x.TournamentId, x.TeamId, x.TaskId }).IsUnique();
            e.Property(x => x.Score).HasPrecision(28, 0); e.Property(x => x.ResultVersion).IsConcurrencyToken();
            e.Property(x => x.Publication).HasColumnType("jsonb");
            e.HasOne<TeamRow>().WithMany().HasForeignKey(x => new { x.TournamentId, x.TeamId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<TaskRow>().WithMany().HasForeignKey(x => new { x.TournamentId, x.TaskId }).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<PublicationRow>(e => { e.ToTable("result_publications"); e.HasKey(x => x.PublicationId);
            e.HasIndex(x => new { x.ResultId, x.ResultVersion }).IsUnique(); e.Property(x => x.Snapshot).HasColumnType("jsonb");
            e.HasOne<ResultRow>().WithMany().HasForeignKey(x => x.ResultId).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<ReceiptRow>(e => { e.ToTable("evaluation_receipts", t => t.HasCheckConstraint("ck_receipt_status", "status IN ('Applied','Rejected')"));
            e.HasKey(x => x.EvaluationId); e.Property(x => x.Response).HasColumnType("jsonb");
            e.HasOne<ResultRow>().WithMany().HasForeignKey(x => x.ResultId).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<InboxRow>(e => { e.ToTable("inbox"); e.HasKey(x => new { x.Consumer, x.MessageId }); });
        m.Entity<OutboxRow>(e => { e.ToTable("outbox", t => t.HasCheckConstraint("ck_outbox_state", "attempts >= 0 AND ((status = 'Pending' AND sent_at IS NULL) OR (status = 'Sent' AND sent_at IS NOT NULL))"));
            e.HasKey(x => x.MessageId); e.Property(x => x.Envelope).HasColumnType("jsonb"); e.HasIndex(x => new { x.Status, x.NextAttemptAt }); });
        m.Entity<FailureRow>(e => { e.ToTable("processing_failures", t => t.HasCheckConstraint("ck_failure_state", "state IN ('Deferred','Quarantined','Resolved') AND attempts >= 0"));
            e.HasKey(x => x.FailureId); e.Property(x => x.Envelope).HasColumnType("text"); e.HasIndex(x => new { x.State, x.NextAttemptAt });
            e.HasIndex(x => new { x.Consumer, x.MessageId, x.PayloadHash }).IsUnique(); });
        m.Entity<TransportRedriveRow>(e => { e.ToTable("transport_redrives"); e.HasKey(x => x.RedriveId);
            e.HasOne<FailureRow>().WithMany().HasForeignKey(x => x.FailureId).OnDelete(DeleteBehavior.Restrict); });
        foreach (var entity in m.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
            property.SetColumnName(string.Concat(property.Name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString())));
    }
}
