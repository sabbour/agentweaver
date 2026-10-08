using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Identity.Broker;

public enum CopilotConnectionState
{
    Pending, Connected, Refreshing, TransientUnavailable, ReconnectRequired, Revoked, RefreshIndeterminate
}

public sealed class CopilotConnectionRecord
{
    public Guid ConnectionId { get; set; }
    public string OwnerIssuer { get; set; } = string.Empty;
    public string OwnerActorId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public ProjectAuthorityResourceType Scope { get; set; }
    public string ScopeId { get; set; } = string.Empty;
    public string? GitHubUserId { get; set; }
    public RuntimeModelCredentialKind CredentialKind { get; set; } = RuntimeModelCredentialKind.GitHubUserAccess;
    public long Revision { get; set; }
    public CopilotConnectionState State { get; set; }
    public string? SecretId { get; set; }
    public string? SecretVersion { get; set; }
    public DateTimeOffset FreshUntil { get; set; }
    public string StateHash { get; set; } = string.Empty;
}

public sealed class CopilotConnectionRevision
{
    public Guid ConnectionId { get; set; }
    public long Revision { get; set; }
    public CopilotConnectionState State { get; set; }
    public string? SecretId { get; set; }
    public string? SecretVersion { get; set; }
    public DateTimeOffset FreshUntil { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

internal static class CopilotConnectionModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<CopilotConnectionRecord>(entity =>
        {
            entity.ToTable("copilot_connections");
            entity.HasKey(row => row.ConnectionId);
            entity.Property(row => row.OwnerIssuer).HasMaxLength(2048);
            entity.Property(row => row.OwnerActorId).HasMaxLength(256);
            entity.Property(row => row.TenantId).HasMaxLength(256);
            entity.Property(row => row.ScopeId).HasMaxLength(256);
            entity.Property(row => row.GitHubUserId).HasMaxLength(64);
            entity.Property(row => row.SecretId).HasMaxLength(256);
            entity.Property(row => row.SecretVersion).HasMaxLength(256);
            entity.Property(row => row.StateHash).HasMaxLength(64);
            entity.Property(row => row.Revision).IsConcurrencyToken();
        });
        model.Entity<CopilotConnectionRevision>(entity =>
        {
            entity.ToTable("copilot_connection_revisions");
            entity.HasKey(row => new { row.ConnectionId, row.Revision });
            entity.Property(row => row.SecretId).HasMaxLength(256);
            entity.Property(row => row.SecretVersion).HasMaxLength(256);
            entity.HasOne<CopilotConnectionRecord>().WithMany()
                .HasForeignKey(row => row.ConnectionId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
