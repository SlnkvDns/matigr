using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Matigr.Appeals.Infrastructure.Migrations;

[DbContext(typeof(AppealsDbContext))]
[Migration("202610230001_InitialSubmissionWindows")]
public sealed class InitialSubmissionWindows : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "submission_windows",
        columns: table => new
        {
            tournament_id = table.Column<Guid>(type: "uuid", nullable: false),
            state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
            version = table.Column<long>(type: "bigint", nullable: false),
            end_fact_id = table.Column<Guid>(type: "uuid", nullable: true),
            ended_by = table.Column<Guid>(type: "uuid", nullable: true),
            ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
        },
        constraints: table =>
        {
            table.PrimaryKey("pk_submission_windows", x => x.tournament_id);
            table.CheckConstraint("ck_submission_windows_state", "state IN ('Open', 'Closed')");
        });

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("submission_windows");
}
