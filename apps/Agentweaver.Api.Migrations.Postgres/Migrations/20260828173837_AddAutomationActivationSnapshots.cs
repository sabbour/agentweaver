using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationActivationSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE old_constraint text;
                BEGIN
                    SELECT conname INTO old_constraint
                    FROM pg_constraint
                    WHERE conrelid = 'automation_activations'::regclass
                      AND contype = 'u'
                      AND pg_get_constraintdef(oid) LIKE
                          'UNIQUE (project_id, installation_id, repository_id, automation_key)%';
                    IF old_constraint IS NOT NULL THEN
                        EXECUTE format('ALTER TABLE automation_activations DROP CONSTRAINT %I', old_constraint);
                    END IF;
                END
                $$;
                """);

            migrationBuilder.AddColumn<string>(
                name: "copilot_binding_grant_digest",
                table: "automation_activations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "copilot_binding_id",
                table: "automation_activations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repository_grant_digest",
                table: "automation_activations",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE automation_activations
                SET status = 2, invalidated_at = COALESCE(invalidated_at, CURRENT_TIMESTAMP)
                WHERE status = 0;

                ALTER TABLE automation_activations
                    ADD CONSTRAINT "CK_automation_activations_snapshot_tuple"
                    CHECK (status <> 0 OR (
                        repository_grant_digest IS NOT NULL AND repository_grant_digest <> '' AND
                        copilot_binding_id IS NOT NULL AND copilot_binding_id <> '' AND
                        copilot_binding_grant_digest IS NOT NULL AND copilot_binding_grant_digest <> ''));

                CREATE FUNCTION prevent_automation_activation_snapshot_mutation()
                RETURNS trigger AS $$
                BEGIN
                    IF NEW.project_id IS DISTINCT FROM OLD.project_id OR
                       NEW.installation_id IS DISTINCT FROM OLD.installation_id OR
                       NEW.repository_id IS DISTINCT FROM OLD.repository_id OR
                       NEW.repository_grant_digest IS DISTINCT FROM OLD.repository_grant_digest OR
                       NEW.copilot_binding_id IS DISTINCT FROM OLD.copilot_binding_id OR
                       NEW.copilot_binding_grant_digest IS DISTINCT FROM OLD.copilot_binding_grant_digest OR
                       NEW.automation_key IS DISTINCT FROM OLD.automation_key OR
                       NEW.activated_at IS DISTINCT FROM OLD.activated_at THEN
                        RAISE EXCEPTION 'Activation snapshot authority is immutable.';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER TR_automation_activations_snapshot_immutable
                BEFORE UPDATE ON automation_activations
                FOR EACH ROW EXECUTE FUNCTION prevent_automation_activation_snapshot_mutation();
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
