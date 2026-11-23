using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Checking.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class TransportRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "redrive_count",
                table: "processing_failures",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "redriven_at",
                table: "processing_failures",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "routing_key",
                table: "processing_failures",
                type: "text",
                nullable: false,
                defaultValue: "");

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
                name: "IX_processing_failures_consumer_message_id_payload_hash",
                table: "processing_failures",
                columns: new[] { "consumer", "message_id", "payload_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_transport_redrives_failure_id",
                table: "transport_redrives",
                column: "failure_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "transport_redrives");

            migrationBuilder.DropIndex(
                name: "IX_processing_failures_consumer_message_id_payload_hash",
                table: "processing_failures");

            migrationBuilder.DropColumn(
                name: "redrive_count",
                table: "processing_failures");

            migrationBuilder.DropColumn(
                name: "redriven_at",
                table: "processing_failures");

            migrationBuilder.DropColumn(
                name: "routing_key",
                table: "processing_failures");
        }
    }
}
