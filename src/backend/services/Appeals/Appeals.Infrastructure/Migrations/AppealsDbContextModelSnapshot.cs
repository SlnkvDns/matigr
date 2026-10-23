using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Matigr.Appeals.Infrastructure.Migrations;

[DbContext(typeof(AppealsDbContext))]
public sealed class AppealsDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasAnnotation("ProductVersion", "10.0.13")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

        modelBuilder.Entity<SubmissionWindow>(entity =>
        {
            entity.ToTable("submission_windows", table => table.HasCheckConstraint(
                "ck_submission_windows_state", "state IN ('Open', 'Closed')"));
            entity.HasKey(window => window.TournamentId);
            entity.Property(window => window.TournamentId).HasColumnName("tournament_id").HasColumnType("uuid");
            entity.Property(window => window.State).HasColumnName("state").HasColumnType("character varying(16)")
                .HasMaxLength(16).IsRequired();
            entity.Property(window => window.Version).HasColumnName("version").HasColumnType("bigint").IsRequired();
            entity.Property(window => window.EndFactId).HasColumnName("end_fact_id").HasColumnType("uuid");
            entity.Property(window => window.EndedBy).HasColumnName("ended_by").HasColumnType("uuid");
            entity.Property(window => window.EndedAt).HasColumnName("ended_at").HasColumnType("timestamp with time zone");
        });
    }
}
