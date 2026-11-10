using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AllowNoneMinimalReasoningEfforts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_hosted_model_reasoning_supported_efforts",
                schema: "projects_config",
                table: "hosted_model_reasoning_catalog");

            migrationBuilder.AddCheckConstraint(
                name: "ck_hosted_model_reasoning_supported_efforts",
                schema: "projects_config",
                table: "hosted_model_reasoning_catalog",
                sql: "supported_reasoning_efforts <@ ARRAY['none', 'minimal', 'low', 'medium', 'high', 'xhigh']::text[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE projects_config.hosted_model_reasoning_catalog " +
                "SET supported_reasoning_efforts = array_remove(array_remove(supported_reasoning_efforts, " +
                "'none'), 'minimal') " +
                "WHERE supported_reasoning_efforts && ARRAY['none', 'minimal']::text[]");

            migrationBuilder.DropCheckConstraint(
                name: "ck_hosted_model_reasoning_supported_efforts",
                schema: "projects_config",
                table: "hosted_model_reasoning_catalog");

            migrationBuilder.AddCheckConstraint(
                name: "ck_hosted_model_reasoning_supported_efforts",
                schema: "projects_config",
                table: "hosted_model_reasoning_catalog",
                sql: "supported_reasoning_efforts <@ ARRAY['low', 'medium', 'high', 'xhigh']::text[]");
        }
    }
}
