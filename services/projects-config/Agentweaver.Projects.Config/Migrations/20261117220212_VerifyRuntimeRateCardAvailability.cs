using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class VerifyRuntimeRateCardAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_model_providers_state",
                schema: "projects_config",
                table: "model_providers");

            migrationBuilder.Sql(
                """
                ALTER TABLE projects_config.model_provider_revisions
                ALTER COLUMN rate_card_version TYPE character varying(64)
                USING rate_card_version::text;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_providers_state",
                schema: "projects_config",
                table: "model_providers",
                sql: "state IN ('Draft', 'Available')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM projects_config.model_provider_revisions
                        WHERE rate_card_version IS NOT NULL
                          AND CASE
                                WHEN rate_card_version ~ '^-?[0-9]+$'
                                THEN rate_card_version::numeric < -9223372036854775808::numeric
                                  OR rate_card_version::numeric > 9223372036854775807::numeric
                                ELSE TRUE
                              END
                    ) THEN
                        RAISE EXCEPTION 'cannot downgrade provider revisions with rate-card versions that are not signed 64-bit integers'
                            USING ERRCODE = '55000';
                    END IF;
                END;
                $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_model_providers_state",
                schema: "projects_config",
                table: "model_providers");

            migrationBuilder.Sql(
                """
                ALTER TABLE projects_config.model_provider_revisions
                ALTER COLUMN rate_card_version TYPE bigint
                USING rate_card_version::bigint;
                """);

            migrationBuilder.Sql(
                """
                UPDATE projects_config.model_providers
                SET state = 'Draft'
                WHERE state = 'Available';
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_providers_state",
                schema: "projects_config",
                table: "model_providers",
                sql: "state = 'Draft'");
        }
    }
}
