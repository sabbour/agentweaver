using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

/// <summary>Current project Owner evidence, including when no repository is connected.</summary>
public sealed record IssueSyncOwnerProof(string ProjectId, string TenantId, string CreatorId);

/// <summary>Current project-owner and repository evidence, never a user credential.</summary>
public sealed record IssueSyncProjectProof(string ProjectId, string TenantId, string CreatorId,
    string ConnectionId, SourceControlRepositoryIdentity Repository);

/// <summary>Safe Identity-owned grant receipt.</summary>
public sealed record IssueSyncGrantReceipt(Guid GrantId, IssueSyncProjectProof Project, bool Active);

/// <summary>Identity's private request to revalidate the grant creator against current Projects authority.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IssueSyncAuthorityQuery(string ProjectId, string TenantId, string CreatorId);

/// <summary>Owner-controlled bounded periodic synchronization settings.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IssueSyncSettings(bool Enabled, Guid GrantId, int IntervalSeconds, int MaxItems,
    int MaxCycleSeconds)
{
    public void Validate()
    {
        if (GrantId == Guid.Empty || IntervalSeconds is < 30 or > 86400 ||
            MaxItems is < 1 or > 100 || MaxCycleSeconds is < 5 or > 120 ||
            MaxCycleSeconds > IntervalSeconds)
            throw new ArgumentException("Issue sync settings exceed their bounds.");
    }
}
