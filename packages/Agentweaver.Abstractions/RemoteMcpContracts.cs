using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Abstractions;

/// <summary>Owner-controlled lifecycle states for a project Remote MCP connection.</summary>
public enum RemoteMcpConnectionState
{
    Draft,
    Enabled,
    Disabled,
    Removed
}

/// <summary>Authentication model supported by a Remote MCP connection.</summary>
public enum RemoteMcpAuthenticationMode
{
    None,
    DelegatedOAuth
}

/// <summary>Protocol transport profile that is pinned for Remote MCP discovery and calls.</summary>
public enum RemoteMcpTransportProfile
{
    StreamableHttp20250618
}

/// <summary>Project-scoped identity of a Remote MCP connection.</summary>
public sealed record RemoteMcpConnectionReference
{
    /// <summary>Creates a reference after checking the project ID and non-empty connection ID.</summary>
    /// <exception cref="ArgumentException">The project ID is blank, unbounded, or contains control characters, or the ID is empty.</exception>
    public RemoteMcpConnectionReference(string projectId, Guid connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (projectId.Length > 256 || projectId.Any(char.IsControl))
            throw new ArgumentException("Project identifiers must be bounded and contain no control characters.", nameof(projectId));
        if (connectionId == Guid.Empty)
            throw new ArgumentException("Connection identifiers must be non-empty.", nameof(connectionId));

        ProjectId = projectId;
        ConnectionId = connectionId;
    }

    /// <summary>Project that owns the connection.</summary>
    public string ProjectId { get; }
    /// <summary>Stable connection identifier within the project.</summary>
    public Guid ConnectionId { get; }
}

/// <summary>Exact registry server metadata pin used when a connection originates from a registry.</summary>
public sealed record RemoteMcpRegistryServerPin
{
    /// <summary>Creates a pin for one named server version and its metadata digest.</summary>
    /// <exception cref="ArgumentException">Text is invalid, the version is mutable, or the digest is malformed.</exception>
    public RemoteMcpRegistryServerPin(
        string serverName,
        string exactVersion,
        string metadataSha256)
    {
        ServerName = ValidateText(serverName, nameof(serverName), 200);
        ExactVersion = ValidateText(exactVersion, nameof(exactVersion), 255);
        if (string.Equals(ExactVersion, "latest", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Registry pins must use an exact server version.", nameof(exactVersion));
        MetadataSha256 = RemoteMcpDigest.ValidateSha256(metadataSha256, nameof(metadataSha256));
    }

    public string ServerName { get; }
    public string ExactVersion { get; }
    public string MetadataSha256 { get; }

    /// <summary>Checks nonempty bounded registry text and rejects control characters.</summary>
    private static string ValidateText(string value, string name, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException("Registry metadata must be bounded and contain no control characters.", name);
        return value;
    }
}

/// <summary>
/// Immutable project-owned endpoint and authentication configuration. The calculated hash
/// binds all connection settings, including optional registry pins, to this exact revision.
/// </summary>
public sealed record RemoteMcpConnectionConfiguration
{
    /// <summary>Creates and hashes a canonical connection configuration.</summary>
    /// <exception cref="ArgumentException">A URI, display name, identity reference, or OAuth resource is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The revision or enum value is outside its supported range.</exception>
    public RemoteMcpConnectionConfiguration(
        RemoteMcpConnectionReference connection,
        long configurationRevision,
        string displayName,
        string endpointUri,
        string? resourceUri,
        RemoteMcpAuthenticationMode authenticationMode,
        string? identityBindingReference,
        RemoteMcpTransportProfile transportProfile,
        RemoteMcpRegistryServerPin? registryServer = null)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        if (configurationRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(configurationRevision));
        if (!Enum.IsDefined(authenticationMode) || !Enum.IsDefined(transportProfile))
            throw new ArgumentOutOfRangeException(nameof(authenticationMode));

        ConfigurationRevision = configurationRevision;
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (displayName.Length > 100 || displayName.Any(char.IsControl))
            throw new ArgumentException("Connection display names must be bounded and contain no control characters.", nameof(displayName));
        DisplayName = displayName;
        EndpointUri = RemoteMcpUri.CanonicalizeHttps(endpointUri, nameof(endpointUri));
        ResourceUri = resourceUri is null
            ? null
            : RemoteMcpUri.CanonicalizeHttps(resourceUri, nameof(resourceUri));
        if (authenticationMode == RemoteMcpAuthenticationMode.DelegatedOAuth && ResourceUri is null)
            throw new ArgumentException("Delegated OAuth requires an explicit canonical resource URI.", nameof(resourceUri));
        if (identityBindingReference is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identityBindingReference);
            if (identityBindingReference.Length > 512 || identityBindingReference.Any(char.IsControl))
                throw new ArgumentException("Identity binding references must be bounded and opaque.", nameof(identityBindingReference));
        }

        AuthenticationMode = authenticationMode;
        IdentityBindingReference = identityBindingReference;
        TransportProfile = transportProfile;
        RegistryServer = registryServer;
        ConfigurationSha256 = RemoteMcpDigest.Sha256(Encoding.UTF8.GetBytes(string.Join(
            '\0',
            connection.ProjectId,
            connection.ConnectionId.ToString("D"),
            configurationRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DisplayName,
            EndpointUri,
            ResourceUri ?? string.Empty,
            authenticationMode.ToString(),
            IdentityBindingReference ?? string.Empty,
            transportProfile.ToString(),
            registryServer?.ServerName ?? string.Empty,
            registryServer?.ExactVersion ?? string.Empty,
            registryServer?.MetadataSha256 ?? string.Empty)));
    }

    public RemoteMcpConnectionReference Connection { get; }
    public long ConfigurationRevision { get; }
    public string DisplayName { get; }
    public string EndpointUri { get; }
    public string? ResourceUri { get; }
    public RemoteMcpAuthenticationMode AuthenticationMode { get; }
    /// <summary>Opaque identity-owner reference; it is not a bearer token or credential value.</summary>
    public string? IdentityBindingReference { get; }
    public RemoteMcpTransportProfile TransportProfile { get; }
    public RemoteMcpRegistryServerPin? RegistryServer { get; }
    /// <summary>Lowercase SHA-256 digest of the canonical configuration fields and revision.</summary>
    public string ConfigurationSha256 { get; }
}

/// <summary>
/// Current owner row for a connection, with row and configuration revisions used to reject
/// stale writes and snapshots. Discovery revision identifies the currently admitted catalog.
/// </summary>
public sealed record RemoteMcpConnectionHead
{
    /// <summary>Creates a connection head with positive revisions and a defined state.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A revision is invalid or the state is undefined.</exception>
    public RemoteMcpConnectionHead(
        RemoteMcpConnectionReference connection,
        long rowRevision,
        long currentConfigurationRevision,
        RemoteMcpConnectionState state,
        long? currentDiscoveryRevision = null)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        if (rowRevision < 1 || currentConfigurationRevision < 1 ||
            currentDiscoveryRevision is < 1 || !Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(rowRevision));

        RowRevision = rowRevision;
        CurrentConfigurationRevision = currentConfigurationRevision;
        State = state;
        CurrentDiscoveryRevision = currentDiscoveryRevision;
    }

    public RemoteMcpConnectionReference Connection { get; }
    public long RowRevision { get; }
    public long CurrentConfigurationRevision { get; }
    public RemoteMcpConnectionState State { get; }
    public long? CurrentDiscoveryRevision { get; }
}

/// <summary>
/// Exact metadata and schema digest for one discovered remote tool. The tool revision is
/// derived from its name, description, and schema hash so altered metadata cannot reuse a pin.
/// </summary>
public sealed record RemoteMcpToolSchemaPin
{
    /// <summary>Creates a bounded tool pin and verifies both schema and tool revision digests.</summary>
    /// <exception cref="ArgumentException">Tool metadata is invalid or either supplied digest does not match.</exception>
    public RemoteMcpToolSchemaPin(
        string name,
        string? description,
        string schemaJson,
        string schemaSha256,
        string toolRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128 || name.Any(char.IsControl))
            throw new ArgumentException("MCP tool names must be bounded and contain no control characters.", nameof(name));
        if (description is { Length: > 4096 } || description?.Any(char.IsControl) == true)
            throw new ArgumentException("MCP tool descriptions must be bounded and contain no control characters.", nameof(description));
        ArgumentNullException.ThrowIfNull(schemaJson);
        if (Encoding.UTF8.GetByteCount(schemaJson) > 256 * 1024)
            throw new ArgumentException("MCP tool schemas must not exceed 256 KiB.", nameof(schemaJson));

        var digest = RemoteMcpDigest.Sha256(Encoding.UTF8.GetBytes(schemaJson));
        SchemaSha256 = RemoteMcpDigest.ValidateSha256(schemaSha256, nameof(schemaSha256));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(digest),
                Convert.FromHexString(SchemaSha256)))
            throw new ArgumentException("The MCP schema digest does not match its exact JSON representation.", nameof(schemaSha256));
        ToolRevision = RemoteMcpDigest.ValidateSha256(toolRevision, nameof(toolRevision));
        var expectedToolRevision = RemoteMcpDigest.Sha256(Encoding.UTF8.GetBytes(
            $"{name}\0{description ?? string.Empty}\0{SchemaSha256}"));
        if (!string.Equals(ToolRevision, expectedToolRevision, StringComparison.Ordinal))
            throw new ArgumentException("The MCP tool revision does not match its exact metadata and schema digest.", nameof(toolRevision));

        Name = name;
        Description = description;
        SchemaJson = schemaJson;
    }

    public string Name { get; }
    public string? Description { get; }
    public string SchemaJson { get; }
    public string SchemaSha256 { get; }
    public string ToolRevision { get; }
}

/// <summary>Reference to the exact Network Policy application admitted for a Remote MCP endpoint.</summary>
public sealed record RemoteMcpAppliedNetworkPolicyReference
{
    /// <summary>Creates an applied-policy pin with a positive generation and valid egress digest.</summary>
    /// <exception cref="ArgumentException">The reference or digest is invalid.</exception>
    /// <exception cref="ArgumentNullException">The Environment fence is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The applied generation is not positive.</exception>
    public RemoteMcpAppliedNetworkPolicyReference(
        string reference,
        EnvironmentGenerationFence environmentFence,
        long appliedGeneration,
        string egressIntentSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (reference.Length > 512 || reference.Any(char.IsControl))
            throw new ArgumentException("Applied policy references must be bounded and opaque.", nameof(reference));
        EnvironmentFence = environmentFence ?? throw new ArgumentNullException(nameof(environmentFence));
        if (appliedGeneration < 1)
            throw new ArgumentOutOfRangeException(nameof(appliedGeneration));

        Reference = reference;
        AppliedGeneration = appliedGeneration;
        EgressIntentSha256 = RemoteMcpDigest.ValidateSha256(egressIntentSha256, nameof(egressIntentSha256));
    }

    public string Reference { get; }
    public EnvironmentGenerationFence EnvironmentFence { get; }
    public long AppliedGeneration { get; }
    /// <summary>Digest of the egress intent whose application this reference identifies.</summary>
    public string EgressIntentSha256 { get; }
}

/// <summary>
/// Immutable discovery snapshot for one connection configuration. Catalog digest is
/// computed from sorted tool revisions; an optional applied-policy reference pins the
/// egress intent active when discovery was admitted.
/// </summary>
public sealed record RemoteMcpDiscoveryCatalogPin
{
    /// <summary>Creates a bounded catalog pin and checks the digest against its exact tool revisions.</summary>
    /// <exception cref="ArgumentException">The tool set is uninitialized, oversized, duplicated, or digest-mismatched.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configuration or discovery revision is not positive.</exception>
    public RemoteMcpDiscoveryCatalogPin(
        RemoteMcpConnectionReference connection,
        long configurationRevision,
        long discoveryRevision,
        string catalogSha256,
        string? registryMetadataSha256,
        ImmutableArray<RemoteMcpToolSchemaPin> tools,
        RemoteMcpAppliedNetworkPolicyReference? appliedNetworkPolicy = null)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        if (configurationRevision < 1 || discoveryRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(configurationRevision));
        if (tools.IsDefault || tools.Length > 128 ||
            tools.Any(tool => tool is null) ||
            tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != tools.Length)
            throw new ArgumentException("A tool catalog must be initialized, bounded, and have unique names.", nameof(tools));
        var orderedTools = tools.OrderBy(tool => tool.Name, StringComparer.Ordinal);
        var expectedCatalogSha256 = RemoteMcpDigest.Sha256(Encoding.UTF8.GetBytes(string.Join(
            '\n',
            orderedTools.Select(tool => $"{tool.Name}\0{tool.ToolRevision}"))));

        ConfigurationRevision = configurationRevision;
        DiscoveryRevision = discoveryRevision;
        CatalogSha256 = RemoteMcpDigest.ValidateSha256(catalogSha256, nameof(catalogSha256));
        if (!string.Equals(CatalogSha256, expectedCatalogSha256, StringComparison.Ordinal))
            throw new ArgumentException("The MCP catalog digest does not match its exact tool revisions.", nameof(catalogSha256));
        RegistryMetadataSha256 = registryMetadataSha256 is null
            ? null
            : RemoteMcpDigest.ValidateSha256(registryMetadataSha256, nameof(registryMetadataSha256));
        Tools = tools;
        AppliedNetworkPolicy = appliedNetworkPolicy;
    }

    public RemoteMcpConnectionReference Connection { get; }
    public long ConfigurationRevision { get; }
    public long DiscoveryRevision { get; }
    public string CatalogSha256 { get; }
    public string? RegistryMetadataSha256 { get; }
    public ImmutableArray<RemoteMcpToolSchemaPin> Tools { get; }
    /// <summary>Network Policy application required for discovery when egress enforcement is pinned.</summary>
    public RemoteMcpAppliedNetworkPolicyReference? AppliedNetworkPolicy { get; }
}

/// <summary>Read model combining the current connection head, immutable configuration, and optional discovery catalog.</summary>
public sealed record RemoteMcpConnectionSnapshot(
    RemoteMcpConnectionHead Head,
    RemoteMcpConnectionConfiguration Configuration,
    RemoteMcpDiscoveryCatalogPin? Catalog);

/// <summary>Canonicalizes endpoint and OAuth resource URIs to absolute HTTPS DNS URIs.</summary>
public static class RemoteMcpUri
{
    /// <summary>Rejects non-HTTPS, non-DNS, credential-bearing, or fragment-bearing URIs.</summary>
    /// <exception cref="ArgumentException">The URI is not an allowed absolute HTTPS DNS URI.</exception>
    public static string CanonicalizeHttps(string value, string? parameterName = null)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.HostNameType != UriHostNameType.Dns)
            throw new ArgumentException(
                "Remote MCP endpoint and resource URIs must be absolute HTTPS DNS URIs without user info or fragments.",
                parameterName);

        var builder = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttps,
            Host = uri.IdnHost.TrimEnd('.').ToLowerInvariant(),
            Port = uri.IsDefaultPort ? -1 : uri.Port
        };
        return builder.Uri.AbsoluteUri;
    }
}

/// <summary>Computes and validates canonical lowercase SHA-256 digests used by Remote MCP pins.</summary>
public static class RemoteMcpDigest
{
    /// <summary>Returns the lowercase hexadecimal SHA-256 digest of the supplied bytes.</summary>
    public static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));

    /// <summary>Returns a valid digest unchanged, or rejects malformed hexadecimal input.</summary>
    /// <exception cref="ArgumentException">The value is not exactly 64 lowercase hexadecimal characters.</exception>
    public static string ValidateSha256(string value, string? parameterName = null)
    {
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("SHA-256 digests must be 64 lowercase hexadecimal characters.", parameterName);
        return value;
    }
}
