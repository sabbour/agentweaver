namespace Agentweaver.Identity.Broker;

/// <summary>Whether an OAuth transaction is for user authorization or App installation setup.</summary>
public enum RepoAppAuthorizationPurpose
{
    UserAuthorization,
    InstallationSetup
}

/// <summary>One-time lifecycle states for a browser authorization callback.</summary>
public enum RepoAppAuthorizationState
{
    Pending,
    Processing,
    Completed,
    Failed
}

/// <summary>Connection lifecycle, including the fail-closed state for an uncertain token rotation.</summary>
public enum RepoAppConnectionState
{
    Connected,
    Revoked,
    RotationUncertain
}

/// <summary>
/// Persisted, short-lived OAuth callback transaction. State and cookie are stored as hashes;
/// the PKCE verifier is protected for the callback exchange.
/// </summary>
public sealed class RepoAppAuthorizationTransaction
{
    public required string StateHash { get; set; }
    public required string TransactionId { get; set; }
    public required Guid OwnerId { get; set; }
    public required RepoAppAuthorizationPurpose Purpose { get; set; }
    public required string CallbackCookieHash { get; set; }
    public required string ProtectedCodeVerifier { get; set; }
    public required string ReturnRouteKey { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }
    public required RepoAppAuthorizationState State { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long? InstallationId { get; set; }
}

/// <summary>
/// Current owner-linked GitHub account and versioned secret references. Connection revision
/// changes authorization state; credential revision fences concurrent refresh operations.
/// </summary>
public sealed class RepoAppConnectionRecord
{
    public required string ConnectionId { get; set; }
    public required Guid OwnerId { get; set; }
    public required string GitHubLogin { get; set; }
    public required string AccessTokenSecretId { get; set; }
    public required string AccessTokenSecretVersion { get; set; }
    public required DateTimeOffset AccessTokenExpiresAt { get; set; }
    public required string RefreshTokenSecretId { get; set; }
    public required string RefreshTokenSecretVersion { get; set; }
    public required DateTimeOffset RefreshTokenExpiresAt { get; set; }
    public required long ConnectionRevision { get; set; }
    public required long CredentialRevision { get; set; }
    public required RepoAppConnectionState State { get; set; }
    public Guid? RefreshLeaseId { get; set; }
    public DateTimeOffset? RefreshLeaseExpiresAt { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Last-known active GitHub App installation metadata bound to a connection revision.</summary>
public sealed class RepoAppInstallationRecord
{
    public required string ConnectionId { get; set; }
    public required long InstallationId { get; set; }
    public required string AccountLogin { get; set; }
    public required string AccountType { get; set; }
    public required string RepositorySelection { get; set; }
    public required long ConnectionRevision { get; set; }
    public required DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// Short-lived, single-use selection of one repository and installation. The code is stored
/// as a hash and later bound to project and permission evidence before minting an installation token.
/// </summary>
public sealed class RepoAppRepositorySelectionRecord
{
    public required string CodeHash { get; set; }
    public required Guid OwnerId { get; set; }
    public required string ConnectionId { get; set; }
    public required long ConnectionRevision { get; set; }
    public required long InstallationId { get; set; }
    public required long RepositoryId { get; set; }
    public required string RepositoryFullName { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }
    public string? ProjectId { get; set; }
    public string? PermissionDigest { get; set; }
    public bool IssueWriteRequested { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}
