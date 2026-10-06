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

public enum ProjectAuthorityResourceType
{
    Platform,
    Tenant,
    Project
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

    public static ProjectConfigException IdempotencyConflict() =>
        new("run_selection_conflict", "The run ID already has a different immutable selection.", StatusCodes.Status409Conflict);

    public static ProjectConfigException Forbidden() =>
        new("forbidden", "The authenticated caller is not authorized for this operation.", StatusCodes.Status403Forbidden);
}
