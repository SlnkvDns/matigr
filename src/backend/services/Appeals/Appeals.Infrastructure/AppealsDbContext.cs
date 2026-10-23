using Microsoft.EntityFrameworkCore;

namespace Matigr.Appeals.Infrastructure;

public sealed class AppealsDbContext(DbContextOptions<AppealsDbContext> options) : DbContext(options)
{
    internal DbSet<SubmissionWindow> SubmissionWindows => Set<SubmissionWindow>();

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
        });
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
