namespace Agentweaver.Api.Memory;

public enum GitHubAppKind { Repo, Copilot }
public enum GitHubAuthorizationPurpose { InteractiveRepository, InteractiveCopilot, UnattendedRepository, UnattendedCopilot }
public enum GitHubCapabilityPurpose { InteractiveRepository, InteractiveCopilot, UnattendedRepository, UnattendedCopilot }
public enum GitHubCapabilitySnapshotSourceKind { UserAuthorization, RepositoryGrant, CopilotBinding }
public enum GitHubBrowseSelectionStatus { Available, Consumed }
public enum GitHubAuthorizationStatus { Pending, Redeeming, Completed, Failed, Expired }
public enum GitHubBindingStatus { Active, Inactive, Revoked }
public enum AutomationActivationStatus { Active, Inactive, Invalidated }
public enum AutomationInvocationOutcome { Claimed, Duplicate, Completed, Failed }
public enum GitHubAuditActorKind { HumanEntraSubject, GitHubWebhook }
public enum GitHubAuditAction { AuthorizationCompleted, BindingChanged, InstallationChanged, GrantChanged, AutomationActivated, AutomationInvoked, RunSnapshotValidated, CapabilitySnapshotMigrated, BrowseAuthorityCreated, BrowseSelectionRecorded, BrowseSelectionConsumed }
public enum GitHubAuditOutcome { Succeeded, Denied, Failed }
public enum GitHubAuditReasonCode { None, BindingUnavailable, InstallationUnavailable, TransactionInvalid, TransactionConsumed, RotationMismatch, DuplicateDelivery, SnapshotMigrationUnavailable, BrowseAuthorityUnavailable, BrowseSelectionUnavailable }

public sealed class GitHubAuthorizationRecord
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string State { get; set; } = "";
    /// <summary>
    /// Opaque, externally safe transaction handle for MCP/browser handoff. It is distinct
    /// from OAuth state and is bound to the App kind and initiating Entra subject.
    /// </summary>
    public string ExternalTransactionId { get; set; } = "";
    public GitHubAppKind AppKind { get; set; }
    public GitHubAuthorizationPurpose Purpose { get; set; }
    public string EntraObjectId { get; set; } = "";
    public string? ProjectId { get; set; }
    public long ExpiresAtUnixMilliseconds { get; set; }
    public string ReturnRouteKey { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string PkceVerifierProtected { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string CallbackCookieHash { get; set; } = "";
    public GitHubAuthorizationStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// The safe, minimal authorization lifecycle view for an initiating MCP or browser client.
/// </summary>
public sealed record GitHubAuthorizationTransactionHandle(
    string TransactionId,
    GitHubAppKind AppKind,
    DateTimeOffset ExpiresAt,
    GitHubAuthorizationStatus Status);

public sealed class GitHubAppAuthorizationRecord
{
    public string Id { get; set; } = "";
    public string EntraObjectId { get; set; } = "";
    public GitHubAppKind AppKind { get; set; }
    public GitHubAuthorizationPurpose Purpose { get; set; }
    public string CredentialReference { get; set; } = "";
    /// <summary>Stable identity of the authorization grant, not an access-token version.</summary>
    public string CredentialVersion { get; set; } = "";
    public string GrantDigest { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class GitHubInstallationRecord
{
    public long InstallationId { get; set; }
    public GitHubAppKind AppKind { get; set; }
    public string? ProjectId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class GitHubRepositoryGrantRecord
{
    public long InstallationId { get; set; }
    public long RepositoryId { get; set; }
    public string ProjectId { get; set; } = "";
    public string FullNameDisplay { get; set; } = "";
    public string PermissionDigest { get; set; } = "";
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class ProjectCopilotBindingRecord
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string EntraObjectId { get; set; } = "";
    public string CredentialReference { get; set; } = "";
    /// <summary>Stable identity of the authorization grant, not an access-token version.</summary>
    public string CredentialVersion { get; set; } = "";
    public string GrantDigest { get; set; } = "";
    public GitHubBindingStatus Status { get; set; }
    public DateTimeOffset BoundAt { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
}

public sealed class AutomationActivationRecord
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public long InstallationId { get; set; }
    public long RepositoryId { get; set; }
    public string AutomationKey { get; set; } = "";
    public AutomationActivationStatus Status { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public DateTimeOffset? InvalidatedAt { get; set; }
}

public sealed class AutomationInvocationRecord
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string ActivationId { get; set; } = "";
    public string OccurrenceKey { get; set; } = "";
    public string? DeliveryId { get; set; }
    public string? EventName { get; set; }
    public long? InstallationId { get; set; }
    public long? RepositoryId { get; set; }
    public AutomationInvocationOutcome Outcome { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// A durably claimed Repo App lifecycle delivery. This remains independent of any
/// automation activation so installation and grant lifecycle events are replay-safe.
/// </summary>
public sealed class GitHubLifecycleDeliveryRecord
{
    public string DeliveryId { get; set; } = "";
    public string EventName { get; set; } = "";
    public long? InstallationId { get; set; }
    public long? RepositoryId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class RunGitHubIdentitySnapshotRecord
{
    public string RunId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public GitHubAppKind AppKind { get; set; }
    public GitHubAuthorizationPurpose Purpose { get; set; }
    public string CredentialReference { get; set; } = "";
    /// <summary>Stable identity of the authorization grant, not an access-token version.</summary>
    public string CredentialVersion { get; set; } = "";
    public string GrantDigest { get; set; } = "";
    public long? InstallationId { get; set; }
    public long? RepositoryId { get; set; }
    public string? EntraObjectId { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
}

/// <summary>
/// Immutable, purpose-specific identity selected for one run. Snapshot references are opaque
/// identifiers, not credential locators or bearer capabilities.
/// </summary>
public sealed class RunGitHubCapabilitySnapshotRecord
{
    public string SnapshotRef { get; set; } = "";
    public string RunId { get; set; } = "";
    public GitHubCapabilityPurpose Purpose { get; set; }
    public GitHubAppKind AppKind { get; set; }
    public GitHubCapabilitySnapshotSourceKind SourceKind { get; set; }
    public string ProjectId { get; set; } = "";
    public string? EntraObjectId { get; set; }
    public string? SourceAuthorizationId { get; set; }
    public string? SourceBindingId { get; set; }
    public long? InstallationId { get; set; }
    public long? RepositoryId { get; set; }
    public string? CredentialReference { get; set; }
    public string? CredentialVersion { get; set; }
    public string GrantDigest { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
    public DateTimeOffset? SnapshotExpiresAt { get; set; }
}

/// <summary>
/// A five-minute, subject-bound authority to browse repositories before a run exists. It is
/// deliberately independent of projects, repositories, installations, runs, and snapshots.
/// </summary>
public sealed class GitHubInteractiveBrowseAuthorityRecord
{
    public string AuthorityRef { get; set; } = "";
    public string EntraObjectId { get; set; } = "";
    public string SourceAuthorizationId { get; set; } = "";
    public string CredentialReference { get; set; } = "";
    public string CredentialVersion { get; set; } = "";
    public string GrantDigest { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// A server-derived repository choice. The opaque reference can be consumed once to authorize
/// exactly one project-creation clone without exposing a GitHub credential.
/// </summary>
public sealed class GitHubBrowseSelectionRecord
{
    public string SelectionRef { get; set; } = "";
    public string AuthorityRef { get; set; } = "";
    public long RepositoryId { get; set; }
    public string FullNameDisplay { get; set; } = "";
    public GitHubBrowseSelectionStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed class GitHubAuditRecord
{
    public long Id { get; set; }
    public string? EntraObjectId { get; set; }
    public GitHubAuditActorKind ActorKind { get; set; }
    public GitHubAuditAction Action { get; set; }
    public string ResourceId { get; set; } = "";
    public GitHubAppKind? AppKind { get; set; }
    public GitHubCapabilityPurpose? CapabilityPurpose { get; set; }
    public GitHubAuditOutcome Outcome { get; set; }
    public GitHubAuditReasonCode ReasonCode { get; set; }
    public string CorrelationId { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    /// <summary>Non-reversible grant digest. Credential versions and references are never audited.</summary>
    public string? GrantDigest { get; set; }
}
