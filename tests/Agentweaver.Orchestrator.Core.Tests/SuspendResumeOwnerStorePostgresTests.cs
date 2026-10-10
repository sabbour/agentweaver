using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class SuspendResumeOwnerStorePostgresTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly CoordinationPostgresFixture _fixture;
    private readonly string _schema = "suspend_resume_" + Guid.NewGuid().ToString("N");
    private readonly CoordinationActor _actor = new(
        "https://identity.example/", Guid.NewGuid().ToString("D"));
    private AuthorizedRunSelection _selection = null!;
    private SessionIdentity _identity;
    private CoordinationOwnerStore _store = null!;

    public SuspendResumeOwnerStorePostgresTests(CoordinationPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _selection = CreateSelection(_actor);
        _identity = new SessionIdentity(
            _selection.Selection.ProjectId, _selection.Selection.RunId, "root");

        await CoordinationOwnerMigrator.MigrateAsync(_fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(_fixture.DataSource, _schema);
        await ApplySuspendResumeMigrationAsync();

        _store = new CoordinationOwnerStore(_fixture.DataSource, _schema);
        _ = await _store.AcceptRootAsync(
            _actor, _selection, _identity.SessionId, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task UnavailableOperationPersistsAnInterruptedManifestAndReplaysAfterRestart()
    {
        var first = await _store.RecordUnavailableSessionSuspendResumeAsync(
            _actor,
            _selection,
            _identity,
            SessionSuspendResumeOperationKind.Suspend,
            "suspend-once",
            sourceManifestId: null,
            CancellationToken.None);

        Assert.Equal(SessionSuspendResumeOperationPhase.Interrupted, first.Phase);
        Assert.Equal(1, first.PhaseVersion);
        Assert.True(first.OwnerExecutionFence > 0);
        Assert.Equal(SessionSuspendResumeManifestState.Interrupted, first.Manifest.State);
        Assert.Null(first.Manifest.CoreExecutionFence);
        Assert.Contains("agenthost_drain_receipt_unavailable", first.Manifest.MissingEvidence);
        Assert.Contains("maf_checkpoint_snapshot_unavailable", first.Manifest.MissingEvidence);
        Assert.False(first.IsDuplicate);

        var restartedStore = new CoordinationOwnerStore(_fixture.DataSource, _schema);
        var replay = await restartedStore.RecordUnavailableSessionSuspendResumeAsync(
            _actor,
            _selection,
            _identity,
            SessionSuspendResumeOperationKind.Suspend,
            "suspend-once",
            sourceManifestId: null,
            CancellationToken.None);

        Assert.True(replay.IsDuplicate);
        Assert.Equal(first.OperationId, replay.OperationId);
        Assert.Equal(first.ManifestId, replay.ManifestId);
        Assert.Equal(first.ManifestHash, replay.ManifestHash);
        Assert.Equal(first.OwnerExecutionFence, replay.OwnerExecutionFence);
        Assert.Equal(first.Manifest.ManifestId, replay.Manifest.ManifestId);
        Assert.Equal(first.Manifest.MissingEvidence, replay.Manifest.MissingEvidence);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT operation_kind, operation_phase, phase_version, owner_execution_fence,
                manifest_hash, manifest::text, claimed_execution_fence
            FROM "{_schema}".coordination_execution_operations o
            JOIN "{_schema}".session_consistency_manifests m
              ON m.project_id = o.project_id AND m.run_id = o.run_id
             AND m.session_id = o.session_id AND m.operation_id = o.operation_id
            WHERE o.project_id = @project AND o.run_id = @run AND o.operation_id = @operation
            """, connection);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, _identity.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, _identity.RunId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, first.OperationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("suspend", reader.GetString(0));
        Assert.Equal("interrupted", reader.GetString(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.Equal(first.OwnerExecutionFence, reader.GetInt64(3));
        Assert.Equal(first.ManifestHash, reader.GetString(4).TrimEnd());
        Assert.True(reader.IsDBNull(6));
        var canonicalManifest = JsonSerializer.Deserialize<SessionSuspendResumeManifest>(
            reader.GetString(5), JsonOptions);
        Assert.NotNull(canonicalManifest);
        var canonicalHash = Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(canonicalManifest, JsonOptions)));
        Assert.Equal(first.ManifestHash, canonicalHash);
    }

    [Fact]
    public async Task IdempotencyKeyCannotBeReusedForAnotherOperation()
    {
        _ = await _store.RecordUnavailableSessionSuspendResumeAsync(
            _actor,
            _selection,
            _identity,
            SessionSuspendResumeOperationKind.Suspend,
            "shared-key",
            sourceManifestId: null,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.RecordUnavailableSessionSuspendResumeAsync(
                _actor,
                _selection,
                _identity,
                SessionSuspendResumeOperationKind.Resume,
                "shared-key",
                Guid.NewGuid(),
                CancellationToken.None));

        Assert.Equal("execution_operation_idempotency_conflict", exception.Code);
        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
    }

    [Fact]
    public async Task ConcurrentRetriesCreateOneOperationAndOneImmutableManifest()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            _store.RecordUnavailableSessionSuspendResumeAsync(
                _actor,
                _selection,
                _identity,
                SessionSuspendResumeOperationKind.Suspend,
                "concurrent-suspend",
                sourceManifestId: null,
                CancellationToken.None)));

        Assert.Single(results.Select(result => result.OperationId).Distinct());
        Assert.Single(results.Select(result => result.ManifestId).Distinct());
        Assert.Equal(1, results.Count(result => !result.IsDuplicate));
        Assert.Equal(3, results.Count(result => result.IsDuplicate));
    }

    [Fact]
    public async Task OperationAndManifestRejectInvalidMutation()
    {
        var result = await _store.RecordUnavailableSessionSuspendResumeAsync(
            _actor,
            _selection,
            _identity,
            SessionSuspendResumeOperationKind.Suspend,
            "immutable-suspend",
            sourceManifestId: null,
            CancellationToken.None);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var operationMutation = new NpgsqlCommand($"""
            UPDATE "{_schema}".coordination_execution_operations
            SET operation_phase = 'dispatched', phase_version = phase_version + 1
            WHERE project_id = @project AND operation_id = @operation
            """, connection);
        operationMutation.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, _identity.ProjectId);
        operationMutation.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, result.OperationId);
        await Assert.ThrowsAsync<PostgresException>(
            () => operationMutation.ExecuteNonQueryAsync());

        await using var manifestMutation = new NpgsqlCommand($"""
            UPDATE "{_schema}".session_consistency_manifests
            SET manifest_state = 'suspended'
            WHERE project_id = @project AND manifest_id = @manifest
            """, connection);
        manifestMutation.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, _identity.ProjectId);
        manifestMutation.Parameters.AddWithValue("manifest", NpgsqlDbType.Uuid, result.ManifestId);
        await Assert.ThrowsAsync<PostgresException>(
            () => manifestMutation.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task UnavailableOperationRequiresCurrentOwnerAndSelection()
    {
        var otherActor = _actor with { Subject = Guid.NewGuid().ToString("D") };
        var denied = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.RecordUnavailableSessionSuspendResumeAsync(
                otherActor,
                _selection,
                _identity,
                SessionSuspendResumeOperationKind.Suspend,
                "foreign-owner",
                sourceManifestId: null,
                CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, denied.StatusCode);

        using var staleSnapshot = JsonDocument.Parse("""{"source":"stale-projects-config"}""");
        var staleSelection = _selection with
        {
            Selection = _selection.Selection with { Snapshot = staleSnapshot.RootElement.Clone() }
        };
        var stale = await Assert.ThrowsAsync<CoordinationException>(() =>
            _store.RecordUnavailableSessionSuspendResumeAsync(
                _actor,
                staleSelection,
                _identity,
                SessionSuspendResumeOperationKind.Suspend,
                "stale-selection",
                sourceManifestId: null,
                CancellationToken.None));
        Assert.Equal("accepted_run_selection_stale", stale.Code);
        Assert.Equal(StatusCodes.Status409Conflict, stale.StatusCode);
    }

    private async Task ApplySuspendResumeMigrationAsync()
    {
        await using var resource = typeof(CoordinationOwnerMigrator).Assembly.GetManifestResourceStream(
            "Agentweaver.Orchestrator.Migrations.017_session_suspend_resume.sql")
            ?? throw new InvalidOperationException("The suspend/resume migration resource is missing.");
        using var text = new StreamReader(resource);
        var sql = (await text.ReadToEndAsync()).Replace(
            "{schema}", $"\"{_schema}\"", StringComparison.Ordinal);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static AuthorizedRunSelection CreateSelection(CoordinationActor actor)
    {
        using var snapshot = JsonDocument.Parse("""{"source":"projects-config"}""");
        var selection = new EffectiveRunSelection(
            "project-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("D"),
            1,
            1,
            1,
            "context-v1",
            snapshot.RootElement.Clone());
        return new AuthorizedRunSelection(
            selection,
            new ProjectsAuthorizationContext(
                1,
                actor.Issuer,
                actor.Subject,
                "tenant-1",
                1,
                selection.ProjectId,
                selection.RunId,
                ImmutableArray.Create(new ProjectsAuthority(
                    "project",
                    selection.ProjectId,
                    ImmutableArray.Create(new ProjectsPermissionGrant("acceptRunSelection", 1))))));
    }
}
