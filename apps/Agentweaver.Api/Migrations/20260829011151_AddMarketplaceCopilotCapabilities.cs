using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceCopilotCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "marketplace_copilot_capabilities",
                columns: table => new
                {
                    capability_ref = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    entra_object_id = table.Column<string>(type: "TEXT", nullable: false),
                    source_binding_id = table.Column<string>(type: "TEXT", nullable: false),
                    credential_reference = table.Column<string>(type: "TEXT", nullable: false),
                    credential_version = table.Column<string>(type: "TEXT", nullable: false),
                    grant_digest = table.Column<string>(type: "TEXT", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_copilot_capabilities", x => x.capability_ref);
                    table.ForeignKey(
                        name: "FK_marketplace_copilot_capabilities_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_copilot_capabilities_expiry",
                table: "marketplace_copilot_capabilities",
                columns: new[] { "project_id", "entra_object_id", "expires_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketplace_copilot_capabilities");
        }
    }
}
