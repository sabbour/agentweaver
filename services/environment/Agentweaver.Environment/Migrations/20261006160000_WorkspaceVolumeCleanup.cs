using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentweaver.Environment.Migrations;

[DbContext(typeof(EnvironmentDbContext))]
[Migration("20261006160000_WorkspaceVolumeCleanup")]
public sealed class WorkspaceVolumeCleanup : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "expected_provider_binding_json",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "target_provider_binding_json",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects",
            type: "jsonb",
            nullable: true);

        migrationBuilder.Sql($"""
            DO $migration$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM "{EnvironmentDbContext.Schema}"."owner_effects" AS latest
                    WHERE latest.effect_kind = 'WorkspaceVolume'
                      AND latest.effect_state = 'Completed'
                      AND latest.target_provider_resource_id IS NOT NULL
                      AND latest.target_provider_binding_json IS NULL
                      AND NOT EXISTS (
                          SELECT 1
                          FROM "{EnvironmentDbContext.Schema}"."owner_effects" AS newer
                          WHERE newer.tenant_id = latest.tenant_id
                            AND newer.project_id = latest.project_id
                            AND newer.run_id = latest.run_id
                            AND newer.environment_id = latest.environment_id
                            AND newer.effect_kind = 'WorkspaceVolume'
                            AND newer.resource_id = latest.resource_id
                            AND newer.effect_state = 'Completed'
                            AND newer.target_transition_revision > latest.target_transition_revision)
                ) THEN
                    RAISE EXCEPTION 'Cannot apply workspace-volume cleanup migration while an active provider generation lacks its pinned binding.';
                END IF;
            END
            $migration$;
            """);

        migrationBuilder.CreateTable(
            name: "workspace_volume_cleanup",
            schema: EnvironmentDbContext.Schema,
            columns: table => new
            {
                work_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                environment_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                lifecycle_generation = table.Column<long>(type: "bigint", nullable: false),
                source_replace_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                volume_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                resource_generation = table.Column<long>(type: "bigint", nullable: false),
                release_request_json = table.Column<string>(type: "jsonb", nullable: false),
                state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                block_reason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                lease_revision = table.Column<long>(type: "bigint", nullable: false),
                lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_environment_workspace_volume_cleanup", row => row.work_id);
                table.CheckConstraint(
                    "ck_environment_workspace_volume_cleanup_state",
                    "state IN ('Pending', 'Leased', 'Blocked', 'Completed')");
                table.CheckConstraint(
                    "ck_environment_workspace_volume_cleanup_lease",
                    "lease_revision >= 0 AND ((state = 'Leased' AND lease_revision > 0 AND lease_id IS NOT NULL AND lease_expires_at IS NOT NULL) OR (state <> 'Leased' AND lease_id IS NULL AND lease_expires_at IS NULL))");
                table.CheckConstraint(
                    "ck_environment_workspace_volume_cleanup_generation",
                    "lifecycle_generation > 0 AND resource_generation > 0");
                table.ForeignKey(
                    "fk_environment_workspace_volume_cleanup_owners",
                    row => new { row.tenant_id, row.project_id, row.run_id, row.environment_id },
                    principalSchema: EnvironmentDbContext.Schema,
                    principalTable: "owners",
                    principalColumns: ["tenant_id", "project_id", "run_id", "environment_id"],
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    "fk_environment_workspace_volume_cleanup_owner_effects",
                    row => row.source_replace_operation_id,
                    principalSchema: EnvironmentDbContext.Schema,
                    principalTable: "owner_effects",
                    principalColumn: "operation_id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_environment_workspace_volume_cleanup_source_replace_operation_id",
            schema: EnvironmentDbContext.Schema,
            table: "workspace_volume_cleanup",
            column: "source_replace_operation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_environment_workspace_volume_cleanup_owner_state_created",
            schema: EnvironmentDbContext.Schema,
            table: "workspace_volume_cleanup",
            columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "state", "created_at" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "workspace_volume_cleanup",
            schema: EnvironmentDbContext.Schema);

        migrationBuilder.DropColumn(
            name: "expected_provider_binding_json",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects");

        migrationBuilder.DropColumn(
            name: "target_provider_binding_json",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects");
    }
}
