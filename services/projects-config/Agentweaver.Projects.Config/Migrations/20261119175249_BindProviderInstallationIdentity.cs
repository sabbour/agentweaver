using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class BindProviderInstallationIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "installation_id",
                schema: "projects_config",
                table: "model_provider_reference_reservations",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                CREATE TABLE projects_config.installation_identity (
                    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                    installation_id uuid NOT NULL CHECK (installation_id <> '00000000-0000-0000-0000-000000000000'::uuid)
                );
                INSERT INTO projects_config.installation_identity (singleton, installation_id)
                VALUES (true, pg_catalog.gen_random_uuid());

                ALTER TABLE projects_config.model_provider_reference_reservations
                    ADD CONSTRAINT ck_provider_reference_installation_id
                    CHECK (installation_id IS NOT NULL AND installation_id <> '00000000-0000-0000-0000-000000000000'::uuid)
                    NOT VALID;

                CREATE FUNCTION projects_config.guard_provider_reference_installation()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $function$
                DECLARE
                    current_installation uuid;
                BEGIN
                    PERFORM pg_catalog.pg_advisory_xact_lock(
                        pg_catalog.hashtextextended('agentweaver.projects_config.model_provider_create', 0));
                    IF pg_catalog.current_setting('transaction_isolation') <> 'read committed' THEN
                        RAISE EXCEPTION 'provider create requires read committed isolation'
                            USING ERRCODE = '40001';
                    END IF;
                    SELECT installation_id INTO current_installation
                    FROM projects_config.installation_identity WHERE singleton = true;
                    IF current_installation IS NULL OR NEW.installation_id IS NULL OR
                       NEW.installation_id <> current_installation THEN
                        RAISE EXCEPTION 'provider reference installation mismatch'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_provider_reference_installation_id';
                    END IF;
                    RETURN NEW;
                END;
                $function$;
                REVOKE ALL ON FUNCTION projects_config.guard_provider_reference_installation() FROM PUBLIC;
                CREATE TRIGGER model_provider_reference_installation_guard
                BEFORE INSERT ON projects_config.model_provider_reference_reservations
                FOR EACH ROW EXECUTE FUNCTION projects_config.guard_provider_reference_installation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $block$
                BEGIN
                    RAISE EXCEPTION 'installation_identity_must_be_retained' USING ERRCODE = '55000';
                END;
                $block$;
                """);
        }
    }
}
