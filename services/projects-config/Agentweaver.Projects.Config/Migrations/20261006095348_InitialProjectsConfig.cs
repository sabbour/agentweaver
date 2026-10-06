using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class InitialProjectsConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "projects_config");

            migrationBuilder.CreateTable(
                name: "platform_runtime_heads",
                schema: "projects_config",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    current_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_runtime_heads", x => x.id);
                    table.CheckConstraint("ck_platform_runtime_singleton", "id = 'default'");
                });

            migrationBuilder.CreateTable(
                name: "projects",
                schema: "projects_config",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    owner_actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    configuration_revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.project_id);
                    table.CheckConstraint("ck_projects_configuration_revision", "configuration_revision > 0");
                    table.CheckConstraint("ck_projects_revision", "revision > 0");
                    table.CheckConstraint("ck_projects_state", "state IN ('Active', 'Archived')");
                });

            migrationBuilder.CreateTable(
                name: "platform_runtime_revisions",
                schema: "projects_config",
                columns: table => new
                {
                    revision = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    head_id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    configuration = table.Column<string>(type: "jsonb", nullable: false),
                    updated_by_actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_runtime_revisions", x => x.revision);
                    table.ForeignKey(
                        name: "FK_platform_runtime_revisions_platform_runtime_heads_head_id",
                        column: x => x.head_id,
                        principalSchema: "projects_config",
                        principalTable: "platform_runtime_heads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "project_configuration_revisions",
                schema: "projects_config",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    configuration = table.Column<string>(type: "jsonb", nullable: false),
                    updated_by_actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_configuration_revisions", x => new { x.project_id, x.revision });
                    table.ForeignKey(
                        name: "FK_project_configuration_revisions_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "projects_config",
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "project_run_selections",
                schema: "projects_config",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    project_revision = table.Column<long>(type: "bigint", nullable: false),
                    project_configuration_revision = table.Column<long>(type: "bigint", nullable: false),
                    platform_runtime_revision = table.Column<long>(type: "bigint", nullable: false),
                    context_revision = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_run_selections", x => x.run_id);
                    table.ForeignKey(
                        name: "FK_project_run_selections_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "projects_config",
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "projects_config",
                table: "platform_runtime_heads",
                columns: new[] { "id", "current_revision" },
                values: new object[] { "default", 0L });

            migrationBuilder.CreateIndex(
                name: "IX_platform_runtime_revisions_head_id",
                schema: "projects_config",
                table: "platform_runtime_revisions",
                column: "head_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_run_selections_project_id_run_id",
                schema: "projects_config",
                table: "project_run_selections",
                columns: new[] { "project_id", "run_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_tenant_id_owner_actor_id_state",
                schema: "projects_config",
                table: "projects",
                columns: new[] { "tenant_id", "owner_actor_id", "state" });

            migrationBuilder.Sql(
                """
                CREATE FUNCTION projects_config.reject_immutable_record_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'records in %.% are immutable', TG_TABLE_SCHEMA, TG_TABLE_NAME
                        USING ERRCODE = '55000';
                END;
                $$;

                CREATE TRIGGER project_configuration_revisions_immutable
                BEFORE UPDATE OR DELETE ON projects_config.project_configuration_revisions
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();

                CREATE TRIGGER project_configuration_revisions_no_truncate
                BEFORE TRUNCATE ON projects_config.project_configuration_revisions
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();

                CREATE TRIGGER platform_runtime_revisions_immutable
                BEFORE UPDATE OR DELETE ON projects_config.platform_runtime_revisions
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();

                CREATE TRIGGER platform_runtime_revisions_no_truncate
                BEFORE TRUNCATE ON projects_config.platform_runtime_revisions
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();

                CREATE TRIGGER project_run_selections_immutable
                BEFORE UPDATE OR DELETE ON projects_config.project_run_selections
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();

                CREATE TRIGGER project_run_selections_no_truncate
                BEFORE TRUNCATE ON projects_config.project_run_selections
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_runtime_revisions",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "project_configuration_revisions",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "project_run_selections",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "platform_runtime_heads",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "projects",
                schema: "projects_config");

            migrationBuilder.Sql(
                """
                DROP FUNCTION IF EXISTS projects_config.reject_immutable_record_mutation();
                """);
        }
    }
}
