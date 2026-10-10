using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class BacklogOwnerStorePostgresTests : IAsyncLifetime
{
    private readonly CoordinationPostgresFixture _fixture;
    private readonly string _schema = "backlog_" + Guid.NewGuid().ToString("N");
    private readonly CoordinationActor _actor = new(
        "https://identity.example/", Guid.NewGuid().ToString("D"));
    private BacklogOwnerStore _backlog = null!;
    private CoordinationOwnerStore _coordination = null!;
    private CoordinatorDecisionOwnerStore _decisions = null!;
    private NoPrerequisitesEvidenceReader _evidence = null!;

    public BacklogOwnerStorePostgresTests(CoordinationPostgresFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(_fixture.DataSource, _schema);
        await ApplyBacklogSchemaAsync();

        var catalog = ProviderCatalog.Create([], [], []).Value!;
        var resolver = new ProviderResolver(catalog);
        var contexts = new CoordinatorRunSelectionContextStore(
            _fixture.DataSource, _schema, catalog, resolver, []);
        _decisions = new CoordinatorDecisionOwnerStore(
            _fixture.DataSource, _schema, contexts, TimeProvider.System);
        _coordination = new CoordinationOwnerStore(
            _fixture.DataSource, _schema, TimeProvider.System, _decisions);
        _backlog = new BacklogOwnerStore(_fixture.DataSource, _schema);
        _evidence = new NoPrerequisitesEvidenceReader();
    }

    public async Task DisposeAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task CappedClaimRevalidationFailureRollsBackTheSameRootAdmissionAndTask()
    {
        var task = TaskRef("capped-feature");
        var graph = await AddTaskAsync(task);
        var selection = Selection("run-capped") with
        {
            Selection = Selection("run-capped").Selection with
            {
                Snapshot = CoordinatorRunAdmissionTests.Selection(task.ProjectId, "run-capped")
            }
        };
        var admission = CoordinatorRunAdmissionTests.Receipt(selection);
        var checks = 0;
        var prices = 0;
        await Assert.ThrowsAsync<CoordinationException>(() => _backlog.ClaimTaskAsync(
            _actor, selection, task, graph.Revision, 0, "capped-claim", _evidence,
            _coordination, _decisions,
            _ => ++checks == 3
                ? Task.FromException(new CoordinationException("run_selection_permission_denied", 403))
                : Task.CompletedTask,
            CancellationToken.None,
            _ => { prices++; return Task.FromResult(admission); }));
        Assert.Equal(1, prices);
        Assert.Equal(3, checks);
        Assert.Equal(0, await ReadCountAsync("accepted_runs", "project_id = 'backlog-project'"));
        Assert.Equal(0, await ReadCountAsync("coordination_sessions", "project_id = 'backlog-project'"));
        Assert.Equal(0, await ReadCountAsync("coordinator_decisions", "project_id = 'backlog-project'"));
        Assert.Equal(0, await ReadCountAsync("backlog_tasks", "claim_id IS NOT NULL"));
        Assert.Equal(0, await ReadCountAsync("outbox_events",
            "event_type = 'orchestrator.run.copilot_admission'"));
    }

    [Fact]
    public async Task ConcurrentClaimsCreateOneTaskClaimRootAndInitialDecision()
    {
        var task = TaskRef("feature");
        var graph = await AddTaskAsync(task);
        var firstSelection = Selection("run-first");
        var secondSelection = Selection("run-second");

        var claims = await Task.WhenAll(
            _backlog.ClaimTaskAsync(
                _actor, firstSelection, task, graph.Revision, 0, "claim-first",
                _evidence, _coordination, _decisions, _ => Task.CompletedTask, CancellationToken.None),
            _backlog.ClaimTaskAsync(
                _actor, secondSelection, task, graph.Revision, 0, "claim-second",
                _evidence, _coordination, _decisions, _ => Task.CompletedTask, CancellationToken.None));

        Assert.Single(claims, claim => claim.IsSuccess);
        Assert.Single(claims, claim => !claim.IsSuccess &&
            claim.Issues.Any(issue => issue.Code == BacklogIssueCode.StaleGraphRevision));
        Assert.Equal(1, await ReadCountAsync("backlog_tasks", "claim_id IS NOT NULL"));
        Assert.Equal(1, await ReadCountAsync("accepted_runs", "project_id = 'backlog-project'"));
        Assert.Equal(1, await ReadCountAsync("coordination_sessions",
            "project_id = 'backlog-project' AND node_kind = 'coordinator'"));
        Assert.Equal(1, await ReadCountAsync("coordinator_decisions",
            "project_id = 'backlog-project' AND action_kind = 'run.initialize'"));
        Assert.Equal(1, await ReadCountAsync("coordinator_decision_outbox",
            "project_id = 'backlog-project' AND event_kind = 'decision.accepted'"));
    }

    [Fact]
    public async Task LateAuthorityLossRollsBackNewClaimRootDecisionAndOutbox()
    {
        var task = TaskRef("feature");
        var graph = await AddTaskAsync(task);
        var selection = Selection("run-late-authority");
        var authorityChecks = 0;

        await Assert.ThrowsAsync<CoordinationException>(() =>
            ClaimAsync(
                task,
                graph.Revision,
                selection,
                "claim-late-authority",
                _ => ++authorityChecks == 2
                    ? Task.FromException(new CoordinationException("run_selection_permission_denied", 403))
                    : Task.CompletedTask));

        Assert.Equal(2, authorityChecks);
        Assert.Equal(0, await ReadCountAsync("accepted_runs", "project_id = 'backlog-project'"));
        Assert.Equal(0, await ReadCountAsync("coordination_sessions",
            "project_id = 'backlog-project' AND node_kind = 'coordinator'"));
        Assert.Equal(0, await ReadCountAsync("coordinator_decisions",
            "project_id = 'backlog-project'"));
        Assert.Equal(0, await ReadCountAsync("coordinator_decision_outbox",
            "project_id = 'backlog-project'"));
        Assert.Equal(0, await ReadCountAsync("backlog_tasks", "claim_id IS NOT NULL"));
        var persistedGraph = await _backlog.ReadGraphAsync(task.ProjectId, CancellationToken.None);
        Assert.Equal(graph.Revision, persistedGraph.Value!.Revision);
        Assert.Equal(BacklogTaskState.Backlog, persistedGraph.Value.Tasks.Single().State);
    }

    [Fact]
    public async Task CurrentDecisionUsesTrustedOriginalHashForJsonbSelectionAndRejectsStaleHash()
    {
        var task = TaskRef("jsonb-selection-hash");
        var graph = await AddTaskAsync(task);
        var selection = Selection("run-jsonb-selection-hash");
        var claim = await ClaimAsync(task, graph.Revision, selection, "claim-jsonb-selection-hash");
        Assert.True(claim.IsSuccess);
        var receipt = claim.Value!;

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var binding = await _coordination.ReadPrerequisiteRootBindingInTransactionAsync(
            connection,
            transaction,
            task.ProjectId,
            receipt.RunId,
            receipt.RootSessionId,
            receipt.SelectionHash,
            receipt.ExecutionFence,
            CancellationToken.None);
        Assert.NotEqual(
            binding.AcceptedSelectionHash,
            CoordinationOwnerStore.HashSelection(binding.Selection));

        var context = CoordinatorWorkflowCatalog.CreateRunSelectionContext(binding.Selection.Snapshot);
        var current = await _decisions.ReadBacklogCurrentDecisionInTransactionAsync(
            connection,
            transaction,
            binding.Actor,
            binding.TenantId,
            new SessionIdentity(task.ProjectId, receipt.RunId, receipt.RootSessionId),
            binding.Selection,
            binding.AcceptedSelectionHash,
            binding.Root.ExecutionFence,
            binding.DecisionStateVersion,
            context,
            CancellationToken.None);
        Assert.Equal(binding.AcceptedSelectionHash, current.SelectionHash);

        var stale = await Assert.ThrowsAsync<CoordinationException>(() =>
            _decisions.ReadBacklogCurrentDecisionInTransactionAsync(
                connection,
                transaction,
                binding.Actor,
                binding.TenantId,
                new SessionIdentity(task.ProjectId, receipt.RunId, receipt.RootSessionId),
                binding.Selection,
                new string('0', 64),
                binding.Root.ExecutionFence,
                binding.DecisionStateVersion,
                context,
                CancellationToken.None));
        Assert.Equal("backlog_prerequisite_binding_stale", stale.Code);
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task IdempotentClaimReplayAfterStoreRestartReturnsTheSameIntent()
    {
        var task = TaskRef("feature");
        var graph = await AddTaskAsync(task);
        var selection = Selection("run-replay");
        var first = await ClaimAsync(task, graph.Revision, selection, "claim-once");
        Assert.True(first.IsSuccess);
        var restartedBacklog = new BacklogOwnerStore(_fixture.DataSource, _schema);
        var restartedCoordination = new CoordinationOwnerStore(
            _fixture.DataSource, _schema, TimeProvider.System, _decisions);
        var authorityChecks = 0;

        var replay = await restartedBacklog.ClaimTaskAsync(
            _actor,
            selection,
            task,
            graph.Revision,
            0,
            "claim-once",
            _evidence,
            restartedCoordination,
            _decisions,
            _ =>
            {
                authorityChecks++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(2, authorityChecks);
        Assert.Equal(1, _evidence.ReadCount);
        Assert.Equal(1, await ReadCountAsync("accepted_runs", "project_id = 'backlog-project'"));
        Assert.Equal(1, await ReadCountAsync("coordination_sessions",
            "project_id = 'backlog-project' AND node_kind = 'coordinator'"));
        Assert.Equal(1, await ReadCountAsync("coordinator_decisions",
            "project_id = 'backlog-project' AND action_kind = 'run.initialize'"));
    }

    [Fact]
    public async Task IdempotentReplayRejectsLateAuthorityLossWithoutChangingTheClaim()
    {
        var task = TaskRef("feature");
        var graph = await AddTaskAsync(task);
        var selection = Selection("run-replay-late-authority");
        var first = await ClaimAsync(task, graph.Revision, selection, "claim-replay-late");
        Assert.True(first.IsSuccess);
        var graphBeforeReplay = await _backlog.ReadGraphAsync(task.ProjectId, CancellationToken.None);
        var restartedBacklog = new BacklogOwnerStore(_fixture.DataSource, _schema);
        var restartedCoordination = new CoordinationOwnerStore(
            _fixture.DataSource, _schema, TimeProvider.System, _decisions);
        var authorityChecks = 0;

        await Assert.ThrowsAsync<CoordinationException>(() =>
            restartedBacklog.ClaimTaskAsync(
                _actor,
                selection,
                task,
                graph.Revision,
                0,
                "claim-replay-late",
                _evidence,
                restartedCoordination,
                _decisions,
                _ => ++authorityChecks == 2
                    ? Task.FromException(new CoordinationException("run_selection_permission_denied", 403))
                    : Task.CompletedTask,
                CancellationToken.None));

        Assert.Equal(2, authorityChecks);
        var graphAfterReplay = await restartedBacklog.ReadGraphAsync(task.ProjectId, CancellationToken.None);
        Assert.Equal(graphBeforeReplay.Value!.Revision, graphAfterReplay.Value!.Revision);
        Assert.Equal(first.Value!.TaskRevision,
            graphAfterReplay.Value.Tasks.Single().Revision);
        Assert.Equal(BacklogTaskState.Claimed, graphAfterReplay.Value.Tasks.Single().State);
        Assert.Equal(1, await ReadCountAsync("accepted_runs", "project_id = 'backlog-project'"));
        Assert.Equal(1, await ReadCountAsync("coordination_sessions",
            "project_id = 'backlog-project' AND node_kind = 'coordinator'"));
        Assert.Equal(1, await ReadCountAsync("coordinator_decisions",
            "project_id = 'backlog-project' AND action_kind = 'run.initialize'"));
        Assert.Equal(1, await ReadCountAsync("coordinator_decision_outbox",
            "project_id = 'backlog-project' AND event_kind = 'decision.accepted'"));
        Assert.Equal(1, await ReadCountAsync("backlog_tasks", "claim_id IS NOT NULL"));
    }

    [Fact]
    public async Task FailedTaskPersistenceRollsBackRootDecisionAndIntentTogether()
    {
        var task = TaskRef("feature");
        var graph = await AddTaskAsync(task);
        var selection = Selection("run-rollback");
        await InstallClaimRejectionTriggerAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            ClaimAsync(task, graph.Revision, selection, "claim-rollback"));

        Assert.Equal("backlog_claim_rejected", error.MessageText);
        Assert.Equal(0, await ReadCountAsync("accepted_runs", "project_id = 'backlog-project'"));
        Assert.Equal(0, await ReadCountAsync("coordination_sessions",
            "project_id = 'backlog-project' AND node_kind = 'coordinator'"));
        Assert.Equal(0, await ReadCountAsync("coordinator_decisions",
            "project_id = 'backlog-project'"));
        Assert.Equal(0, await ReadCountAsync("coordinator_decision_outbox",
            "project_id = 'backlog-project'"));
        Assert.Equal(0, await ReadCountAsync("backlog_tasks", "claim_id IS NOT NULL"));
        Assert.Equal(1, await ReadCountAsync("backlog_projects",
            "project_id = 'backlog-project' AND graph_revision = 1"));
    }

    private async Task<BacklogDependencyGraph> AddTaskAsync(BacklogTaskReference task)
    {
        var added = await _backlog.AddTaskAsync(task, 0, CancellationToken.None);
        Assert.True(added.IsSuccess);
        return added.Value!;
    }

    private Task<BacklogCoreResult<BacklogTaskClaimReceipt>> ClaimAsync(
        BacklogTaskReference task,
        long graphRevision,
        AuthorizedRunSelection selection,
        string idempotencyKey,
        Func<CancellationToken, Task>? revalidateCurrentAuthority = null) =>
        _backlog.ClaimTaskAsync(
            _actor,
            selection,
            task,
            graphRevision,
            0,
            idempotencyKey,
            _evidence,
            _coordination,
            _decisions,
            revalidateCurrentAuthority ?? (_ => Task.CompletedTask),
            CancellationToken.None);

    private AuthorizedRunSelection Selection(string runId)
    {
        using var snapshot = JsonDocument.Parse("""
            {
              "projectConfiguration": {
                "blueprintWorkflowReferences": [],
                "agentCharters": [],
                "casting": []
              },
              "runLimits": {
                "maxChildren": 100,
                "maxConcurrentChildren": 32
              }
            }
            """);
        var runSelection = new EffectiveRunSelection(
            "backlog-project",
            runId,
            1,
            1,
            1,
            "context-v1",
            snapshot.RootElement.Clone());
        return new AuthorizedRunSelection(
            runSelection,
            new ProjectsAuthorizationContext(
                1,
                _actor.Issuer,
                _actor.Subject,
                "tenant-1",
                1,
                runSelection.ProjectId,
                runSelection.RunId,
                [
                    new ProjectsAuthority(
                        "project",
                        runSelection.ProjectId,
                        [new ProjectsPermissionGrant("acceptRunSelection", 1)])
                ]));
    }

    private static BacklogTaskReference TaskRef(string taskId) =>
        new("backlog-project", taskId);

    private async Task ApplyBacklogSchemaAsync()
    {
        var assembly = typeof(BacklogOwnerStorePostgresTests).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith("016_project_backlog.sql", StringComparison.Ordinal));
        await using var resource = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(resource);
        var sql = (await reader.ReadToEndAsync()).Replace(
            "{schema}", $"\"{_schema}\"", StringComparison.Ordinal);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> ReadCountAsync(string table, string predicate)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{_schema}\".{table} WHERE {predicate}", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task InstallClaimRejectionTriggerAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            CREATE FUNCTION "{_schema}".reject_backlog_claim() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.task_state = 'claimed' THEN
                    RAISE EXCEPTION 'backlog_claim_rejected';
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER reject_backlog_claim
            BEFORE UPDATE ON "{_schema}".backlog_tasks
            FOR EACH ROW EXECUTE FUNCTION "{_schema}".reject_backlog_claim();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class NoPrerequisitesEvidenceReader : IBacklogPrerequisiteEvidenceReader
    {
        public int ReadCount { get; private set; }

        public Task<BacklogPrerequisiteEvidencePreparation> PrepareForClaimAsync(
            CoordinationActor actor,
            AuthorizedRunSelection selection,
            BacklogDependencyGraph graph,
            BacklogTaskReference task,
            CancellationToken cancellationToken)
        {
            Assert.DoesNotContain(graph.Dependencies, edge => edge.Task.TaskId == task.TaskId);
            return Task.FromResult(new BacklogPrerequisiteEvidencePreparation([]));
        }

        public Task<ImmutableArray<BacklogPrerequisiteExecutionSnapshot>> ReadCurrentForClaimAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CoordinationActor actor,
            AuthorizedRunSelection selection,
            BacklogDependencyGraph graph,
            BacklogTaskReference task,
            BacklogPrerequisiteEvidencePreparation prepared,
            long decisionStateVersion,
            long executionFence,
            CancellationToken cancellationToken)
        {
            Assert.Same(connection, transaction.Connection);
            Assert.DoesNotContain(graph.Dependencies, edge => edge.Task.TaskId == task.TaskId);
            Assert.Empty(prepared.Prerequisites);
            Assert.Equal(1, decisionStateVersion);
            Assert.True(executionFence > 0);
            ReadCount++;
            return Task.FromResult(ImmutableArray<BacklogPrerequisiteExecutionSnapshot>.Empty);
        }
    }
}
