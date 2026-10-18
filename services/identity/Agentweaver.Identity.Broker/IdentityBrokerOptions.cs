using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Root configuration section ("IdentityBroker") binding the broker's own issuer identity,
/// its upstream external federation provider, and its operator-seeded OAuth clients.
/// Required production values have no environment-based fallback, and consumers do not
/// supply implicit development credentials.
/// </summary>
public sealed class IdentityBrokerOptions
{
    public const string SectionName = "IdentityBroker";
    internal const string WebCorsPolicyName = "identity-broker-web";
    internal const string RemoteMcpOAuthCallbackPath = "/auth/remote-mcp/oauth/callback";

    /// <summary>The broker's own absolute, HTTPS (or explicit loopback-for-tests) issuer URI.</summary>
    [Required]
    public required string Issuer { get; set; }

    /// <summary>Exact HTTPS origin of the browser application that calls this broker.</summary>
    public string? WebOrigin { get; set; }

    /// <summary>Signing certificate file and password used for broker-issued tokens.</summary>
    [Required]
    public required SigningCredentialOptions Signing { get; set; }

    /// <summary>Upstream OpenID Connect authority and client used for user sign-in.</summary>
    [Required]
    public required ExternalProviderOptions ExternalProvider { get; set; }

    /// <summary>Audience, Key Vault, and workload-identity settings for secret redemption.</summary>
    [Required]
    public required SecretRedemptionOptions SecretRedemption { get; set; }

    /// <summary>Optional pinned address and TLS settings for the Projects run-binding owner.</summary>
    public BrokerRunBindingOptions? RunBinding { get; set; }

    /// <summary>Optional allowlist of remote OAuth issuers and their approved resources and scopes.</summary>
    public RemoteMcpOAuthOptions? RemoteMcpOAuth { get; set; }

    /// <summary>Optional GitHub OAuth and App credentials for repository connections.</summary>
    public GitHubRepoAppOptions? GitHubRepoApp { get; set; }

    /// <summary>Directory for persisted ASP.NET Data Protection keys; it must be supplied by the host.</summary>
    [Required]
    public required string DataProtectionKeyPath { get; set; }

    /// <summary>Explicit OAuth clients reconciled at startup; dynamic client registration is not supported.</summary>
    [Required]
    [MinLength(1)]
    public required IReadOnlyList<BrokerClientOptions> Clients { get; set; }
}

/// <summary>Optional pinned TLS and audience settings for calls to the Projects run-binding owner.</summary>
public sealed class BrokerRunBindingOptions
{
    /// <summary>Projects owner base address used to verify run-binding requests.</summary>
    public string? ProjectsOwnerAddress { get; set; }

    /// <summary>Expected audience for calls to the Projects owner.</summary>
    public string? ProjectsAudience { get; set; }

    /// <summary>Optional SHA-256 pin for the Projects owner's server certificate.</summary>
    public string? ProjectsServerCertificateSha256 { get; set; }

    /// <summary>Optional SHA-256 pin for the trust bundle used to verify the Projects owner.</summary>
    public string? ProjectsTrustBundleSha256 { get; set; }
}

/// <summary>Owner addresses and the operator-approved remote OAuth providers for MCP connections.</summary>
public sealed class RemoteMcpOAuthOptions
{
    /// <summary>Projects owner that supplies the authorized remote OAuth connection context.</summary>
    [Required]
    public required string ProjectsOwnerAddress { get; set; }

    /// <summary>Environment owner that supplies the current gateway policy.</summary>
    [Required]
    public required string EnvironmentOwnerAddress { get; set; }

    /// <summary>Nonempty allowlist of OAuth providers the broker may connect to.</summary>
    [Required, MinLength(1)]
    public required IReadOnlyList<RemoteMcpOAuthProviderOptions> Providers { get; set; }
}

/// <summary>
/// Allowlist for one remote OAuth issuer, including its client, callback, resources,
/// metadata endpoints and scopes. Unlisted provider values are rejected by the broker.
/// </summary>
public sealed class RemoteMcpOAuthProviderOptions
{
    /// <summary>Exact issuer URI allowed for this provider.</summary>
    [Required]
    public required string IssuerUri { get; set; }

    /// <summary>Resource identifiers accepted for this provider.</summary>
    [Required, MinLength(1)]
    public required IReadOnlyList<string> ApprovedResources { get; set; }

    /// <summary>OAuth client identifier registered with the provider.</summary>
    [Required]
    public required string ClientId { get; set; }

    /// <summary>Callback URI registered with the provider.</summary>
    [Required]
    public required string RedirectUri { get; set; }

    /// <summary>Metadata and authorization endpoints the broker may contact.</summary>
    [Required, MinLength(2)]
    public required IReadOnlyList<string> ApprovedOAuthEndpoints { get; set; }

    /// <summary>Scopes the broker may request from this provider.</summary>
    [Required, MinLength(1)]
    public required IReadOnlyList<string> ApprovedScopes { get; set; }
}

/// <summary>Operator-supplied GitHub OAuth and App identity, with the private key referenced by secret version.</summary>
public sealed class GitHubRepoAppOptions
{
    /// <summary>OAuth client identifier used to start repository-connection authorization.</summary>
    [Required]
    public required string OAuthClientId { get; set; }

    /// <summary>OAuth client secret used in the authorization-code exchange.</summary>
    [Required]
    public required string OAuthClientSecret { get; set; }

    /// <summary>HTTPS callback URI for the GitHub OAuth flow.</summary>
    [Required]
    public required string CallbackUri { get; set; }

    /// <summary>Numeric GitHub App identifier.</summary>
    [Range(1, long.MaxValue)]
    public required long AppId { get; set; }

    /// <summary>GitHub App slug used to identify the installed application.</summary>
    [Required]
    public required string AppSlug { get; set; }

    /// <summary>Key Vault secret identifier containing the App private key.</summary>
    [Required]
    public required string PrivateKeySecretId { get; set; }

    /// <summary>Exact Key Vault secret version to use for the App private key.</summary>
    [Required]
    public required string PrivateKeySecretVersion { get; set; }
}

/// <summary>
/// Explicit production settings for the Identity-owned redemption endpoint and its
/// workload-identity-authenticated Key Vault backend. None of these values has a
/// development default.
/// </summary>
public sealed class SecretRedemptionOptions
{
    /// <summary>HTTPS audience registered on an Identity client for the redemption endpoint.</summary>
    [Required]
    public required string Audience { get; set; }

    /// <summary>Key Vault URI used to redeem secret references.</summary>
    [Required]
    public required string VaultUri { get; set; }

    /// <summary>Tenant ID for the workload identity used to access Key Vault.</summary>
    [Required]
    public required string WorkloadIdentityTenantId { get; set; }

    /// <summary>Client ID for the workload identity used to access Key Vault.</summary>
    [Required]
    public required string WorkloadIdentityClientId { get; set; }

    /// <summary>Absolute path to the projected workload-identity token file.</summary>
    [Required]
    public required string WorkloadIdentityTokenFilePath { get; set; }
}

/// <summary>
/// An X.509 certificate with a private key, supplied by the trusted host as a PFX file
/// plus password. Production composition is responsible for mounting real material; this
/// type never generates or falls back to a development certificate.
/// </summary>
public sealed class SigningCredentialOptions
{
    /// <summary>Path to the PFX certificate containing the broker signing private key.</summary>
    [Required]
    public required string PfxPath { get; set; }

    /// <summary>Password used to open the PFX file.</summary>
    [Required]
    public required string PfxPassword { get; set; }
}

/// <summary>The single external OpenID Connect provider this broker federates against.</summary>
public sealed class ExternalProviderOptions
{
    /// <summary>HTTPS authority of the upstream OpenID Connect provider.</summary>
    [Required]
    public required string Authority { get; set; }

    /// <summary>Client identifier registered with the upstream provider.</summary>
    [Required]
    public required string ClientId { get; set; }

    /// <summary>Optional client secret; omit it when the upstream client does not use one.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Overrides the discovery metadata address. Production composition omits this and
    /// derives it from <see cref="Authority"/>; tests point it at an in-process fake IdP.
    /// </summary>
    public string? MetadataAddress { get; set; }
}

/// <summary>Whether a seeded OAuth client authenticates with a secret or relies on PKCE as a public client.</summary>
public enum BrokerClientType
{
    /// <summary>Client authenticates to the broker with a configured secret.</summary>
    Confidential,
    /// <summary>Client has no secret and uses the required PKCE flow.</summary>
    Public,
}

/// <summary>
/// An operator-seeded OAuth client. There is no dynamic client registration endpoint:
/// every accepted client, redirect URI, scope, and audience is declared here and
/// reconciled at startup. PKCE (S256) is always required and is not configurable.
/// </summary>
public sealed class BrokerClientOptions
{
    /// <summary>OAuth client identifier accepted by this broker.</summary>
    [Required]
    public required string ClientId { get; set; }

    /// <summary>Operator-facing name displayed for the client.</summary>
    [Required]
    public required string DisplayName { get; set; }

    /// <summary>Whether this client is confidential or public.</summary>
    [EnumDataType(typeof(BrokerClientType))]
    public required BrokerClientType Type { get; set; }

    /// <summary>Required for confidential clients and omitted for public clients.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Exact redirect URIs allowed for this client.</summary>
    [Required, MinLength(1)]
    public required IReadOnlyList<string> RedirectUris { get; set; }

    /// <summary>OAuth scopes granted to this client.</summary>
    [Required, MinLength(1)]
    public required IReadOnlyList<string> Scopes { get; set; }

    /// <summary>Audiences bound into issued access tokens for this client.</summary>
    [Required, MinLength(1)]
    public required IReadOnlyList<string> Resources { get; set; }
}
