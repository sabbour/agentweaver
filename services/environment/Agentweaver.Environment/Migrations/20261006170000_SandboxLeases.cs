using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentweaver.Environment.Migrations;

[DbContext(typeof(EnvironmentDbContext))]
[Migration("20261006170000_SandboxLeases")]
public sealed class SandboxLeases : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "sandbox_leases",
            schema: EnvironmentDbContext.Schema,
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                environment_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                lifecycle_generation = table.Column<long>(type: "bigint", nullable: false),
                resource_generation = table.Column<long>(type: "bigint", nullable: false),
                provider_fencing_generation = table.Column<long>(type: "bigint", nullable: false),
                current_fencing_generation = table.Column<long>(type: "bigint", nullable: false),
                operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                provision_idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                adapter_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                options_schema_version = table.Column<int>(type: "integer", nullable: false),
                options_revision = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                options_snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                selection_snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                provider_request_json = table.Column<string>(type: "jsonb", nullable: false),
                provider_request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                lease_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                resource_json = table.Column<string>(type: "jsonb", nullable: true),
                retirement_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                terminal_evidence_json = table.Column<string>(type: "jsonb", nullable: true),
                retiring_issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                retiring_actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                retiring_membership_revision = table.Column<long>(type: "bigint", nullable: true),
                release_idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                retirement_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                is_current = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                lease_revision = table.Column<long>(type: "bigint", nullable: false),
                lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                partial_release_receipt_json = table.Column<string>(type: "jsonb", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "pk_environment_sandbox_leases",
                    row => new { row.tenant_id, row.project_id, row.run_id, row.environment_id, row.resource_generation });
                table.CheckConstraint(
                    "ck_environment_sandbox_leases_generations",
                    "lifecycle_generation > 0 AND resource_generation > 0 AND provider_fencing_generation > 0 AND current_fencing_generation >= provider_fencing_generation");
                table.CheckConstraint(
                    "ck_environment_sandbox_leases_state",
                    "lease_state IN ('Provisioning', 'Active', 'Releasing', 'Released', 'ReconciliationRequired', 'Failed')");
                table.CheckConstraint(
                    "ck_environment_sandbox_leases_revision_expiry",
                    "lease_revision >= 1 AND ((lease_state IN ('Released', 'Failed') AND lease_expires_at IS NULL) OR (lease_state NOT IN ('Released', 'Failed') AND lease_expires_at IS NOT NULL))");
                table.CheckConstraint(
                    "ck_environment_sandbox_leases_json",
                    "jsonb_typeof(options_snapshot_json) = 'object' AND jsonb_typeof(selection_snapshot_json) = 'object' AND jsonb_typeof(provider_request_json) = 'object' AND (resource_json IS NULL OR jsonb_typeof(resource_json) = 'object') AND (terminal_evidence_json IS NULL OR jsonb_typeof(terminal_evidence_json) = 'object') AND (partial_release_receipt_json IS NULL OR jsonb_typeof(partial_release_receipt_json) = 'object')");
                table.CheckConstraint(
                    "ck_environment_sandbox_leases_retirement",
                    """
                    (lease_state NOT IN ('Releasing', 'Released') OR
                        (retirement_reason IS NOT NULL AND release_idempotency_key IS NOT NULL
                         AND retirement_fingerprint IS NOT NULL))
                    AND (retirement_reason IS NULL OR
                        (retirement_reason = 'Finished' AND terminal_evidence_json IS NOT NULL
                         AND retiring_issuer IS NULL AND retiring_actor_id IS NULL
                         AND retiring_membership_revision IS NULL)
                        OR (retirement_reason = 'AuthorizedAbandon' AND terminal_evidence_json IS NULL
                            AND retiring_issuer IS NOT NULL AND retiring_actor_id IS NOT NULL
                            AND retiring_membership_revision > 0))
                    AND (lease_state <> 'Active' OR resource_json IS NOT NULL)
                    AND (partial_release_receipt_json IS NULL OR
                        lease_state IN ('Released', 'ReconciliationRequired'))
                    """);
                table.ForeignKey(
                    "fk_environment_sandbox_leases_owners",
                    row => new { row.tenant_id, row.project_id, row.run_id, row.environment_id },
                    principalSchema: EnvironmentDbContext.Schema,
                    principalTable: "owners",
                    principalColumns: ["tenant_id", "project_id", "run_id", "environment_id"],
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_environment_sandbox_leases_operation_id",
            schema: EnvironmentDbContext.Schema,
            table: "sandbox_leases",
            column: "operation_id",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ix_environment_sandbox_leases_provision_idempotency",
            schema: EnvironmentDbContext.Schema,
            table: "sandbox_leases",
            columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "provision_idempotency_key" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ix_environment_sandbox_leases_current",
            schema: EnvironmentDbContext.Schema,
            table: "sandbox_leases",
            columns: new[] { "tenant_id", "project_id", "run_id", "environment_id" },
            unique: true,
            filter: "\"is_current\" = TRUE");
        migrationBuilder.CreateIndex(
            name: "ix_environment_sandbox_leases_state_updated",
            schema: EnvironmentDbContext.Schema,
            table: "sandbox_leases",
            columns: new[] { "lease_state", "updated_at" });
        migrationBuilder.CreateIndex(
            name: "ix_environment_sandbox_leases_state_expiry",
            schema: EnvironmentDbContext.Schema,
            table: "sandbox_leases",
            columns: new[] { "lease_state", "lease_expires_at" },
            filter: "\"lease_expires_at\" IS NOT NULL");

    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(
            name: "sandbox_leases",
            schema: EnvironmentDbContext.Schema);
}
