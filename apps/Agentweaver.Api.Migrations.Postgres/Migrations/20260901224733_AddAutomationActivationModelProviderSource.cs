using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationActivationModelProviderSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "byok_provider_id",
                table: "automation_activations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "model_provider_source",
                table: "automation_activations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Replace the CHECK constraint/immutability trigger from AddAutomationActivationSnapshots
            // so a BYOK-sourced snapshot (repository grant + byok_provider_id, no Copilot binding) is
            // accepted as a complete authority tuple too, and so byok_provider_id/model_provider_source
            // join the immutable set. All existing (GitHubCopilot-sourced, model_provider_source = 0)
            // rows still satisfy the updated CHECK's Copilot-tuple branch unchanged.
            migrationBuilder.Sql("""
                ALTER TABLE automation_activations
                    DROP CONSTRAINT "CK_automation_activations_snapshot_tuple";

                ALTER TABLE automation_activations
                    ADD CONSTRAINT "CK_automation_activations_snapshot_tuple"
                    CHECK (status <> 0 OR (
                        repository_grant_digest IS NOT NULL AND repository_grant_digest <> '' AND (
                            (model_provider_source = 1 AND
                                byok_provider_id IS NOT NULL AND byok_provider_id <> '' AND
                                (copilot_binding_id IS NULL OR copilot_binding_id = '') AND
                                (copilot_binding_grant_digest IS NULL OR copilot_binding_grant_digest = ''))
                            OR
                            (model_provider_source <> 1 AND
                                copilot_binding_id IS NOT NULL AND copilot_binding_id <> '' AND
                                copilot_binding_grant_digest IS NOT NULL AND copilot_binding_grant_digest <> '' AND
                                (byok_provider_id IS NULL OR byok_provider_id = '')))));

                CREATE OR REPLACE FUNCTION prevent_automation_activation_snapshot_mutation()
                RETURNS trigger AS $$
                BEGIN
                    IF NEW.project_id IS DISTINCT FROM OLD.project_id OR
                       NEW.installation_id IS DISTINCT FROM OLD.installation_id OR
                       NEW.repository_id IS DISTINCT FROM OLD.repository_id OR
                       NEW.repository_grant_digest IS DISTINCT FROM OLD.repository_grant_digest OR
                       NEW.copilot_binding_id IS DISTINCT FROM OLD.copilot_binding_id OR
                       NEW.copilot_binding_grant_digest IS DISTINCT FROM OLD.copilot_binding_grant_digest OR
                       NEW.byok_provider_id IS DISTINCT FROM OLD.byok_provider_id OR
                       NEW.model_provider_source IS DISTINCT FROM OLD.model_provider_source OR
                       NEW.automation_key IS DISTINCT FROM OLD.automation_key OR
                       NEW.activated_at IS DISTINCT FROM OLD.activated_at THEN
                        RAISE EXCEPTION 'Activation snapshot authority is immutable.';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Activation snapshot schema changes are forward-only.");
        }
    }
}
