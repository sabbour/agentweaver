using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

/// <summary>Raw UTF-8 Skill.md bytes and bundled resources awaiting validation.</summary>
public sealed record SkillContentCandidate(
    ReadOnlyMemory<byte> SkillMarkdown,
    ImmutableArray<SkillContentResourceInput> Resources)
{
    /// <summary>Validates the manifest and resources and computes the canonical content digest.</summary>
    public ValidatedSkillContent Validate()
    {
        if (Resources.IsDefault)
            throw new SkillContentValidationException("The skill resource inventory is required.");
        return SkillContentValidator.Validate(SkillMarkdown, Resources);
    }
}

/// <summary>Source repository revision, resolved commit, and selected path retained as import provenance.</summary>
public sealed record SkillContentSourceSelection(
    string SourceId,
    string SourceRevision,
    string RequestedRef,
    string ResolvedCommitSha,
    string SelectedPath);

/// <summary>Validated skill metadata and digest returned before an import is committed.</summary>
public sealed record SkillContentPreview(
    string Name,
    string Description,
    string ContentDigest,
    int ResourceCount,
    long TotalBytes);

/// <summary>Import input containing idempotency, content, optional revision-fence, and source-provenance data.</summary>
public sealed record SkillContentImportRequest(
    string IdempotencyKey,
    string ExpectedContentDigest,
    string? SkillId,
    long? ExpectedRevision,
    SkillContentSourceSelection? Source,
    SkillContentCandidate Candidate);

/// <summary>Receipt identifying the immutable skill revision and its validated content digest.</summary>
public sealed record SkillContentImportReceipt(
    string SkillId,
    long Revision,
    string Name,
    string Description,
    string ContentDigest,
    int ResourceCount,
    long TotalBytes);

/// <summary>One immutable skill revision with source provenance and current revocation status.</summary>
public sealed record SkillContentRevisionSummary(
    string SkillId,
    long Revision,
    string Name,
    string Description,
    string ContentDigest,
    int ResourceCount,
    long TotalBytes,
    string CreatedByActorId,
    DateTimeOffset CreatedAt,
    string? SourceId,
    string? SourceRevision,
    string? RequestedRef,
    string? ResolvedCommitSha,
    string? SelectedPath,
    bool IsRevoked,
    string? RevokedByActorId,
    DateTimeOffset? RevokedAt);

/// <summary>Current project configuration assignment for one skill.</summary>
public sealed record SkillContentAssignmentSummary(
    string SkillId,
    bool Enabled,
    int Order,
    long? Revision,
    string? ContentDigest,
    ImmutableArray<string>? AgentIds);

/// <summary>Bounded skill inventory page with the configuration revision and opaque continuation cursor.</summary>
public sealed record SkillContentInventoryPage(
    long ConfigurationRevision,
    ImmutableArray<SkillContentAssignmentSummary> Assignments,
    ImmutableArray<SkillContentRevisionSummary> Revisions,
    int PageSize,
    string? NextCursor);

/// <summary>Owner boundary for preview, inventory, import, revocation, and accepted-run skill reads.</summary>
public interface ISkillContentService
{
    /// <summary>Validates proposed bytes without writing content or project state.</summary>
    SkillContentPreview Preview(SkillContentCandidate candidate);

    /// <summary>Lists bounded revision and assignment metadata after checking current project-owner authority.</summary>
    Task<SkillContentInventoryPage> ListInventoryAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Stores an immutable content revision and idempotency receipt after digest and authority checks.</summary>
    Task<SkillContentImportReceipt> ImportAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        SkillContentImportRequest request,
        CancellationToken cancellationToken);

    /// <summary>Appends a revocation marker for a specific revision; repeated revocation is a no-op.</summary>
    Task RevokeAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string skillId,
        long revision,
        string reason,
        CancellationToken cancellationToken);

    /// <summary>Reads only skills pinned by the accepted run and assigned to the requested agent.</summary>
    Task<ImmutableArray<SkillRuntimeContentV1>> ReadAcceptedRunSkillsAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string runId,
        string agentId,
        CancellationToken cancellationToken);
}

/// <summary>Requested skill revision, digest, enabled state, ordering, and target project agents.</summary>
public sealed record SkillAssignmentUpdateRequest(
    long ExpectedProjectConfigurationRevision,
    string SkillId,
    long Revision,
    string ContentDigest,
    bool Enabled,
    int Order,
    ImmutableArray<string> AgentIds);

/// <summary>Owner boundary for updating a project skill assignment through its normal configuration revision.</summary>
public interface ISkillAssignmentService
{
    /// <summary>Changes one assignment only when the expected configuration revision is still current.</summary>
    Task<VersionedProjectConfiguration> UpdateAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        SkillAssignmentUpdateRequest request,
        CancellationToken cancellationToken);
}
