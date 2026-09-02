using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AllowRepositorylessAutomationActivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS TR_automation_activations_snapshot_insert;
                DROP TRIGGER IF EXISTS TR_automation_activations_snapshot_immutable;
                """);

            migrationBuilder.AlterColumn<long>(
                name: "repository_id",
                table: "automation_activations",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<long>(
                name: "installation_id",
                table: "automation_activations",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.Sql("""
                CREATE TRIGGER TR_automation_activations_snapshot_insert
                BEFORE INSERT ON automation_activations
                WHEN NEW.status = 0 AND (
                    NOT (
                        (NEW.installation_id IS NULL AND NEW.repository_id IS NULL AND
                            COALESCE(NEW.repository_grant_digest, '') = '')
                        OR
                        (NEW.installation_id > 0 AND NEW.repository_id > 0 AND
                            COALESCE(NEW.repository_grant_digest, '') != '')
                    ) OR
                    (NEW.model_provider_source = 1 AND (
                        COALESCE(NEW.byok_provider_id, '') = '' OR
                        COALESCE(NEW.copilot_binding_id, '') != '' OR
                        COALESCE(NEW.copilot_binding_grant_digest, '') != '')) OR
                    (NEW.model_provider_source != 1 AND (
                        COALESCE(NEW.copilot_binding_id, '') = '' OR
                        COALESCE(NEW.copilot_binding_grant_digest, '') = '' OR
                        COALESCE(NEW.byok_provider_id, '') != '')))
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
                     NEW.byok_provider_id IS NOT OLD.byok_provider_id OR
                     NEW.model_provider_source IS NOT OLD.model_provider_source OR
                     NEW.automation_key IS NOT OLD.automation_key OR
                     NEW.activated_at IS NOT OLD.activated_at
                BEGIN
                    SELECT RAISE(ABORT, 'Activation snapshot authority is immutable.');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Repository-less activation schema changes are forward-only.");
        }
    }
}
