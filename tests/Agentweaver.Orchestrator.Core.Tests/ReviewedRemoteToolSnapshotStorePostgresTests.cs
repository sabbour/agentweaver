using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class ReviewedRemoteToolSnapshotStorePostgresTests : IAsyncLifetime
{
    private readonly CoordinationPostgresFixture _fixture;
    private readonly string _schema = "reviewed_remote_tools_" + Guid.NewGuid().ToString("N");
    private ReviewedRemoteToolSnapshotStore _store = null!;

    public ReviewedRemoteToolSnapshotStorePostgresTests(CoordinationPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using (var createSchema = new NpgsqlCommand($"CREATE SCHEMA \"{_schema}\"", connection))
            await createSchema.ExecuteNonQueryAsync();
        await using var resource = typeof(CoordinationOwnerMigrator).Assembly.GetManifestResourceStream(
            "Agentweaver.Orchestrator.Migrations.018_reviewed_remote_tool_snapshots.sql")
            ?? throw new InvalidOperationException("The reviewed remote tool snapshot migration resource is missing.");
        using var text = new StreamReader(resource);
        var sql = (await text.ReadToEndAsync()).Replace(
            "{schema}", $"\"{_schema}\"", StringComparison.Ordinal);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
        _store = new ReviewedRemoteToolSnapshotStore(_fixture.DataSource, _schema);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task PersistResolvesEverySnapshotFieldAndReplaysOnlyTheExactSnapshot()
    {
        var snapshot = Snapshot();

        var reference = await _store.PersistAsync(snapshot, CancellationToken.None);
        var replay = await _store.PersistAsync(snapshot, CancellationToken.None);
        var resolved = await _store.ResolveAsync(reference, CancellationToken.None);

        AssertReferenceEqual(snapshot.Reference, reference);
        AssertReferenceEqual(reference, replay);
        Assert.NotNull(resolved);
        AssertSnapshotEqual(snapshot, resolved);

        var changedSnapshot = Snapshot(endpointUri: "https://changed.example.test/api");
        var conflict = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.PersistAsync(changedSnapshot, CancellationToken.None));
        Assert.Equal("reviewed_remote_tool_snapshot_conflict", conflict.Code);

        Assert.Null(await _store.ResolveAsync(
            new ReviewedRemoteToolSnapshotReference(
                reference.ProjectId, reference.SnapshotId, reference.SnapshotDigest,
                "other-agent", reference.NodeId),
            CancellationToken.None));
        Assert.Null(await _store.ResolveAsync(
            new ReviewedRemoteToolSnapshotReference(
                reference.ProjectId, reference.SnapshotId, reference.SnapshotDigest,
                reference.AgentId, "other-node"),
            CancellationToken.None));
        Assert.Null(await _store.ResolveAsync(
            new ReviewedRemoteToolSnapshotReference(
                reference.ProjectId, reference.SnapshotId, new string('f', 64),
                reference.AgentId, reference.NodeId),
            CancellationToken.None));
    }

    [Fact]
    public async Task ResolveRejectsTamperedSnapshotDocument()
    {
        var reference = await _store.PersistAsync(Snapshot(), CancellationToken.None);
        var tamperedSnapshotId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".reviewed_remote_tool_snapshots
                (project_id, snapshot_id, snapshot_digest, agent_id, node_id, snapshot_json)
            SELECT project_id,
                   '44444444-4444-4444-4444-444444444444'::uuid,
                   snapshot_digest,
                   agent_id,
                   node_id,
                   jsonb_set(
                       jsonb_set(
                           snapshot_json,
                           ARRAY['snapshotId'],
                           to_jsonb('44444444-4444-4444-4444-444444444444'::text)),
                       ARRAY['endpointUri'],
                       to_jsonb('https://tampered.example.test/api'::text))
            FROM "{_schema}".reviewed_remote_tool_snapshots
            WHERE project_id = @project AND snapshot_id = @snapshot
            """, connection))
        {
            command.Parameters.AddWithValue("project", reference.ProjectId);
            command.Parameters.AddWithValue("snapshot", reference.SnapshotId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var tamperedReference = new ReviewedRemoteToolSnapshotReference(
            reference.ProjectId,
            tamperedSnapshotId,
            reference.SnapshotDigest,
            reference.AgentId,
            reference.NodeId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.ResolveAsync(tamperedReference, CancellationToken.None));
    }

    [Fact]
    public async Task MigrationRejectsNullIdentityAndImmutableRowMutation()
    {
        var reference = await _store.PersistAsync(Snapshot(), CancellationToken.None);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        foreach (var identityProperty in new[] { "snapshotId", "projectId", "agentId", "nodeId" })
        {
            await using var invalidInsert = new NpgsqlCommand($"""
                INSERT INTO "{_schema}".reviewed_remote_tool_snapshots
                    (project_id, snapshot_id, snapshot_digest, agent_id, node_id, snapshot_json)
                SELECT project_id, @invalid_snapshot, snapshot_digest, agent_id, node_id,
                       jsonb_set(
                           snapshot_json,
                           ARRAY[CAST(@identity_property AS text)],
                           'null'::jsonb)
                FROM "{_schema}".reviewed_remote_tool_snapshots
                WHERE project_id = @project AND snapshot_id = @snapshot
                """, connection);
            invalidInsert.Parameters.AddWithValue("invalid_snapshot", Guid.NewGuid());
            invalidInsert.Parameters.AddWithValue("identity_property", identityProperty);
            invalidInsert.Parameters.AddWithValue("project", reference.ProjectId);
            invalidInsert.Parameters.AddWithValue("snapshot", reference.SnapshotId);
            var invalidIdentity = await Assert.ThrowsAsync<PostgresException>(
                () => invalidInsert.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, invalidIdentity.SqlState);
        }

        await using (var update = new NpgsqlCommand($"""
            UPDATE "{_schema}".reviewed_remote_tool_snapshots
            SET snapshot_json = snapshot_json
            WHERE project_id = @project AND snapshot_id = @snapshot
            """, connection))
        {
            update.Parameters.AddWithValue("project", reference.ProjectId);
            update.Parameters.AddWithValue("snapshot", reference.SnapshotId);
            var error = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
            Assert.Contains("immutable", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        await using (var delete = new NpgsqlCommand($"""
            DELETE FROM "{_schema}".reviewed_remote_tool_snapshots
            WHERE project_id = @project AND snapshot_id = @snapshot
            """, connection))
        {
            delete.Parameters.AddWithValue("project", reference.ProjectId);
            delete.Parameters.AddWithValue("snapshot", reference.SnapshotId);
            var error = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
            Assert.Contains("immutable", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ReviewedRemoteToolSnapshot Snapshot(
        string endpointUri = "https://mcp.example.test/api") =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "project-1",
            "agent-1",
            "node-1",
            "connection-1",
            6,
            4,
            8,
            "Enabled",
            new string('a', 64),
            endpointUri,
            "https://mcp.example.test/resource",
            "DelegatedOAuth",
            "identity-binding-1",
            "StreamableHttp20250618",
            new ReviewedRemoteToolRegistryServerPin(
                "registry.example.test", "1.2.3", new string('2', 64)),
            "catalog-r8",
            new string('b', 64),
            "remote.lookup",
            "tool-r2",
            new string('c', 64),
            "schema-r3",
            new string('d', 64),
            """{"description":"Lookup","name":"remote.lookup"}""",
            """{"properties":{"query":{"type":"string"}},"type":"object"}""",
            new ReviewedRemoteToolPermissionMetadata(
                "tool.read", "runtime.execution", """{"source":"explicit-review","version":1}"""));

    private static void AssertReferenceEqual(
        ReviewedRemoteToolSnapshotReference expected,
        ReviewedRemoteToolSnapshotReference actual)
    {
        Assert.Equal(expected.ProjectId, actual.ProjectId);
        Assert.Equal(expected.SnapshotId, actual.SnapshotId);
        Assert.Equal(expected.SnapshotDigest, actual.SnapshotDigest);
        Assert.Equal(expected.AgentId, actual.AgentId);
        Assert.Equal(expected.NodeId, actual.NodeId);
    }

    private static void AssertSnapshotEqual(
        ReviewedRemoteToolSnapshot expected,
        ReviewedRemoteToolSnapshot actual)
    {
        Assert.Equal(expected.SnapshotId, actual.SnapshotId);
        Assert.Equal(expected.ProjectId, actual.ProjectId);
        Assert.Equal(expected.AgentId, actual.AgentId);
        Assert.Equal(expected.NodeId, actual.NodeId);
        Assert.Equal(expected.ConnectionId, actual.ConnectionId);
        Assert.Equal(expected.ConnectionRowRevision, actual.ConnectionRowRevision);
        Assert.Equal(expected.ConnectionConfigurationRevision, actual.ConnectionConfigurationRevision);
        Assert.Equal(expected.ConnectionDiscoveryRevision, actual.ConnectionDiscoveryRevision);
        Assert.Equal(expected.ConnectionState, actual.ConnectionState);
        Assert.Equal(expected.ConfigurationSha256, actual.ConfigurationSha256);
        Assert.Equal(expected.EndpointUri, actual.EndpointUri);
        Assert.Equal(expected.ResourceUri, actual.ResourceUri);
        Assert.Equal(expected.AuthenticationMode, actual.AuthenticationMode);
        Assert.Equal(expected.IdentityBindingReference, actual.IdentityBindingReference);
        Assert.Equal(expected.TransportProfile, actual.TransportProfile);
        Assert.Equal(expected.RegistryServerPin?.ServerName, actual.RegistryServerPin?.ServerName);
        Assert.Equal(expected.RegistryServerPin?.ExactVersion, actual.RegistryServerPin?.ExactVersion);
        Assert.Equal(expected.RegistryServerPin?.MetadataSha256, actual.RegistryServerPin?.MetadataSha256);
        Assert.Equal(expected.CatalogRevision, actual.CatalogRevision);
        Assert.Equal(expected.CatalogDigest, actual.CatalogDigest);
        Assert.Equal(expected.ToolId, actual.ToolId);
        Assert.Equal(expected.ToolRevision, actual.ToolRevision);
        Assert.Equal(expected.ToolDigest, actual.ToolDigest);
        Assert.Equal(expected.ToolSchemaRevision, actual.ToolSchemaRevision);
        Assert.Equal(expected.ToolSchemaDigest, actual.ToolSchemaDigest);
        Assert.Equal(expected.CanonicalToolMetadataJson, actual.CanonicalToolMetadataJson);
        Assert.Equal(expected.CanonicalToolSchemaJson, actual.CanonicalToolSchemaJson);
        Assert.Equal(expected.Permission.ActionId, actual.Permission.ActionId);
        Assert.Equal(expected.Permission.Purpose, actual.Permission.Purpose);
        Assert.Equal(expected.Permission.CanonicalMetadataJson, actual.Permission.CanonicalMetadataJson);
        Assert.Equal(expected.Permission.Digest, actual.Permission.Digest);
        Assert.Equal(expected.SnapshotDigest, actual.SnapshotDigest);
        AssertReferenceEqual(expected.Reference, actual.Reference);
    }
}
