using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Environment.Migrations
{
    /// <inheritdoc />
    public partial class ProviderLifecycleReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_lifecycle_reports",
                schema: "environment",
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
                    table.PrimaryKey("pk_environment_provider_lifecycle_reports", x => new { x.tenant_id, x.project_id, x.run_id, x.environment_id, x.provider_event_id });
                    table.CheckConstraint("ck_environment_provider_lifecycle_reports_core_fence", "core_execution_fence IS NULL OR core_execution_fence > 0");
                    table.CheckConstraint("ck_environment_provider_lifecycle_reports_generations", "contract_version > 0 AND lifecycle_generation > 0 AND resource_generation > 0\r\nAND provider_fencing_generation > 0\r\nAND current_fencing_generation = provider_fencing_generation\r\nAND lease_revision > 0");
                    table.CheckConstraint("ck_environment_provider_lifecycle_reports_kind", "event_kind IN ('Suspend', 'Relocation')");
                    table.CheckConstraint("ck_environment_provider_lifecycle_reports_state", "reconciliation_state = 'Pending'");
                    table.ForeignKey(
                        name: "fk_environment_provider_lifecycle_reports_owners",
                        columns: x => new { x.tenant_id, x.project_id, x.run_id, x.environment_id },
                        principalSchema: "environment",
                        principalTable: "owners",
                        principalColumns: new[] { "tenant_id", "project_id", "run_id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_environment_provider_lifecycle_reports_sandbox_leases",
                        columns: x => new { x.tenant_id, x.project_id, x.run_id, x.environment_id, x.resource_generation },
                        principalSchema: "environment",
                        principalTable: "sandbox_leases",
                        principalColumns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "resource_generation" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_provider_lifecycle_reports_tenant_id_project_id_run_id_envi~",
                schema: "environment",
                table: "provider_lifecycle_reports",
                columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "resource_generation" });

            migrationBuilder.CreateIndex(
                name: "ix_environment_provider_lifecycle_reports_core_operation",
                schema: "environment",
                table: "provider_lifecycle_reports",
                columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "core_operation_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_environment_provider_lifecycle_reports_pending",
                schema: "environment",
                table: "provider_lifecycle_reports",
                columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "created_at" },
                filter: "\"reconciliation_state\" = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_lifecycle_reports",
                schema: "environment");
        }
    }
}
