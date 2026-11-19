using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Appeals.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AppealLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "appeals",
                columns: table => new
                {
                    appeal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tournament_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_result_version = table.Column<long>(type: "bigint", nullable: false),
                    solution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    material_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_check_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_publication_id = table.Column<Guid>(type: "uuid", nullable: false),
                    final_publication_id = table.Column<Guid>(type: "uuid", nullable: true),
                    window_version = table.Column<long>(type: "bigint", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    basis = table.Column<string>(type: "jsonb", nullable: false),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    current_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    check_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appeals", x => x.appeal_id);
                    table.CheckConstraint("ck_appeal_final", "(state = 'Closed') = (final_publication_id IS NOT NULL)");
                    table.CheckConstraint("ck_appeal_state", "state IN ('Registered','AcceptedAwaitingRecheck','RecheckPending','AwaitingSeniorReview','AwaitingResultApplication','Closed','NeedsResolution')");
                    table.CheckConstraint("ck_appeal_version", "version > 0 AND expected_result_version > 0 AND window_version > 0");
                    table.ForeignKey(
                        name: "FK_appeals_submission_windows_tournament_id",
                        column: x => x.tournament_id,
                        principalTable: "submission_windows",
                        principalColumn: "tournament_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "http_idempotency",
                columns: table => new
                {
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: false),
                    response = table.Column<string>(type: "jsonb", nullable: false),
                    e_tag = table.Column<string>(type: "text", nullable: true),
                    location = table.Column<string>(type: "text", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_http_idempotency", x => new { x.subject_id, x.operation, x.key });
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
                name: "manual_end_facts",
                columns: table => new
                {
                    fact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tournament_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload_hash = table.Column<string>(type: "text", nullable: false),
                    response = table.Column<string>(type: "jsonb", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manual_end_facts", x => x.fact_id);
                    table.ForeignKey(
                        name: "FK_manual_end_facts_submission_windows_tournament_id",
                        column: x => x.tournament_id,
                        principalTable: "submission_windows",
                        principalColumn: "tournament_id",
                        onDelete: ReferentialAction.Restrict);
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
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.message_id);
                    table.CheckConstraint("ck_outbox_state", "attempts >= 0 AND ((status='Pending' AND sent_at IS NULL) OR (status='Sent' AND sent_at IS NOT NULL))");
                });

            migrationBuilder.CreateTable(
                name: "processing_failures",
                columns: table => new
                {
                    failure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer = table.Column<string>(type: "text", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload_hash = table.Column<string>(type: "text", nullable: false),
                    envelope = table.Column<string>(type: "text", nullable: false),
                    routing_key = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    redrive_count = table.Column<int>(type: "integer", nullable: false),
                    redriven_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processing_failures", x => x.failure_id);
                    table.CheckConstraint("ck_failure_state", "state IN ('Deferred','Quarantined','Resolved') AND attempts >= 0");
                });

            migrationBuilder.CreateTable(
                name: "appeal_history",
                columns: table => new
                {
                    history_id = table.Column<Guid>(type: "uuid", nullable: false),
                    appeal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    check_id = table.Column<Guid>(type: "uuid", nullable: true),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appeal_history", x => x.history_id);
                    table.ForeignKey(
                        name: "FK_appeal_history_appeals_appeal_id",
                        column: x => x.appeal_id,
                        principalTable: "appeals",
                        principalColumn: "appeal_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "selection_attempts",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    appeal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    payload_hash = table.Column<string>(type: "text", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_selection_attempts", x => x.request_id);
                    table.CheckConstraint("ck_attempt_state", "state IN ('Pending','Assigned','RejectedBusy','RejectedConflict')");
                    table.ForeignKey(
                        name: "FK_selection_attempts_appeals_appeal_id",
                        column: x => x.appeal_id,
                        principalTable: "appeals",
                        principalColumn: "appeal_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transport_redrives",
                columns: table => new
                {
                    redrive_id = table.Column<Guid>(type: "uuid", nullable: false),
                    failure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload_hash = table.Column<string>(type: "text", nullable: false),
                    redriven_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transport_redrives", x => x.redrive_id);
                    table.ForeignKey(
                        name: "FK_transport_redrives_processing_failures_failure_id",
                        column: x => x.failure_id,
                        principalTable: "processing_failures",
                        principalColumn: "failure_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_appeal_history_appeal_id_version",
                table: "appeal_history",
                columns: new[] { "appeal_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_appeals_check_id",
                table: "appeals",
                column: "check_id");

            migrationBuilder.CreateIndex(
                name: "IX_appeals_current_request_id",
                table: "appeals",
                column: "current_request_id");

            migrationBuilder.CreateIndex(
                name: "IX_appeals_team_id_tournament_id",
                table: "appeals",
                columns: new[] { "team_id", "tournament_id" });

            migrationBuilder.CreateIndex(
                name: "IX_appeals_tournament_id_state_submitted_at_appeal_id",
                table: "appeals",
                columns: new[] { "tournament_id", "state", "submitted_at", "appeal_id" });

            migrationBuilder.CreateIndex(
                name: "IX_appeals_tournament_id_team_id_task_id",
                table: "appeals",
                columns: new[] { "tournament_id", "team_id", "task_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_manual_end_facts_tournament_id",
                table: "manual_end_facts",
                column: "tournament_id");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_status_next_attempt_at",
                table: "outbox",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_processing_failures_consumer_message_id_payload_hash",
                table: "processing_failures",
                columns: new[] { "consumer", "message_id", "payload_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_processing_failures_state_next_attempt_at",
                table: "processing_failures",
                columns: new[] { "state", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_selection_attempts_appeal_id",
                table: "selection_attempts",
                column: "appeal_id",
                unique: true,
                filter: "state IN ('Pending','Assigned')");

            migrationBuilder.CreateIndex(
                name: "IX_transport_redrives_failure_id",
                table: "transport_redrives",
                column: "failure_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "appeal_history");

            migrationBuilder.DropTable(
                name: "http_idempotency");

            migrationBuilder.DropTable(
                name: "inbox");

            migrationBuilder.DropTable(
                name: "manual_end_facts");

            migrationBuilder.DropTable(
                name: "outbox");

            migrationBuilder.DropTable(
                name: "selection_attempts");

            migrationBuilder.DropTable(
                name: "transport_redrives");

            migrationBuilder.DropTable(
                name: "appeals");

            migrationBuilder.DropTable(
                name: "processing_failures");
        }
    }
}
