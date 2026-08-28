using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationActivationSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_automation_activations_project_id_installation_id_repository_id_automation_key",
                table: "automation_activations");

            migrationBuilder.AddColumn<string>(
                name: "copilot_binding_grant_digest",
                table: "automation_activations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "copilot_binding_id",
                table: "automation_activations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repository_grant_digest",
                table: "automation_activations",
                type: "TEXT",
                nullable: true);

            // Earlier activation rows have no binding/grant tuple and cannot be safely revived.
            migrationBuilder.Sql("""
                UPDATE automation_activations
                SET status = 2, invalidated_at = COALESCE(invalidated_at, CURRENT_TIMESTAMP)
                WHERE status = 0;

                CREATE TRIGGER TR_automation_activations_snapshot_insert
                BEFORE INSERT ON automation_activations
                WHEN NEW.status = 0 AND (
                    COALESCE(NEW.repository_grant_digest, '') = '' OR
                    COALESCE(NEW.copilot_binding_id, '') = '' OR
                    COALESCE(NEW.copilot_binding_grant_digest, '') = '')
                BEGIN
                    SELECT RAISE(ABORT, 'Activation snapshots require a complete authority tuple.');
                END;

                CREATE TRIGGER TR_automation_activations_snapshot_immutable
                BEFORE UPDATE ON automation_activations
                WHEN NEW.project_id IS NOT OLD.project_id OR
                     NEW.installation_id IS NOT OLD.installation_id OR
                     NEW.repository_id IS NOT OLD.repository_id OR
                     NEW.repository_grant_digest IS NOT OLD.repository_grant_digest OR
                     NEW.copilot_binding_id IS NOT OLD.copilot_binding_id OR
                     NEW.copilot_binding_grant_digest IS NOT OLD.copilot_binding_grant_digest OR
                     NEW.automation_key IS NOT OLD.automation_key OR
                     NEW.activated_at IS NOT OLD.activated_at
                BEGIN
                    SELECT RAISE(ABORT, 'Activation snapshot authority is immutable.');
                END;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_automation_activations_active_project",
                table: "automation_activations",
                column: "project_id",
                unique: true,
                filter: "status = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Activation snapshot schema changes are forward-only.");
        }
    }
}
