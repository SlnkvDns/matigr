using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Checking.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialCheckingStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "http_idempotency",
                columns: table => new
                {
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: false),
                    etag = table.Column<string>(type: "text", nullable: true),
                    response_ciphertext = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_http_idempotency", x => new { x.subject_id, x.operation, x.key });
                    table.CheckConstraint("ck_http_status", "status_code BETWEEN 200 AND 599");
                });

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
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.message_id);
                    table.CheckConstraint("ck_outbox_delivery", "attempts >= 0 AND ((status = 'Pending' AND sent_at IS NULL) OR (status = 'Sent' AND sent_at IS NOT NULL))");
                });

            migrationBuilder.CreateTable(
                name: "processing_failures",
                columns: table => new
                {
                    failure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer = table.Column<string>(type: "text", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload_hash = table.Column<string>(type: "text", nullable: false),
                    raw_envelope = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processing_failures", x => x.failure_id);
                    table.CheckConstraint("ck_failure_state", "state IN ('Deferred','Quarantined','Resolved') AND attempts >= 0");
                });

            migrationBuilder.CreateTable(
                name: "reviewer_scopes",
                columns: table => new
                {
                    reviewer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tournament_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reviewer_scopes", x => new { x.reviewer_id, x.tournament_id, x.role });
                    table.CheckConstraint("ck_reviewer_scope_role", "role IN ('Reviewer','Senior')");
                });

            migrationBuilder.CreateTable(
                name: "solution_references",
                columns: table => new
                {
                    solution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tournament_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    material_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    material_ref = table.Column<string>(type: "text", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_solution_references", x => x.solution_id);
                    table.UniqueConstraint("AK_solution_references_solution_id_tournament_id", x => new { x.solution_id, x.tournament_id });
                    table.CheckConstraint("ck_solution_source_version", "source_version > 0");
                });

            migrationBuilder.CreateTable(
                name: "checks",
                columns: table => new
                {
                    check_id = table.Column<Guid>(type: "uuid", nullable: false),
                    solution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tournament_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    review_revision = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    current_assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    origin = table.Column<string>(type: "text", nullable: true),
                    escalation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    appeal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recheck_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expected_result_version = table.Column<long>(type: "bigint", nullable: true),
                    base_check_id = table.Column<Guid>(type: "uuid", nullable: true),
                    base_publication_id = table.Column<Guid>(type: "uuid", nullable: true),
                    selected_reviewer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    base_outcome = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_checks", x => x.check_id);
                    table.UniqueConstraint("AK_checks_check_id_tournament_id", x => new { x.check_id, x.tournament_id });
                    table.CheckConstraint("ck_check_assignment", "(state IN ('Assigned','SeniorAssigned','Completed')) = (current_assignment_id IS NOT NULL)");
                    table.CheckConstraint("ck_check_completion", "(state = 'Completed' AND outcome IS NOT NULL AND outcome IN ('Correct','Incorrect')\n AND origin IS NOT NULL AND completed_at IS NOT NULL AND completed_at >= created_at\n AND ((origin = 'OrdinaryReview' AND kind = 'Initial' AND escalation_id IS NULL)\n   OR (origin = 'AppealReview' AND kind = 'Appeal' AND escalation_id IS NULL)\n   OR (origin = 'SeniorEscalation' AND escalation_id IS NOT NULL)))\nOR (state <> 'Completed' AND outcome IS NULL AND origin IS NULL AND completed_at IS NULL)");
                    table.CheckConstraint("ck_check_escalation", "state NOT IN ('SeniorQueued','SeniorAssigned') OR escalation_id IS NOT NULL");
                    table.CheckConstraint("ck_check_kind_basis", "(kind = 'Initial' AND appeal_id IS NULL AND recheck_request_id IS NULL AND result_id IS NULL\n  AND expected_result_version IS NULL AND base_check_id IS NULL AND base_publication_id IS NULL\n  AND selected_reviewer_id IS NULL AND base_outcome IS NULL)\nOR (kind = 'Appeal' AND appeal_id IS NOT NULL AND recheck_request_id IS NOT NULL AND result_id IS NOT NULL\n  AND expected_result_version IS NOT NULL AND expected_result_version > 0 AND base_check_id IS NOT NULL\n  AND base_check_id <> check_id AND base_publication_id IS NOT NULL AND selected_reviewer_id IS NOT NULL\n  AND base_outcome IS NOT NULL AND base_outcome IN ('Correct', 'Incorrect') AND state <> 'Queued')");
                    table.CheckConstraint("ck_check_ordinary", "state NOT IN ('Queued','Assigned') OR escalation_id IS NULL");
                    table.CheckConstraint("ck_check_state", "state IN ('Queued','Assigned','SeniorQueued','SeniorAssigned','Completed')");
                    table.CheckConstraint("ck_check_version", "version > 0 AND review_revision > 0");
                    table.ForeignKey(
                        name: "FK_checks_checks_base_check_id",
                        column: x => x.base_check_id,
                        principalTable: "checks",
                        principalColumn: "check_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_checks_solution_references_solution_id_tournament_id",
                        columns: x => new { x.solution_id, x.tournament_id },
                        principalTable: "solution_references",
                        principalColumns: new[] { "solution_id", "tournament_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "assignments",
                columns: table => new
                {
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    check_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tournament_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    capability_hash = table.Column<string>(type: "text", nullable: false),
                    capability_ciphertext = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assignments", x => x.assignment_id);
                    table.UniqueConstraint("AK_assignments_check_id_assignment_id", x => new { x.check_id, x.assignment_id });
                    table.CheckConstraint("ck_assignment_capability", "capability_hash ~ '^[a-f0-9]{64}$' AND length(capability_ciphertext) > 0");
                    table.CheckConstraint("ck_assignment_state_time", "(state = 'Active' AND ended_at IS NULL)\nOR (state IN ('Completed','Refused') AND ended_at IS NOT NULL AND ended_at >= started_at)");
                    table.ForeignKey(
                        name: "FK_assignments_checks_check_id_tournament_id",
                        columns: x => new { x.check_id, x.tournament_id },
                        principalTable: "checks",
                        principalColumns: new[] { "check_id", "tournament_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_assignments_reviewer_scopes_reviewer_id_tournament_id_role",
                        columns: x => new { x.reviewer_id, x.tournament_id, x.role },
                        principalTable: "reviewer_scopes",
                        principalColumns: new[] { "reviewer_id", "tournament_id", "role" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "check_history",
                columns: table => new
                {
                    history_id = table.Column<Guid>(type: "uuid", nullable: false),
                    check_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    transition_type = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    escalation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_state = table.Column<string>(type: "text", nullable: true),
                    to_state = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_check_history", x => x.history_id);
                    table.CheckConstraint("ck_history_version", "version > 0");
                    table.ForeignKey(
                        name: "FK_check_history_checks_check_id",
                        column: x => x.check_id,
                        principalTable: "checks",
                        principalColumn: "check_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recheck_request_receipts",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    appeal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    check_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason_code = table.Column<string>(type: "text", nullable: true),
                    response_envelope = table.Column<string>(type: "jsonb", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recheck_request_receipts", x => x.request_id);
                    table.CheckConstraint("ck_recheck_receipt_outcome", "(outcome = 'Assigned' AND check_id IS NOT NULL AND reason_code IS NULL)\nOR (outcome = 'Rejected' AND check_id IS NULL AND reason_code IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_recheck_request_receipts_checks_check_id",
                        column: x => x.check_id,
                        principalTable: "checks",
                        principalColumn: "check_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assignments_check_id",
                table: "assignments",
                column: "check_id",
                unique: true,
                filter: "state = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_assignments_check_id_tournament_id",
                table: "assignments",
                columns: new[] { "check_id", "tournament_id" });

            migrationBuilder.CreateIndex(
                name: "IX_assignments_reviewer_id",
                table: "assignments",
                column: "reviewer_id",
                unique: true,
                filter: "state = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_assignments_reviewer_id_tournament_id_role",
                table: "assignments",
                columns: new[] { "reviewer_id", "tournament_id", "role" });

            migrationBuilder.CreateIndex(
                name: "IX_check_history_check_id_version_transition_type",
                table: "check_history",
                columns: new[] { "check_id", "version", "transition_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_checks_appeal_id",
                table: "checks",
                column: "appeal_id",
                unique: true,
                filter: "kind = 'Appeal'");

            migrationBuilder.CreateIndex(
                name: "IX_checks_base_check_id",
                table: "checks",
                column: "base_check_id");

            migrationBuilder.CreateIndex(
                name: "IX_checks_solution_id",
                table: "checks",
                column: "solution_id",
                unique: true,
                filter: "kind = 'Initial'");

            migrationBuilder.CreateIndex(
                name: "IX_checks_solution_id_review_revision",
                table: "checks",
                columns: new[] { "solution_id", "review_revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_checks_solution_id_tournament_id",
                table: "checks",
                columns: new[] { "solution_id", "tournament_id" });

            migrationBuilder.CreateIndex(
                name: "IX_checks_state_tournament_id_created_at_check_id",
                table: "checks",
                columns: new[] { "state", "tournament_id", "created_at", "check_id" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_status_next_attempt_at",
                table: "outbox",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_processing_failures_consumer_message_id",
                table: "processing_failures",
                columns: new[] { "consumer", "message_id" });

            migrationBuilder.CreateIndex(
                name: "IX_processing_failures_state_next_attempt_at",
                table: "processing_failures",
                columns: new[] { "state", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_recheck_request_receipts_appeal_id",
                table: "recheck_request_receipts",
                column: "appeal_id");

            migrationBuilder.CreateIndex(
                name: "IX_recheck_request_receipts_check_id",
                table: "recheck_request_receipts",
                column: "check_id");

            migrationBuilder.CreateIndex(
                name: "IX_solution_references_tournament_id_team_id_task_id",
                table: "solution_references",
                columns: new[] { "tournament_id", "team_id", "task_id" },
                unique: true);

            // The pointer and its assignment are written in one transaction. Deferral permits
            // creation of an already Assigned appeal Check before inserting its Assignment.
            // Kept in SQL to avoid an EF insert-order cycle between checks and assignments.
            migrationBuilder.Sql("""
                ALTER TABLE checks ADD CONSTRAINT fk_checks_current_assignment
                FOREIGN KEY (check_id, current_assignment_id)
                REFERENCES assignments (check_id, assignment_id)
                DEFERRABLE INITIALLY DEFERRED;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE checks DROP CONSTRAINT fk_checks_current_assignment;");
            migrationBuilder.DropTable(
                name: "assignments");

            migrationBuilder.DropTable(
                name: "check_history");

            migrationBuilder.DropTable(
                name: "http_idempotency");

            migrationBuilder.DropTable(
                name: "inbox");

            migrationBuilder.DropTable(
                name: "outbox");

            migrationBuilder.DropTable(
                name: "processing_failures");

            migrationBuilder.DropTable(
                name: "recheck_request_receipts");

            migrationBuilder.DropTable(
                name: "reviewer_scopes");

            migrationBuilder.DropTable(
                name: "checks");

            migrationBuilder.DropTable(
                name: "solution_references");
        }
    }
}
