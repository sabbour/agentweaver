using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddUserModelProviderSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_copilot_bindings",
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
                    table.PrimaryKey("PK_user_copilot_bindings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_model_provider_settings",
                columns: table => new
                {
                    entra_object_id = table.Column<string>(type: "TEXT", nullable: false),
                    preference = table.Column<int>(type: "INTEGER", nullable: false),
                    byok_provider_id = table.Column<string>(type: "TEXT", nullable: true),
                    byok_name = table.Column<string>(type: "TEXT", nullable: true),
                    byok_type = table.Column<string>(type: "TEXT", nullable: true),
                    byok_base_url = table.Column<string>(type: "TEXT", nullable: true),
                    byok_model = table.Column<string>(type: "TEXT", nullable: true),
                    byok_wire_api = table.Column<string>(type: "TEXT", nullable: true),
                    byok_headers_json = table.Column<string>(type: "TEXT", nullable: true),
                    byok_azure_api_version = table.Column<string>(type: "TEXT", nullable: true),
                    byok_credential_reference = table.Column<string>(type: "TEXT", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_model_provider_settings", x => x.entra_object_id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_user_copilot_bindings_active_user",
                table: "user_copilot_bindings",
                column: "entra_object_id",
                unique: true,
                filter: "status = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_copilot_bindings");

            migrationBuilder.DropTable(
                name: "user_model_provider_settings");
        }
    }
}
