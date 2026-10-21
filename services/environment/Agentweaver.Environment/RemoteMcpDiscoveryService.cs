using System.Net;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

/// <summary>Raw bytes of one tools/list page and the trusted transport facts for that page.</summary>
public sealed record RemoteMcpToolsListResponse(
    ReadOnlyMemory<byte> Body,
    string NegotiatedProtocolVersion,
    RemoteMcpAppliedNetworkPolicyReference AppliedNetworkPolicy);

/// <summary>
/// Trusted network source for Registry metadata and remote tools/list pages. The Environment owner
/// parses and pins every byte it returns. The trusted transport supplies the production
/// implementation; it applies the project egress policy and returns the applied policy.
/// </summary>
public interface IRemoteMcpDiscoverySource
{
    /// <summary>Reads one raw Registry server list page.</summary>
    Task<ReadOnlyMemory<byte>> ReadRegistryServersAsync(string? cursor, CancellationToken cancellationToken);

    /// <summary>Reads the raw Registry document for one exact server version.</summary>
    Task<ReadOnlyMemory<byte>> ReadRegistryServerVersionAsync(
        string serverName,
        string exactVersion,
        CancellationToken cancellationToken);

    /// <summary>Reads one tools/list page from the exact configuration endpoint through the trusted transport.</summary>
    Task<RemoteMcpToolsListResponse> ReadToolsListPageAsync(
        RemoteMcpConnectionConfiguration configuration,
        string? cursor,
        CancellationToken cancellationToken);
}

/// <summary>Default source used until a trusted transport is registered. Every read fails with 503.</summary>
public sealed class UnavailableRemoteMcpDiscoverySource : IRemoteMcpDiscoverySource
{
    public Task<ReadOnlyMemory<byte>> ReadRegistryServersAsync(string? cursor, CancellationToken cancellationToken) =>
        throw Unavailable();

    public Task<ReadOnlyMemory<byte>> ReadRegistryServerVersionAsync(
        string serverName,
        string exactVersion,
        CancellationToken cancellationToken) =>
        throw Unavailable();

    public Task<RemoteMcpToolsListResponse> ReadToolsListPageAsync(
        RemoteMcpConnectionConfiguration configuration,
        string? cursor,
        CancellationToken cancellationToken) =>
        throw Unavailable();

    private static RemoteMcpConnectionException Unavailable() =>
        new(
            "remote_mcp_discovery_transport_unavailable",
            "No trusted Remote MCP discovery transport is configured.",
            (int)HttpStatusCode.ServiceUnavailable);
}

/// <summary>
/// Runs bounded Registry selection and tools/list discovery for project connections, then pins the
/// result through <see cref="RemoteMcpConnectionStore"/>. Discovery output is a review candidate only.
/// </summary>
public sealed class RemoteMcpDiscoveryService(
    RemoteMcpConnectionStore store,
    IRemoteMcpDiscoverySource source)
{
    /// <summary>The only MCP protocol version that the pinned transport profile accepts.</summary>
    public const string RequiredProtocolVersion = "2025-06-18";
    /// <summary>Maximum tools/list pages read in one discovery.</summary>
    public const int MaximumToolsListPages = 16;

    /// <summary>Reads one bounded Registry server page for browsing. It does not change any connection.</summary>
    internal async Task<RemoteMcpRegistryServerPage> ReadRegistryPageAsync(
        CurrentCallerRequest caller,
        string projectId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        // Validate the caller-supplied cursor before any authority or network work. The cursor is
        // forwarded to the Registry, so it must stay within the parser limit and contain no control characters.
        if (cursor is { Length: > RemoteMcpMetadataParser.MaximumCursorLength } || cursor?.Any(char.IsControl) == true)
            throw new ArgumentException("The Registry cursor is too long or contains control characters.", nameof(cursor));
        // Browsing only reads, so current read authority on the project is enough.
        await store.EnsureAuthorityAsync(caller, projectId, write: false, cancellationToken).ConfigureAwait(false);
        // An empty cursor means the first page. The source expects null for that case.
        var bytes = await ReadRegistryAsync(
            () => source.ReadRegistryServersAsync(string.IsNullOrEmpty(cursor) ? null : cursor, cancellationToken))
            .ConfigureAwait(false);
        return ParseRegistry(() => RemoteMcpMetadataParser.ParseRegistryServerPage(bytes));
    }

    /// <summary>
    /// Resolves an exact Registry version and endpoint into a draft. A floating version, an endpoint that
    /// the version does not advertise, or an unavailable Registry fails before any connection changes.
    /// </summary>
    internal async Task<RemoteMcpConnectionDraft> ResolveDraftAsync(
        CurrentCallerRequest caller,
        string projectId,
        string displayName,
        string endpointUri,
        string? resourceUri,
        RemoteMcpAuthenticationMode authenticationMode,
        string? registryServerName,
        string? registryExactVersion,
        CancellationToken cancellationToken)
    {
        // No Registry selection: the caller supplied the endpoint directly, so there is nothing to resolve.
        if (registryServerName is null && registryExactVersion is null)
            return new(displayName, endpointUri, resourceUri, authenticationMode);
        // A Registry selection needs both parts. A name without a version would let the Registry pick one.
        if (string.IsNullOrWhiteSpace(registryServerName) || string.IsNullOrWhiteSpace(registryExactVersion))
            throw new ArgumentException("Registry selection requires both a server name and an exact version.");
        // "latest" is a floating alias. A pin must name one immutable version so its digest can't drift.
        if (string.Equals(registryExactVersion, "latest", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Registry selection requires an exact server version.", nameof(registryExactVersion));

        // Resolving a draft prepares a connection change, so it needs write authority before the network read.
        await store.EnsureAuthorityAsync(caller, projectId, write: true, cancellationToken).ConfigureAwait(false);
        var bytes = await ReadRegistryAsync(
            () => source.ReadRegistryServerVersionAsync(registryServerName, registryExactVersion, cancellationToken))
            .ConfigureAwait(false);
        // The parser checks that the version advertises the requested endpoint and returns the
        // Registry's canonical form of it. The draft stores that canonical endpoint, not the raw input.
        var selection = ParseRegistry(() => RemoteMcpMetadataParser.ParseRegistryServerVersion(
            bytes, registryServerName, registryExactVersion, endpointUri));
        return new(displayName, selection.SelectedEndpointUri, resourceUri, authenticationMode, selection.Server);
    }

    /// <summary>
    /// Discovers the full tool catalog for an enabled connection at the expected row revision and pins it.
    /// A replay of the same idempotency key returns the saved result without network calls. A Registry or
    /// remote failure leaves the current pin unchanged. A disable, update, or remove that wins the race makes
    /// the save fail with a revision conflict.
    /// </summary>
    public async Task<RemoteMcpConnectionMutationResult> DiscoverAsync(
        CurrentCallerRequest caller,
        string projectId,
        Guid connectionId,
        long expectedRowRevision,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Row revisions start at 1, so a smaller value can't match any row.
        if (expectedRowRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRowRevision));
        // Check for a replay first. A retried request with the same idempotency key returns the saved
        // result and makes no network calls, even if the remote server is now unavailable.
        var replay = await store.ReadDiscoveryReplayAsync(
            caller, projectId, connectionId, expectedRowRevision, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
            return replay;

        // Read the current row and reject early if it moved or isn't enabled. The save step checks the
        // revision again, because the row can still change while the network reads below run.
        var current = await store.GetForDiscoveryAsync(caller, projectId, connectionId, cancellationToken)
            .ConfigureAwait(false);
        if (current.Head.RowRevision != expectedRowRevision)
            throw new RemoteMcpConnectionException(
                "connection_revision_stale",
                "The connection changed after it was read.",
                (int)HttpStatusCode.Conflict);
        if (current.Head.State != RemoteMcpConnectionState.Enabled)
            throw new RemoteMcpConnectionException(
                "connection_not_enabled",
                "Only an enabled connection can be discovered.",
                (int)HttpStatusCode.Conflict);

        // For a Registry-pinned connection, read the exact version again and compare its digest with the pin.
        // A Registry publisher can change a version's metadata after the pin. In that case discovery stops
        // and keeps the old catalog, so an unreviewed change never reaches the catalog.
        string? registryMetadataSha256 = null;
        if (current.Configuration.RegistryServer is { } registry)
        {
            var bytes = await ReadRegistryAsync(
                () => source.ReadRegistryServerVersionAsync(registry.ServerName, registry.ExactVersion, cancellationToken))
                .ConfigureAwait(false);
            var selection = ParseRegistry(() => RemoteMcpMetadataParser.ParseRegistryServerVersion(
                bytes, registry.ServerName, registry.ExactVersion, current.Configuration.EndpointUri));
            if (!string.Equals(selection.Server.MetadataSha256, registry.MetadataSha256, StringComparison.Ordinal))
                throw new RemoteMcpConnectionException(
                    "registry_metadata_changed",
                    "The Registry metadata for the pinned version changed. Review and update the connection.",
                    (int)HttpStatusCode.Conflict);
            registryMetadataSha256 = registry.MetadataSha256;
        }

        var (catalog, appliedPolicy) = await ReadCatalogAsync(current.Configuration, cancellationToken)
            .ConfigureAwait(false);
        // The store saves the catalog only if the row is still at the expected revision. A disable,
        // update, or remove that committed during the network reads makes this save fail with a conflict.
        return await store.SaveDiscoveryAsync(
            caller,
            projectId,
            connectionId,
            expectedRowRevision,
            catalog,
            registryMetadataSha256,
            appliedPolicy,
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(RemoteMcpParsedToolCatalog Catalog, RemoteMcpAppliedNetworkPolicyReference Policy)> ReadCatalogAsync(
        RemoteMcpConnectionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var tools = new List<RemoteMcpToolSchemaPin>();
        // Track every cursor the server returned. A repeated cursor would loop forever, so it fails discovery.
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        RemoteMcpAppliedNetworkPolicyReference? policy = null;
        string? cursor = null;
        // Follow tools/list pages until the server returns no next cursor. The page, tool, and cursor
        // limits bound the work even when the remote server misbehaves.
        for (var page = 0; ; page++)
        {
            if (page == MaximumToolsListPages)
                throw DiscoveryInvalid("The remote server returned more tools/list pages than the limit.");
            RemoteMcpToolsListResponse response;
            try
            {
                response = await source.ReadToolsListPageAsync(configuration, cursor, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                throw RemoteUnavailable();
            }
            // A timeout surfaces as TaskCanceledException without the caller's token being canceled.
            // Report it as an unavailable server. A real caller cancellation still propagates.
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw RemoteUnavailable();
            }

            ArgumentNullException.ThrowIfNull(response);
            // The catalog is pinned for one protocol version. A server that negotiates another version
            // could describe tools differently, so the result isn't accepted.
            if (!string.Equals(response.NegotiatedProtocolVersion, RequiredProtocolVersion, StringComparison.Ordinal))
                throw DiscoveryInvalid("The remote server negotiated an unsupported MCP protocol version.");
            // Every page must come through the same applied network policy. The catalog records one
            // policy, so a change between pages means the pages can't be trusted as one catalog.
            if (policy is null)
                policy = response.AppliedNetworkPolicy;
            else if (response.AppliedNetworkPolicy != policy)
                throw new RemoteMcpConnectionException(
                    "applied_policy_changed",
                    "The applied network policy changed during discovery. Retry discovery.",
                    (int)HttpStatusCode.Conflict);

            var parsed = ParseDiscovery(() => RemoteMcpMetadataParser.ParseToolsListPage(response.Body));
            // Check the running total, because each page is within the limit on its own.
            tools.AddRange(parsed.Tools);
            if (tools.Count > RemoteMcpMetadataParser.MaximumTools)
                throw DiscoveryInvalid("The remote server returned more tools than the limit.");
            if (parsed.NextCursor is null)
                break;
            if (!seenCursors.Add(parsed.NextCursor))
                throw DiscoveryInvalid("The remote server repeated a tools/list cursor.");
            cursor = parsed.NextCursor;
        }

        // BuildCatalog rejects a tool name that appears on more than one page and computes the catalog digest.
        // The loop ran at least once, so policy is set.
        var catalog = ParseDiscovery(() => RemoteMcpMetadataParser.BuildCatalog(tools));
        return (catalog, policy!);
    }

    private static async Task<ReadOnlyMemory<byte>> ReadRegistryAsync(Func<Task<ReadOnlyMemory<byte>>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        // Map network failures and timeouts to one stable 503 error. A cancellation that the caller
        // requested still propagates unchanged.
        catch (HttpRequestException)
        {
            throw RegistryUnavailable();
        }
        catch (TaskCanceledException exception) when (exception.CancellationToken == default ||
                                                       !exception.CancellationToken.IsCancellationRequested)
        {
            throw RegistryUnavailable();
        }
    }

    // Parser failures mean the upstream returned bad data, so they map to 502 rather than 400.
    private static T ParseRegistry<T>(Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (InvalidDataException exception)
        {
            throw new RemoteMcpConnectionException(
                "registry_metadata_invalid",
                exception.Message,
                (int)HttpStatusCode.BadGateway);
        }
    }

    private static T ParseDiscovery<T>(Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (InvalidDataException exception)
        {
            throw DiscoveryInvalid(exception.Message);
        }
    }

    private static RemoteMcpConnectionException RegistryUnavailable() =>
        new(
            "remote_mcp_registry_unavailable",
            "The MCP Registry is unavailable. The current pin is unchanged.",
            (int)HttpStatusCode.ServiceUnavailable);

    private static RemoteMcpConnectionException RemoteUnavailable() =>
        new(
            "remote_mcp_server_unavailable",
            "The remote MCP server is unavailable. The current pin is unchanged.",
            (int)HttpStatusCode.ServiceUnavailable);

    private static RemoteMcpConnectionException DiscoveryInvalid(string message) =>
        new("remote_mcp_discovery_invalid", message, (int)HttpStatusCode.BadGateway);
}
