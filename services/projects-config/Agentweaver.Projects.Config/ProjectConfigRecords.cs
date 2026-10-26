using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

/// <summary>Lifecycle states that control whether a project can accept new configuration changes or runs.</summary>
public enum ProjectLifecycleState
{
    Active,
    Archived
}

/// <summary>Mutable project metadata and revision heads; configuration bytes are stored in separate append-only rows.</summary>
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

/// <summary>Whether an authority membership or assignment can currently grant permissions.</summary>
public enum ProjectAuthorityRecordState
{
    Active,
    Revoked
}

/// <summary>Roles recognized by the Projects &amp; Config authority owner.</summary>
public enum ProjectAuthorityRole
{
    PlatformAdmin,
    TenantAdmin,
    Owner,
    Contributor,
    Viewer,
    Orchestrator
}

/// <summary>An issuer-and-subject membership in one tenant, with a revisioned revocation state.</summary>
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

/// <summary>A revisioned role grant scoped to one platform, tenant, or project resource.</summary>
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

/// <summary>Append-only evidence for membership grants/revocations and role assignments/revocations.</summary>
public sealed class ProjectAuthorityAuditRecord
{
    public Guid EventId { get; set; }
    public Guid? InvocationId { get; set; }
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

/// <summary>An immutable JSON snapshot for one project configuration revision.</summary>
public sealed class ProjectConfigurationRevisionRecord
{
    public required string ProjectId { get; set; }
    public long Revision { get; set; }
    public required string ConfigurationJson { get; set; }
    public required string UpdatedByActorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The singleton head that points to the current platform runtime-default revision.</summary>
public sealed class PlatformRuntimeHeadRecord
{
    public const string SingletonId = "default";

    public required string Id { get; set; }
    public long CurrentRevision { get; set; }
}

/// <summary>An immutable JSON snapshot of platform runtime defaults.</summary>
public sealed class PlatformRuntimeRevisionRecord
{
    public required string HeadId { get; set; }
    public long Revision { get; set; }
    public required string ConfigurationJson { get; set; }
    public required string UpdatedByActorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An immutable selection snapshot bound to one run ID and the revisions used to resolve it.</summary>
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

/// <summary>Current actor, role, project, configuration, and selection hashes proven to the private Broker route.</summary>
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

/// <summary>The operator setup or cleanup operation represented by a privileged project request.</summary>
public enum ProjectSetupOperationKind
{
    Setup,
    Cleanup
}

/// <summary>Whether setup created state or cleanup completed or retained it pending existing run evidence.</summary>
public enum ProjectSetupOperationResultState
{
    Created,
    Cleaned,
    RetainedPending
}

/// <summary>Immutable idempotency record and receipt binding for a privileged setup or cleanup invocation.</summary>
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
    public Guid? OwnerAssignmentId { get; set; }
    public long? OwnerAssignmentRevision { get; set; }
    public Guid? OwnerAuthorityAuditEventId { get; set; }
    public Guid? OwnerRevocationAuditEventId { get; set; }
    public long? ExpectedProjectRevision { get; set; }
    public long? ExpectedAssignmentRevision { get; set; }
    public Guid? AuthorityAuditEventId { get; set; }
    public ProjectSetupOperationResultState ResultState { get; set; }
    public required string ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Standard-input envelope containing the access token and versioned operation details.</summary>
public sealed record PrivilegedProjectSetupInput(
    int Version,
    string AccessToken,
    Guid InvocationId,
    ProjectSetupOperationKind OperationKind,
    string TenantId,
    long? ExpectedProjectRevision,
    long? ExpectedAssignmentRevision);

/// <summary>Token-free operation data passed to the privileged authority store after authentication.</summary>
public sealed record ProjectSetupOperationRequest(
    int Version,
    Guid InvocationId,
    ProjectSetupOperationKind OperationKind,
    string TenantId,
    long? ExpectedProjectRevision,
    long? ExpectedAssignmentRevision);

/// <summary>Immutable receipt proving which project, initial revisions, assignments, and audit events setup created.</summary>
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
    DateTimeOffset CreatedAt)
{
    public Guid? OwnerAssignmentId { get; init; }
    public long? OwnerAssignmentRevision { get; init; }
    public Guid? OwnerAuthorityAuditEventId { get; init; }
}

/// <summary>Readback of setup-owned resources, used to report whether the original receipt still matches stored state.</summary>
public sealed record ProjectSetupReadback(
    bool ProjectExists,
    ProjectLifecycleState? ProjectState,
    long? ProjectRevision,
    long? ProjectConfigurationRevision,
    bool InitialConfigurationExists,
    bool AssignmentExists,
    ProjectAuthorityRecordState? AssignmentState,
    long? AssignmentRevision,
    bool AuthorityAuditExists,
    Guid? OwnerAssignmentId = null,
    bool OwnerAssignmentExists = false,
    ProjectAuthorityRecordState? OwnerAssignmentState = null,
    long? OwnerAssignmentRevision = null,
    bool OwnerAuthorityAuditExists = false);

/// <summary>Receipt for cleanup, including whether existing selections prevented destructive cleanup.</summary>
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
    DateTimeOffset CreatedAt)
{
    public Guid? OwnerAssignmentId { get; init; }
    public long? OwnerAssignmentRevision { get; init; }
    public Guid? OwnerRevocationAuditEventId { get; init; }
}

/// <summary>Command result containing a setup receipt/readback or cleanup receipt and whether the call was replayed.</summary>
public sealed record ProjectSetupCommandResult(
    ProjectSetupOperationKind OperationKind,
    bool Replayed,
    ProjectSetupReceipt? Setup,
    ProjectSetupReadback? SetupReadback,
    ProjectCleanupReceipt? Cleanup);

/// <summary>Review lifecycle for a proposed project casting change.</summary>
public enum ProjectCastingProposalState
{
    Pending,
    Confirmed,
    Rejected
}

/// <summary>Persisted proposal draft and base/draft revisions used to serialize review and confirmation.</summary>
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

/// <summary>Generation approaches supported for reviewable casting proposals.</summary>
public enum ProjectCastingGenerationMode
{
    Formulate,
    Analyze
}

/// <summary>Lifecycle states for one run-bound casting generation request.</summary>
public enum ProjectCastingGenerationRequestState
{
    Pending,
    Completed,
    Failed,
    Stale,
    Cancelled
}

/// <summary>Idempotency and run binding for a generated casting proposal.</summary>
public sealed class ProjectCastingGenerationRequestRecord
{
    public Guid RequestId { get; set; }
    public required string ProjectId { get; set; }
    public Guid ProposalId { get; set; }
    public long BaseConfigurationRevision { get; set; }
    public required string RunId { get; set; }
    public ProjectCastingGenerationMode Mode { get; set; }
    public ProjectCastingGenerationRequestState State { get; set; }
    public required string ActorIssuer { get; set; }
    public required string ActorId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestDigest { get; set; }
    public string? OutputDigest { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Immutable metadata and object-store key for one imported skill content revision.</summary>
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

/// <summary>Actor-scoped idempotency key and request digest for a completed skill import.</summary>
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

/// <summary>Append-only revocation marker that makes one immutable skill revision unavailable to new reads.</summary>
public sealed class ProjectSkillContentRevocationRecord
{
    public required string ProjectId { get; set; }
    public required string SkillId { get; set; }
    public long Revision { get; set; }
    public required string RevokedByActorId { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset RevokedAt { get; set; }
}

/// <summary>Stable HTTP error code, message, and status used by Projects &amp; Config endpoints.</summary>
public sealed class ProjectConfigException(
    string code,
    string message,
    int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;

    /// <summary>Creates a not-found response without disclosing whether a foreign project exists.</summary>
    public static ProjectConfigException NotFound() =>
        new("not_found", "The requested project or configuration was not found.", StatusCodes.Status404NotFound);

    /// <summary>Creates a revision-conflict response with the supplied safe detail.</summary>
    public static ProjectConfigException Conflict(string message) =>
        new("revision_conflict", message, StatusCodes.Status409Conflict);

    /// <summary>Creates a conflict response with a caller-selected stable error code.</summary>
    public static ProjectConfigException Conflict(string code, string message) =>
        new(code, message, StatusCodes.Status409Conflict);

    /// <summary>Creates a bad-request response for invalid input.</summary>
    public static ProjectConfigException Invalid(string code, string message) =>
        new(code, message, StatusCodes.Status400BadRequest);

    /// <summary>Creates a conflict when a run ID is reused for a different immutable selection request.</summary>
    public static ProjectConfigException IdempotencyConflict() =>
        new("run_selection_conflict", "The run ID already has a different immutable selection.", StatusCodes.Status409Conflict);

    /// <summary>Creates a forbidden response for a caller who cannot perform the requested operation.</summary>
    public static ProjectConfigException Forbidden() =>
        new("forbidden", "The authenticated caller is not authorized for this operation.", StatusCodes.Status403Forbidden);
}
