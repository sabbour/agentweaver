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
        await CoordinationOwnerMigrator.VerifyAsync(_fixture.DataSource, _schema);

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
    public async Task ProjectContentSurvivesRestartAndConcurrentIssueImportsHaveOneWinner()
    {
        var content = new BacklogTaskContent("Issue title", "Issue body",
            RepositoryIssue: new("github", new("Owner", "Repo"), 1915));
        var contenders = await Task.WhenAll(
            _backlog.AddTaskAsync(TaskRef("import-a"), content, 0, CancellationToken.None),
            _backlog.AddTaskAsync(TaskRef("import-b"), content, 0, CancellationToken.None));
        Assert.Single(contenders, result => result.IsSuccess);
        Assert.Single(contenders, result => !result.IsSuccess);
        var restarted = new BacklogOwnerStore(_fixture.DataSource, _schema);
        var graph = (await restarted.ReadGraphAsync("backlog-project", CancellationToken.None)).Value!;
        var task = Assert.Single(graph.Tasks);
        Assert.Equal("Issue title", task.Title);
        Assert.Equal("Issue body", task.Description);
        Assert.Equal("owner/repo", task.RepositoryIssue!.Repository.FullName);
        var duplicate = await restarted.AddTaskAsync(TaskRef("duplicate"), content,
            graph.Revision, CancellationToken.None);
        Assert.Contains(duplicate.Issues, issue => issue.Code == BacklogIssueCode.DuplicateRepositoryIssue);
        var closed = await restarted.SetTaskContentAsync(task.Reference,
            content with { Lifecycle = BacklogTaskLifecycle.Closed },
            graph.Revision, task.Revision, CancellationToken.None);
        Assert.True(closed.IsSuccess);
        var stale = await restarted.SetTaskContentAsync(task.Reference, content,
            graph.Revision, task.Revision, CancellationToken.None);
        Assert.Contains(stale.Issues, issue => issue.Code == BacklogIssueCode.StaleGraphRevision);
        var noOp = await restarted.SetTaskContentAsync(task.Reference,
            content with { Lifecycle = BacklogTaskLifecycle.Closed },
            closed.Value!.Revision, task.Revision + 1, CancellationToken.None);
        Assert.True(noOp.IsSuccess);
        Assert.Equal(closed.Value.Revision, noOp.Value!.Revision);
        Assert.Equal(0, await ReadCountAsync("accepted_runs", "project_id = 'backlog-project'"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RevocationDuringProjectCaptureRollsBackTaskAndGraph(int failOnCheck)
    {
        var checks = 0;
        await Assert.ThrowsAsync<CoordinationException>(() => _backlog.AddTaskAsync(
            TaskRef("revoked-capture"), new BacklogTaskContent("Title"), 0, CancellationToken.None,
            _ => ++checks == failOnCheck
                ? Task.FromException(new CoordinationException("backlog_project_permission_denied", 403))
                : Task.CompletedTask));
        var graph = (await _backlog.ReadGraphAsync("backlog-project", CancellationToken.None)).Value!;
        Assert.Equal(0, graph.Revision);
        Assert.Empty(graph.Tasks);
    }

    [Fact]
    public async Task ProjectStateArchiveAndDependencyWritesRecheckAuthorityInsideTheirTransactions()
    {
        var task = TaskRef("project-mutation");
        var prerequisite = TaskRef("project-prerequisite");
        var graph = await AddTaskAsync(task);
        graph = (await _backlog.AddTaskAsync(
            prerequisite, graph.Revision, CancellationToken.None)).Value!;

        async Task AssertRollback(
            Func<Func<CancellationToken, Task>, Task> mutation,
            int failOnCheck)
        {
            var checks = 0;
            await Assert.ThrowsAsync<CoordinationException>(() => mutation(token =>
                ++checks == failOnCheck
                    ? Task.FromException(new CoordinationException("backlog_project_permission_denied", 403))
                    : Task.CompletedTask));
            Assert.Equal(failOnCheck, checks);
        }

        await AssertRollback(
            revalidate => _backlog.SetTaskStateAsync(
                task, BacklogTaskState.Ready, graph.Revision, 0, CancellationToken.None, revalidate),
            failOnCheck: 1);
        await AssertRollback(
            revalidate => _backlog.ArchiveTaskAsync(
                task, graph.Revision, 0, CancellationToken.None, revalidate),
            failOnCheck: 2);
        await AssertRollback(
            revalidate => _backlog.AddDependencyAsync(
                task, prerequisite, graph.Revision, CancellationToken.None, revalidate),
            failOnCheck: 2);

        var added = await _backlog.AddDependencyAsync(
            task, prerequisite, graph.Revision, CancellationToken.None);
        Assert.True(added.IsSuccess);
        await AssertRollback(
            revalidate => _backlog.RemoveDependencyAsync(
                task, prerequisite, added.Value!.Revision, CancellationToken.None, revalidate),
            failOnCheck: 2);

        var persisted = (await _backlog.ReadGraphAsync(task.ProjectId, CancellationToken.None)).Value!;
        Assert.Equal(added.Value!.Revision, persisted.Revision);
        Assert.Equal(BacklogTaskState.Backlog,
            persisted.Tasks.Single(item => item.Reference == task).State);
        Assert.False(persisted.Tasks.Single(item => item.Reference == task).IsArchived);
        Assert.Single(persisted.Dependencies);
    }

    [Fact]
    public async Task ContentPersistenceFailureRollsBackRevisionAndSurfacesDatabaseError()
    {
        var captured = await _backlog.AddTaskAsync(TaskRef("content-failure"),
            new BacklogTaskContent("Original"), 0, CancellationToken.None);
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"ALTER TABLE \"{_schema}\".backlog_tasks ADD CONSTRAINT test_title_write CHECK (title <> 'Rejected')",
            connection))
            await command.ExecuteNonQueryAsync();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => _backlog.SetTaskContentAsync(
            TaskRef("content-failure"), new BacklogTaskContent("Rejected"), captured.Value!.Revision, 0,
            CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        var graph = (await _backlog.ReadGraphAsync("backlog-project", CancellationToken.None)).Value!;
        Assert.Equal(captured.Value!.Revision, graph.Revision);
        Assert.Equal("Original", Assert.Single(graph.Tasks).Title);
        Assert.Equal(0, Assert.Single(graph.Tasks).Revision);
    }

    [Fact]
    public async Task RestrictedRuntimeRoleEditsTheGraphWithoutSchemaOrHistoryWritePrivileges()
    {
        var role = _schema + "_runtime";
        var password = Guid.NewGuid().ToString("N");
        await using var ownerConnection = await _fixture.DataSource.OpenConnectionAsync();
        await using (var createRole = new NpgsqlCommand($"""
            CREATE ROLE "{role}" LOGIN PASSWORD '{password}'
                NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT
            """, ownerConnection))
            await createRole.ExecuteNonQueryAsync();

        try
        {
            await using (var grants = new NpgsqlCommand($"""
                GRANT USAGE ON SCHEMA "{_schema}" TO "{role}";
                GRANT SELECT ON "{_schema}".coordination_schema_migrations,
                    "{_schema}".outbox_schema_migrations TO "{role}";
                GRANT SELECT, INSERT, UPDATE ON "{_schema}".backlog_projects,
                    "{_schema}".backlog_tasks TO "{role}";
                GRANT SELECT, INSERT, UPDATE, DELETE ON "{_schema}".backlog_dependencies TO "{role}";
                """, ownerConnection))
                await grants.ExecuteNonQueryAsync();

            var connectionString = new NpgsqlConnectionStringBuilder(_fixture.DataSource.ConnectionString)
            {
                Username = role,
                Password = password,
                Pooling = false
            }.ConnectionString;
            await using var runtime = NpgsqlDataSource.Create(connectionString);
            await CoordinationOwnerMigrator.VerifyAsync(runtime, _schema);
            var store = new BacklogOwnerStore(runtime, _schema);
            var task = TaskRef("runtime-task");
            var prerequisite = TaskRef("runtime-prerequisite");
            var first = await store.AddTaskAsync(task, 0, CancellationToken.None);
            Assert.True(first.IsSuccess);
            var second = await store.AddTaskAsync(prerequisite, first.Value!.Revision, CancellationToken.None);
            Assert.True(second.IsSuccess);
            var added = await store.AddDependencyAsync(
                task, prerequisite, second.Value!.Revision, CancellationToken.None);
            Assert.True(added.IsSuccess);
            Assert.Single(added.Value!.Dependencies);
            var removed = await store.RemoveDependencyAsync(
                task, prerequisite, added.Value.Revision, CancellationToken.None);
            Assert.True(removed.IsSuccess);
            Assert.Empty(removed.Value!.Dependencies);
            Assert.Equal(4, removed.Value.Revision);
            var graph = await store.ReadGraphAsync(task.ProjectId, CancellationToken.None);
            Assert.True(graph.IsSuccess);
            Assert.Equal(removed.Value.Revision, graph.Value!.Revision);
            Assert.Equal(2, graph.Value.Tasks.Length);
            Assert.Contains(graph.Value.Tasks, item => item.Reference == task);
            Assert.Contains(graph.Value.Tasks, item => item.Reference == prerequisite);
            Assert.Empty(graph.Value.Dependencies);
            var updated = await store.SetTaskContentAsync(task,
                new BacklogTaskContent("Runtime title", RepositoryIssue: new("github", new("owner", "repo"), 42)),
                graph.Value.Revision, 0, CancellationToken.None);
            Assert.True(updated.IsSuccess);
            Assert.Equal("Runtime title", updated.Value!.Tasks.Single(item => item.Reference == task).Title);

            await using var runtimeConnection = await runtime.OpenConnectionAsync();
            foreach (var sql in new[]
            {
                $"CREATE TABLE \"{_schema}\".runtime_forbidden (id integer)",
                $"ALTER TABLE \"{_schema}\".backlog_tasks ADD COLUMN runtime_forbidden integer",
                $"DELETE FROM \"{_schema}\".backlog_tasks",
                $"DELETE FROM \"{_schema}\".backlog_projects",
                $"TRUNCATE \"{_schema}\".backlog_dependencies",
                $"TRUNCATE \"{_schema}\".backlog_tasks CASCADE",
                $"TRUNCATE \"{_schema}\".backlog_projects CASCADE"
            }.Concat(new[] { "coordination_schema_migrations", "outbox_schema_migrations" }
                .SelectMany(table => new[]
                {
                    $"INSERT INTO \"{_schema}\".{table} (version) VALUES (999)",
                    $"UPDATE \"{_schema}\".{table} SET version = version",
                    $"DELETE FROM \"{_schema}\".{table}",
                    $"TRUNCATE \"{_schema}\".{table}"
                })))
            {
                await using var denied = new NpgsqlCommand(sql, runtimeConnection);
                var error = await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            }
            await CoordinationOwnerMigrator.VerifyAsync(runtime, _schema);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand(
                $"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\"", ownerConnection);
            await cleanup.ExecuteNonQueryAsync();
        }
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
    public async Task ReusingClaimKeyForDifferentTaskReturnsConflictWithoutAnotherRun()
    {
        var firstTask = TaskRef("claim-key-first");
        var secondTask = TaskRef("claim-key-second");
        var graph = await AddTaskAsync(firstTask);
        graph = (await _backlog.AddTaskAsync(
            secondTask, graph.Revision, CancellationToken.None)).Value!;
        var first = await ClaimAsync(firstTask, graph.Revision, Selection("run-key-first"), "shared-claim-key");
        Assert.True(first.IsSuccess);

        var conflict = await Assert.ThrowsAsync<CoordinationException>(() => ClaimAsync(
            secondTask, graph.Revision, Selection("run-key-second"), "shared-claim-key"));

        Assert.Equal("backlog_claim_idempotency_conflict", conflict.Code);
        Assert.Equal(1, await ReadCountAsync("backlog_tasks", "claim_id IS NOT NULL"));
        Assert.Equal(1, await ReadCountAsync("accepted_runs", "project_id = 'backlog-project'"));
        Assert.Equal(1, await ReadCountAsync("coordination_sessions",
            "project_id = 'backlog-project' AND node_kind = 'coordinator'"));
        var persisted = (await _backlog.ReadGraphAsync(
            firstTask.ProjectId, CancellationToken.None)).Value!;
        Assert.Equal(BacklogTaskState.Backlog,
            persisted.Tasks.Single(item => item.Reference == secondTask).State);
    }

    [Fact]
    public async Task ConfigurationChangeDuringClaimWaitRollsBackTheClaimAndAcceptedRoot()
    {
        var task = TaskRef("configuration-changed");
        var graph = await AddTaskAsync(task);
        var selection = Selection("run-configuration-changed");
        var expectedConfigurationRevision = 1;
        var currentConfigurationRevision = expectedConfigurationRevision;
        var authorityChecks = 0;

        var error = await Assert.ThrowsAsync<CoordinationException>(() => _backlog.ClaimTaskAsync(
            _actor,
            selection,
            task,
            graph.Revision,
            0,
            "claim-configuration-changed",
            _evidence,
            _coordination,
            _decisions,
            _ =>
            {
                authorityChecks++;
                if (authorityChecks == 1)
                {
                    // The configuration can change after the pre-transaction check and while locks are acquired.
                    currentConfigurationRevision++;
                    return Task.CompletedTask;
                }
                return currentConfigurationRevision == expectedConfigurationRevision
                    ? Task.CompletedTask
                    : Task.FromException(new CoordinationException("backlog_configuration_stale", 409));
            },
            CancellationToken.None));

        Assert.Equal("backlog_configuration_stale", error.Code);
        Assert.Equal(2, authorityChecks);
        Assert.Equal(0, await ReadCountAsync("accepted_runs", "project_id = 'backlog-project'"));
        Assert.Equal(0, await ReadCountAsync("coordination_sessions",
            "project_id = 'backlog-project' AND node_kind = 'coordinator'"));
        Assert.Equal(0, await ReadCountAsync("coordinator_decisions",
            "project_id = 'backlog-project'"));
        Assert.Equal(0, await ReadCountAsync("backlog_tasks", "claim_id IS NOT NULL"));
        var persisted = (await _backlog.ReadGraphAsync(task.ProjectId, CancellationToken.None)).Value!;
        Assert.Equal(graph.Revision, persisted.Revision);
        Assert.Equal(BacklogTaskState.Backlog, Assert.Single(persisted.Tasks).State);
    }

    [Fact]
    public async Task ClaimWithPendingPrerequisiteEvidenceIsBlockedWithoutPersistingAClaim()
    {
        var task = TaskRef("dependent-work");
        var prerequisite = TaskRef("unfinished-prerequisite");
        var graph = await AddTaskAsync(task);
        graph = (await _backlog.AddTaskAsync(
            prerequisite, graph.Revision, CancellationToken.None)).Value!;
        graph = (await _backlog.AddDependencyAsync(
            task, prerequisite, graph.Revision, CancellationToken.None)).Value!;
        graph = (await _backlog.SetTaskStateAsync(
            task, BacklogTaskState.Ready, graph.Revision, 0, CancellationToken.None)).Value!;

        var result = await _backlog.ClaimTaskAsync(
            _actor,
            Selection("run-dependent-work"),
            task,
            graph.Revision,
            1,
            "claim-dependent-work",
            new PendingPrerequisiteEvidenceReader(prerequisite),
            _coordination,
            _decisions,
            _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Code == BacklogIssueCode.PrerequisiteNotMerged);
        Assert.Equal(0, await ReadCountAsync("accepted_runs", "project_id = 'backlog-project'"));
        Assert.Equal(0, await ReadCountAsync("coordination_sessions",
            "project_id = 'backlog-project' AND node_kind = 'coordinator'"));
        Assert.Equal(0, await ReadCountAsync("backlog_tasks", "claim_id IS NOT NULL"));
        var persisted = (await _backlog.ReadGraphAsync(task.ProjectId, CancellationToken.None)).Value!;
        Assert.Equal(graph.Revision, persisted.Revision);
        Assert.Equal(BacklogTaskState.Ready,
            persisted.Tasks.Single(item => item.Reference == task).State);
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

    private sealed class PendingPrerequisiteEvidenceReader(
        BacklogTaskReference prerequisite) : IBacklogPrerequisiteEvidenceReader
    {
        public Task<BacklogPrerequisiteEvidencePreparation> PrepareForClaimAsync(
            CoordinationActor actor,
            AuthorizedRunSelection selection,
            BacklogDependencyGraph graph,
            BacklogTaskReference task,
            CancellationToken cancellationToken)
        {
            Assert.Contains(graph.Dependencies, edge =>
                edge.Task == task && edge.Prerequisite == prerequisite);
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
            var prerequisiteTask = Assert.Single(graph.Tasks, item => item.Reference == prerequisite);
            return Task.FromResult(ImmutableArray.Create(new BacklogPrerequisiteExecutionSnapshot(
                prerequisite,
                prerequisiteTask.Revision,
                1,
                BacklogPrerequisiteExecutionState.Pending,
                BacklogOutputProofState.NotRequired,
                null)));
        }
    }
}
