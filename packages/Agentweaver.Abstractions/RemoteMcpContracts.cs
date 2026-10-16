using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Abstractions;

public enum RemoteMcpConnectionState
{
    Draft,
    Enabled,
    Disabled,
    Removed
}

public enum RemoteMcpAuthenticationMode
{
    None,
    DelegatedOAuth
}

public enum RemoteMcpTransportProfile
{
    StreamableHttp20250618
}

public sealed record RemoteMcpConnectionReference
{
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

    public string ProjectId { get; }
    public Guid ConnectionId { get; }
}

public sealed record RemoteMcpRegistryServerPin
{
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

    private static string ValidateText(string value, string name, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException("Registry metadata must be bounded and contain no control characters.", name);
        return value;
    }
}

public sealed record RemoteMcpConnectionConfiguration
{
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
    public string? IdentityBindingReference { get; }
    public RemoteMcpTransportProfile TransportProfile { get; }
    public RemoteMcpRegistryServerPin? RegistryServer { get; }
    public string ConfigurationSha256 { get; }
}

public sealed record RemoteMcpConnectionHead
{
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

public sealed record RemoteMcpToolSchemaPin
{
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

public sealed record RemoteMcpAppliedNetworkPolicyReference
{
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
    public string EgressIntentSha256 { get; }
}

public sealed record RemoteMcpDiscoveryCatalogPin
{
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
    public RemoteMcpAppliedNetworkPolicyReference? AppliedNetworkPolicy { get; }
}

public sealed record RemoteMcpConnectionSnapshot(
    RemoteMcpConnectionHead Head,
    RemoteMcpConnectionConfiguration Configuration,
    RemoteMcpDiscoveryCatalogPin? Catalog);

public static class RemoteMcpUri
{
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

public static class RemoteMcpDigest
{
    public static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));

    public static string ValidateSha256(string value, string? parameterName = null)
    {
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("SHA-256 digests must be 64 lowercase hexadecimal characters.", parameterName);
        return value;
    }
}
