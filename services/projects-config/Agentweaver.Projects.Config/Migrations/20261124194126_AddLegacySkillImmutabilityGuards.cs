using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddLegacySkillImmutabilityGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION projects_config.require_skill_admission_writer()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    current_state text;
                BEGIN
                    SELECT state INTO current_state
                    FROM projects_config.project_skill_admission_fence
                    WHERE id = 1
                    FOR SHARE;

                    IF current_state = 'Open' THEN
                        RETURN NEW;
                    END IF;
                    IF current_state = 'Active'
                       AND current_setting('agentweaver.skill_admission_v1', true) = '1'
                       AND NEW.admission_classification <> 'LegacyThirdParty' THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'skill admission fence blocks imports';
                END;
                $function$;

                CREATE TRIGGER project_skill_content_revisions_immutable
                BEFORE UPDATE OR DELETE ON projects_config.project_skill_content_revisions
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                CREATE TRIGGER project_skill_content_revisions_no_truncate
                BEFORE TRUNCATE ON projects_config.project_skill_content_revisions
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER project_skill_content_revisions_immutable
                ON projects_config.project_skill_content_revisions;
                DROP TRIGGER project_skill_content_revisions_no_truncate
                ON projects_config.project_skill_content_revisions;

                CREATE OR REPLACE FUNCTION projects_config.require_skill_admission_writer()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    current_state text;
                BEGIN
                    SELECT state INTO current_state
                    FROM projects_config.project_skill_admission_fence
                    WHERE id = 1
                    FOR SHARE;

                    IF current_state = 'Open' THEN
                        RETURN NEW;
                    END IF;
                    IF current_state = 'Active'
                       AND current_setting('agentweaver.skill_admission_v1', true) = '1' THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'skill admission fence blocks imports';
                END;
                $function$;
                """);
        }
    }
}
