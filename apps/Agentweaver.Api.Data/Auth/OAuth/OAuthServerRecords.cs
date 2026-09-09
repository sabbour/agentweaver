using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Auth.OAuth;

public sealed class OAuthConsentRecord
{
    [Key] public Guid Id { get; set; }
    public required string Subject { get; set; }
    public required string ClientId { get; set; }
    public required string Scopes { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class OAuthAuthorizationTransaction
{
    [Key] public required string HandleHash { get; set; }
    public required string ClientId { get; set; }
    public required string RedirectUri { get; set; }
    public required string CodeChallenge { get; set; }
    public required string Scope { get; set; }
    public string? ClientState { get; set; }
    public string? BrowserSessionId { get; set; }
    public string? Subject { get; set; }
    public string? ContinuationDecision { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed class OAuthDynamicRegistration
{
    [Key] public Guid Id { get; set; }
    public required string ClientId { get; set; }
    public required string SourceHash { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
}

public sealed class OAuthRefreshTokenFamily
{
    [Key] public Guid Id { get; set; }
    public required string AuthorizationId { get; set; }
    public required string Subject { get; set; }
    public required string ClientId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }
}

public sealed class OAuthMaintenanceLease
{
    [Key] public required string Name { get; set; }
    public required string Owner { get; set; }
    public DateTimeOffset LeaseExpiresAt { get; set; }
}
