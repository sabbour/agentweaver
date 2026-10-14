using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentweaver.Environment.Migrations;

[DbContext(typeof(EnvironmentDbContext))]
[Migration("20261013190000_RuntimeBootstrapProfileReplacements")]
public sealed class RuntimeBootstrapProfileReplacements : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "runtime_bootstrap_profile_replacements",
            schema: EnvironmentDbContext.Schema,
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                environment_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                profile_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                source_profile_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                lifecycle_generation = table.Column<long>(type: "bigint", nullable: false),
                current_fencing_generation = table.Column<long>(type: "bigint", nullable: false),
                provider_fencing_generation = table.Column<long>(type: "bigint", nullable: false),
                lease_revision = table.Column<long>(type: "bigint", nullable: false),
                lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                replacement_json = table.Column<string>(type: "jsonb", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "pk_environment_runtime_bootstrap_profile_replacements",
                    x => new { x.tenant_id, x.project_id, x.run_id, x.environment_id, x.profile_id });
                table.CheckConstraint(
                    "ck_environment_runtime_bootstrap_profile_replacements_versions",
                    "lifecycle_generation > 0 AND current_fencing_generation > 0 " +
                    "AND provider_fencing_generation = current_fencing_generation AND lease_revision > 0");
            });
        migrationBuilder.CreateIndex(
            name: "ix_environment_runtime_bootstrap_profile_replacements_source",
            schema: EnvironmentDbContext.Schema,
            table: "runtime_bootstrap_profile_replacements",
            columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "source_profile_id", "created_at" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "runtime_bootstrap_profile_replacements",
            schema: EnvironmentDbContext.Schema);
    }
}
