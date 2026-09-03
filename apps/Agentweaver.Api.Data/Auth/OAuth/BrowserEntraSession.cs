using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Auth.OAuth;

/// <summary>
/// A short-lived, opaque browser session established only after a validated Entra sign-in.
/// It is used exclusively to bind browser-only OAuth handoffs to the human who started them.
/// </summary>
public sealed class BrowserEntraSession
{
    [Key] public required string Id { get; set; }
    public required string EntraObjectId { get; set; }
    public string PlatformRoles { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}
