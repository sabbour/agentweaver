using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddModelProviderDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_providers",
                schema: "projects_config",
                columns: table => new
                {
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    base_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    deployment = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    wire_api = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    azure_api_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reasoning_efforts = table.Column<string[]>(type: "text[]", nullable: false),
                    prompt_capacity_tokens = table.Column<int>(type: "integer", nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    current_revision = table.Column<long>(type: "bigint", nullable: false),
                    created_by_actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_providers", x => x.provider_id);
                    table.CheckConstraint("ck_model_providers_prompt_capacity", "prompt_capacity_tokens IS NULL OR prompt_capacity_tokens > 0");
                    table.CheckConstraint("ck_model_providers_reasoning_efforts", "cardinality(reasoning_efforts) <= 6 AND reasoning_efforts <@ ARRAY['none', 'minimal', 'low', 'medium', 'high', 'xhigh']::text[]");
                    table.CheckConstraint("ck_model_providers_revision", "current_revision > 0");
                    table.CheckConstraint("ck_model_providers_scope_project", "(scope = 'Platform' AND project_id IS NULL) OR (scope = 'Project' AND project_id IS NOT NULL)");
                    table.CheckConstraint("ck_model_providers_state", "state = 'Draft'");
                    table.ForeignKey(
                        name: "FK_model_providers_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "projects_config",
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "model_provider_revisions",
                schema: "projects_config",
                columns: table => new
                {
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_revision = table.Column<long>(type: "bigint", nullable: false),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    base_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    deployment = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    wire_api = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    azure_api_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reasoning_efforts = table.Column<string[]>(type: "text[]", nullable: false),
                    prompt_capacity_tokens = table.Column<int>(type: "integer", nullable: true),
                    configuration_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    credential_version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    rate_card_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    rate_card_version = table.Column<long>(type: "bigint", nullable: true),
                    created_by_actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_provider_revisions", x => new { x.provider_id, x.provider_revision });
                    table.CheckConstraint("ck_model_provider_revisions_configuration_hash", "configuration_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_model_provider_revisions_prompt_capacity", "prompt_capacity_tokens IS NULL OR prompt_capacity_tokens > 0");
                    table.CheckConstraint("ck_model_provider_revisions_reasoning_efforts", "cardinality(reasoning_efforts) <= 6 AND reasoning_efforts <@ ARRAY['none', 'minimal', 'low', 'medium', 'high', 'xhigh']::text[]");
                    table.CheckConstraint("ck_model_provider_revisions_request_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_model_provider_revisions_revision", "provider_revision > 0");
                    table.CheckConstraint("ck_model_provider_revisions_scope_project", "(scope = 'Platform' AND project_id IS NULL) OR (scope = 'Project' AND project_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_model_provider_revisions_model_providers_provider_id",
                        column: x => x.provider_id,
                        principalSchema: "projects_config",
                        principalTable: "model_providers",
                        principalColumn: "provider_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_model_provider_revisions_operation_id",
                schema: "projects_config",
                table: "model_provider_revisions",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_providers_project_id",
                schema: "projects_config",
                table: "model_providers",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_model_providers_scope_project_name",
                schema: "projects_config",
                table: "model_providers",
                columns: new[] { "scope", "project_id", "display_name" });

            migrationBuilder.Sql(
                """
                CREATE TRIGGER model_provider_revisions_immutable
                BEFORE UPDATE OR DELETE ON projects_config.model_provider_revisions
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();

                CREATE TRIGGER model_provider_revisions_no_truncate
                BEFORE TRUNCATE ON projects_config.model_provider_revisions
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_provider_revisions",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "model_providers",
                schema: "projects_config");
        }
    }
}
