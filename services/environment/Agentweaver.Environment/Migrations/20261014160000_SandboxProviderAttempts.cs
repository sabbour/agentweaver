using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentweaver.Environment.Migrations;

[DbContext(typeof(EnvironmentDbContext))]
[Migration("20261014160000_SandboxProviderAttempts")]
public sealed class SandboxProviderAttempts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "sandbox_provider_attempts",
            schema: EnvironmentDbContext.Schema,
            columns: table => new
            {
                attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                environment_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                lifecycle_generation = table.Column<long>(type: "bigint", nullable: false),
                resource_generation = table.Column<long>(type: "bigint", nullable: false),
                provider_fencing_generation = table.Column<long>(type: "bigint", nullable: false),
                operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                effect_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                late_resource_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                attempt_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                outcome_json = table.Column<string>(type: "jsonb", nullable: true),
                failure_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                outcome_recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_environment_sandbox_provider_attempts", row => row.attempt_id);
                table.UniqueConstraint(
                    "ak_environment_sandbox_provider_attempts_owner_attempt",
                    row => new
                    {
                        row.tenant_id,
                        row.project_id,
                        row.run_id,
                        row.environment_id,
                        row.attempt_id
                    });
                table.CheckConstraint(
                    "ck_environment_sandbox_provider_attempts_generations",
                    "lifecycle_generation > 0 AND resource_generation > 0 AND provider_fencing_generation > 0");
                table.CheckConstraint(
                    "ck_environment_sandbox_provider_attempts_effect",
                    "effect_kind IN ('Provision', 'Release', 'PartialRelease', 'LateResourceRelease', 'LegacyUnknown') AND " +
                    "((effect_kind = 'LateResourceRelease' AND late_resource_fingerprint ~ '^[0-9a-f]{64}$') OR " +
                    "(effect_kind <> 'LateResourceRelease' AND late_resource_fingerprint IS NULL))");
                table.CheckConstraint(
                    "ck_environment_sandbox_provider_attempts_request_fingerprint",
                    "request_fingerprint ~ '^[0-9a-f]{64}$'");
                table.CheckConstraint(
                    "ck_environment_sandbox_provider_attempts_state",
                    """
                    attempt_state IN ('InProgress', 'ResultRecorded', 'FailedNoEffect', 'Unknown', 'Completed', 'LegacyUnknown')
                    AND (
                        (attempt_state IN ('InProgress', 'LegacyUnknown')
                         AND outcome_json IS NULL AND outcome_recorded_at IS NULL AND resolved_at IS NULL)
                        OR
                        (attempt_state IN ('ResultRecorded', 'FailedNoEffect', 'Unknown', 'Completed')
                         AND jsonb_typeof(outcome_json) = 'object' AND outcome_recorded_at IS NOT NULL)
                    )
                    AND (attempt_state IN ('FailedNoEffect', 'Completed') AND resolved_at IS NOT NULL
                         OR attempt_state NOT IN ('FailedNoEffect', 'Completed') AND resolved_at IS NULL)
                    AND (attempt_state IN ('ResultRecorded', 'Completed') AND failure_code IS NULL
                         OR attempt_state IN ('FailedNoEffect', 'Unknown') AND failure_code IS NOT NULL
                         OR attempt_state IN ('InProgress', 'LegacyUnknown') AND failure_code IS NULL)
                    """);
                table.CheckConstraint(
                    "ck_environment_sandbox_provider_attempts_timestamps",
                    "updated_at >= started_at AND " +
                    "(outcome_recorded_at IS NULL OR outcome_recorded_at >= started_at) AND " +
                    "(resolved_at IS NULL OR resolved_at >= outcome_recorded_at)");
                table.ForeignKey(
                    "fk_environment_sandbox_provider_attempts_lease",
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
            name: "ix_environment_sandbox_provider_attempts_operation",
            schema: EnvironmentDbContext.Schema,
            table: "sandbox_provider_attempts",
            columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "operation_id", "started_at", "attempt_id" });
        migrationBuilder.CreateIndex(
            name: "ix_environment_sandbox_provider_attempts_unresolved",
            schema: EnvironmentDbContext.Schema,
            table: "sandbox_provider_attempts",
            columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "started_at" },
            filter: "\"attempt_state\" IN ('InProgress', 'ResultRecorded', 'Unknown', 'LegacyUnknown')");

        migrationBuilder.AddColumn<Guid>(
            name: "provision_attempt_id",
            schema: EnvironmentDbContext.Schema,
            table: "sandbox_late_resource_cleanups",
            type: "uuid",
            nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "release_attempt_id",
            schema: EnvironmentDbContext.Schema,
            table: "sandbox_late_resource_cleanups",
            type: "uuid",
            nullable: true);
        migrationBuilder.AddForeignKey(
            name: "fk_environment_sandbox_late_cleanups_provision_attempt",
            schema: EnvironmentDbContext.Schema,
            table: "sandbox_late_resource_cleanups",
            columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "provision_attempt_id" },
            principalSchema: EnvironmentDbContext.Schema,
            principalTable: "sandbox_provider_attempts",
            principalColumns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "attempt_id" },
            onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey(
            name: "fk_environment_sandbox_late_cleanups_release_attempt",
            schema: EnvironmentDbContext.Schema,
            table: "sandbox_late_resource_cleanups",
            columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "release_attempt_id" },
            principalSchema: EnvironmentDbContext.Schema,
            principalTable: "sandbox_provider_attempts",
            principalColumns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "attempt_id" },
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.Sql(
            """
            WITH legacy_attempts AS (
                SELECT
                    tenant_id,
                    project_id,
                    run_id,
                    environment_id,
                    lifecycle_generation,
                    resource_generation,
                    provider_fencing_generation,
                    operation_id,
                    provider_request_fingerprint AS request_fingerprint,
                    created_at,
                    (
                        substr(md5(concat_ws(E'\x1f', 'agentweaver', 'sandbox-legacy-attempt-v1',
                            tenant_id, project_id, run_id, environment_id,
                            resource_generation::text, operation_id::text)), 1, 8) || '-' ||
                        substr(md5(concat_ws(E'\x1f', 'agentweaver', 'sandbox-legacy-attempt-v1',
                            tenant_id, project_id, run_id, environment_id,
                            resource_generation::text, operation_id::text)), 9, 4) || '-' ||
                        substr(md5(concat_ws(E'\x1f', 'agentweaver', 'sandbox-legacy-attempt-v1',
                            tenant_id, project_id, run_id, environment_id,
                            resource_generation::text, operation_id::text)), 13, 4) || '-' ||
                        substr(md5(concat_ws(E'\x1f', 'agentweaver', 'sandbox-legacy-attempt-v1',
                            tenant_id, project_id, run_id, environment_id,
                            resource_generation::text, operation_id::text)), 17, 4) || '-' ||
                        substr(md5(concat_ws(E'\x1f', 'agentweaver', 'sandbox-legacy-attempt-v1',
                            tenant_id, project_id, run_id, environment_id,
                            resource_generation::text, operation_id::text)), 21, 12)
                    )::uuid AS attempt_id
                FROM environment.sandbox_leases
                WHERE provider_request_fingerprint IS NOT NULL
            )
            INSERT INTO environment.sandbox_provider_attempts
                (attempt_id, tenant_id, project_id, run_id, environment_id,
                 lifecycle_generation, resource_generation, provider_fencing_generation,
                 operation_id, effect_kind, request_fingerprint, late_resource_fingerprint,
                 attempt_state, started_at, updated_at)
            SELECT attempt_id, tenant_id, project_id, run_id, environment_id,
                   lifecycle_generation, resource_generation, provider_fencing_generation,
                   operation_id, 'LegacyUnknown', request_fingerprint, NULL,
                   'LegacyUnknown', created_at, created_at
            FROM legacy_attempts;

            WITH legacy_cleanup_attempts AS (
                SELECT
                    cleanup.tenant_id,
                    cleanup.project_id,
                    cleanup.run_id,
                    cleanup.environment_id,
                    cleanup.resource_generation,
                    lease.lifecycle_generation,
                    lease.provider_fencing_generation,
                    lease.operation_id,
                    cleanup.provider_resource_fingerprint AS request_fingerprint,
                    cleanup.created_at,
                    (
                        substr(md5(concat_ws(E'\x1f', 'agentweaver', 'sandbox-legacy-cleanup-v1',
                            cleanup.tenant_id, cleanup.project_id, cleanup.run_id,
                            cleanup.environment_id, cleanup.resource_generation::text,
                            cleanup.provider_resource_fingerprint)), 1, 8) || '-' ||
                        substr(md5(concat_ws(E'\x1f', 'agentweaver', 'sandbox-legacy-cleanup-v1',
                            cleanup.tenant_id, cleanup.project_id, cleanup.run_id,
                            cleanup.environment_id, cleanup.resource_generation::text,
                            cleanup.provider_resource_fingerprint)), 9, 4) || '-' ||
                        substr(md5(concat_ws(E'\x1f', 'agentweaver', 'sandbox-legacy-cleanup-v1',
                            cleanup.tenant_id, cleanup.project_id, cleanup.run_id,
                            cleanup.environment_id, cleanup.resource_generation::text,
                            cleanup.provider_resource_fingerprint)), 13, 4) || '-' ||
                        substr(md5(concat_ws(E'\x1f', 'agentweaver', 'sandbox-legacy-cleanup-v1',
                            cleanup.tenant_id, cleanup.project_id, cleanup.run_id,
                            cleanup.environment_id, cleanup.resource_generation::text,
                            cleanup.provider_resource_fingerprint)), 17, 4) || '-' ||
                        substr(md5(concat_ws(E'\x1f', 'agentweaver', 'sandbox-legacy-cleanup-v1',
                            cleanup.tenant_id, cleanup.project_id, cleanup.run_id,
                            cleanup.environment_id, cleanup.resource_generation::text,
                            cleanup.provider_resource_fingerprint)), 21, 12)
                    )::uuid AS attempt_id
                FROM environment.sandbox_late_resource_cleanups AS cleanup
                JOIN environment.sandbox_leases AS lease
                  USING (tenant_id, project_id, run_id, environment_id, resource_generation)
                WHERE cleanup.release_receipt_json IS NULL
            )
            INSERT INTO environment.sandbox_provider_attempts
                (attempt_id, tenant_id, project_id, run_id, environment_id,
                 lifecycle_generation, resource_generation, provider_fencing_generation,
                 operation_id, effect_kind, request_fingerprint, late_resource_fingerprint,
                 attempt_state, started_at, updated_at)
            SELECT attempt_id, tenant_id, project_id, run_id, environment_id,
                   lifecycle_generation, resource_generation, provider_fencing_generation,
                   operation_id, 'LegacyUnknown', request_fingerprint, NULL,
                   'LegacyUnknown', created_at, created_at
            FROM legacy_cleanup_attempts;

            UPDATE environment.sandbox_late_resource_cleanups AS cleanup
            SET provision_attempt_id = source.attempt_id
            FROM environment.sandbox_leases AS lease
            JOIN environment.sandbox_provider_attempts AS source
              ON source.tenant_id = lease.tenant_id
             AND source.project_id = lease.project_id
             AND source.run_id = lease.run_id
             AND source.environment_id = lease.environment_id
             AND source.operation_id = lease.operation_id
             AND source.effect_kind = 'LegacyUnknown'
             AND source.request_fingerprint = lease.provider_request_fingerprint
            WHERE cleanup.tenant_id = lease.tenant_id
              AND cleanup.project_id = lease.project_id
              AND cleanup.run_id = lease.run_id
              AND cleanup.environment_id = lease.environment_id
              AND cleanup.resource_generation = lease.resource_generation
              AND lease.provider_request_fingerprint IS NOT NULL;

            UPDATE environment.sandbox_late_resource_cleanups AS cleanup
            SET release_attempt_id = release_attempt.attempt_id
            FROM environment.sandbox_provider_attempts AS release_attempt
            WHERE cleanup.release_receipt_json IS NULL
              AND release_attempt.tenant_id = cleanup.tenant_id
              AND release_attempt.project_id = cleanup.project_id
              AND release_attempt.run_id = cleanup.run_id
              AND release_attempt.environment_id = cleanup.environment_id
              AND release_attempt.resource_generation = cleanup.resource_generation
              AND release_attempt.effect_kind = 'LegacyUnknown'
              AND release_attempt.request_fingerprint = cleanup.provider_resource_fingerprint;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "fk_environment_sandbox_late_cleanups_provision_attempt",
            schema: EnvironmentDbContext.Schema,
            table: "sandbox_late_resource_cleanups");
        migrationBuilder.DropForeignKey(
            name: "fk_environment_sandbox_late_cleanups_release_attempt",
            schema: EnvironmentDbContext.Schema,
            table: "sandbox_late_resource_cleanups");
        migrationBuilder.DropColumn(
            name: "provision_attempt_id",
            schema: EnvironmentDbContext.Schema,
            table: "sandbox_late_resource_cleanups");
        migrationBuilder.DropColumn(
            name: "release_attempt_id",
            schema: EnvironmentDbContext.Schema,
            table: "sandbox_late_resource_cleanups");
        migrationBuilder.DropTable(
            name: "sandbox_provider_attempts",
            schema: EnvironmentDbContext.Schema);
    }
}
