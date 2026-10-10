using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Environment;

public sealed class RemoteMcpConnectionException(string code, string message, int statusCode)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record RemoteMcpConnectionDraft(
    string DisplayName,
    string EndpointUri,
    string? ResourceUri,
    RemoteMcpAuthenticationMode AuthenticationMode);

public sealed record RemoteMcpConnectionMutationResult(
    RemoteMcpConnectionSnapshot Snapshot,
    bool Replayed);

public sealed record RemoteMcpIdentityBindingReceipt(
    string ProjectId,
    Guid ConnectionId,
    string OperationId,
    long FinalConfigurationRevision,
    string FinalConfigurationSha256,
    string IdentityBindingReference);

public sealed record RemoteMcpConnectionPage(
    ImmutableArray<RemoteMcpConnectionSnapshot> Connections,
    Guid? NextConnectionId);

public sealed class RemoteMcpConnectionStore(
    NpgsqlDataSource dataSource,
    IProjectsConfigClient projectsClient,
    TimeProvider timeProvider)
{
    private const string Connections = "\"environment\".\"remote_mcp_connections\"";
    private const string Configurations = "\"environment\".\"remote_mcp_connection_configurations\"";
    private const string CatalogSnapshots = "\"environment\".\"remote_mcp_catalog_snapshots\"";
    private const string Idempotency = "\"environment\".\"remote_mcp_connection_idempotency\"";
    private const int MaximumPageSize = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RemoteMcpConnectionPage> ListAsync(
        CurrentCallerRequest caller,
        string projectId,
        int pageSize,
        Guid? afterConnectionId,
        CancellationToken cancellationToken)
    {
        ValidateProjectId(projectId);
        if (pageSize is < 1 or > MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        var initial = await AuthorizeAsync(
            caller, projectId, ProjectAuthorizationPermission.ReadProjects, cancellationToken).ConfigureAwait(false);
        var results = ImmutableArray.CreateBuilder<RemoteMcpConnectionSnapshot>();
        Guid? next = null;
        List<Guid> ids;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var command = new NpgsqlCommand($"""
                SELECT connection_id
                FROM {Connections}
                WHERE tenant_id = @tenant_id AND project_id = @project_id
                  AND state <> 'Removed'
                  AND (@after_connection_id IS NULL OR connection_id > @after_connection_id)
                ORDER BY connection_id
                LIMIT @limit
                """, connection);
            command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Text, initial.TenantId);
            command.Parameters.AddWithValue("project_id", NpgsqlDbType.Text, projectId);
            command.Parameters.AddWithValue(
                "after_connection_id",
                NpgsqlDbType.Uuid,
                (object?)afterConnectionId ?? DBNull.Value);
            command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            ids = new List<Guid>(pageSize + 1);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                ids.Add(reader.GetGuid(0));
            if (ids.Count > pageSize)
            {
                next = ids[pageSize - 1];
                ids.RemoveAt(pageSize);
            }
        }
        foreach (var id in ids)
        {
            await using var readConnection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = await ReadSnapshotAsync(
                readConnection, null, initial.TenantId, projectId, id, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null)
                results.Add(snapshot);
        }

        var current = await AuthorizeAsync(
            caller, projectId, ProjectAuthorizationPermission.ReadProjects, cancellationToken).ConfigureAwait(false);
        EnsureAuthorizationUnchanged(initial, current, projectId, ProjectAuthorizationPermission.ReadProjects);
        return new(results.ToImmutable(), next);
    }

    public async Task<RemoteMcpConnectionSnapshot?> GetAsync(
        CurrentCallerRequest caller,
        string projectId,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        ValidateProjectId(projectId);
        var initial = await AuthorizeAsync(
            caller, projectId, ProjectAuthorizationPermission.ReadProjects, cancellationToken).ConfigureAwait(false);
        RemoteMcpConnectionSnapshot? snapshot;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
            snapshot = await ReadSnapshotAsync(
                connection, null, initial.TenantId, projectId, connectionId, cancellationToken).ConfigureAwait(false);
        var current = await AuthorizeAsync(
            caller, projectId, ProjectAuthorizationPermission.ReadProjects, cancellationToken).ConfigureAwait(false);
        EnsureAuthorizationUnchanged(initial, current, projectId, ProjectAuthorizationPermission.ReadProjects);
        return snapshot;
    }

    public async Task<RemoteMcpConnectionConfiguration?> GetConfigurationRevisionAsync(
        CurrentCallerRequest caller,
        string projectId,
        Guid connectionId,
        long configurationRevision,
        CancellationToken cancellationToken)
    {
        ValidateProjectId(projectId);
        if (configurationRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(configurationRevision));
        var initial = await AuthorizeAsync(
            caller, projectId, ProjectAuthorizationPermission.ReadProjects, cancellationToken).ConfigureAwait(false);
        RemoteMcpConnectionConfiguration? configuration;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
            configuration = await ReadConfigurationAsync(
                connection,
                null,
                initial.TenantId,
                projectId,
                connectionId,
                configurationRevision,
                cancellationToken).ConfigureAwait(false);
        var current = await AuthorizeAsync(
            caller, projectId, ProjectAuthorizationPermission.ReadProjects, cancellationToken).ConfigureAwait(false);
        EnsureAuthorizationUnchanged(initial, current, projectId, ProjectAuthorizationPermission.ReadProjects);
        return configuration;
    }

    public Task<RemoteMcpConnectionMutationResult> CreateAsync(
        CurrentCallerRequest caller,
        string projectId,
        RemoteMcpConnectionDraft draft,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateProjectId(projectId);
        ValidateIdempotencyKey(idempotencyKey);
        ArgumentNullException.ThrowIfNull(draft);
        var fingerprint = Fingerprint(
            "Create",
            draft.DisplayName,
            draft.EndpointUri,
            draft.ResourceUri ?? string.Empty,
            draft.AuthenticationMode.ToString());
        return MutateAsync(
            caller,
            projectId,
            idempotencyKey,
            fingerprint,
            null,
            "Create",
            ProjectAuthorizationPermission.WriteProjects,
            async (connection, transaction, tenantId, cancellationToken) =>
            {
                var connectionId = Guid.NewGuid();
                var reference = new RemoteMcpConnectionReference(projectId, connectionId);
                var configuration = CreateConfiguration(reference, 1, draft);
                var now = timeProvider.GetUtcNow();
                await using (var insertHead = new NpgsqlCommand($"""
                    INSERT INTO {Connections}
                        (tenant_id, project_id, connection_id, row_revision,
                         current_configuration_revision, current_discovery_revision, state,
                         created_at, updated_at)
                    VALUES (@tenant_id, @project_id, @connection_id, 1, 1, NULL, 'Draft', @now, @now)
                    """, connection, transaction))
                {
                    AddOwner(insertHead, tenantId, projectId);
                    insertHead.Parameters.AddWithValue("connection_id", NpgsqlDbType.Uuid, connectionId);
                    insertHead.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
                    await insertHead.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await InsertConfigurationAsync(
                    connection, transaction, tenantId, projectId, configuration, now, cancellationToken)
                    .ConfigureAwait(false);
                return await ReadSnapshotAsync(
                    connection, transaction, tenantId, projectId, connectionId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The new remote MCP connection was not persisted.");
            },
            cancellationToken);
    }

    public Task<RemoteMcpConnectionMutationResult> UpdateAsync(
        CurrentCallerRequest caller,
        string projectId,
        Guid connectionId,
        long expectedRowRevision,
        RemoteMcpConnectionDraft draft,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateProjectId(projectId);
        ValidateIdempotencyKey(idempotencyKey);
        ArgumentNullException.ThrowIfNull(draft);
        if (expectedRowRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRowRevision));
        var fingerprint = Fingerprint(
            "Update",
            connectionId.ToString("D"),
            expectedRowRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            draft.DisplayName,
            draft.EndpointUri,
            draft.ResourceUri ?? string.Empty,
            draft.AuthenticationMode.ToString());
        return MutateAsync(
            caller,
            projectId,
            idempotencyKey,
            fingerprint,
            connectionId,
            "Update",
            ProjectAuthorizationPermission.WriteProjects,
            async (connection, transaction, tenantId, cancellationToken) =>
            {
                var current = await ReadSnapshotAsync(
                    connection, transaction, tenantId, projectId, connectionId, cancellationToken).ConfigureAwait(false)
                    ?? throw NotFound();
                RequireExpectedRevision(current.Head, expectedRowRevision);
                if (current.Head.State == RemoteMcpConnectionState.Removed)
                    throw Conflict("connection_removed", "A removed connection cannot be changed.");

                var newConfiguration = CreateConfiguration(
                    current.Head.Connection,
                    checked(current.Configuration.ConfigurationRevision + 1),
                    draft);
                var now = timeProvider.GetUtcNow();
                await InsertConfigurationAsync(
                    connection, transaction, tenantId, projectId, newConfiguration, now, cancellationToken)
                    .ConfigureAwait(false);
                await UpdateHeadAsync(
                    connection,
                    transaction,
                    tenantId,
                    projectId,
                    connectionId,
                    expectedRowRevision,
                    newConfiguration.ConfigurationRevision,
                    null,
                    RemoteMcpConnectionState.Draft,
                    now,
                    cancellationToken).ConfigureAwait(false);
                return await ReadSnapshotAsync(
                    connection, transaction, tenantId, projectId, connectionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The updated remote MCP connection was not persisted.");
            },
            cancellationToken);
    }

    public async Task<RemoteMcpIdentityBindingReceipt> BindIdentityAsync(
        CurrentCallerRequest caller,
        string projectId,
        Guid connectionId,
        long expectedConfigurationRevision,
        string expectedConfigurationSha256,
        string identityBindingReference,
        string operationId,
        CancellationToken cancellationToken)
    {
        ValidateProjectId(projectId);
        ValidateIdempotencyKey(operationId);
        if (expectedConfigurationRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedConfigurationRevision));
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedConfigurationSha256);
        var expectedDigest = RemoteMcpDigest.ValidateSha256(
            expectedConfigurationSha256,
            nameof(expectedConfigurationSha256));
        ArgumentException.ThrowIfNullOrWhiteSpace(identityBindingReference);
        if (!Guid.TryParseExact(identityBindingReference, "N", out var identityBindingId) ||
            identityBindingId == Guid.Empty ||
            !string.Equals(identityBindingId.ToString("N"), identityBindingReference, StringComparison.Ordinal))
            throw new ArgumentException(
                "Identity binding references must be canonical lower-case UUID-N values.",
                nameof(identityBindingReference));

        var authorizedCaller = await AuthorizeAsync(
            caller,
            projectId,
            ProjectAuthorizationPermission.WriteProjects,
            cancellationToken).ConfigureAwait(false);
        var fingerprint = Fingerprint(
            "IdentityBinding",
            authorizedCaller.Issuer,
            authorizedCaller.ActorId,
            connectionId.ToString("D"),
            expectedConfigurationRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            expectedDigest,
            identityBindingReference);
        var result = await MutateAsync(
            caller,
            projectId,
            operationId,
            fingerprint,
            connectionId,
            "Update",
            ProjectAuthorizationPermission.WriteProjects,
            async (connection, transaction, tenantId, cancellationToken) =>
            {
                var current = await ReadSnapshotAsync(
                    connection, transaction, tenantId, projectId, connectionId, cancellationToken)
                    .ConfigureAwait(false) ?? throw NotFound();
                if (current.Head.State == RemoteMcpConnectionState.Removed)
                    throw Conflict("connection_removed", "A removed connection cannot be changed.");
                if (current.Configuration.AuthenticationMode != RemoteMcpAuthenticationMode.DelegatedOAuth)
                    throw Conflict(
                        "identity_binding_requires_oauth",
                        "Identity bindings are only valid for delegated OAuth connections.");
                if (current.Configuration.ConfigurationRevision != expectedConfigurationRevision ||
                    !string.Equals(
                        current.Configuration.ConfigurationSha256,
                        expectedDigest,
                        StringComparison.Ordinal))
                    throw Conflict(
                        "configuration_changed",
                        "The remote MCP configuration changed before the Identity binding was applied.");

                var newConfiguration = new RemoteMcpConnectionConfiguration(
                    current.Head.Connection,
                    checked(current.Configuration.ConfigurationRevision + 1),
                    current.Configuration.DisplayName,
                    current.Configuration.EndpointUri,
                    current.Configuration.ResourceUri,
                    current.Configuration.AuthenticationMode,
                    identityBindingReference,
                    current.Configuration.TransportProfile,
                    current.Configuration.RegistryServer);
                var now = timeProvider.GetUtcNow();
                await InsertConfigurationAsync(
                    connection,
                    transaction,
                    tenantId,
                    projectId,
                    newConfiguration,
                    now,
                    cancellationToken).ConfigureAwait(false);
                await UpdateHeadAsync(
                    connection,
                    transaction,
                    tenantId,
                    projectId,
                    connectionId,
                    current.Head.RowRevision,
                    newConfiguration.ConfigurationRevision,
                    null,
                    RemoteMcpConnectionState.Draft,
                    now,
                    cancellationToken).ConfigureAwait(false);
                return await ReadSnapshotAsync(
                    connection, transaction, tenantId, projectId, connectionId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Identity-bound connection was not persisted.");
            },
            cancellationToken,
            initialAuthorizationGuard: authorizedCaller).ConfigureAwait(false);
        return new(
            projectId,
            connectionId,
            operationId,
            result.Snapshot.Configuration.ConfigurationRevision,
            result.Snapshot.Configuration.ConfigurationSha256,
            result.Snapshot.Configuration.IdentityBindingReference
                ?? throw new InvalidOperationException("The Identity binding reference was not persisted."));
    }

    public Task<RemoteMcpConnectionMutationResult> SetStateAsync(
        CurrentCallerRequest caller,
        string projectId,
        Guid connectionId,
        long expectedRowRevision,
        RemoteMcpConnectionState targetState,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateProjectId(projectId);
        ValidateIdempotencyKey(idempotencyKey);
        if (expectedRowRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRowRevision));
        if (targetState is not (RemoteMcpConnectionState.Enabled or
            RemoteMcpConnectionState.Disabled or RemoteMcpConnectionState.Removed))
            throw new ArgumentOutOfRangeException(nameof(targetState));
        var fingerprint = Fingerprint(
            "SetState",
            connectionId.ToString("D"),
            expectedRowRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            targetState.ToString());
        return MutateAsync(
            caller,
            projectId,
            idempotencyKey,
            fingerprint,
            connectionId,
            targetState switch
            {
                RemoteMcpConnectionState.Enabled => "Enable",
                RemoteMcpConnectionState.Disabled => "Disable",
                _ => "Remove"
            },
            ProjectAuthorizationPermission.WriteProjects,
            async (connection, transaction, tenantId, cancellationToken) =>
            {
                var current = await ReadSnapshotAsync(
                    connection, transaction, tenantId, projectId, connectionId, cancellationToken).ConfigureAwait(false)
                    ?? throw NotFound();
                RequireExpectedRevision(current.Head, expectedRowRevision);
                if (current.Head.State == RemoteMcpConnectionState.Removed &&
                    targetState != RemoteMcpConnectionState.Removed)
                    throw Conflict("connection_removed", "A removed connection cannot be re-enabled.");

                if (current.Head.State != targetState)
                {
                    var now = timeProvider.GetUtcNow();
                    await UpdateHeadAsync(
                        connection,
                        transaction,
                        tenantId,
                        projectId,
                        connectionId,
                        expectedRowRevision,
                        current.Head.CurrentConfigurationRevision,
                        targetState == RemoteMcpConnectionState.Enabled
                            ? current.Head.CurrentDiscoveryRevision
                            : null,
                        targetState,
                        now,
                        cancellationToken).ConfigureAwait(false);
                }

                return await ReadSnapshotAsync(
                    connection, transaction, tenantId, projectId, connectionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The remote MCP connection state was not persisted.");
            },
            cancellationToken);
    }

    internal Task<RemoteMcpConnectionMutationResult> SaveDiscoveryAsync(
        CurrentCallerRequest caller,
        string projectId,
        Guid connectionId,
        long expectedRowRevision,
        RemoteMcpParsedToolCatalog parsedCatalog,
        string? registryMetadataSha256,
        RemoteMcpAppliedNetworkPolicyReference appliedNetworkPolicy,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateProjectId(projectId);
        ValidateIdempotencyKey(idempotencyKey);
        ArgumentNullException.ThrowIfNull(parsedCatalog);
        ArgumentNullException.ThrowIfNull(appliedNetworkPolicy);
        if (registryMetadataSha256 is not null)
            RemoteMcpDigest.ValidateSha256(registryMetadataSha256, nameof(registryMetadataSha256));
        if (expectedRowRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRowRevision));
        var fingerprint = Fingerprint(
            "SaveDiscovery",
            connectionId.ToString("D"),
            expectedRowRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            parsedCatalog.CatalogSha256,
            registryMetadataSha256 ?? string.Empty,
            appliedNetworkPolicy.Reference,
            appliedNetworkPolicy.AppliedGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            appliedNetworkPolicy.EgressIntentSha256,
            appliedNetworkPolicy.EnvironmentFence.LifecycleGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return MutateAsync(
            caller,
            projectId,
            idempotencyKey,
            fingerprint,
            connectionId,
            "SaveDiscovery",
            ProjectAuthorizationPermission.WriteProjects,
            async (connection, transaction, tenantId, cancellationToken) =>
            {
                if (!string.Equals(appliedNetworkPolicy.EnvironmentFence.Owner.TenantId, tenantId, StringComparison.Ordinal) ||
                    !string.Equals(appliedNetworkPolicy.EnvironmentFence.Owner.ProjectId, projectId, StringComparison.Ordinal))
                    throw Forbidden("applied_policy_scope_mismatch", "Applied network policy is bound to another tenant or project.");

                var current = await ReadSnapshotAsync(
                    connection, transaction, tenantId, projectId, connectionId, cancellationToken).ConfigureAwait(false)
                    ?? throw NotFound();
                RequireExpectedRevision(current.Head, expectedRowRevision);
                if (current.Head.State != RemoteMcpConnectionState.Enabled)
                    throw Conflict("connection_not_enabled", "Discovery results cannot be pinned for a disabled connection.");
                if (current.Configuration.RegistryServer is { } registry &&
                    !string.Equals(registry.MetadataSha256, registryMetadataSha256, StringComparison.Ordinal))
                    throw Conflict("registry_metadata_changed", "Discovery metadata does not match the pinned Registry version.");
                if (current.Configuration.RegistryServer is null && registryMetadataSha256 is not null)
                    throw Conflict("registry_metadata_unexpected", "Custom endpoints cannot claim Registry provenance.");

                var discoveryRevision = checked(await ReadLastDiscoveryRevisionAsync(
                    connection,
                    transaction,
                    tenantId,
                    projectId,
                    connectionId,
                    cancellationToken).ConfigureAwait(false) + 1);
                var catalog = new RemoteMcpDiscoveryCatalogPin(
                    current.Head.Connection,
                    current.Head.CurrentConfigurationRevision,
                    discoveryRevision,
                    parsedCatalog.CatalogSha256,
                    registryMetadataSha256,
                    parsedCatalog.Tools,
                    appliedNetworkPolicy);
                var now = timeProvider.GetUtcNow();
                await InsertCatalogAsync(
                    connection, transaction, tenantId, projectId, connectionId, catalog, now, cancellationToken)
                    .ConfigureAwait(false);
                await UpdateHeadAsync(
                    connection,
                    transaction,
                    tenantId,
                    projectId,
                    connectionId,
                    expectedRowRevision,
                    current.Head.CurrentConfigurationRevision,
                    discoveryRevision,
                    RemoteMcpConnectionState.Enabled,
                    now,
                    cancellationToken).ConfigureAwait(false);
                return await ReadSnapshotAsync(
                    connection, transaction, tenantId, projectId, connectionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The remote MCP discovery pin was not persisted.");
            },
            cancellationToken);
    }

    private async Task<RemoteMcpConnectionMutationResult> MutateAsync(
        CurrentCallerRequest caller,
        string projectId,
        string idempotencyKey,
        string fingerprint,
        Guid? connectionId,
        string mutationOperation,
        ProjectAuthorizationPermission permission,
        Func<NpgsqlConnection, NpgsqlTransaction, string, CancellationToken, Task<RemoteMcpConnectionSnapshot>> mutation,
        CancellationToken cancellationToken,
        ProjectAuthorizationContextResponse? initialAuthorizationGuard = null)
    {
        var initial = await AuthorizeAsync(caller, projectId, permission, cancellationToken).ConfigureAwait(false);
        if (initialAuthorizationGuard is not null)
            EnsureAuthorizationUnchanged(initialAuthorizationGuard, initial, projectId, permission);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireLockAsync(
            connection,
            transaction,
            $"remote-mcp-idempotency:{initial.TenantId}:{projectId}:{idempotencyKey}",
            cancellationToken).ConfigureAwait(false);
        if (connectionId is { } id)
            await AcquireLockAsync(
                connection,
                transaction,
                $"remote-mcp-connection:{initial.TenantId}:{projectId}:{id:D}",
                cancellationToken).ConfigureAwait(false);

        var authorized = await AuthorizeAsync(caller, projectId, permission, cancellationToken).ConfigureAwait(false);
        EnsureAuthorizationUnchanged(initial, authorized, projectId, permission);
        var replay = await ReadIdempotencyAsync(
            connection,
            transaction,
            initial.TenantId,
            projectId,
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            if (!string.Equals(replay.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw Conflict("idempotency_conflict", "The idempotency key was already used for a different request.");
            var replayed = DeserializeSnapshot(replay.Value.ResultJson);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await EnsureCurrentAuthorityAsync(
                caller, initial, projectId, permission, cancellationToken).ConfigureAwait(false);
            return new(replayed, Replayed: true);
        }

        var snapshot = await mutation(connection, transaction, initial.TenantId, cancellationToken)
            .ConfigureAwait(false);
        await InsertIdempotencyAsync(
            connection,
            transaction,
            initial.TenantId,
            projectId,
            idempotencyKey,
            connectionId ?? snapshot.Head.Connection.ConnectionId,
            mutationOperation,
            fingerprint,
            SerializeSnapshot(snapshot),
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        await EnsureCurrentAuthorityAsync(
            caller, initial, projectId, permission, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await EnsureCurrentAuthorityAsync(
            caller, initial, projectId, permission, cancellationToken).ConfigureAwait(false);
        return new(snapshot, Replayed: false);
    }

    private async Task<ProjectAuthorizationContextResponse> AuthorizeAsync(
        CurrentCallerRequest caller,
        string projectId,
        ProjectAuthorizationPermission permission,
        CancellationToken cancellationToken)
    {
        var context = await projectsClient.GetAuthorizationContextAsync(caller, cancellationToken)
            .ConfigureAwait(false);
        if (context.BoundRunId is not null ||
            context.BoundProjectId is not null &&
            !string.Equals(context.BoundProjectId, projectId, StringComparison.Ordinal))
            throw Forbidden("authorization_context_mismatch", "The current caller is bound to another project or run.");
        if (!context.EffectiveAuthority.Any(resource =>
                resource.ResourceType == ProjectAuthorityResourceType.Project &&
                string.Equals(resource.ResourceId, projectId, StringComparison.Ordinal) &&
                resource.Permissions.Any(grant =>
                    grant.Permission == permission && grant.RoleRevision > 0)))
            throw Forbidden(
                permission == ProjectAuthorizationPermission.WriteProjects
                    ? "project_write_not_authorized"
                    : "project_read_not_authorized",
                "The current caller does not have the required project authority.");
        return context;
    }

    private async Task EnsureCurrentAuthorityAsync(
        CurrentCallerRequest caller,
        ProjectAuthorizationContextResponse initial,
        string projectId,
        ProjectAuthorizationPermission permission,
        CancellationToken cancellationToken)
    {
        var current = await AuthorizeAsync(caller, projectId, permission, cancellationToken).ConfigureAwait(false);
        EnsureAuthorizationUnchanged(initial, current, projectId, permission);
    }

    private static void EnsureAuthorizationUnchanged(
        ProjectAuthorizationContextResponse initial,
        ProjectAuthorizationContextResponse current,
        string projectId,
        ProjectAuthorizationPermission permission)
    {
        var initialRoleRevision = RoleRevision(initial, projectId, permission);
        var currentRoleRevision = RoleRevision(current, projectId, permission);
        if (!string.Equals(initial.Issuer, current.Issuer, StringComparison.Ordinal) ||
            !string.Equals(initial.ActorId, current.ActorId, StringComparison.Ordinal) ||
            !string.Equals(initial.TenantId, current.TenantId, StringComparison.Ordinal) ||
            initial.MembershipRevision != current.MembershipRevision ||
            initialRoleRevision != currentRoleRevision ||
            !string.Equals(initial.BoundProjectId, current.BoundProjectId, StringComparison.Ordinal) ||
            !string.Equals(initial.BoundRunId, current.BoundRunId, StringComparison.Ordinal))
            throw Conflict("authorization_changed", "Project authority changed while the connection operation was in progress.");
    }

    private static long RoleRevision(
        ProjectAuthorizationContextResponse context,
        string projectId,
        ProjectAuthorizationPermission permission) =>
        context.EffectiveAuthority
            .Where(resource =>
                resource.ResourceType == ProjectAuthorityResourceType.Project &&
                string.Equals(resource.ResourceId, projectId, StringComparison.Ordinal))
            .SelectMany(resource => resource.Permissions)
            .Where(grant => grant.Permission == permission)
            .Select(grant => grant.RoleRevision)
            .DefaultIfEmpty(0)
            .Max();

    private static RemoteMcpConnectionConfiguration CreateConfiguration(
        RemoteMcpConnectionReference connection,
        long revision,
        RemoteMcpConnectionDraft draft) =>
        new(
            connection,
            revision,
            draft.DisplayName,
            draft.EndpointUri,
            draft.ResourceUri,
            draft.AuthenticationMode,
            identityBindingReference: null,
            RemoteMcpTransportProfile.StreamableHttp20250618);

    private static async Task InsertConfigurationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string projectId,
        RemoteMcpConnectionConfiguration configuration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {Configurations}
                (tenant_id, project_id, connection_id, configuration_revision, display_name,
                 endpoint_uri, resource_uri, authentication_mode, identity_binding_reference,
                 transport_profile, registry_server_name, registry_exact_version,
                 registry_metadata_sha256, configuration_sha256, created_at)
            VALUES
                (@tenant_id, @project_id, @connection_id, @revision, @display_name,
                 @endpoint_uri, @resource_uri, @authentication_mode, @identity_binding_reference,
                 @transport_profile, @registry_server_name, @registry_exact_version,
                 @registry_metadata_sha256, @configuration_sha256, @created_at)
            """, connection, transaction);
        AddOwner(command, tenantId, projectId);
        command.Parameters.AddWithValue("connection_id", NpgsqlDbType.Uuid, configuration.Connection.ConnectionId);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, configuration.ConfigurationRevision);
        command.Parameters.AddWithValue("display_name", NpgsqlDbType.Text, configuration.DisplayName);
        command.Parameters.AddWithValue("endpoint_uri", NpgsqlDbType.Text, configuration.EndpointUri);
        AddNullable(command, "resource_uri", NpgsqlDbType.Text, configuration.ResourceUri);
        command.Parameters.AddWithValue("authentication_mode", NpgsqlDbType.Text, configuration.AuthenticationMode.ToString());
        AddNullable(command, "identity_binding_reference", NpgsqlDbType.Text, configuration.IdentityBindingReference);
        command.Parameters.AddWithValue("transport_profile", NpgsqlDbType.Text, configuration.TransportProfile.ToString());
        AddNullable(command, "registry_server_name", NpgsqlDbType.Text, configuration.RegistryServer?.ServerName);
        AddNullable(command, "registry_exact_version", NpgsqlDbType.Text, configuration.RegistryServer?.ExactVersion);
        AddNullable(command, "registry_metadata_sha256", NpgsqlDbType.Text, configuration.RegistryServer?.MetadataSha256);
        command.Parameters.AddWithValue("configuration_sha256", NpgsqlDbType.Text, configuration.ConfigurationSha256);
        command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertCatalogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string projectId,
        Guid connectionId,
        RemoteMcpDiscoveryCatalogPin catalog,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var policy = catalog.AppliedNetworkPolicy
            ?? throw new ArgumentException("A discovery catalog requires an owner-issued applied network-policy reference.");
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {CatalogSnapshots}
                (tenant_id, project_id, connection_id, configuration_revision, discovery_revision,
                 catalog_sha256, registry_metadata_sha256, tools_json, applied_policy_reference,
                 environment_tenant_id, environment_project_id, environment_run_id, environment_id,
                 environment_lifecycle_generation, applied_policy_generation, egress_intent_sha256, created_at)
            VALUES
                (@tenant_id, @project_id, @connection_id, @configuration_revision, @discovery_revision,
                 @catalog_sha256, @registry_metadata_sha256, @tools_json, @applied_policy_reference,
                 @environment_tenant_id, @environment_project_id, @environment_run_id, @environment_id,
                 @environment_lifecycle_generation, @applied_policy_generation, @egress_intent_sha256, @created_at)
            """, connection, transaction);
        AddOwner(command, tenantId, projectId);
        command.Parameters.AddWithValue("connection_id", NpgsqlDbType.Uuid, connectionId);
        command.Parameters.AddWithValue("configuration_revision", NpgsqlDbType.Bigint, catalog.ConfigurationRevision);
        command.Parameters.AddWithValue("discovery_revision", NpgsqlDbType.Bigint, catalog.DiscoveryRevision);
        command.Parameters.AddWithValue("catalog_sha256", NpgsqlDbType.Text, catalog.CatalogSha256);
        AddNullable(command, "registry_metadata_sha256", NpgsqlDbType.Text, catalog.RegistryMetadataSha256);
        command.Parameters.AddWithValue(
            "tools_json",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(catalog.Tools, JsonOptions));
        command.Parameters.AddWithValue("applied_policy_reference", NpgsqlDbType.Text, policy.Reference);
        command.Parameters.AddWithValue("environment_tenant_id", NpgsqlDbType.Text, policy.EnvironmentFence.Owner.TenantId);
        command.Parameters.AddWithValue("environment_project_id", NpgsqlDbType.Text, policy.EnvironmentFence.Owner.ProjectId);
        command.Parameters.AddWithValue("environment_run_id", NpgsqlDbType.Text, policy.EnvironmentFence.Owner.RunId);
        command.Parameters.AddWithValue("environment_id", NpgsqlDbType.Text, policy.EnvironmentFence.Owner.EnvironmentId);
        command.Parameters.AddWithValue("environment_lifecycle_generation", NpgsqlDbType.Bigint, policy.EnvironmentFence.LifecycleGeneration);
        command.Parameters.AddWithValue("applied_policy_generation", NpgsqlDbType.Bigint, policy.AppliedGeneration);
        command.Parameters.AddWithValue("egress_intent_sha256", NpgsqlDbType.Text, policy.EgressIntentSha256);
        command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteMcpConnectionSnapshot?> ReadSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenantId,
        string projectId,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        var head = await ReadHeadAsync(
            connection, transaction, tenantId, projectId, connectionId, cancellationToken).ConfigureAwait(false);
        if (head is null)
            return null;
        var configuration = await ReadConfigurationAsync(
            connection,
            transaction,
            tenantId,
            projectId,
            connectionId,
            head.CurrentConfigurationRevision,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A remote MCP connection head has no immutable configuration revision.");
        var catalog = head.CurrentDiscoveryRevision is { } discoveryRevision
            ? await ReadCatalogAsync(
                connection,
                transaction,
                tenantId,
                projectId,
                connectionId,
                head.CurrentConfigurationRevision,
                discoveryRevision,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A remote MCP head points to a missing discovery catalog.")
            : null;
        return new(head, configuration, catalog);
    }

    private static async Task<RemoteMcpConnectionHead?> ReadHeadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenantId,
        string projectId,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT row_revision, current_configuration_revision, current_discovery_revision, state
            FROM {Connections}
            WHERE tenant_id = @tenant_id AND project_id = @project_id AND connection_id = @connection_id
            """, connection, transaction);
        AddOwner(command, tenantId, projectId);
        command.Parameters.AddWithValue("connection_id", NpgsqlDbType.Uuid, connectionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var rowRevision = reader.GetInt64(0);
        var configurationRevision = reader.GetInt64(1);
        long? discoveryRevision = reader.IsDBNull(2) ? null : reader.GetInt64(2);
        var state = Enum.Parse<RemoteMcpConnectionState>(reader.GetString(3), ignoreCase: false);
        return new(new(projectId, connectionId), rowRevision, configurationRevision, state, discoveryRevision);
    }

    private static async Task<RemoteMcpConnectionConfiguration?> ReadConfigurationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenantId,
        string projectId,
        Guid connectionId,
        long revision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT display_name, endpoint_uri, resource_uri, authentication_mode,
                   identity_binding_reference, transport_profile, registry_server_name,
                   registry_exact_version, registry_metadata_sha256, configuration_sha256
            FROM {Configurations}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND connection_id = @connection_id AND configuration_revision = @revision
            """, connection, transaction);
        AddOwner(command, tenantId, projectId);
        command.Parameters.AddWithValue("connection_id", NpgsqlDbType.Uuid, connectionId);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var registryName = reader.IsDBNull(6) ? null : reader.GetString(6);
        var registryVersion = reader.IsDBNull(7) ? null : reader.GetString(7);
        var registryDigest = reader.IsDBNull(8) ? null : reader.GetString(8);
        var registry = registryName is null
            ? null
            : new RemoteMcpRegistryServerPin(registryName, registryVersion!, registryDigest!);
        var configuration = new RemoteMcpConnectionConfiguration(
            new(projectId, connectionId),
            revision,
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            Enum.Parse<RemoteMcpAuthenticationMode>(reader.GetString(3), ignoreCase: false),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            Enum.Parse<RemoteMcpTransportProfile>(reader.GetString(5), ignoreCase: false),
            registry);
        if (!string.Equals(configuration.ConfigurationSha256, reader.GetString(9), StringComparison.Ordinal))
            throw new InvalidOperationException("The stored remote MCP configuration digest does not match its revision.");
        return configuration;
    }

    private static async Task<RemoteMcpDiscoveryCatalogPin?> ReadCatalogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenantId,
        string projectId,
        Guid connectionId,
        long configurationRevision,
        long discoveryRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT catalog_sha256, registry_metadata_sha256, tools_json, applied_policy_reference,
                   environment_tenant_id, environment_project_id, environment_run_id, environment_id,
                   environment_lifecycle_generation, applied_policy_generation, egress_intent_sha256
            FROM {CatalogSnapshots}
            WHERE tenant_id = @tenant_id AND project_id = @project_id AND connection_id = @connection_id
              AND configuration_revision = @configuration_revision AND discovery_revision = @discovery_revision
            """, connection, transaction);
        AddOwner(command, tenantId, projectId);
        command.Parameters.AddWithValue("connection_id", NpgsqlDbType.Uuid, connectionId);
        command.Parameters.AddWithValue("configuration_revision", NpgsqlDbType.Bigint, configurationRevision);
        command.Parameters.AddWithValue("discovery_revision", NpgsqlDbType.Bigint, discoveryRevision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var tools = JsonSerializer.Deserialize<ImmutableArray<RemoteMcpToolSchemaPin>>(
            reader.GetString(2), JsonOptions);
        if (tools.IsDefault)
            throw new InvalidOperationException("The stored remote MCP catalog has an invalid tool list.");
        var environmentOwner = new EnvironmentOwnerIdentity(
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7));
        var appliedPolicy = new RemoteMcpAppliedNetworkPolicyReference(
            reader.GetString(3),
            new(environmentOwner, reader.GetInt64(8)),
            reader.GetInt64(9),
            reader.GetString(10));
        return new(
            new(projectId, connectionId),
            configurationRevision,
            discoveryRevision,
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            tools,
            appliedPolicy);
    }

    private static async Task UpdateHeadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string projectId,
        Guid connectionId,
        long expectedRowRevision,
        long configurationRevision,
        long? discoveryRevision,
        RemoteMcpConnectionState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            UPDATE {Connections}
            SET row_revision = row_revision + 1,
                current_configuration_revision = @configuration_revision,
                current_discovery_revision = @discovery_revision,
                state = @state,
                updated_at = @updated_at
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND connection_id = @connection_id AND row_revision = @expected_row_revision
            """, connection, transaction);
        AddOwner(command, tenantId, projectId);
        command.Parameters.AddWithValue("connection_id", NpgsqlDbType.Uuid, connectionId);
        command.Parameters.AddWithValue("configuration_revision", NpgsqlDbType.Bigint, configurationRevision);
        command.Parameters.AddWithValue("discovery_revision", NpgsqlDbType.Bigint, (object?)discoveryRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("state", NpgsqlDbType.Text, state.ToString());
        command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, now);
        command.Parameters.AddWithValue("expected_row_revision", NpgsqlDbType.Bigint, expectedRowRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw Conflict("connection_revision_stale", "The remote MCP connection changed before the operation committed.");
    }

    private static async Task<(string Fingerprint, string ResultJson)?> ReadIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string projectId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT request_fingerprint, result_json
            FROM {Idempotency}
            WHERE tenant_id = @tenant_id AND project_id = @project_id AND idempotency_key = @idempotency_key
            """, connection, transaction);
        AddOwner(command, tenantId, projectId);
        command.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static async Task<long> ReadLastDiscoveryRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string projectId,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT COALESCE(MAX(discovery_revision), 0)
            FROM {CatalogSnapshots}
            WHERE tenant_id = @tenant_id AND project_id = @project_id AND connection_id = @connection_id
            """, connection, transaction);
        AddOwner(command, tenantId, projectId);
        command.Parameters.AddWithValue("connection_id", NpgsqlDbType.Uuid, connectionId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Could not read the last remote MCP discovery revision."));
    }

    private static async Task InsertIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string projectId,
        string idempotencyKey,
        Guid connectionId,
        string operation,
        string fingerprint,
        string resultJson,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {Idempotency}
                (tenant_id, project_id, idempotency_key, connection_id, operation,
                 request_fingerprint, result_json, created_at)
            VALUES
                (@tenant_id, @project_id, @idempotency_key, @connection_id, @operation,
                 @request_fingerprint, @result_json, @created_at)
            """, connection, transaction);
        AddOwner(command, tenantId, projectId);
        command.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, idempotencyKey);
        command.Parameters.AddWithValue("connection_id", NpgsqlDbType.Uuid, connectionId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Text, operation);
        command.Parameters.AddWithValue("request_fingerprint", NpgsqlDbType.Text, fingerprint);
        command.Parameters.AddWithValue("result_json", NpgsqlDbType.Jsonb, resultJson);
        command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string SerializeSnapshot(RemoteMcpConnectionSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, JsonOptions);

    private static RemoteMcpConnectionSnapshot DeserializeSnapshot(string json) =>
        JsonSerializer.Deserialize<RemoteMcpConnectionSnapshot>(json, JsonOptions)
        ?? throw new InvalidOperationException("The persisted remote MCP idempotency result is invalid.");

    private static async Task AcquireLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))",
            connection,
            transaction);
        command.Parameters.AddWithValue("lock_key", NpgsqlDbType.Text, key);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddOwner(NpgsqlCommand command, string tenantId, string projectId)
    {
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Text, tenantId);
        command.Parameters.AddWithValue("project_id", NpgsqlDbType.Text, projectId);
    }

    private static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, string? value) =>
        command.Parameters.AddWithValue(name, type, (object?)value ?? DBNull.Value);

    private static void RequireExpectedRevision(RemoteMcpConnectionHead head, long expectedRowRevision)
    {
        if (head.RowRevision != expectedRowRevision)
            throw Conflict("connection_revision_stale", "The expected remote MCP connection revision is stale.");
    }

    private static void ValidateProjectId(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (projectId.Length > 256 || projectId.Any(char.IsControl))
            throw new ArgumentException("Project identifiers must be bounded and contain no control characters.", nameof(projectId));
    }

    private static void ValidateIdempotencyKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 128 || key.Any(char.IsControl))
            throw new ArgumentException("Idempotency keys must be bounded and contain no control characters.", nameof(key));
    }

    private static string Fingerprint(string operation, params string[] values) =>
        RemoteMcpDigest.Sha256(Encoding.UTF8.GetBytes(string.Join('\0', new[] { operation }.Concat(values))));

    private static RemoteMcpConnectionException NotFound() =>
        new("connection_not_found", "The remote MCP connection was not found.", (int)HttpStatusCode.NotFound);

    private static RemoteMcpConnectionException Forbidden(string code, string message) =>
        new(code, message, (int)HttpStatusCode.Forbidden);

    private static RemoteMcpConnectionException Conflict(string code, string message) =>
        new(code, message, (int)HttpStatusCode.Conflict);
}
