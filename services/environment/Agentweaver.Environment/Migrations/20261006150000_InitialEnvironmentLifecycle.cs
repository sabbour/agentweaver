using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentweaver.Environment.Migrations;

[DbContext(typeof(EnvironmentDbContext))]
[Migration("20261006150000_InitialEnvironmentLifecycle")]
public sealed class InitialEnvironmentLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: EnvironmentDbContext.Schema);

        migrationBuilder.CreateTable(
            name: "owners",
            schema: EnvironmentDbContext.Schema,
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                environment_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                lifecycle_generation = table.Column<long>(type: "bigint", nullable: false),
                state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "pk_environment_owners",
                    row => new { row.tenant_id, row.project_id, row.run_id, row.environment_id });
                table.CheckConstraint("ck_environment_owners_lifecycle_generation", "lifecycle_generation > 0");
                table.CheckConstraint("ck_environment_owners_state", "state IN ('Active', 'Released')");
            });

        migrationBuilder.CreateTable(
            name: "lifecycle_operations",
            schema: EnvironmentDbContext.Schema,
            columns: table => new
            {
                operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                environment_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                expected_lifecycle_generation = table.Column<long>(type: "bigint", nullable: false),
                result_lifecycle_generation = table.Column<long>(type: "bigint", nullable: false),
                result_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_environment_lifecycle_operations", row => row.operation_id);
                table.CheckConstraint(
                    "ck_environment_lifecycle_operations_expected_generation",
                    "expected_lifecycle_generation >= 0");
                table.CheckConstraint(
                    "ck_environment_lifecycle_operations_result_generation",
                    "result_lifecycle_generation > 0");
                table.CheckConstraint(
                    "ck_environment_lifecycle_operations_state",
                    "result_state IN ('Active', 'Released')");
                table.ForeignKey(
                    "fk_environment_lifecycle_operations_owners",
                    row => new { row.tenant_id, row.project_id, row.run_id, row.environment_id },
                    principalSchema: EnvironmentDbContext.Schema,
                    principalTable: "owners",
                    principalColumns: ["tenant_id", "project_id", "run_id", "environment_id"],
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "owner_effects",
            schema: EnvironmentDbContext.Schema,
            columns: table => new
            {
                operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                environment_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                lifecycle_generation = table.Column<long>(type: "bigint", nullable: false),
                effect_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                resource_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                expected_provider_seam = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                expected_provider_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                expected_provider_resource_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                target_provider_seam = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                target_provider_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                target_provider_resource_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                policy_generation = table.Column<long>(type: "bigint", nullable: true),
                expected_previous_policy_generation = table.Column<long>(type: "bigint", nullable: true),
                expected_transition_revision = table.Column<long>(type: "bigint", nullable: true),
                expected_resource_generation = table.Column<long>(type: "bigint", nullable: true),
                expected_data_generation = table.Column<long>(type: "bigint", nullable: true),
                target_transition_revision = table.Column<long>(type: "bigint", nullable: true),
                target_resource_generation = table.Column<long>(type: "bigint", nullable: true),
                target_data_generation = table.Column<long>(type: "bigint", nullable: true),
                target_volume_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                specification_json = table.Column<string>(type: "jsonb", nullable: true),
                idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                effect_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_environment_owner_effects", row => row.operation_id);
                table.CheckConstraint(
                    "ck_environment_owner_effects_lifecycle_generation",
                    "lifecycle_generation > 0");
                table.CheckConstraint(
                    "ck_environment_owner_effects_kind",
                    "effect_kind IN ('NetworkPolicy', 'WorkspaceVolume')");
                table.CheckConstraint(
                    "ck_environment_owner_effects_state",
                    "effect_state IN ('Reserved', 'Completed', 'ReconciliationRequired', 'Reconciled', 'Failed', 'Stale')");
                table.ForeignKey(
                    "fk_environment_owner_effects_owners",
                    row => new { row.tenant_id, row.project_id, row.run_id, row.environment_id },
                    principalSchema: EnvironmentDbContext.Schema,
                    principalTable: "owners",
                    principalColumns: ["tenant_id", "project_id", "run_id", "environment_id"],
                    onDelete: ReferentialAction.Restrict);
                table.CheckConstraint(
                    "ck_environment_owner_effects_network_generations",
                    "effect_kind <> 'NetworkPolicy' OR (policy_generation > 0 AND expected_previous_policy_generation >= 0 AND expected_previous_policy_generation < policy_generation AND target_transition_revision IS NULL AND target_resource_generation IS NULL AND target_data_generation IS NULL)");
                table.CheckConstraint(
                    "ck_environment_owner_effects_volume_generations",
                    "effect_kind <> 'WorkspaceVolume' OR (resource_id IS NOT NULL AND policy_generation IS NULL AND expected_previous_policy_generation IS NULL AND expected_transition_revision IS NOT NULL AND expected_resource_generation IS NOT NULL AND expected_data_generation IS NOT NULL AND target_transition_revision IS NOT NULL AND target_resource_generation IS NOT NULL AND target_data_generation IS NOT NULL AND target_volume_state IS NOT NULL AND target_transition_revision = expected_transition_revision + 1 AND target_resource_generation >= 0 AND target_data_generation >= 0 AND operation IN ('Create', 'Provision', 'Replace', 'Bind', 'Unbind', 'Attach', 'Detach', 'Flush', 'Release') AND target_volume_state IN ('Requested', 'Ready', 'Bound', 'Attached', 'Released') AND ((operation = 'Create' AND specification_json IS NOT NULL AND expected_transition_revision = 0 AND expected_resource_generation = 0 AND expected_data_generation = 0 AND target_resource_generation = 0 AND target_data_generation = 0 AND target_volume_state = 'Requested') OR (operation = 'Provision' AND specification_json IS NULL AND expected_resource_generation = 0 AND target_resource_generation = 1 AND target_data_generation = expected_data_generation AND target_volume_state = 'Ready') OR (operation = 'Replace' AND specification_json IS NULL AND expected_resource_generation > 0 AND target_resource_generation = expected_resource_generation + 1 AND target_data_generation = expected_data_generation AND target_volume_state = 'Ready') OR (operation = 'Bind' AND specification_json IS NULL AND expected_resource_generation > 0 AND target_resource_generation = expected_resource_generation AND target_data_generation = expected_data_generation AND target_volume_state = 'Bound') OR (operation = 'Unbind' AND specification_json IS NULL AND expected_resource_generation > 0 AND target_resource_generation = expected_resource_generation AND target_data_generation = expected_data_generation AND target_volume_state = 'Ready') OR (operation = 'Attach' AND specification_json IS NULL AND expected_resource_generation > 0 AND target_resource_generation = expected_resource_generation AND target_data_generation = expected_data_generation AND target_volume_state = 'Attached') OR (operation = 'Detach' AND specification_json IS NULL AND expected_resource_generation > 0 AND target_resource_generation = expected_resource_generation AND target_data_generation = expected_data_generation AND target_volume_state = 'Bound') OR (operation = 'Flush' AND specification_json IS NULL AND expected_resource_generation > 0 AND target_resource_generation = expected_resource_generation AND target_data_generation = expected_data_generation + 1 AND target_volume_state IN ('Ready', 'Bound', 'Attached')) OR (operation = 'Release' AND specification_json IS NULL AND target_resource_generation = expected_resource_generation AND target_data_generation = expected_data_generation AND target_volume_state = 'Released')))");
            });

        migrationBuilder.AddCheckConstraint(
            name: "ck_environment_owner_effects_volume_provider_resources",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects",
            sql: """
                effect_kind <> 'WorkspaceVolume' OR (
                    ((expected_resource_generation = 0 AND expected_provider_seam IS NULL
                        AND expected_provider_id IS NULL AND expected_provider_resource_id IS NULL)
                     OR (expected_resource_generation > 0 AND expected_provider_seam = 'Storage'
                        AND expected_provider_id IS NOT NULL AND expected_provider_resource_id IS NOT NULL))
                    AND ((target_provider_seam IS NULL AND target_provider_id IS NULL
                            AND target_provider_resource_id IS NULL)
                         OR (target_provider_seam = 'Storage' AND target_provider_id IS NOT NULL
                            AND target_provider_resource_id IS NOT NULL))
                    AND (operation NOT IN ('Create', 'Release')
                         OR (target_provider_seam IS NULL AND target_provider_id IS NULL
                             AND target_provider_resource_id IS NULL))
                    AND (operation NOT IN ('Bind', 'Unbind', 'Attach', 'Detach', 'Flush')
                         OR (target_provider_seam IS NOT DISTINCT FROM expected_provider_seam
                             AND target_provider_id IS NOT DISTINCT FROM expected_provider_id
                             AND target_provider_resource_id IS NOT DISTINCT FROM expected_provider_resource_id))
                    AND (effect_state <> 'Completed' OR target_resource_generation = 0
                         OR target_volume_state = 'Released' OR target_provider_seam = 'Storage')
                )
                """);

        migrationBuilder.CreateIndex(
            name: "ix_environment_lifecycle_operations_owner_idempotency",
            schema: EnvironmentDbContext.Schema,
            table: "lifecycle_operations",
            columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "idempotency_key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_environment_owner_effects_owner_idempotency",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects",
            columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "idempotency_key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_environment_owner_effects_owner_policy_generation",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects",
            columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "policy_generation" },
            unique: true,
            filter: "\"effect_kind\" = 'NetworkPolicy' AND \"effect_state\" NOT IN ('Failed', 'Stale')");

        migrationBuilder.CreateIndex(
            name: "ix_environment_owner_effects_owner_volume_revision",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects",
            columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "resource_id", "target_transition_revision" },
            unique: true,
            filter: "\"effect_kind\" = 'WorkspaceVolume' AND \"effect_state\" IN ('Reserved', 'Completed')");

        migrationBuilder.CreateIndex(
            name: "ix_environment_owner_effects_owner_volume_reserved",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects",
            columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "resource_id" },
            unique: true,
            filter: "\"effect_kind\" = 'WorkspaceVolume' AND \"effect_state\" IN ('Reserved', 'ReconciliationRequired')");

        migrationBuilder.CreateIndex(
            name: "ix_environment_owner_effects_network_reserved",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects",
            columns: new[] { "tenant_id", "project_id", "run_id", "environment_id", "resource_id" },
            unique: true,
            filter: "\"effect_kind\" = 'NetworkPolicy' AND \"effect_state\" IN ('Reserved', 'ReconciliationRequired')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "lifecycle_operations", schema: EnvironmentDbContext.Schema);
        migrationBuilder.DropTable(name: "owner_effects", schema: EnvironmentDbContext.Schema);
        migrationBuilder.DropTable(name: "owners", schema: EnvironmentDbContext.Schema);
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(EnvironmentDbContext.Schema);
        modelBuilder.HasAnnotation("ProductVersion", "10.0.12");

        modelBuilder.Entity<EnvironmentOwnerRow>(entity =>
        {
            entity.ToTable("owners", EnvironmentDbContext.Schema);
            entity.HasKey(row => new { row.TenantId, row.ProjectId, row.RunId, row.EnvironmentId });
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            entity.Property(row => row.ProjectId).HasColumnName("project_id").HasMaxLength(256);
            entity.Property(row => row.RunId).HasColumnName("run_id").HasMaxLength(256);
            entity.Property(row => row.EnvironmentId).HasColumnName("environment_id").HasMaxLength(256);
            entity.Property(row => row.LifecycleGeneration).HasColumnName("lifecycle_generation").IsConcurrencyToken();
            entity.Property(row => row.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
            entity.Property(row => row.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<EnvironmentLifecycleOperationRow>(entity =>
        {
            entity.ToTable("lifecycle_operations", EnvironmentDbContext.Schema);
            entity.HasKey(row => row.OperationId);
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.ProjectId,
                row.RunId,
                row.EnvironmentId,
                row.IdempotencyKey
            }).IsUnique();
            entity.Property(row => row.OperationId).HasColumnName("operation_id");
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            entity.Property(row => row.ProjectId).HasColumnName("project_id").HasMaxLength(256);
            entity.Property(row => row.RunId).HasColumnName("run_id").HasMaxLength(256);
            entity.Property(row => row.EnvironmentId).HasColumnName("environment_id").HasMaxLength(256);
            entity.Property(row => row.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
            entity.Property(row => row.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64);
            entity.Property(row => row.ExpectedLifecycleGeneration).HasColumnName("expected_lifecycle_generation");
            entity.Property(row => row.ResultLifecycleGeneration).HasColumnName("result_lifecycle_generation");
            entity.Property(row => row.ResultState).HasColumnName("result_state")
                .HasConversion<string>().HasMaxLength(16);
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.HasOne<EnvironmentOwnerRow>().WithMany()
                .HasForeignKey(row => new { row.TenantId, row.ProjectId, row.RunId, row.EnvironmentId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EnvironmentOwnerEffectRow>(entity =>
        {
            entity.ToTable("owner_effects", EnvironmentDbContext.Schema, table =>
            {
                table.HasCheckConstraint(
                    "ck_environment_owner_effects_lifecycle_generation",
                    "lifecycle_generation > 0");
                table.HasCheckConstraint(
                    "ck_environment_owner_effects_kind",
                    "effect_kind IN ('NetworkPolicy', 'WorkspaceVolume')");
                table.HasCheckConstraint(
                    "ck_environment_owner_effects_state",
                    "effect_state IN ('Reserved', 'Completed', 'ReconciliationRequired', 'Reconciled', 'Failed', 'Stale')");
                table.HasCheckConstraint(
                    "ck_environment_owner_effects_network_generations",
                    "effect_kind <> 'NetworkPolicy' OR (policy_generation > 0 AND expected_previous_policy_generation >= 0 AND expected_previous_policy_generation < policy_generation AND target_transition_revision IS NULL AND target_resource_generation IS NULL AND target_data_generation IS NULL)");
                table.HasCheckConstraint(
                    "ck_environment_owner_effects_volume_generations",
                    "effect_kind <> 'WorkspaceVolume' OR (resource_id IS NOT NULL AND policy_generation IS NULL AND expected_previous_policy_generation IS NULL AND expected_transition_revision IS NOT NULL AND expected_resource_generation IS NOT NULL AND expected_data_generation IS NOT NULL AND target_transition_revision IS NOT NULL AND target_resource_generation IS NOT NULL AND target_data_generation IS NOT NULL AND target_volume_state IS NOT NULL AND target_transition_revision = expected_transition_revision + 1 AND target_resource_generation >= 0 AND target_data_generation >= 0 AND operation IN ('Create', 'Provision', 'Replace', 'Bind', 'Unbind', 'Attach', 'Detach', 'Flush', 'Release') AND target_volume_state IN ('Requested', 'Ready', 'Bound', 'Attached', 'Released') AND ((operation = 'Create' AND specification_json IS NOT NULL AND expected_transition_revision = 0 AND expected_resource_generation = 0 AND expected_data_generation = 0 AND target_resource_generation = 0 AND target_data_generation = 0 AND target_volume_state = 'Requested') OR (operation = 'Provision' AND specification_json IS NULL AND expected_resource_generation = 0 AND target_resource_generation = 1 AND target_data_generation = expected_data_generation AND target_volume_state = 'Ready') OR (operation = 'Replace' AND specification_json IS NULL AND expected_resource_generation > 0 AND target_resource_generation = expected_resource_generation + 1 AND target_data_generation = expected_data_generation AND target_volume_state = 'Ready') OR (operation = 'Bind' AND specification_json IS NULL AND expected_resource_generation > 0 AND target_resource_generation = expected_resource_generation AND target_data_generation = expected_data_generation AND target_volume_state = 'Bound') OR (operation = 'Unbind' AND specification_json IS NULL AND expected_resource_generation > 0 AND target_resource_generation = expected_resource_generation AND target_data_generation = expected_data_generation AND target_volume_state = 'Ready') OR (operation = 'Attach' AND specification_json IS NULL AND expected_resource_generation > 0 AND target_resource_generation = expected_resource_generation AND target_data_generation = expected_data_generation AND target_volume_state = 'Attached') OR (operation = 'Detach' AND specification_json IS NULL AND expected_resource_generation > 0 AND target_resource_generation = expected_resource_generation AND target_data_generation = expected_data_generation AND target_volume_state = 'Bound') OR (operation = 'Flush' AND specification_json IS NULL AND expected_resource_generation > 0 AND target_resource_generation = expected_resource_generation AND target_data_generation = expected_data_generation + 1 AND target_volume_state IN ('Ready', 'Bound', 'Attached')) OR (operation = 'Release' AND specification_json IS NULL AND target_resource_generation = expected_resource_generation AND target_data_generation = expected_data_generation AND target_volume_state = 'Released')))");
            });
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_environment_owner_effects_volume_provider_resources",
                """
                effect_kind <> 'WorkspaceVolume' OR (
                    ((expected_resource_generation = 0 AND expected_provider_seam IS NULL
                        AND expected_provider_id IS NULL AND expected_provider_resource_id IS NULL)
                     OR (expected_resource_generation > 0 AND expected_provider_seam = 'Storage'
                        AND expected_provider_id IS NOT NULL AND expected_provider_resource_id IS NOT NULL))
                    AND ((target_provider_seam IS NULL AND target_provider_id IS NULL
                            AND target_provider_resource_id IS NULL)
                         OR (target_provider_seam = 'Storage' AND target_provider_id IS NOT NULL
                            AND target_provider_resource_id IS NOT NULL))
                    AND (operation NOT IN ('Create', 'Release')
                         OR (target_provider_seam IS NULL AND target_provider_id IS NULL
                             AND target_provider_resource_id IS NULL))
                    AND (operation NOT IN ('Bind', 'Unbind', 'Attach', 'Detach', 'Flush')
                         OR (target_provider_seam IS NOT DISTINCT FROM expected_provider_seam
                             AND target_provider_id IS NOT DISTINCT FROM expected_provider_id
                             AND target_provider_resource_id IS NOT DISTINCT FROM expected_provider_resource_id))
                    AND (effect_state <> 'Completed' OR target_resource_generation = 0
                         OR target_volume_state = 'Released' OR target_provider_seam = 'Storage')
                )
                """));
            entity.HasKey(row => row.OperationId);
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.ProjectId,
                row.RunId,
                row.EnvironmentId,
                row.IdempotencyKey
            }).IsUnique();
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.ProjectId,
                row.RunId,
                row.EnvironmentId,
                row.PolicyGeneration
            }).IsUnique().HasFilter(
                "\"effect_kind\" = 'NetworkPolicy' AND \"effect_state\" NOT IN ('Failed', 'Stale')");
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.ProjectId,
                row.RunId,
                row.EnvironmentId,
                row.ResourceId,
                row.TargetTransitionRevision
            }).IsUnique().HasFilter(
                "\"effect_kind\" = 'WorkspaceVolume' AND \"effect_state\" IN ('Reserved', 'Completed')");
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.ProjectId,
                row.RunId,
                row.EnvironmentId,
                row.ResourceId
            }).IsUnique().HasFilter(
                "\"effect_kind\" = 'WorkspaceVolume' AND \"effect_state\" IN ('Reserved', 'ReconciliationRequired')");
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.ProjectId,
                row.RunId,
                row.EnvironmentId,
                row.ResourceId
            }).IsUnique().HasFilter(
                "\"effect_kind\" = 'NetworkPolicy' AND \"effect_state\" IN ('Reserved', 'ReconciliationRequired')");
            entity.Property(row => row.OperationId).HasColumnName("operation_id");
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            entity.Property(row => row.ProjectId).HasColumnName("project_id").HasMaxLength(256);
            entity.Property(row => row.RunId).HasColumnName("run_id").HasMaxLength(256);
            entity.Property(row => row.EnvironmentId).HasColumnName("environment_id").HasMaxLength(256);
            entity.Property(row => row.LifecycleGeneration).HasColumnName("lifecycle_generation");
            entity.Property(row => row.EffectKind).HasColumnName("effect_kind").HasMaxLength(32);
            entity.Property(row => row.ResourceId).HasColumnName("resource_id").HasMaxLength(512);
            entity.Property(row => row.ExpectedProviderSeam).HasColumnName("expected_provider_seam").HasMaxLength(32);
            entity.Property(row => row.ExpectedProviderId).HasColumnName("expected_provider_id").HasMaxLength(256);
            entity.Property(row => row.ExpectedProviderResourceId)
                .HasColumnName("expected_provider_resource_id").HasMaxLength(512);
            entity.Property(row => row.TargetProviderSeam).HasColumnName("target_provider_seam").HasMaxLength(32);
            entity.Property(row => row.TargetProviderId).HasColumnName("target_provider_id").HasMaxLength(256);
            entity.Property(row => row.TargetProviderResourceId)
                .HasColumnName("target_provider_resource_id").HasMaxLength(512);
            entity.Property(row => row.PolicyGeneration).HasColumnName("policy_generation");
            entity.Property(row => row.ExpectedPreviousPolicyGeneration)
                .HasColumnName("expected_previous_policy_generation");
            entity.Property(row => row.ExpectedTransitionRevision).HasColumnName("expected_transition_revision");
            entity.Property(row => row.ExpectedResourceGeneration).HasColumnName("expected_resource_generation");
            entity.Property(row => row.ExpectedDataGeneration).HasColumnName("expected_data_generation");
            entity.Property(row => row.TargetTransitionRevision).HasColumnName("target_transition_revision");
            entity.Property(row => row.TargetResourceGeneration).HasColumnName("target_resource_generation");
            entity.Property(row => row.TargetDataGeneration).HasColumnName("target_data_generation");
            entity.Property(row => row.TargetVolumeState).HasColumnName("target_volume_state").HasMaxLength(16);
            entity.Property(row => row.Operation).HasColumnName("operation").HasMaxLength(32);
            entity.Property(row => row.SpecificationJson).HasColumnName("specification_json").HasColumnType("jsonb");
            entity.Property(row => row.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
            entity.Property(row => row.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64);
            entity.Property(row => row.State).HasColumnName("effect_state").HasMaxLength(32);
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.CompletedAt).HasColumnName("completed_at");
            entity.HasOne<EnvironmentOwnerRow>().WithMany()
                .HasForeignKey(row => new { row.TenantId, row.ProjectId, row.RunId, row.EnvironmentId })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
