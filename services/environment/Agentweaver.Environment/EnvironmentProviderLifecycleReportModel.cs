using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Environment;

internal static class EnvironmentProviderLifecycleReportModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EnvironmentProviderLifecycleReportRow>(entity =>
        {
            entity.ToTable("provider_lifecycle_reports", table =>
            {
                table.HasCheckConstraint(
                    "ck_environment_provider_lifecycle_reports_generations",
                    """
                    contract_version > 0 AND lifecycle_generation > 0 AND resource_generation > 0
                    AND provider_fencing_generation > 0
                    AND current_fencing_generation = provider_fencing_generation
                    AND lease_revision > 0
                    """);
                table.HasCheckConstraint(
                    "ck_environment_provider_lifecycle_reports_kind",
                    "event_kind IN ('Suspend', 'Relocation')");
                table.HasCheckConstraint(
                    "ck_environment_provider_lifecycle_reports_state",
                    "reconciliation_state = 'Pending'");
                table.HasCheckConstraint(
                    "ck_environment_provider_lifecycle_reports_core_fence",
                    "core_execution_fence IS NULL OR core_execution_fence > 0");
            });
            entity.HasKey(row => new
            {
                row.TenantId,
                row.ProjectId,
                row.RunId,
                row.EnvironmentId,
                row.ProviderEventId
            });
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            entity.Property(row => row.ProjectId).HasColumnName("project_id").HasMaxLength(256);
            entity.Property(row => row.RunId).HasColumnName("run_id").HasMaxLength(256);
            entity.Property(row => row.EnvironmentId).HasColumnName("environment_id").HasMaxLength(256);
            entity.Property(row => row.ProviderEventId).HasColumnName("provider_event_id");
            entity.Property(row => row.ContractVersion).HasColumnName("contract_version");
            entity.Property(row => row.EventKind).HasColumnName("event_kind").HasMaxLength(32);
            entity.Property(row => row.ReportedAt).HasColumnName("reported_at");
            entity.Property(row => row.ProviderId).HasColumnName("provider_id").HasMaxLength(128);
            entity.Property(row => row.AdapterVersion).HasColumnName("adapter_version").HasMaxLength(32);
            entity.Property(row => row.ResourceId).HasColumnName("resource_id").HasMaxLength(512);
            entity.Property(row => row.LifecycleGeneration).HasColumnName("lifecycle_generation");
            entity.Property(row => row.ResourceGeneration).HasColumnName("resource_generation");
            entity.Property(row => row.ProviderFencingGeneration).HasColumnName("provider_fencing_generation");
            entity.Property(row => row.CurrentFencingGeneration).HasColumnName("current_fencing_generation");
            entity.Property(row => row.LeaseRevision).HasColumnName("lease_revision");
            entity.Property(row => row.SandboxOperationId).HasColumnName("sandbox_operation_id");
            entity.Property(row => row.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64);
            entity.Property(row => row.CoreOperationKey).HasColumnName("core_operation_key").HasMaxLength(128);
            entity.Property(row => row.CoreExecutionFence).HasColumnName("core_execution_fence");
            entity.Property(row => row.State)
                .HasColumnName("reconciliation_state")
                .HasConversion<string>()
                .HasMaxLength(32);
            entity.Property(row => row.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(128);
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.ProjectId,
                row.RunId,
                row.EnvironmentId,
                row.CoreOperationKey
            }).IsUnique();
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.ProjectId,
                row.RunId,
                row.EnvironmentId,
                row.CreatedAt
            }).HasFilter("\"reconciliation_state\" = 'Pending'");
            entity.HasOne<EnvironmentOwnerRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.ProjectId, row.RunId, row.EnvironmentId })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class EnvironmentProviderLifecycleReportRow
{
    public string TenantId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string EnvironmentId { get; set; } = string.Empty;
    public Guid ProviderEventId { get; set; }
    public int ContractVersion { get; set; }
    public string EventKind { get; set; } = string.Empty;
    public DateTimeOffset ReportedAt { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string AdapterVersion { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public long LifecycleGeneration { get; set; }
    public long ResourceGeneration { get; set; }
    public long ProviderFencingGeneration { get; set; }
    public long CurrentFencingGeneration { get; set; }
    public long LeaseRevision { get; set; }
    public Guid SandboxOperationId { get; set; }
    public string RequestFingerprint { get; set; } = string.Empty;
    public string CoreOperationKey { get; set; } = string.Empty;
    public long? CoreExecutionFence { get; set; }
    public EnvironmentProviderLifecycleReportState State { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
