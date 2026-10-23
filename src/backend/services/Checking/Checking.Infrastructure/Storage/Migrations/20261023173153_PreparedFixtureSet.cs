using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Checking.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class PreparedFixtureSet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fixture_sets",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    manifest_hash = table.Column<string>(type: "text", nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fixture_sets", x => x.name);
                    table.CheckConstraint("ck_fixture_set", "version > 0 AND manifest_hash ~ '^[a-f0-9]{64}$'");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fixture_sets");
        }
    }
}
