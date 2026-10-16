using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Resolves an immutable reviewed remote-tool snapshot by its accepted reference.
/// </summary>
internal interface IReviewedRemoteToolSnapshotResolver
{
    /// <summary>
    /// Reads a snapshot only when the persisted snapshot matches the complete reference.
    /// </summary>
    Task<ReviewedRemoteToolSnapshot?> ResolveAsync(
        ReviewedRemoteToolSnapshotReference reference,
        CancellationToken cancellationToken);
}

/// <summary>
/// Stores reviewed remote-tool snapshots as immutable JSON documents indexed by project and snapshot ID.
/// </summary>
internal sealed class ReviewedRemoteToolSnapshotStore : IReviewedRemoteToolSnapshotResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _snapshots;

    /// <summary>
    /// Creates the store using a validated non-reserved PostgreSQL schema.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the schema is invalid or reserved.</exception>
    public ReviewedRemoteToolSnapshotStore(NpgsqlDataSource dataSource, string schema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (schema is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                schema, "^[a-z][a-z0-9_]{0,62}\\z",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            schema is "public" or "pg_catalog" or "information_schema" ||
            schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));

        _dataSource = dataSource;
        _snapshots = $"\"{schema}\".reviewed_remote_tool_snapshots";
    }

    /// <summary>
    /// Persists a snapshot or verifies that an existing snapshot with the same key is identical.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when the project/snapshot key is already bound to different snapshot data.</exception>
    internal async Task<ReviewedRemoteToolSnapshotReference> PersistAsync(
        ReviewedRemoteToolSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var document = JsonSerializer.Serialize(SnapshotDocument.From(snapshot), JsonOptions);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_snapshots}
                (project_id, snapshot_id, snapshot_digest, agent_id, node_id, snapshot_json)
            VALUES
                (@project, @snapshot, @digest, @agent, @node, @document)
            ON CONFLICT (project_id, snapshot_id) DO NOTHING
            """, connection, transaction))
        {
            AddSnapshotParameters(insert, snapshot);
            insert.Parameters.AddWithValue("document", NpgsqlDbType.Jsonb, document);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var persisted = await ReadRowAsync(
            connection, transaction, snapshot.ProjectId, snapshot.SnapshotId, cancellationToken)
            .ConfigureAwait(false);
        if (persisted is null ||
            persisted.Snapshot.SnapshotDigest != snapshot.SnapshotDigest ||
            persisted.Snapshot.AgentId != snapshot.AgentId ||
            persisted.Snapshot.NodeId != snapshot.NodeId)
            throw new CoordinationException(
                "reviewed_remote_tool_snapshot_conflict", StatusCodes.Status409Conflict);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return persisted.Snapshot.Reference;
    }

    /// <summary>
    /// Resolves the immutable snapshot only when its ID, digest, agent, and node match the supplied reference.
    /// </summary>
    internal async Task<ReviewedRemoteToolSnapshot?> ResolveAsync(
        ReviewedRemoteToolSnapshotReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var persisted = await ReadRowAsync(
            connection, transaction: null, reference.ProjectId, reference.SnapshotId, cancellationToken)
            .ConfigureAwait(false);
        if (persisted is null ||
            persisted.Snapshot.SnapshotDigest != reference.SnapshotDigest ||
            persisted.Snapshot.AgentId != reference.AgentId ||
            persisted.Snapshot.NodeId != reference.NodeId)
            return null;
        return persisted.Snapshot;
    }

    /// <summary>
    /// Implements the snapshot resolver contract with the store's validated lookup.
    /// </summary>
    Task<ReviewedRemoteToolSnapshot?> IReviewedRemoteToolSnapshotResolver.ResolveAsync(
        ReviewedRemoteToolSnapshotReference reference,
        CancellationToken cancellationToken) =>
        ResolveAsync(reference, cancellationToken);

    /// <summary>
    /// Reads and verifies the stored JSON snapshot against its indexed identity and digest fields.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the stored snapshot or its index is inconsistent.</exception>
    private async Task<PersistedSnapshot?> ReadRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string projectId,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT snapshot_json::text, snapshot_digest, agent_id, node_id
            FROM {_snapshots}
            WHERE project_id = @project AND snapshot_id = @snapshot
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("snapshot", NpgsqlDbType.Uuid, snapshotId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var document = JsonSerializer.Deserialize<SnapshotDocument>(reader.GetString(0), JsonOptions)
            ?? throw new InvalidOperationException("A stored reviewed remote tool snapshot is invalid.");
        var snapshot = document.ToSnapshot();
        if (snapshot.ProjectId != projectId ||
            snapshot.SnapshotId != snapshotId ||
            snapshot.SnapshotDigest != reader.GetString(1) ||
            snapshot.AgentId != reader.GetString(2) ||
            snapshot.NodeId != reader.GetString(3))
            throw new InvalidOperationException("A stored reviewed remote tool snapshot does not match its index.");

        return new PersistedSnapshot(snapshot);
    }

    /// <summary>
    /// Adds the indexed identity parameters used when inserting a snapshot.
    /// </summary>
    private static void AddSnapshotParameters(NpgsqlCommand command, ReviewedRemoteToolSnapshot snapshot)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, snapshot.ProjectId);
        command.Parameters.AddWithValue("snapshot", NpgsqlDbType.Uuid, snapshot.SnapshotId);
        command.Parameters.AddWithValue("digest", NpgsqlDbType.Char, snapshot.SnapshotDigest);
        command.Parameters.AddWithValue("agent", NpgsqlDbType.Varchar, snapshot.AgentId);
        command.Parameters.AddWithValue("node", NpgsqlDbType.Varchar, snapshot.NodeId);
    }

    /// <summary>
    /// Holds a snapshot document after it has been checked against its database index.
    /// </summary>
    private sealed record PersistedSnapshot(ReviewedRemoteToolSnapshot Snapshot);

    /// <summary>
    /// Serialization shape for all immutable fields that contribute to a reviewed snapshot.
    /// </summary>
    private sealed record SnapshotDocument(
        Guid SnapshotId,
        string ProjectId,
        string AgentId,
        string NodeId,
        string ConnectionId,
        long ConnectionRowRevision,
        long ConnectionConfigurationRevision,
        long? ConnectionDiscoveryRevision,
        string ConnectionState,
        string ConfigurationSha256,
        string EndpointUri,
        string? ResourceUri,
        string AuthenticationMode,
        string? IdentityBindingReference,
        string TransportProfile,
        ReviewedRemoteToolRegistryServerPin? RegistryServerPin,
        string CatalogRevision,
        string CatalogDigest,
        string ToolId,
        string ToolRevision,
        string ToolDigest,
        string ToolSchemaRevision,
        string ToolSchemaDigest,
        string ToolMetadataJson,
        string ToolSchemaJson,
        string PermissionActionId,
        string PermissionPurpose,
        string PermissionMetadataJson)
    {
        /// <summary>
        /// Copies the canonical snapshot fields into their persistence representation.
        /// </summary>
        public static SnapshotDocument From(ReviewedRemoteToolSnapshot snapshot) =>
            new(
                snapshot.SnapshotId,
                snapshot.ProjectId,
                snapshot.AgentId,
                snapshot.NodeId,
                snapshot.ConnectionId,
                snapshot.ConnectionRowRevision,
                snapshot.ConnectionConfigurationRevision,
                snapshot.ConnectionDiscoveryRevision,
                snapshot.ConnectionState,
                snapshot.ConfigurationSha256,
                snapshot.EndpointUri,
                snapshot.ResourceUri,
                snapshot.AuthenticationMode,
                snapshot.IdentityBindingReference,
                snapshot.TransportProfile,
                snapshot.RegistryServerPin,
                snapshot.CatalogRevision,
                snapshot.CatalogDigest,
                snapshot.ToolId,
                snapshot.ToolRevision,
                snapshot.ToolDigest,
                snapshot.ToolSchemaRevision,
                snapshot.ToolSchemaDigest,
                snapshot.CanonicalToolMetadataJson,
                snapshot.CanonicalToolSchemaJson,
                snapshot.Permission.ActionId,
                snapshot.Permission.Purpose,
                snapshot.Permission.CanonicalMetadataJson);

        /// <summary>
        /// Reconstructs the domain snapshot from its persisted fields.
        /// </summary>
        public ReviewedRemoteToolSnapshot ToSnapshot() =>
            new(
                SnapshotId,
                ProjectId,
                AgentId,
                NodeId,
                ConnectionId,
                ConnectionRowRevision,
                ConnectionConfigurationRevision,
                ConnectionDiscoveryRevision,
                ConnectionState,
                ConfigurationSha256,
                EndpointUri,
                ResourceUri,
                AuthenticationMode,
                IdentityBindingReference,
                TransportProfile,
                RegistryServerPin,
                CatalogRevision,
                CatalogDigest,
                ToolId,
                ToolRevision,
                ToolDigest,
                ToolSchemaRevision,
                ToolSchemaDigest,
                ToolMetadataJson,
                ToolSchemaJson,
                new ReviewedRemoteToolPermissionMetadata(
                    PermissionActionId, PermissionPurpose, PermissionMetadataJson));
    }
}
