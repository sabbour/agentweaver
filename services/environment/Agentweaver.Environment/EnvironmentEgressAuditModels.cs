using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Environment;

internal sealed class EnvironmentSandboxOwnerMappingRow
{
    public string OwnerFingerprint { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string EnvironmentId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class EnvironmentAttributedEgressFlowRow
{
    public string FlowId { get; set; } = "";
    public string OwnerFingerprint { get; set; } = "";
    public string? Host { get; set; }
    public string? Address { get; set; }
    public int Port { get; set; }
    public string Verdict { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public long FlowCount { get; set; }
    public string? WorkloadRef { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

internal sealed class EnvironmentUnattributedEgressFlowRow
{
    public string FlowId { get; set; } = "";
    public string? Host { get; set; }
    public string? Address { get; set; }
    public int Port { get; set; }
    public string Verdict { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public long FlowCount { get; set; }
    public string? WorkloadRef { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

internal static class EnvironmentEgressAuditModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EnvironmentSandboxOwnerMappingRow>(entity =>
        {
            entity.ToTable("sandbox_owner_mappings", table =>
            {
                table.HasCheckConstraint(
                    "ck_environment_sandbox_owner_mappings_fingerprint",
                    "owner_fingerprint ~ '^[0-9a-f]{40}$'");
            });
            entity.HasKey(row => row.OwnerFingerprint);
            entity.HasIndex(row => new { row.TenantId, row.ProjectId, row.RunId, row.EnvironmentId })
                .IsUnique();
            entity.Property(row => row.OwnerFingerprint).HasColumnName("owner_fingerprint").HasMaxLength(40);
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            entity.Property(row => row.ProjectId).HasColumnName("project_id").HasMaxLength(256);
            entity.Property(row => row.RunId).HasColumnName("run_id").HasMaxLength(256);
            entity.Property(row => row.EnvironmentId).HasColumnName("environment_id").HasMaxLength(256);
            entity.Property(row => row.CreatedAt).HasColumnName("created_at")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<EnvironmentAttributedEgressFlowRow>(entity =>
        {
            entity.ToTable("sandbox_egress_flows_attributed", table =>
            {
                table.HasCheckConstraint(
                    "ck_environment_sandbox_egress_attributed_port",
                    "port BETWEEN 1 AND 65535");
                table.HasCheckConstraint(
                    "ck_environment_sandbox_egress_attributed_verdict",
                    "verdict IN ('Allowed', 'Denied')");
                table.HasCheckConstraint(
                    "ck_environment_sandbox_egress_attributed_times",
                    "last_seen >= first_seen");
                table.HasCheckConstraint(
                    "ck_environment_sandbox_egress_attributed_count",
                    "flow_count > 0");
            });
            entity.HasKey(row => row.FlowId);
            entity.Property(row => row.FlowId).HasColumnName("flow_id").HasMaxLength(64);
            entity.Property(row => row.OwnerFingerprint).HasColumnName("owner_fingerprint").HasMaxLength(40);
            entity.Property(row => row.Host).HasColumnName("host").HasMaxLength(512);
            entity.Property(row => row.Address).HasColumnName("address").HasMaxLength(512);
            entity.Property(row => row.Port).HasColumnName("port");
            entity.Property(row => row.Verdict).HasColumnName("verdict").HasMaxLength(8);
            entity.Property(row => row.FirstSeen).HasColumnName("first_seen");
            entity.Property(row => row.LastSeen).HasColumnName("last_seen");
            entity.Property(row => row.FlowCount).HasColumnName("flow_count");
            entity.Property(row => row.WorkloadRef).HasColumnName("workload_ref").HasMaxLength(512);
            entity.Property(row => row.RecordedAt).HasColumnName("recorded_at")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasOne<EnvironmentSandboxOwnerMappingRow>()
                .WithMany()
                .HasForeignKey(row => row.OwnerFingerprint)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(row => new { row.OwnerFingerprint, row.FirstSeen });
        });

        modelBuilder.Entity<EnvironmentUnattributedEgressFlowRow>(entity =>
        {
            entity.ToTable("sandbox_egress_flows_unattributed", table =>
            {
                table.HasCheckConstraint(
                    "ck_environment_sandbox_egress_unattributed_port",
                    "port BETWEEN 1 AND 65535");
                table.HasCheckConstraint(
                    "ck_environment_sandbox_egress_unattributed_verdict",
                    "verdict IN ('Allowed', 'Denied')");
                table.HasCheckConstraint(
                    "ck_environment_sandbox_egress_unattributed_times",
                    "last_seen >= first_seen");
                table.HasCheckConstraint(
                    "ck_environment_sandbox_egress_unattributed_count",
                    "flow_count > 0");
            });
            entity.HasKey(row => row.FlowId);
            entity.Property(row => row.FlowId).HasColumnName("flow_id").HasMaxLength(64);
            entity.Property(row => row.Host).HasColumnName("host").HasMaxLength(512);
            entity.Property(row => row.Address).HasColumnName("address").HasMaxLength(512);
            entity.Property(row => row.Port).HasColumnName("port");
            entity.Property(row => row.Verdict).HasColumnName("verdict").HasMaxLength(8);
            entity.Property(row => row.FirstSeen).HasColumnName("first_seen");
            entity.Property(row => row.LastSeen).HasColumnName("last_seen");
            entity.Property(row => row.FlowCount).HasColumnName("flow_count");
            entity.Property(row => row.WorkloadRef).HasColumnName("workload_ref").HasMaxLength(512);
            entity.Property(row => row.RecordedAt).HasColumnName("recorded_at")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(row => row.FirstSeen);
        });
    }
}
