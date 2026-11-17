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

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_providers_state",
                schema: "projects_config",
                table: "model_providers",
                sql: "state = 'Draft'");
        }
    }
}
