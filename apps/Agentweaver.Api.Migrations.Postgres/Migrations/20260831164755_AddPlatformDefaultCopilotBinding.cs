using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
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
                    id = table.Column<string>(type: "text", nullable: false),
                    entra_object_id = table.Column<string>(type: "text", nullable: false),
                    credential_reference = table.Column<string>(type: "text", nullable: false),
                    credential_version = table.Column<string>(type: "text", nullable: false),
                    grant_digest = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    bound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
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
