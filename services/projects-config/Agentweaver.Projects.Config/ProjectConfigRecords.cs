using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

public enum ProjectLifecycleState
{
    Active,
    Archived
}

public sealed class ProjectRecord
{
    public required string ProjectId { get; set; }
    public required string TenantId { get; set; }
    public required string CreatedByActorId { get; set; }
    public required string Name { get; set; }
    public ProjectLifecycleState State { get; set; }
    public long Revision { get; set; }
    public long ConfigurationRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ProjectAuthorityRecordState
{
    Active,
    Revoked
}

public enum ProjectAuthorityRole
{
    PlatformAdmin,
    TenantAdmin,
    Owner,
    Contributor,
    Viewer,
    Orchestrator
}

public sealed class ProjectTenantMembershipRecord
{
    public Guid MembershipId { get; set; }
    public required string Issuer { get; set; }
    public required string Subject { get; set; }
    public required string TenantId { get; set; }
    public ProjectAuthorityRecordState State { get; set; }
    public long Revision { get; set; }
    public required string GrantedBy { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public string? RevokedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class ProjectRoleAssignmentRecord
{
    public Guid AssignmentId { get; set; }
    public Guid MembershipId { get; set; }
    public ProjectAuthorityResourceType ResourceType { get; set; }
    public required string ResourceId { get; set; }
    public ProjectAuthorityRole Role { get; set; }
    public ProjectAuthorityRecordState State { get; set; }
    public long Revision { get; set; }
    public required string GrantedBy { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public string? RevokedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class ProjectAuthorityAuditRecord
{
    public Guid EventId { get; set; }
    public required string EventType { get; set; }
    public Guid MembershipId { get; set; }
    public Guid? AssignmentId { get; set; }
    public required string Issuer { get; set; }
    public required string Subject { get; set; }
    public required string TenantId { get; set; }
    public ProjectAuthorityResourceType? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public ProjectAuthorityRole? Role { get; set; }
    public long Revision { get; set; }
    public required string Actor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ProjectConfigurationRevisionRecord
{
    public required string ProjectId { get; set; }
    public long Revision { get; set; }
    public required string ConfigurationJson { get; set; }
    public required string UpdatedByActorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PlatformRuntimeHeadRecord
{
    public const string SingletonId = "default";

    public required string Id { get; set; }
    public long CurrentRevision { get; set; }
}

public sealed class PlatformRuntimeRevisionRecord
{
    public required string HeadId { get; set; }
    public long Revision { get; set; }
    public required string ConfigurationJson { get; set; }
    public required string UpdatedByActorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ProjectRunSelectionRecord
{
    public required string RunId { get; set; }
    public required string ProjectId { get; set; }
    public long ProjectRevision { get; set; }
    public long ProjectConfigurationRevision { get; set; }
    public long PlatformRuntimeRevision { get; set; }
    public required string ContextRevision { get; set; }
    public required string RequestFingerprint { get; set; }
    public required string SnapshotJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record BrokerRunBindingProofResponse(
    string ActorIssuer,
    string ActorSubject,
    string TenantId,
    Guid MembershipId,
    long MembershipRevision,
    string ProjectId,
    string RunId,
    Guid AssignmentId,
    long AssignmentRevision,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    string ContextRevision,
    string SelectionHash);

public enum ProjectSetupOperationKind
{
    Setup,
    Cleanup
}

public enum ProjectSetupOperationResultState
{
    Created,
    Cleaned,
    RetainedPending
}

public sealed class ProjectSetupOperationRecord
{
    public Guid InvocationId { get; set; }
    public ProjectSetupOperationKind OperationKind { get; set; }
    public required string RequestHash { get; set; }
    public required string Issuer { get; set; }
    public required string Subject { get; set; }
    public required string TenantId { get; set; }
    public Guid MembershipId { get; set; }
    public long MembershipRevision { get; set; }
    public required string ProjectId { get; set; }
    public long ProjectRevision { get; set; }
    public long ProjectConfigurationRevision { get; set; }
    public Guid AssignmentId { get; set; }
    public long AssignmentRevision { get; set; }
    public long? ExpectedProjectRevision { get; set; }
    public long? ExpectedAssignmentRevision { get; set; }
    public Guid? AuthorityAuditEventId { get; set; }
    public ProjectSetupOperationResultState ResultState { get; set; }
    public required string ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record PrivilegedProjectSetupInput(
    int Version,
    string AccessToken,
    Guid InvocationId,
    ProjectSetupOperationKind OperationKind,
    string TenantId,
    long? ExpectedProjectRevision,
    long? ExpectedAssignmentRevision);

public sealed record ProjectSetupOperationRequest(
    int Version,
    Guid InvocationId,
    ProjectSetupOperationKind OperationKind,
    string TenantId,
    long? ExpectedProjectRevision,
    long? ExpectedAssignmentRevision);

public sealed record ProjectSetupReceipt(
    Guid InvocationId,
    string Issuer,
    string Subject,
    string TenantId,
    Guid MembershipId,
    long MembershipRevision,
    string ProjectId,
    string CreatedByActorId,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    Guid AssignmentId,
    long AssignmentRevision,
    Guid AuthorityAuditEventId,
    DateTimeOffset CreatedAt);

public sealed record ProjectSetupReadback(
    bool ProjectExists,
    ProjectLifecycleState? ProjectState,
    long? ProjectRevision,
    long? ProjectConfigurationRevision,
    bool InitialConfigurationExists,
    bool AssignmentExists,
    ProjectAuthorityRecordState? AssignmentState,
    long? AssignmentRevision,
    bool AuthorityAuditExists);

public sealed record ProjectCleanupReceipt(
    Guid InvocationId,
    string TenantId,
    string ProjectId,
    Guid AssignmentId,
    ProjectSetupOperationResultState State,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    long AssignmentRevision,
    Guid? AuthorityAuditEventId,
    DateTimeOffset CreatedAt);

public sealed record ProjectSetupCommandResult(
    ProjectSetupOperationKind OperationKind,
    bool Replayed,
    ProjectSetupReceipt? Setup,
    ProjectSetupReadback? SetupReadback,
    ProjectCleanupReceipt? Cleanup);

public enum ProjectCastingProposalState
{
    Pending,
    Confirmed,
    Rejected
}

public sealed class ProjectCastingProposalRecord
{
    public Guid ProposalId { get; set; }
    public required string ProjectId { get; set; }
    public long BaseConfigurationRevision { get; set; }
    public long DraftRevision { get; set; }
    public ProjectCastingProposalState State { get; set; }
    public required string DraftJson { get; set; }
    public long? ConfirmedConfigurationRevision { get; set; }
    public string? ResultJson { get; set; }
    public int? TransferFormatVersion { get; set; }
    public string? TransferSourceProjectId { get; set; }
    public long? TransferSourceConfigurationRevision { get; set; }
    public string? TransferContentDigest { get; set; }
    public required string CreatedByActorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string UpdatedByActorId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ProjectSkillContentRevisionRecord
{
    public required string ProjectId { get; set; }
    public required string SkillId { get; set; }
    public long Revision { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public required string ContentDigest { get; set; }
    public required string ObjectKey { get; set; }
    public int ResourceCount { get; set; }
    public long TotalBytes { get; set; }
    public required string CreatedByActorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? SourceId { get; set; }
    public string? SourceRevision { get; set; }
    public string? RequestedRef { get; set; }
    public string? ResolvedCommitSha { get; set; }
    public string? SelectedPath { get; set; }
}

public sealed class ProjectSkillImportIdempotencyRecord
{
    public required string ProjectId { get; set; }
    public required string ScopeDigest { get; set; }
    public required string ActorIssuer { get; set; }
    public required string ActorId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestDigest { get; set; }
    public required string ReceiptJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ProjectSkillContentRevocationRecord
{
    public required string ProjectId { get; set; }
    public required string SkillId { get; set; }
    public long Revision { get; set; }
    public required string RevokedByActorId { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset RevokedAt { get; set; }
}

public sealed class ProjectConfigException(
    string code,
    string message,
    int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;

    public static ProjectConfigException NotFound() =>
        new("not_found", "The requested project or configuration was not found.", StatusCodes.Status404NotFound);

    public static ProjectConfigException Conflict(string message) =>
        new("revision_conflict", message, StatusCodes.Status409Conflict);

    public static ProjectConfigException Conflict(string code, string message) =>
        new(code, message, StatusCodes.Status409Conflict);

    public static ProjectConfigException Invalid(string code, string message) =>
        new(code, message, StatusCodes.Status400BadRequest);

    public static ProjectConfigException IdempotencyConflict() =>
        new("run_selection_conflict", "The run ID already has a different immutable selection.", StatusCodes.Status409Conflict);

    public static ProjectConfigException Forbidden() =>
        new("forbidden", "The authenticated caller is not authorized for this operation.", StatusCodes.Status403Forbidden);
}
