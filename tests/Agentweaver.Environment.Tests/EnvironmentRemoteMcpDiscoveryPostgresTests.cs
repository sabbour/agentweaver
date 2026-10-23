using System.Collections.Immutable;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Environment;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentRemoteMcpDiscoveryPostgresTests(EnvironmentPostgresFixture fixture)
    : IClassFixture<EnvironmentPostgresFixture>
{
    private const string RegistryName = "example/weather";
    private const string RegistryVersion = "1.2.3";
    private const string RegistryJson =
        """{"server":{"name":"example/weather","version":"1.2.3","remotes":[{"type":"streamable-http","url":"https://mcp.example.com/mcp"}]}}""";

    [Fact]
    public async Task PagedDiscoveryPinsFullCatalogAndReplaysWithoutNetwork()
    {
        var (store, discovery, source, projectId, caller) = Create();
        var enabled = await CreateEnabledAsync(store, caller, projectId);
        source.Pages[""] = Page("""[{"name":"zeta","inputSchema":{"type":"object"}}]""", "page-2");
        source.Pages["page-2"] = Page("""[{"name":"alpha","inputSchema":{"type":"object"}}]""", null);

        var discovered = await discovery.DiscoverAsync(
            caller, projectId, ConnectionId(enabled), enabled.Head.RowRevision, "discover-1", CancellationToken.None);
        var reads = source.ToolsListReads;
        var replay = await discovery.DiscoverAsync(
            caller, projectId, ConnectionId(enabled), enabled.Head.RowRevision, "discover-1", CancellationToken.None);

        Assert.False(discovered.Replayed);
        Assert.Equal(["alpha", "zeta"], discovered.Snapshot.Catalog!.Tools.Select(tool => tool.Name));
        Assert.Equal(2, reads);
        Assert.True(replay.Replayed);
        Assert.Equal(reads, source.ToolsListReads);
        Assert.Equal(discovered.Snapshot.Catalog.CatalogSha256, replay.Snapshot.Catalog!.CatalogSha256);

        var conflict = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() => discovery.DiscoverAsync(
            caller, projectId, ConnectionId(enabled), enabled.Head.RowRevision + 5, "discover-1", CancellationToken.None));
        Assert.Equal("idempotency_conflict", conflict.Code);
    }

    [Fact]
    public async Task SchemaChangeCreatesNewCatalogAndWithholdsReviewedTool()
    {
        var (store, discovery, source, projectId, caller) = Create();
        var enabled = await CreateEnabledAsync(store, caller, projectId);
        source.Pages[""] = Page("""[{"name":"forecast","inputSchema":{"type":"object"}}]""", null);
        var first = await discovery.DiscoverAsync(
            caller, projectId, ConnectionId(enabled), enabled.Head.RowRevision, "discover-a", CancellationToken.None);
        var reviewed = RemoteMcpConnectionReviewReference.FromSnapshot(first.Snapshot);
        var tool = first.Snapshot.Catalog!.Tools[0];
        Assert.True((await store.EvaluateInvocationAsync(
            caller, reviewed, tool.Name, tool.ToolRevision, CancellationToken.None)).Allowed);

        source.Pages[""] = Page(
            """[{"name":"forecast","inputSchema":{"type":"object","properties":{"city":{"type":"string"}}}}]""",
            null);
        var second = await discovery.DiscoverAsync(
            caller, projectId, ConnectionId(enabled), first.Snapshot.Head.RowRevision, "discover-b", CancellationToken.None);
        var validity = await store.EvaluateInvocationAsync(
            caller, reviewed, tool.Name, tool.ToolRevision, CancellationToken.None);

        Assert.Equal(2, second.Snapshot.Catalog!.DiscoveryRevision);
        Assert.NotEqual(first.Snapshot.Catalog.CatalogSha256, second.Snapshot.Catalog.CatalogSha256);
        Assert.False(validity.Allowed);
        Assert.True(validity.ReviewRequired);
        Assert.Equal(RemoteMcpInvocationDenial.CatalogChanged, validity.Denial);
        Assert.Equal(second.Snapshot.Catalog.CatalogSha256, validity.CurrentReference!.CatalogSha256);
    }

    [Fact]
    public async Task RegistryPinnedDiscoveryFailsExplicitlyAndKeepsExistingPin()
    {
        var (store, discovery, source, projectId, caller) = Create();
        source.RegistryVersion = Encoding.UTF8.GetBytes(RegistryJson);
        var draft = await discovery.ResolveDraftAsync(
            caller,
            projectId,
            "Weather",
            "https://mcp.example.com/mcp",
            null,
            RemoteMcpAuthenticationMode.None,
            RemoteMcpTransportProfile.StreamableHttp20250618,
            RegistryName,
            RegistryVersion,
            CancellationToken.None);
        var enabled = await CreateEnabledAsync(store, caller, projectId, draft);
        Assert.Equal(RegistryVersion, enabled.Configuration.RegistryServer!.ExactVersion);
        source.Pages[""] = Page("""[{"name":"forecast","inputSchema":{"type":"object"}}]""", null);
        var pinned = await discovery.DiscoverAsync(
            caller, projectId, ConnectionId(enabled), enabled.Head.RowRevision, "discover-registry", CancellationToken.None);
        Assert.Equal(enabled.Configuration.RegistryServer.MetadataSha256, pinned.Snapshot.Catalog!.RegistryMetadataSha256);

        source.RegistryUnavailable = true;
        var unavailable = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() => discovery.DiscoverAsync(
            caller, projectId, ConnectionId(enabled), pinned.Snapshot.Head.RowRevision, "discover-down", CancellationToken.None));
        source.RegistryUnavailable = false;
        source.RegistryVersion = Encoding.UTF8.GetBytes(RegistryJson.Replace("}}", ",\"title\":\"Changed\"}}", StringComparison.Ordinal));
        var changed = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() => discovery.DiscoverAsync(
            caller, projectId, ConnectionId(enabled), pinned.Snapshot.Head.RowRevision, "discover-changed", CancellationToken.None));
        var current = await store.GetAsync(caller, projectId, ConnectionId(enabled), CancellationToken.None);
        var tool = pinned.Snapshot.Catalog.Tools[0];
        var validity = await store.EvaluateInvocationAsync(
            caller,
            RemoteMcpConnectionReviewReference.FromSnapshot(pinned.Snapshot),
            tool.Name,
            tool.ToolRevision,
            CancellationToken.None);

        Assert.Equal("remote_mcp_registry_unavailable", unavailable.Code);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.Equal("registry_metadata_changed", changed.Code);
        Assert.Equal(pinned.Snapshot.Head.RowRevision, current!.Head.RowRevision);
        Assert.Equal(pinned.Snapshot.Catalog.CatalogSha256, current.Catalog!.CatalogSha256);
        Assert.True(validity.Allowed);
    }

    [Fact]
    public async Task RegistrySelectionRejectsFloatingVersionAndUnadvertisedEndpoint()
    {
        var (_, discovery, source, projectId, caller) = Create();
        source.RegistryVersion = Encoding.UTF8.GetBytes(RegistryJson);

        await Assert.ThrowsAsync<ArgumentException>(() => discovery.ResolveDraftAsync(
            caller, projectId, "Weather", "https://mcp.example.com/mcp", null,
            RemoteMcpAuthenticationMode.None, RemoteMcpTransportProfile.StreamableHttp20250618,
            RegistryName, "latest", CancellationToken.None));
        var unadvertised = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() => discovery.ResolveDraftAsync(
            caller, projectId, "Weather", "https://other.example.com/mcp", null,
            RemoteMcpAuthenticationMode.None, RemoteMcpTransportProfile.StreamableHttp20250618,
            RegistryName, RegistryVersion, CancellationToken.None));
        Assert.Equal("registry_metadata_invalid", unadvertised.Code);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => discovery.ResolveDraftAsync(
            caller, projectId, "Weather", "https://mcp.example.com/mcp", null,
            RemoteMcpAuthenticationMode.None, (RemoteMcpTransportProfile)999, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task DisableDuringDiscoveryRejectsTheSaveAndPinsNothing()
    {
        var (store, discovery, source, projectId, caller) = Create();
        var enabled = await CreateEnabledAsync(store, caller, projectId);
        source.Pages[""] = Page("""[{"name":"forecast","inputSchema":{"type":"object"}}]""", null);
        source.OnToolsListRead = () => store.SetStateAsync(
            caller,
            projectId,
            ConnectionId(enabled),
            enabled.Head.RowRevision,
            RemoteMcpConnectionState.Disabled,
            "disable-during-discovery",
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() => discovery.DiscoverAsync(
            caller, projectId, ConnectionId(enabled), enabled.Head.RowRevision, "discover-race", CancellationToken.None));
        var current = await store.GetAsync(caller, projectId, ConnectionId(enabled), CancellationToken.None);

        Assert.Equal("connection_revision_stale", exception.Code);
        Assert.Equal(RemoteMcpConnectionState.Disabled, current!.Head.State);
        Assert.Null(current.Catalog);
    }

    [Fact]
    public async Task ReadOnlyActorCannotDiscoverAndCausesNoNetworkRead()
    {
        var projectId = "project-" + Guid.NewGuid().ToString("N");
        var caller = new CurrentCallerRequest("test-bearer");
        var projects = new SwitchableProjectsClient(projectId);
        var store = new RemoteMcpConnectionStore(fixture.DataSource, projects, TimeProvider.System);
        var source = new FakeDiscoverySource();
        var discovery = new RemoteMcpDiscoveryService(store, source);
        var enabled = await CreateEnabledAsync(store, caller, projectId);
        source.Pages[""] = Page("""[{"name":"forecast","inputSchema":{"type":"object"}}]""", null);
        projects.Write = false;

        var exception = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() => discovery.DiscoverAsync(
            caller, projectId, ConnectionId(enabled), enabled.Head.RowRevision, "discover-read-only", CancellationToken.None));
        var directory = await store.ListDirectoryAsync(caller, projectId, 10, null, CancellationToken.None);

        Assert.Equal("project_write_not_authorized", exception.Code);
        Assert.Equal(0, source.ToolsListReads);
        var entry = Assert.Single(directory.Connections);
        Assert.Equal(enabled.Configuration.ConfigurationSha256, entry.ConfigurationSha256);
        Assert.Null(entry.CatalogSha256);

        projects.Write = true;
        projects.ProjectId = "project-other";
        var otherProject = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() =>
            store.ListDirectoryAsync(caller, projectId, 10, null, CancellationToken.None));
        Assert.Equal("project_read_not_authorized", otherProject.Code);
    }

    [Fact]
    public async Task DiscoveryRejectsUnsupportedProtocolCursorLoopAndPolicyChange()
    {
        var (store, discovery, source, projectId, caller) = Create();
        var enabled = await CreateEnabledAsync(store, caller, projectId);

        source.Pages[""] = Page("""[{"name":"a","inputSchema":{"type":"object"}}]""", null);
        source.ProtocolVersion = "2025-03-26";
        var protocol = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() => discovery.DiscoverAsync(
            caller, projectId, ConnectionId(enabled), enabled.Head.RowRevision, "discover-protocol", CancellationToken.None));
        source.ProtocolVersion = RemoteMcpDiscoveryService.RequiredProtocolVersion;
        source.Pages[""] = new byte[RemoteMcpTransportAdapterOptions.CurrentMaximumResponseBytes + 1];
        var oversized = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() => discovery.DiscoverAsync(
            caller, projectId, ConnectionId(enabled), enabled.Head.RowRevision, "discover-oversized", CancellationToken.None));

        source.Pages[""] = Page("""[{"name":"a","inputSchema":{"type":"object"}}]""", "loop");
        source.Pages["loop"] = Page("""[{"name":"b","inputSchema":{"type":"object"}}]""", "loop");
        var loop = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() => discovery.DiscoverAsync(
            caller, projectId, ConnectionId(enabled), enabled.Head.RowRevision, "discover-loop", CancellationToken.None));

        source.Pages["loop"] = Page("""[{"name":"b","inputSchema":{"type":"object"}}]""", null);
        source.ChangePolicyAfterFirstPage = true;
        var policy = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() => discovery.DiscoverAsync(
            caller, projectId, ConnectionId(enabled), enabled.Head.RowRevision, "discover-policy", CancellationToken.None));
        var current = await store.GetAsync(caller, projectId, ConnectionId(enabled), CancellationToken.None);

        Assert.Equal("remote_mcp_discovery_invalid", protocol.Code);
        Assert.Equal("remote_mcp_discovery_invalid", oversized.Code);
        Assert.Equal("remote_mcp_discovery_invalid", loop.Code);
        Assert.Equal("applied_policy_changed", policy.Code);
        Assert.Null(current!.Catalog);
    }

    [Fact]
    public async Task UnavailableTransportFailsExplicitly()
    {
        var projectId = "project-" + Guid.NewGuid().ToString("N");
        var caller = new CurrentCallerRequest("test-bearer");
        var store = new RemoteMcpConnectionStore(
            fixture.DataSource, new SwitchableProjectsClient(projectId), TimeProvider.System);
        var discovery = new RemoteMcpDiscoveryService(store, new UnavailableRemoteMcpDiscoverySource());
        var enabled = await CreateEnabledAsync(store, caller, projectId);

        var exception = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() => discovery.DiscoverAsync(
            caller, projectId, ConnectionId(enabled), enabled.Head.RowRevision, "discover-none", CancellationToken.None));

        Assert.Equal("remote_mcp_discovery_transport_unavailable", exception.Code);
        Assert.Equal(503, exception.StatusCode);
    }

    private (RemoteMcpConnectionStore Store, RemoteMcpDiscoveryService Discovery, FakeDiscoverySource Source, string ProjectId, CurrentCallerRequest Caller) Create()
    {
        var projectId = "project-" + Guid.NewGuid().ToString("N");
        var store = new RemoteMcpConnectionStore(
            fixture.DataSource, new SwitchableProjectsClient(projectId), TimeProvider.System);
        var source = new FakeDiscoverySource { ProjectId = projectId };
        return (store, new RemoteMcpDiscoveryService(store, source), source, projectId, new CurrentCallerRequest("test-bearer"));
    }

    private static async Task<RemoteMcpConnectionSnapshot> CreateEnabledAsync(
        RemoteMcpConnectionStore store,
        CurrentCallerRequest caller,
        string projectId,
        RemoteMcpConnectionDraft? draft = null)
    {
        var created = await store.CreateAsync(
            caller,
            projectId,
            draft ?? new("Weather", "https://mcp.example.com/mcp", null, RemoteMcpAuthenticationMode.None),
            "create-" + Guid.NewGuid().ToString("N"),
            CancellationToken.None);
        var enabled = await store.SetStateAsync(
            caller,
            projectId,
            created.Snapshot.Head.Connection.ConnectionId,
            created.Snapshot.Head.RowRevision,
            RemoteMcpConnectionState.Enabled,
            "enable-" + Guid.NewGuid().ToString("N"),
            CancellationToken.None);
        return enabled.Snapshot;
    }

    private static Guid ConnectionId(RemoteMcpConnectionSnapshot snapshot) => snapshot.Head.Connection.ConnectionId;

    private static byte[] Page(string toolsJson, string? nextCursor) =>
        Encoding.UTF8.GetBytes(nextCursor is null
            ? $$$"""{"result":{"tools":{{{toolsJson}}}}}"""
            : $$$"""{"result":{"tools":{{{toolsJson}}},"nextCursor":"{{{nextCursor}}}"}}""");

    private sealed class FakeDiscoverySource : IRemoteMcpDiscoverySource
    {
        public string ProjectId { get; set; } = "project";
        public Dictionary<string, byte[]> Pages { get; } = new(StringComparer.Ordinal);
        public byte[] RegistryVersion { get; set; } = [];
        public bool RegistryUnavailable { get; set; }
        public bool ChangePolicyAfterFirstPage { get; set; }
        public string ProtocolVersion { get; set; } = RemoteMcpDiscoveryService.RequiredProtocolVersion;
        public Func<Task>? OnToolsListRead { get; set; }
        public int ToolsListReads { get; private set; }

        public Task<ReadOnlyMemory<byte>> ReadRegistryServersAsync(string? cursor, CancellationToken cancellationToken) =>
            Task.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes("""{"servers":[]}"""));

        public Task<ReadOnlyMemory<byte>> ReadRegistryServerVersionAsync(
            string serverName,
            string exactVersion,
            CancellationToken cancellationToken) =>
            RegistryUnavailable
                ? throw new HttpRequestException("Registry unavailable.")
                : Task.FromResult<ReadOnlyMemory<byte>>(RegistryVersion);

        public async Task<RemoteMcpToolsListResponse> ReadToolsListPageAsync(
            RemoteMcpConnectionConfiguration configuration,
            string? cursor,
            CancellationToken cancellationToken)
        {
            ToolsListReads++;
            if (OnToolsListRead is { } callback)
            {
                OnToolsListRead = null;
                await callback();
            }

            var generation = ChangePolicyAfterFirstPage && cursor is not null ? 2 : 1;
            return new(
                Pages[cursor ?? string.Empty],
                ProtocolVersion,
                new RemoteMcpAppliedNetworkPolicyReference(
                    "policy-reference",
                    new EnvironmentGenerationFence(
                        new EnvironmentOwnerIdentity("tenant-test", ProjectId, "run-test", "environment-test"),
                        lifecycleGeneration: 1),
                    appliedGeneration: generation,
                    egressIntentSha256: new string('a', 64)));
        }
    }

    private sealed class SwitchableProjectsClient(string projectId) : IProjectsConfigClient
    {
        public string ProjectId { get; set; } = projectId;
        public bool Write { get; set; } = true;

        public Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
            CurrentCallerRequest caller,
            CancellationToken cancellationToken)
        {
            var grants = Write
                ? ImmutableArray.Create(
                    new ProjectAuthorizationPermissionGrant(ProjectAuthorizationPermission.ReadProjects, RoleRevision: 1),
                    new ProjectAuthorizationPermissionGrant(ProjectAuthorizationPermission.WriteProjects, RoleRevision: 1))
                : ImmutableArray.Create(
                    new ProjectAuthorizationPermissionGrant(ProjectAuthorizationPermission.ReadProjects, RoleRevision: 1));
            return Task.FromResult(new ProjectAuthorizationContextResponse(
                ProjectAuthorizationContextContract.CurrentVersion,
                "https://identity.example",
                "actor-test",
                "tenant-test",
                1,
                null,
                null,
                ImmutableArray.Create(new EffectiveProjectAuthorization(
                    ProjectAuthorityResourceType.Project,
                    ProjectId,
                    grants))));
        }

        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller,
            string projectId,
            string runId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Remote MCP discovery tests do not read run selection.");
    }
}
