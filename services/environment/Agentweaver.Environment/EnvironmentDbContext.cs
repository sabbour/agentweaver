using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Environment;

public sealed class EnvironmentDbContext(DbContextOptions<EnvironmentDbContext> options) : DbContext(options)
{
    public const string Schema = "environment";

    internal DbSet<EnvironmentOwnerRow> Owners => Set<EnvironmentOwnerRow>();
    internal DbSet<EnvironmentLifecycleOperationRow> LifecycleOperations =>
        Set<EnvironmentLifecycleOperationRow>();
    internal DbSet<EnvironmentOwnerEffectRow> OwnerEffects => Set<EnvironmentOwnerEffectRow>();
    internal DbSet<EnvironmentWorkspaceVolumeCleanupRow> WorkspaceVolumeCleanup =>
        Set<EnvironmentWorkspaceVolumeCleanupRow>();
    internal DbSet<RemoteMcpConnectionRow> RemoteMcpConnections => Set<RemoteMcpConnectionRow>();
    internal DbSet<RemoteMcpConnectionConfigurationRow> RemoteMcpConnectionConfigurations =>
        Set<RemoteMcpConnectionConfigurationRow>();
    internal DbSet<RemoteMcpCatalogSnapshotRow> RemoteMcpCatalogSnapshots =>
        Set<RemoteMcpCatalogSnapshotRow>();
    internal DbSet<RemoteMcpConnectionIdempotencyRow> RemoteMcpConnectionIdempotency =>
        Set<RemoteMcpConnectionIdempotencyRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        RemoteMcpConnectionModel.Configure(modelBuilder);

        modelBuilder.Entity<EnvironmentOwnerRow>(entity =>
        {
            entity.ToTable("owners", table =>
            {
                table.HasCheckConstraint("ck_environment_owners_lifecycle_generation", "lifecycle_generation > 0");
                table.HasCheckConstraint("ck_environment_owners_state", "state IN ('Active', 'Released')");
            });
            entity.HasKey(row => new { row.TenantId, row.ProjectId, row.RunId, row.EnvironmentId });
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            entity.Property(row => row.ProjectId).HasColumnName("project_id").HasMaxLength(256);
            entity.Property(row => row.RunId).HasColumnName("run_id").HasMaxLength(256);
            entity.Property(row => row.EnvironmentId).HasColumnName("environment_id").HasMaxLength(256);
            entity.Property(row => row.LifecycleGeneration)
                .HasColumnName("lifecycle_generation")
                .IsConcurrencyToken();
            entity.Property(row => row.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
            entity.Property(row => row.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<EnvironmentLifecycleOperationRow>(entity =>
        {
            entity.ToTable("lifecycle_operations", table =>
            {
                table.HasCheckConstraint(
                    "ck_environment_lifecycle_operations_expected_generation",
                    "expected_lifecycle_generation >= 0");
                table.HasCheckConstraint(
                    "ck_environment_lifecycle_operations_result_generation",
                    "result_lifecycle_generation > 0");
                table.HasCheckConstraint(
                    "ck_environment_lifecycle_operations_state",
                    "result_state IN ('Active', 'Released')");
            });
            entity.HasKey(row => row.OperationId);
            entity.Property(row => row.OperationId).HasColumnName("operation_id");
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            entity.Property(row => row.ProjectId).HasColumnName("project_id").HasMaxLength(256);
            entity.Property(row => row.RunId).HasColumnName("run_id").HasMaxLength(256);
            entity.Property(row => row.EnvironmentId).HasColumnName("environment_id").HasMaxLength(256);
            entity.Property(row => row.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
            entity.Property(row => row.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64);
            entity.Property(row => row.ExpectedLifecycleGeneration)
                .HasColumnName("expected_lifecycle_generation");
            entity.Property(row => row.ResultLifecycleGeneration)
                .HasColumnName("result_lifecycle_generation");
            entity.Property(row => row.ResultState).HasColumnName("result_state")
                .HasConversion<string>().HasMaxLength(16);
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(row => new
                {
                    row.TenantId,
                    row.ProjectId,
                    row.RunId,
                    row.EnvironmentId,
                    row.IdempotencyKey
                })
                .IsUnique();
            entity.HasOne<EnvironmentOwnerRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.ProjectId, row.RunId, row.EnvironmentId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EnvironmentOwnerEffectRow>(entity =>
        {
            entity.ToTable("owner_effects", table =>
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
                table.HasCheckConstraint(
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
                    """);
            });
            entity.HasKey(row => row.OperationId);
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
            entity.Property(row => row.ExpectedProviderBindingJson)
                .HasColumnName("expected_provider_binding_json").HasColumnType("jsonb");
            entity.Property(row => row.TargetProviderBindingJson)
                .HasColumnName("target_provider_binding_json").HasColumnType("jsonb");
            entity.Property(row => row.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
            entity.Property(row => row.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64);
            entity.Property(row => row.State).HasColumnName("effect_state").HasMaxLength(32);
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.CompletedAt).HasColumnName("completed_at");
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
                row.ResourceId
            }).IsUnique().HasFilter(
                "\"effect_kind\" = 'NetworkPolicy' AND \"effect_state\" IN ('Reserved', 'ReconciliationRequired')");
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
            entity.HasOne<EnvironmentOwnerRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.ProjectId, row.RunId, row.EnvironmentId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EnvironmentWorkspaceVolumeCleanupRow>(entity =>
        {
            entity.ToTable("workspace_volume_cleanup", table =>
            {
                table.HasCheckConstraint(
                    "ck_environment_workspace_volume_cleanup_state",
                    "state IN ('Pending', 'Leased', 'Blocked', 'Completed')");
                table.HasCheckConstraint(
                    "ck_environment_workspace_volume_cleanup_lease",
                    "lease_revision >= 0 AND ((state = 'Leased' AND lease_revision > 0 AND lease_id IS NOT NULL AND lease_expires_at IS NOT NULL) OR (state <> 'Leased' AND lease_id IS NULL AND lease_expires_at IS NULL))");
                table.HasCheckConstraint(
                    "ck_environment_workspace_volume_cleanup_generation",
                    "lifecycle_generation > 0 AND resource_generation > 0");
            });
            entity.HasKey(row => row.WorkId);
            entity.Property(row => row.WorkId).HasColumnName("work_id");
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            entity.Property(row => row.ProjectId).HasColumnName("project_id").HasMaxLength(256);
            entity.Property(row => row.RunId).HasColumnName("run_id").HasMaxLength(256);
            entity.Property(row => row.EnvironmentId).HasColumnName("environment_id").HasMaxLength(256);
            entity.Property(row => row.LifecycleGeneration).HasColumnName("lifecycle_generation");
            entity.Property(row => row.SourceReplaceOperationId).HasColumnName("source_replace_operation_id");
            entity.Property(row => row.VolumeId).HasColumnName("volume_id").HasMaxLength(512);
            entity.Property(row => row.ResourceGeneration).HasColumnName("resource_generation");
            entity.Property(row => row.ReleaseRequestJson).HasColumnName("release_request_json").HasColumnType("jsonb");
            entity.Property(row => row.State).HasColumnName("state").HasMaxLength(16);
            entity.Property(row => row.BlockReason).HasColumnName("block_reason").HasMaxLength(128);
            entity.Property(row => row.LeaseRevision).HasColumnName("lease_revision");
            entity.Property(row => row.LeaseId).HasColumnName("lease_id");
            entity.Property(row => row.LeaseExpiresAt).HasColumnName("lease_expires_at");
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.UpdatedAt).HasColumnName("updated_at");
            entity.Property(row => row.CompletedAt).HasColumnName("completed_at");
            entity.HasIndex(row => row.SourceReplaceOperationId).IsUnique();
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.ProjectId,
                row.RunId,
                row.EnvironmentId,
                row.State,
                row.CreatedAt
            });
            entity.HasOne<EnvironmentOwnerRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.ProjectId, row.RunId, row.EnvironmentId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<EnvironmentOwnerEffectRow>()
                .WithMany()
                .HasForeignKey(row => row.SourceReplaceOperationId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class EnvironmentOwnerRow
{
    public string TenantId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string EnvironmentId { get; set; } = string.Empty;
    public long LifecycleGeneration { get; set; }
    public EnvironmentLifecycleState State { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class EnvironmentLifecycleOperationRow
{
    public Guid OperationId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string EnvironmentId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public long ExpectedLifecycleGeneration { get; set; }
    public long ResultLifecycleGeneration { get; set; }
    public EnvironmentLifecycleState ResultState { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class EnvironmentOwnerEffectRow
{
    public Guid OperationId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string EnvironmentId { get; set; } = string.Empty;
    public long LifecycleGeneration { get; set; }
    public string EffectKind { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public string? ExpectedProviderSeam { get; set; }
    public string? ExpectedProviderId { get; set; }
    public string? ExpectedProviderResourceId { get; set; }
    public string? TargetProviderSeam { get; set; }
    public string? TargetProviderId { get; set; }
    public string? TargetProviderResourceId { get; set; }
    public long? PolicyGeneration { get; set; }
    public long? ExpectedPreviousPolicyGeneration { get; set; }
    public long? ExpectedTransitionRevision { get; set; }
    public long? ExpectedResourceGeneration { get; set; }
    public long? ExpectedDataGeneration { get; set; }
    public long? TargetTransitionRevision { get; set; }
    public long? TargetResourceGeneration { get; set; }
    public long? TargetDataGeneration { get; set; }
    public string? TargetVolumeState { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string? SpecificationJson { get; set; }
    public string? ExpectedProviderBindingJson { get; set; }
    public string? TargetProviderBindingJson { get; set; }
    public string State { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

internal sealed class EnvironmentWorkspaceVolumeCleanupRow
{
    public Guid WorkId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string EnvironmentId { get; set; } = string.Empty;
    public long LifecycleGeneration { get; set; }
    public Guid SourceReplaceOperationId { get; set; }
    public string VolumeId { get; set; } = string.Empty;
    public long ResourceGeneration { get; set; }
    public string ReleaseRequestJson { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? BlockReason { get; set; }
    public long LeaseRevision { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
