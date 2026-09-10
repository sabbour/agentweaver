using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRunModelProviderSnapshotOwners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "run_model_provider_snapshot_owners",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    secret_reference = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_model_provider_snapshot_owners", x => x.run_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "run_model_provider_snapshot_owners");
        }
    }
}
