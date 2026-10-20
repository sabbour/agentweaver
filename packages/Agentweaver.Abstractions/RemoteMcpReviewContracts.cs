using System.Collections.Immutable;

namespace Agentweaver.Abstractions;

/// <summary>
/// Safe directory view of one project Remote MCP connection. It carries the owner-held
/// configuration revision and checksum that fenced Identity requests require, and it never
/// carries tokens, secrets, tool schemas, or the opaque Identity binding reference.
/// </summary>
public sealed record RemoteMcpConnectionDirectoryEntry(
    Guid ConnectionId,
    string DisplayName,
    string EndpointUri,
    string? ResourceUri,
    RemoteMcpAuthenticationMode AuthenticationMode,
    RemoteMcpTransportProfile TransportProfile,
    RemoteMcpConnectionState State,
    long RowRevision,
    long ConfigurationRevision,
    string ConfigurationSha256,
    bool IdentityBound,
    string? RegistryServerName,
    string? RegistryExactVersion,
    long? DiscoveryRevision,
    string? CatalogSha256,
    int ToolCount)
{
    /// <summary>Projects a full owner snapshot to the safe directory fields.</summary>
    public static RemoteMcpConnectionDirectoryEntry FromSnapshot(RemoteMcpConnectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var configuration = snapshot.Configuration;
        return new(
            snapshot.Head.Connection.ConnectionId,
            configuration.DisplayName,
            configuration.EndpointUri,
            configuration.ResourceUri,
            configuration.AuthenticationMode,
            configuration.TransportProfile,
            snapshot.Head.State,
            snapshot.Head.RowRevision,
            configuration.ConfigurationRevision,
            configuration.ConfigurationSha256,
            configuration.IdentityBindingReference is not null,
            configuration.RegistryServer?.ServerName,
            configuration.RegistryServer?.ExactVersion,
            snapshot.Catalog?.DiscoveryRevision,
            snapshot.Catalog?.CatalogSha256,
            snapshot.Catalog?.Tools.Length ?? 0);
    }
}

/// <summary>One page of safe project connection directory entries and the cursor for the next page.</summary>
public sealed record RemoteMcpConnectionDirectoryPage(
    ImmutableArray<RemoteMcpConnectionDirectoryEntry> Connections,
    Guid? NextConnectionId);

/// <summary>
/// Immutable reference that a reviewed tool snapshot stores for one Remote MCP connection.
/// It pins the exact configuration revision and checksum and the exact discovery catalog.
/// Endpoint, resource, transport, authentication mode, Identity binding, and Registry pin
/// are all inside the configuration checksum, so a change to any of them changes the reference.
/// A delegated token rotation does not change the configuration, so it does not change the reference.
/// </summary>
public sealed record RemoteMcpConnectionReviewReference
{
    /// <summary>Creates a review reference after checking revisions and digests.</summary>
    /// <exception cref="ArgumentException">A digest is malformed or the connection reference is missing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A revision is not positive.</exception>
    public RemoteMcpConnectionReviewReference(
        RemoteMcpConnectionReference connection,
        long configurationRevision,
        string configurationSha256,
        long discoveryRevision,
        string catalogSha256)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        if (configurationRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(configurationRevision));
        if (discoveryRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(discoveryRevision));
        ConfigurationRevision = configurationRevision;
        ConfigurationSha256 = RemoteMcpDigest.ValidateSha256(configurationSha256, nameof(configurationSha256));
        DiscoveryRevision = discoveryRevision;
        CatalogSha256 = RemoteMcpDigest.ValidateSha256(catalogSha256, nameof(catalogSha256));
    }

    public RemoteMcpConnectionReference Connection { get; }
    public long ConfigurationRevision { get; }
    public string ConfigurationSha256 { get; }
    public long DiscoveryRevision { get; }
    public string CatalogSha256 { get; }

    /// <summary>
    /// Returns the reference for the current pinned catalog of an enabled connection.
    /// Discovery output is a candidate for review only; this reference does not accept any tool.
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection is not enabled or has no current catalog.</exception>
    public static RemoteMcpConnectionReviewReference FromSnapshot(RemoteMcpConnectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Head.State != RemoteMcpConnectionState.Enabled || snapshot.Catalog is null)
            throw new InvalidOperationException("Only an enabled connection with a pinned catalog can be reviewed.");
        return new(
            snapshot.Head.Connection,
            snapshot.Configuration.ConfigurationRevision,
            snapshot.Configuration.ConfigurationSha256,
            snapshot.Catalog.DiscoveryRevision,
            snapshot.Catalog.CatalogSha256);
    }
}

/// <summary>Reason the Environment owner denies use of a reviewed Remote MCP tool.</summary>
public enum RemoteMcpInvocationDenial
{
    None,
    ConnectionNotFound,
    ConnectionNotEnabled,
    ConnectionRemoved,
    CatalogNotPinned,
    ConfigurationChanged,
    CatalogChanged,
    ToolNotInCatalog,
    ToolChanged,
    IdentityBindingMissing
}

/// <summary>
/// Environment owner decision for one reviewed tool at the current connection head.
/// <see cref="ReviewRequired"/> is true when the current configuration or catalog differs
/// from the reviewed reference; the new state needs an explicit reviewed rebind.
/// <see cref="CredentialValidityRequired"/> is true for delegated OAuth; the caller must also
/// get a current Identity credential decision before it sends any request.
/// </summary>
public sealed record RemoteMcpInvocationValidity(
    bool Allowed,
    RemoteMcpInvocationDenial Denial,
    bool ReviewRequired,
    bool CredentialValidityRequired,
    string? IdentityBindingReference,
    RemoteMcpConnectionReviewReference? CurrentReference);

/// <summary>Pure rules that compare a reviewed reference with the current owner snapshot.</summary>
public static class RemoteMcpConnectionReview
{
    /// <summary>
    /// Checks that the connection is enabled, that the configuration and catalog still match the
    /// reviewed reference, and that the named tool still has the reviewed tool revision.
    /// It never accepts a changed connection; a changed connection returns <c>ReviewRequired</c>.
    /// </summary>
    public static RemoteMcpInvocationValidity Evaluate(
        RemoteMcpConnectionReviewReference reviewed,
        RemoteMcpConnectionSnapshot? current,
        string toolName,
        string toolRevision)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        RemoteMcpDigest.ValidateSha256(toolRevision, nameof(toolRevision));
        if (current is null || current.Head.Connection != reviewed.Connection)
            return Deny(RemoteMcpInvocationDenial.ConnectionNotFound, reviewRequired: false, current);
        if (current.Head.State == RemoteMcpConnectionState.Removed)
            return Deny(RemoteMcpInvocationDenial.ConnectionRemoved, reviewRequired: false, current);
        if (current.Head.State != RemoteMcpConnectionState.Enabled)
            return Deny(RemoteMcpInvocationDenial.ConnectionNotEnabled, reviewRequired: false, current);
        if (current.Configuration.ConfigurationRevision != reviewed.ConfigurationRevision ||
            !string.Equals(
                current.Configuration.ConfigurationSha256,
                reviewed.ConfigurationSha256,
                StringComparison.Ordinal))
            return Deny(RemoteMcpInvocationDenial.ConfigurationChanged, reviewRequired: true, current);
        if (current.Catalog is null)
            return Deny(RemoteMcpInvocationDenial.CatalogNotPinned, reviewRequired: false, current);
        var tool = current.Catalog.Tools.FirstOrDefault(item =>
            string.Equals(item.Name, toolName, StringComparison.Ordinal));
        if (current.Catalog.DiscoveryRevision != reviewed.DiscoveryRevision ||
            !string.Equals(current.Catalog.CatalogSha256, reviewed.CatalogSha256, StringComparison.Ordinal))
        {
            // A rediscovery that kept the same tool set still has a new revision; review it before use.
            return Deny(
                tool is null ? RemoteMcpInvocationDenial.ToolNotInCatalog : RemoteMcpInvocationDenial.CatalogChanged,
                reviewRequired: true,
                current);
        }
        if (tool is null)
            return Deny(RemoteMcpInvocationDenial.ToolNotInCatalog, reviewRequired: false, current);
        if (!string.Equals(tool.ToolRevision, toolRevision, StringComparison.Ordinal))
            return Deny(RemoteMcpInvocationDenial.ToolChanged, reviewRequired: true, current);

        var oauth = current.Configuration.AuthenticationMode == RemoteMcpAuthenticationMode.DelegatedOAuth;
        if (oauth && current.Configuration.IdentityBindingReference is null)
            return Deny(RemoteMcpInvocationDenial.IdentityBindingMissing, reviewRequired: false, current);
        return new(
            Allowed: true,
            RemoteMcpInvocationDenial.None,
            ReviewRequired: false,
            CredentialValidityRequired: oauth,
            current.Configuration.IdentityBindingReference,
            RemoteMcpConnectionReviewReference.FromSnapshot(current));
    }

    private static RemoteMcpInvocationValidity Deny(
        RemoteMcpInvocationDenial denial,
        bool reviewRequired,
        RemoteMcpConnectionSnapshot? current) =>
        new(
            Allowed: false,
            denial,
            reviewRequired,
            CredentialValidityRequired: false,
            IdentityBindingReference: null,
            current is { Head.State: RemoteMcpConnectionState.Enabled, Catalog: not null }
                ? RemoteMcpConnectionReviewReference.FromSnapshot(current)
                : null);
}
