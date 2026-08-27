namespace Agentweaver.Api.Memory;

public enum GitHubAppKind { Repo, Copilot }
public enum GitHubAuthorizationPurpose { InteractiveRepository, InteractiveCopilot, UnattendedRepository, UnattendedCopilot }
public enum GitHubAuthorizationStatus { Pending, Redeeming, Completed, Failed }
public enum GitHubBindingStatus { Active, Inactive, Revoked }
public enum AutomationActivationStatus { Active, Inactive, Invalidated }
public enum AutomationInvocationOutcome { Claimed, Duplicate, Completed, Failed }
public enum GitHubAuditActorKind { HumanEntraSubject, GitHubWebhook }
public enum GitHubAuditAction { AuthorizationCompleted, BindingChanged, InstallationChanged, GrantChanged, AutomationActivated, AutomationInvoked, RunSnapshotValidated }
public enum GitHubAuditOutcome { Succeeded, Denied, Failed }
public enum GitHubAuditReasonCode { None, BindingUnavailable, InstallationUnavailable, TransactionInvalid, TransactionConsumed, RotationMismatch, DuplicateDelivery }

public sealed class GitHubAuthorizationRecord
{
    public string State { get; set; } = "";
    public GitHubAppKind AppKind { get; set; }
    public GitHubAuthorizationPurpose Purpose { get; set; }
    public string EntraObjectId { get; set; } = "";
    public string? ProjectId { get; set; }
    public long ExpiresAtUnixMilliseconds { get; set; }
    public string ReturnRouteKey { get; set; } = "";
    public string PkceVerifierProtected { get; set; } = "";
    public string CallbackCookieHash { get; set; } = "";
    public GitHubAuthorizationStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class GitHubAppAuthorizationRecord
{
    public string Id { get; set; } = "";
    public string EntraObjectId { get; set; } = "";
    public GitHubAppKind AppKind { get; set; }
    public GitHubAuthorizationPurpose Purpose { get; set; }
    public string CredentialReference { get; set; } = "";
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

public sealed class RunGitHubIdentitySnapshotRecord
{
    public string RunId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public GitHubAppKind AppKind { get; set; }
    public GitHubAuthorizationPurpose Purpose { get; set; }
    public string CredentialReference { get; set; } = "";
    public string CredentialVersion { get; set; } = "";
    public string GrantDigest { get; set; } = "";
    public long? InstallationId { get; set; }
    public long? RepositoryId { get; set; }
    public string? EntraObjectId { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
}

public sealed class GitHubAuditRecord
{
    public long Id { get; set; }
    public string? EntraObjectId { get; set; }
    public GitHubAuditActorKind ActorKind { get; set; }
    public GitHubAuditAction Action { get; set; }
    public string ResourceId { get; set; } = "";
    public GitHubAppKind? AppKind { get; set; }
    public GitHubAuthorizationPurpose? Purpose { get; set; }
    public GitHubAuditOutcome Outcome { get; set; }
    public GitHubAuditReasonCode ReasonCode { get; set; }
    public string CorrelationId { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public string? CredentialVersionOrDigest { get; set; }
}
