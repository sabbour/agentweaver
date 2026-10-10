using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Environment;

internal static class RemoteMcpConnectionModel
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RemoteMcpConnectionRow>(entity =>
        {
            entity.ToTable("remote_mcp_connections", EnvironmentDbContext.Schema, table =>
            {
                table.HasCheckConstraint(
                    "ck_environment_remote_mcp_connections_revisions",
                    "row_revision > 0 AND current_configuration_revision > 0 AND (current_discovery_revision IS NULL OR current_discovery_revision > 0)");
                table.HasCheckConstraint(
                    "ck_environment_remote_mcp_connections_state",
                    "state IN ('Draft', 'Enabled', 'Disabled', 'Removed')");
            });
            entity.HasKey(row => new { row.TenantId, row.ProjectId, row.ConnectionId })
                .HasName("pk_environment_remote_mcp_connections");
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            entity.Property(row => row.ProjectId).HasColumnName("project_id").HasMaxLength(256);
            entity.Property(row => row.ConnectionId).HasColumnName("connection_id");
            entity.Property(row => row.RowRevision).HasColumnName("row_revision");
            entity.Property(row => row.CurrentConfigurationRevision).HasColumnName("current_configuration_revision");
            entity.Property(row => row.CurrentDiscoveryRevision).HasColumnName("current_discovery_revision");
            entity.Property(row => row.State).HasColumnName("state").HasMaxLength(16);
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<RemoteMcpConnectionConfigurationRow>(entity =>
        {
            entity.ToTable("remote_mcp_connection_configurations", EnvironmentDbContext.Schema, table =>
            {
                table.HasCheckConstraint(
                    "ck_environment_remote_mcp_connection_configurations_revision",
                    "configuration_revision > 0");
                table.HasCheckConstraint(
                    "ck_environment_remote_mcp_connection_configurations_authentication",
                    "authentication_mode IN ('None', 'DelegatedOAuth')");
                table.HasCheckConstraint(
                    "ck_environment_remote_mcp_connection_configurations_transport",
                    "transport_profile = 'StreamableHttp20250618'");
                table.HasCheckConstraint(
                    "ck_environment_remote_mcp_connection_configurations_registry_pin",
                    "(registry_server_name IS NULL AND registry_exact_version IS NULL AND registry_metadata_sha256 IS NULL) OR (registry_server_name IS NOT NULL AND registry_exact_version IS NOT NULL AND registry_exact_version <> 'latest' AND registry_metadata_sha256 ~ '^[0-9a-f]{64}$')");
                table.HasCheckConstraint(
                    "ck_environment_remote_mcp_connection_configurations_oauth_resource",
                    "authentication_mode <> 'DelegatedOAuth' OR resource_uri IS NOT NULL");
            });
            entity.HasKey(row => new
                {
                    row.TenantId,
                    row.ProjectId,
                    row.ConnectionId,
                    row.ConfigurationRevision
                })
                .HasName("pk_environment_remote_mcp_connection_configurations");
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            entity.Property(row => row.ProjectId).HasColumnName("project_id").HasMaxLength(256);
            entity.Property(row => row.ConnectionId).HasColumnName("connection_id");
            entity.Property(row => row.ConfigurationRevision).HasColumnName("configuration_revision");
            entity.Property(row => row.DisplayName).HasColumnName("display_name").HasMaxLength(100);
            entity.Property(row => row.EndpointUri).HasColumnName("endpoint_uri").HasMaxLength(2048);
            entity.Property(row => row.ResourceUri).HasColumnName("resource_uri").HasMaxLength(2048);
            entity.Property(row => row.AuthenticationMode).HasColumnName("authentication_mode").HasMaxLength(32);
            entity.Property(row => row.IdentityBindingReference).HasColumnName("identity_binding_reference").HasMaxLength(512);
            entity.Property(row => row.TransportProfile).HasColumnName("transport_profile").HasMaxLength(64);
            entity.Property(row => row.RegistryServerName).HasColumnName("registry_server_name").HasMaxLength(200);
            entity.Property(row => row.RegistryExactVersion).HasColumnName("registry_exact_version").HasMaxLength(255);
            entity.Property(row => row.RegistryMetadataSha256).HasColumnName("registry_metadata_sha256").HasMaxLength(64);
            entity.Property(row => row.ConfigurationSha256).HasColumnName("configuration_sha256").HasMaxLength(64);
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.HasOne<RemoteMcpConnectionRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.ProjectId, row.ConnectionId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_environment_remote_mcp_configurations_connection");
        });

        modelBuilder.Entity<RemoteMcpCatalogSnapshotRow>(entity =>
        {
            entity.ToTable("remote_mcp_catalog_snapshots", EnvironmentDbContext.Schema, table =>
            {
                table.HasCheckConstraint(
                    "ck_environment_remote_mcp_catalog_snapshots_revisions",
                    "configuration_revision > 0 AND discovery_revision > 0");
                table.HasCheckConstraint(
                    "ck_environment_remote_mcp_catalog_snapshots_digests",
                    "catalog_sha256 ~ '^[0-9a-f]{64}$' AND (registry_metadata_sha256 IS NULL OR registry_metadata_sha256 ~ '^[0-9a-f]{64}$') AND egress_intent_sha256 ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_environment_remote_mcp_catalog_snapshots_applied_policy",
                    "applied_policy_reference IS NOT NULL AND applied_policy_generation > 0 AND environment_tenant_id IS NOT NULL AND environment_project_id IS NOT NULL AND environment_run_id IS NOT NULL AND environment_id IS NOT NULL AND environment_lifecycle_generation > 0");
                table.HasCheckConstraint(
                    "ck_environment_remote_mcp_catalog_snapshots_tools_json",
                    "jsonb_typeof(tools_json) = 'array'");
            });
            entity.HasKey(row => new
                {
                    row.TenantId,
                    row.ProjectId,
                    row.ConnectionId,
                    row.ConfigurationRevision,
                    row.DiscoveryRevision
                })
                .HasName("pk_environment_remote_mcp_catalog_snapshots");
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            entity.Property(row => row.ProjectId).HasColumnName("project_id").HasMaxLength(256);
            entity.Property(row => row.ConnectionId).HasColumnName("connection_id");
            entity.Property(row => row.ConfigurationRevision).HasColumnName("configuration_revision");
            entity.Property(row => row.DiscoveryRevision).HasColumnName("discovery_revision");
            entity.Property(row => row.CatalogSha256).HasColumnName("catalog_sha256").HasMaxLength(64);
            entity.Property(row => row.RegistryMetadataSha256).HasColumnName("registry_metadata_sha256").HasMaxLength(64);
            entity.Property(row => row.ToolsJson).HasColumnName("tools_json").HasColumnType("jsonb");
            entity.Property(row => row.AppliedPolicyReference).HasColumnName("applied_policy_reference").HasMaxLength(512);
            entity.Property(row => row.EnvironmentTenantId).HasColumnName("environment_tenant_id").HasMaxLength(256);
            entity.Property(row => row.EnvironmentProjectId).HasColumnName("environment_project_id").HasMaxLength(256);
            entity.Property(row => row.EnvironmentRunId).HasColumnName("environment_run_id").HasMaxLength(256);
            entity.Property(row => row.EnvironmentId).HasColumnName("environment_id").HasMaxLength(256);
            entity.Property(row => row.EnvironmentLifecycleGeneration).HasColumnName("environment_lifecycle_generation");
            entity.Property(row => row.AppliedPolicyGeneration).HasColumnName("applied_policy_generation");
            entity.Property(row => row.EgressIntentSha256).HasColumnName("egress_intent_sha256").HasMaxLength(64);
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.HasOne<RemoteMcpConnectionConfigurationRow>()
                .WithMany()
                .HasForeignKey(row => new
                {
                    row.TenantId,
                    row.ProjectId,
                    row.ConnectionId,
                    row.ConfigurationRevision
                })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_environment_remote_mcp_catalog_snapshots_configuration");
        });

        modelBuilder.Entity<RemoteMcpConnectionIdempotencyRow>(entity =>
        {
            entity.ToTable("remote_mcp_connection_idempotency", EnvironmentDbContext.Schema, table =>
            {
                table.HasCheckConstraint(
                    "ck_environment_remote_mcp_connection_idempotency_fingerprint",
                    "request_fingerprint ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_environment_remote_mcp_connection_idempotency_operation",
                    "operation IN ('Create', 'Update', 'Enable', 'Disable', 'Remove', 'SaveDiscovery')");
                table.HasCheckConstraint(
                    "ck_environment_remote_mcp_connection_idempotency_result",
                    "jsonb_typeof(result_json) = 'object'");
            });
            entity.HasKey(row => new { row.TenantId, row.ProjectId, row.IdempotencyKey })
                .HasName("pk_environment_remote_mcp_connection_idempotency");
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            entity.Property(row => row.ProjectId).HasColumnName("project_id").HasMaxLength(256);
            entity.Property(row => row.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
            entity.Property(row => row.ConnectionId).HasColumnName("connection_id");
            entity.Property(row => row.Operation).HasColumnName("operation").HasMaxLength(32);
            entity.Property(row => row.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64);
            entity.Property(row => row.ResultJson).HasColumnName("result_json").HasColumnType("jsonb");
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
        });
    }
}

internal sealed class RemoteMcpConnectionRow
{
    public string TenantId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public Guid ConnectionId { get; set; }
    public long RowRevision { get; set; }
    public long CurrentConfigurationRevision { get; set; }
    public long? CurrentDiscoveryRevision { get; set; }
    public string State { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class RemoteMcpConnectionConfigurationRow
{
    public string TenantId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public Guid ConnectionId { get; set; }
    public long ConfigurationRevision { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string EndpointUri { get; set; } = string.Empty;
    public string? ResourceUri { get; set; }
    public string AuthenticationMode { get; set; } = string.Empty;
    public string? IdentityBindingReference { get; set; }
    public string TransportProfile { get; set; } = string.Empty;
    public string? RegistryServerName { get; set; }
    public string? RegistryExactVersion { get; set; }
    public string? RegistryMetadataSha256 { get; set; }
    public string ConfigurationSha256 { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class RemoteMcpCatalogSnapshotRow
{
    public string TenantId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public Guid ConnectionId { get; set; }
    public long ConfigurationRevision { get; set; }
    public long DiscoveryRevision { get; set; }
    public string CatalogSha256 { get; set; } = string.Empty;
    public string? RegistryMetadataSha256 { get; set; }
    public string ToolsJson { get; set; } = "[]";
    public string AppliedPolicyReference { get; set; } = string.Empty;
    public string EnvironmentTenantId { get; set; } = string.Empty;
    public string EnvironmentProjectId { get; set; } = string.Empty;
    public string EnvironmentRunId { get; set; } = string.Empty;
    public string EnvironmentId { get; set; } = string.Empty;
    public long EnvironmentLifecycleGeneration { get; set; }
    public long AppliedPolicyGeneration { get; set; }
    public string EgressIntentSha256 { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class RemoteMcpConnectionIdempotencyRow
{
    public string TenantId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public Guid ConnectionId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public string ResultJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
