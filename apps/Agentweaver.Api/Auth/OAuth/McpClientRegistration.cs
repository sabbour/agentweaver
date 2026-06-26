using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Auth.OAuth;

/// <summary>
/// A dynamically-registered OAuth public client (RFC 7591 Dynamic Client Registration / T5).
///
/// Public MCP clients (Copilot CLI, VS Code, Claude Desktop, ...) self-register at
/// <c>/oauth/register</c> with their exact redirect URIs and receive an ephemeral, non-secret
/// <c>client_id</c>. Once registered, the stored <see cref="RedirectUris"/> become the authoritative
/// per-client allowlist enforced at <c>/oauth/authorize</c> (exact match) — the static
/// <c>Auth:OAuth:AllowedRedirectUriPrefixes</c> allowlist (F2) remains as defense-in-depth.
///
/// All registered clients are public (<c>token_endpoint_auth_method=none</c>); no client secret is
/// ever issued or stored.
/// </summary>
public sealed class McpClientRegistration
{
    [Key] public int Id { get; set; }

    /// <summary>The issued ephemeral public client identifier. Unique.</summary>
    public required string ClientId { get; set; }

    /// <summary>Newline-separated exact redirect URIs registered by the client.</summary>
    public required string RedirectUris { get; set; }

    /// <summary>Optional human-readable client name supplied at registration.</summary>
    public string? ClientName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
