using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Results.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialResultsStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inbox",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "text", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload_hash = table.Column<string>(type: "text", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox", x => new { x.consumer, x.message_id });
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    envelope = table.Column<string>(type: "jsonb", nullable: false),
                    routing_key = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lock_id = table.Column<Guid>(type: "uuid", nullable: true),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.message_id);
                    table.CheckConstraint("ck_outbox_state", "attempts >= 0 AND ((status = 'Pending' AND sent_at IS NULL) OR (status = 'Sent' AND sent_at IS NOT NULL))");
                });

            migrationBuilder.CreateTable(
                name: "processing_failures",
                columns: table => new
                {
                    failure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer = table.Column<string>(type: "text", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    envelope = table.Column<string>(type: "jsonb", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processing_failures", x => x.failure_id);
                    table.CheckConstraint("ck_failure_state", "state IN ('Deferred','Quarantined','Resolved') AND attempts >= 0");
                });

            migrationBuilder.CreateTable(
                name: "tournament_snapshots",
                columns: table => new
                {
                    tournament_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    manifest_hash = table.Column<string>(type: "text", nullable: false),
                    fixture_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tournament_snapshots", x => x.tournament_id);
                    table.CheckConstraint("ck_snapshot_version", "version >= 0 AND fixture_version > 0");
                });

            migrationBuilder.CreateTable(
                name: "tournament_tasks",
                columns: table => new
                {
                    tournament_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tournament_tasks", x => new { x.tournament_id, x.task_id });
                    table.ForeignKey(
                        name: "FK_tournament_tasks_tournament_snapshots_tournament_id",
                        column: x => x.tournament_id,
                        principalTable: "tournament_snapshots",
                        principalColumn: "tournament_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tournament_teams",
                columns: table => new
                {
                    tournament_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tournament_teams", x => new { x.tournament_id, x.team_id });
                    table.ForeignKey(
                        name: "FK_tournament_teams_tournament_snapshots_tournament_id",
                        column: x => x.tournament_id,
                        principalTable: "tournament_snapshots",
                        principalColumn: "tournament_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scoring_rules",
                columns: table => new
                {
                    tournament_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_version = table.Column<long>(type: "bigint", nullable: false),
                    points = table.Column<decimal>(type: "numeric(28,0)", precision: 28, scale: 0, nullable: false),
                    manifest_hash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scoring_rules", x => new { x.tournament_id, x.task_id, x.rule_version });
                    table.CheckConstraint("ck_rule_points", "points >= 0 AND rule_version > 0");
                    table.ForeignKey(
                        name: "FK_scoring_rules_tournament_tasks_tournament_id_task_id",
                        columns: x => new { x.tournament_id, x.task_id },
                        principalTable: "tournament_tasks",
                        principalColumns: new[] { "tournament_id", "task_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "team_task_results",
                columns: table => new
                {
                    result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tournament_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    solution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    material_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_version = table.Column<long>(type: "bigint", nullable: false),
                    score = table.Column<decimal>(type: "numeric(28,0)", precision: 28, scale: 0, nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    publication_id = table.Column<Guid>(type: "uuid", nullable: false),
                    publication = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_task_results", x => x.result_id);
                    table.CheckConstraint("ck_result_state", "result_version > 0 AND score >= 0 AND outcome IN ('Correct','Incorrect')");
                    table.ForeignKey(
                        name: "FK_team_task_results_tournament_tasks_tournament_id_task_id",
                        columns: x => new { x.tournament_id, x.task_id },
                        principalTable: "tournament_tasks",
                        principalColumns: new[] { "tournament_id", "task_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_team_task_results_tournament_teams_tournament_id_team_id",
                        columns: x => new { x.tournament_id, x.team_id },
                        principalTable: "tournament_teams",
                        principalColumns: new[] { "tournament_id", "team_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "evaluation_receipts",
                columns: table => new
                {
                    evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    semantic_hash = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    response = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evaluation_receipts", x => x.evaluation_id);
                    table.CheckConstraint("ck_receipt_status", "status IN ('Applied','Rejected')");
                    table.ForeignKey(
                        name: "FK_evaluation_receipts_team_task_results_result_id",
                        column: x => x.result_id,
                        principalTable: "team_task_results",
                        principalColumn: "result_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "result_publications",
                columns: table => new
                {
                    publication_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_version = table.Column<long>(type: "bigint", nullable: false),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_result_publications", x => x.publication_id);
                    table.ForeignKey(
                        name: "FK_result_publications_team_task_results_result_id",
                        column: x => x.result_id,
                        principalTable: "team_task_results",
                        principalColumn: "result_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_evaluation_receipts_result_id",
                table: "evaluation_receipts",
                column: "result_id");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_status_next_attempt_at",
                table: "outbox",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_processing_failures_state_next_attempt_at",
                table: "processing_failures",
                columns: new[] { "state", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_result_publications_result_id_result_version",
                table: "result_publications",
                columns: new[] { "result_id", "result_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_task_results_tournament_id_task_id",
                table: "team_task_results",
                columns: new[] { "tournament_id", "task_id" });

            migrationBuilder.CreateIndex(
                name: "IX_team_task_results_tournament_id_team_id_task_id",
                table: "team_task_results",
                columns: new[] { "tournament_id", "team_id", "task_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tournament_tasks_tournament_id_order",
                table: "tournament_tasks",
                columns: new[] { "tournament_id", "order" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evaluation_receipts");

            migrationBuilder.DropTable(
                name: "inbox");

            migrationBuilder.DropTable(
                name: "outbox");

            migrationBuilder.DropTable(
                name: "processing_failures");

            migrationBuilder.DropTable(
                name: "result_publications");

            migrationBuilder.DropTable(
                name: "scoring_rules");

            migrationBuilder.DropTable(
                name: "team_task_results");

            migrationBuilder.DropTable(
                name: "tournament_tasks");

            migrationBuilder.DropTable(
                name: "tournament_teams");

            migrationBuilder.DropTable(
                name: "tournament_snapshots");
        }
    }
}
