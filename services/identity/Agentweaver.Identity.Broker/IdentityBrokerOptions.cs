using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Root configuration section ("IdentityBroker") binding the broker's own issuer identity,
/// its upstream external federation provider, and its operator-seeded OAuth clients.
/// Every value here is required and explicit: there is no environment-based fallback and
/// no implicit development credential anywhere in this type or its consumers.
/// </summary>
public sealed class IdentityBrokerOptions
{
    public const string SectionName = "IdentityBroker";

    /// <summary>The broker's own absolute, HTTPS (or explicit loopback-for-tests) issuer URI.</summary>
    [Required]
    public required string Issuer { get; set; }

    [Required]
    public required SigningCredentialOptions Signing { get; set; }

    [Required]
    public required ExternalProviderOptions ExternalProvider { get; set; }

    [Required]
    public required SecretRedemptionOptions SecretRedemption { get; set; }

    [Required]
    public required string DataProtectionKeyPath { get; set; }

    [Required]
    [MinLength(1)]
    public required IReadOnlyList<BrokerClientOptions> Clients { get; set; }
}

/// <summary>
/// Explicit production settings for the Identity-owned redemption endpoint and its
/// workload-identity-authenticated Key Vault backend. None of these values has a
/// development default.
/// </summary>
public sealed class SecretRedemptionOptions
{
    [Required]
    public required string Audience { get; set; }

    [Required]
    public required string VaultUri { get; set; }

    [Required]
    public required string WorkloadIdentityTenantId { get; set; }

    [Required]
    public required string WorkloadIdentityClientId { get; set; }

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
    [Required]
    public required string PfxPath { get; set; }

    [Required]
    public required string PfxPassword { get; set; }
}

/// <summary>The single external OpenID Connect provider this broker federates against.</summary>
public sealed class ExternalProviderOptions
{
    [Required]
    public required string Authority { get; set; }

    [Required]
    public required string ClientId { get; set; }

    [Required]
    public required string ClientSecret { get; set; }

    /// <summary>
    /// Overrides the discovery metadata address. Production composition omits this and
    /// derives it from <see cref="Authority"/>; tests point it at an in-process fake IdP.
    /// </summary>
    public string? MetadataAddress { get; set; }
}

public enum BrokerClientType
{
    Confidential,
    Public,
}

/// <summary>
/// An operator-seeded OAuth client. There is no dynamic client registration endpoint:
/// every accepted client, redirect URI, scope, and audience is declared here and
/// reconciled at startup. PKCE (S256) is always required and is not configurable.
/// </summary>
public sealed class BrokerClientOptions
{
    [Required]
    public required string ClientId { get; set; }

    [Required]
    public required string DisplayName { get; set; }

    [EnumDataType(typeof(BrokerClientType))]
    public required BrokerClientType Type { get; set; }

    /// <summary>Required when <see cref="Type"/> is <see cref="BrokerClientType.Confidential"/>.</summary>
    public string? ClientSecret { get; set; }

    [Required, MinLength(1)]
    public required IReadOnlyList<string> RedirectUris { get; set; }

    [Required, MinLength(1)]
    public required IReadOnlyList<string> Scopes { get; set; }

    /// <summary>Audiences bound into issued access tokens for this client.</summary>
    [Required, MinLength(1)]
    public required IReadOnlyList<string> Resources { get; set; }
}
