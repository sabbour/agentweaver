using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentweaver.Environment.Migrations;

[DbContext(typeof(EnvironmentDbContext))]
[Migration("20261009060000_ProviderLifecycleReports")]
public sealed class ProviderLifecycleReports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "provider_lifecycle_reports",
            schema: EnvironmentDbContext.Schema,
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                environment_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                provider_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                contract_version = table.Column<int>(type: "integer", nullable: false),
                event_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                adapter_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                resource_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                lifecycle_generation = table.Column<long>(type: "bigint", nullable: false),
                resource_generation = table.Column<long>(type: "bigint", nullable: false),
                provider_fencing_generation = table.Column<long>(type: "bigint", nullable: false),
                current_fencing_generation = table.Column<long>(type: "bigint", nullable: false),
                lease_revision = table.Column<long>(type: "bigint", nullable: false),
                sandbox_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                core_operation_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                core_execution_fence = table.Column<long>(type: "bigint", nullable: true),
                reconciliation_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                last_error_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "pk_environment_provider_lifecycle_reports",
                    row => new
                    {
                        row.tenant_id,
                        row.project_id,
                        row.run_id,
                        row.environment_id,
                        row.provider_event_id
                    });
                table.CheckConstraint(
                    "ck_environment_provider_lifecycle_reports_generations",
                    """
                    contract_version > 0 AND lifecycle_generation > 0 AND resource_generation > 0
                    AND provider_fencing_generation > 0
                    AND current_fencing_generation = provider_fencing_generation
                    AND lease_revision > 0
                    """);
                table.CheckConstraint(
                    "ck_environment_provider_lifecycle_reports_kind",
                    "event_kind IN ('Suspend', 'Relocation')");
                table.CheckConstraint(
                    "ck_environment_provider_lifecycle_reports_state",
                    "reconciliation_state = 'Pending'");
                table.CheckConstraint(
                    "ck_environment_provider_lifecycle_reports_core_fence",
                    "core_execution_fence IS NULL OR core_execution_fence > 0");
                table.ForeignKey(
                    "fk_environment_provider_lifecycle_reports_owners",
                    row => new { row.tenant_id, row.project_id, row.run_id, row.environment_id },
                    principalSchema: EnvironmentDbContext.Schema,
                    principalTable: "owners",
                    principalColumns: ["tenant_id", "project_id", "run_id", "environment_id"],
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    "fk_environment_provider_lifecycle_reports_sandbox_leases",
                    row => new
                    {
                        row.tenant_id,
                        row.project_id,
                        row.run_id,
                        row.environment_id,
                        row.resource_generation
                    },
                    principalSchema: EnvironmentDbContext.Schema,
                    principalTable: "sandbox_leases",
                    principalColumns:
                    [
                        "tenant_id",
                        "project_id",
                        "run_id",
                        "environment_id",
                        "resource_generation"
                    ],
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_environment_provider_lifecycle_reports_core_operation",
            schema: EnvironmentDbContext.Schema,
            table: "provider_lifecycle_reports",
            columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "core_operation_key" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ix_environment_provider_lifecycle_reports_pending",
            schema: EnvironmentDbContext.Schema,
            table: "provider_lifecycle_reports",
            columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "created_at" },
            filter: "\"reconciliation_state\" = 'Pending'");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(
            name: "provider_lifecycle_reports",
            schema: EnvironmentDbContext.Schema);
}
