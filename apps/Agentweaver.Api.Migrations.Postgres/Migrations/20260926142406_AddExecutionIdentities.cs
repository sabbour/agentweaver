using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "execution_identities",
                columns: table => new
                {
                    descriptor_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    run_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    initiating_principal_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    executing_service_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    agent_assignment_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    agent_role = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    agent_display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    parent_run_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    parent_descriptor_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    retry_of_run_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    retry_of_descriptor_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    workflow_run_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    subtask_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    approval_policy_snapshot_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    executable_workflow_content_digest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_identities", x => x.descriptor_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_execution_identities_parent_descriptor_id",
                table: "execution_identities",
                column: "parent_descriptor_id");

            migrationBuilder.CreateIndex(
                name: "IX_execution_identities_retry_of_descriptor_id",
                table: "execution_identities",
                column: "retry_of_descriptor_id");

            migrationBuilder.CreateIndex(
                name: "IX_execution_identities_run_id_attempt",
                table: "execution_identities",
                columns: new[] { "run_id", "attempt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "execution_identities");
        }
    }
}
