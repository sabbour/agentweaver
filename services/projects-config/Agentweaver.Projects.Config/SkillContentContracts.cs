using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

public sealed record SkillContentCandidate(
    ReadOnlyMemory<byte> SkillMarkdown,
    ImmutableArray<SkillContentResourceInput> Resources)
{
    public ValidatedSkillContent Validate()
    {
        if (Resources.IsDefault)
            throw new SkillContentValidationException("The skill resource inventory is required.");
        return SkillContentValidator.Validate(SkillMarkdown, Resources);
    }
}

public sealed record SkillContentSourceSelection(
    string SourceId,
    string SourceRevision,
    string RequestedRef,
    string ResolvedCommitSha,
    string SelectedPath);

public sealed record SkillContentPreview(
    string Name,
    string Description,
    string ContentDigest,
    int ResourceCount,
    long TotalBytes);

public sealed record SkillContentImportRequest(
    string IdempotencyKey,
    string ExpectedContentDigest,
    string? SkillId,
    long? ExpectedRevision,
    SkillContentSourceSelection? Source,
    SkillContentCandidate Candidate);

public sealed record SkillContentImportReceipt(
    string SkillId,
    long Revision,
    string Name,
    string Description,
    string ContentDigest,
    int ResourceCount,
    long TotalBytes);

public interface ISkillContentService
{
    SkillContentPreview Preview(SkillContentCandidate candidate);

    Task<SkillContentImportReceipt> ImportAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        SkillContentImportRequest request,
        CancellationToken cancellationToken);

    Task RevokeAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string skillId,
        long revision,
        string reason,
        CancellationToken cancellationToken);

    Task<ImmutableArray<SkillRuntimeContentV1>> ReadAcceptedRunSkillsAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string runId,
        string agentId,
        CancellationToken cancellationToken);
}

public sealed record SkillAssignmentUpdateRequest(
    long ExpectedProjectConfigurationRevision,
    string SkillId,
    long Revision,
    string ContentDigest,
    bool Enabled,
    int Order,
    ImmutableArray<string> AgentIds);

public interface ISkillAssignmentService
{
    Task<VersionedProjectConfiguration> UpdateAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        SkillAssignmentUpdateRequest request,
        CancellationToken cancellationToken);
}
