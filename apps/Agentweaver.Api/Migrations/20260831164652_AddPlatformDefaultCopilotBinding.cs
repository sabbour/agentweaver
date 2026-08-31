using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformDefaultCopilotBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_default_copilot_bindings",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    entra_object_id = table.Column<string>(type: "TEXT", nullable: false),
                    credential_reference = table.Column<string>(type: "TEXT", nullable: false),
                    credential_version = table.Column<string>(type: "TEXT", nullable: false),
                    grant_digest = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    bound_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    deactivated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_default_copilot_bindings", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_default_copilot_bindings");
        }
    }
}
