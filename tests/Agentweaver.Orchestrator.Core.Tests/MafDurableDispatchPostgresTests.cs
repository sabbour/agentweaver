using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class MafDurableDispatchPostgresTests(CoordinationPostgresFixture fixture) : IAsyncLifetime
{
    private readonly string _schema = "maf_dispatch_" + Guid.NewGuid().ToString("N");
    private readonly CoordinationActor _actor = new("https://identity.example/", Guid.NewGuid().ToString("D"));
    private CoordinationOwnerStore _owner = null!;
    private MafExecutionCheckpointStore _checkpoints = null!;
    private SessionIdentity _root;
    private long _fence;

    public async Task InitializeAsync()
    {
        // Every test owns an isolated disposable owner schema; no shared migration history is changed.
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        using var document = JsonDocument.Parse("""{"modelSelection":{"reference":"model:provider/alpha"}}""");
        var selected = new EffectiveRunSelection(
            "project-1", Guid.NewGuid().ToString("D"), 1, 1, 1, "selection-1",
            document.RootElement.Clone());
        var authorized = new AuthorizedRunSelection(selected, new ProjectsAuthorizationContext(
            1, _actor.Issuer, _actor.Subject, "tenant-1", 1, selected.ProjectId, selected.RunId,
            [new ProjectsAuthority("project", selected.ProjectId,
                [new ProjectsPermissionGrant("acceptRunSelection", 1)])]));
        _root = new SessionIdentity(selected.ProjectId, selected.RunId, "root");
        _owner = new CoordinationOwnerStore(fixture.DataSource, _schema);
        _fence = (await _owner.AcceptRootAsync(
            _actor, authorized, _root.SessionId, CancellationToken.None)).ExecutionFence;
        _checkpoints = CreateCheckpoints(_fence);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task IndependentReadyFrontierCommitsBothChildrenWithoutWaitingForAJoin()
    {
        var workflow = WorkflowTestData.Snapshot(WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn, WorkflowTestData.Open("implement", 0, [])));
        var validated = WorkPlanValidator.ValidateAndSnapshot(
            workflow, WorkflowTestData.Plan(
                WorkflowTestData.Item("first", outputs: ["src/first.cs"]),
                WorkflowTestData.Item("second", outputs: ["src/second.cs"])),
            WorkflowTestData.SelectionContext());
        Assert.True(validated.IsValid);
        var plan = validated.Value!;
        var step = plan.Workflow.Definition.Steps[0];

        // Independent work remains ready after its sibling starts; the owner persists each intent separately.
        _ = await SpawnAsync(plan, new MafExecutionAction(step, plan.Plan.Items[0]));
        _ = await SpawnAsync(plan, new MafExecutionAction(step, plan.Plan.Items[1]));
        var checkpoint = (await _checkpoints.ReadLatestAsync(CancellationToken.None))!;
        Assert.Equal(2, checkpoint.State.PendingDispatches.Count);
        Assert.All(checkpoint.State.Progress.WorkItems.Values,
            status => Assert.Equal(MafExecutionTaskStatus.Running, status));
        Assert.Equal((2L, 2L), await CountChildrenAndSpawnsAsync());
    }

    [Fact]
    public async Task ReadyFrontierPersistsOneIntentAndBlocksOverlappingOutputUntilPredecessorSucceeds()
    {
        var plan = ConflictingPlan();
        var first = plan.Plan.Items[0];
        var second = plan.Plan.Items[1];
        Assert.Contains(first.Id, second.DependsOn);
        var firstAction = new MafExecutionAction(plan.Workflow.Definition.Steps[0], first);
        var secondAction = new MafExecutionAction(plan.Workflow.Definition.Steps[0], second);

        _ = await SpawnAsync(plan, firstAction);
        var blocked = await Assert.ThrowsAsync<CoordinationException>(() => SpawnAsync(plan, secondAction));
        Assert.Equal("maf_execution_frontier_stale", blocked.Code);
        Assert.Equal((1L, 1L), await CountChildrenAndSpawnsAsync());
        Assert.Single((await _checkpoints.ReadLatestAsync(CancellationToken.None))!.State.PendingDispatches);

        // A terminal predecessor and its result are append-only owner evidence for the next frontier.
        var latest = (await _checkpoints.ReadLatestAsync(CancellationToken.None))!;
        var completed = latest.State with
        {
            Revision = latest.State.Revision + 1,
            Progress = latest.State.Progress with
            {
                WorkItems = latest.State.Progress.WorkItems.SetItem(first.Id, MafExecutionTaskStatus.Succeeded)
            },
            PendingDispatches = latest.State.PendingDispatches.Remove(first.Id),
            Results = latest.State.Results.Add(first.Id, "Done")
        };
        _ = await _checkpoints.AppendAsync("terminal-first", latest.Info, completed, CancellationToken.None);
        _ = await SpawnAsync(plan, secondAction);
        Assert.Equal((2L, 2L), await CountChildrenAndSpawnsAsync());
    }

    [Fact]
    public async Task DuplicateSpawnReusesThePersistedIntentAndOutboxAfterOwnerRestart()
    {
        var plan = ConflictingPlan();
        var action = new MafExecutionAction(plan.Workflow.Definition.Steps[0], plan.Plan.Items[0]);
        var first = await SpawnAsync(plan, action);
        _owner = new CoordinationOwnerStore(fixture.DataSource, _schema);
        _checkpoints = CreateCheckpoints(_fence);
        var replay = await SpawnAsync(plan, action);

        Assert.Equal(first.CommandId, replay.CommandId);
        Assert.Equal((1L, 1L), await CountChildrenAndSpawnsAsync());
        Assert.Equal(1, (await _checkpoints.ReadLatestAsync(CancellationToken.None))!.State.Revision);
    }

    [Fact]
    public async Task StaleOwnerFenceRollsBackChildOutboxAndCheckpointTogether()
    {
        var plan = ConflictingPlan();
        var action = new MafExecutionAction(plan.Workflow.Definition.Steps[0], plan.Plan.Items[0]);
        _checkpoints = CreateCheckpoints(_fence + 1);

        var failure = await Assert.ThrowsAsync<CoordinationException>(() => SpawnAsync(plan, action));
        Assert.Equal("checkpoint_execution_fence_stale", failure.Code);
        Assert.Equal((0L, 0L), await CountChildrenAndSpawnsAsync());
        Assert.Null(await CreateCheckpoints(_fence).ReadLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConflictingCallbackRollsBackTheSpawnAndDoesNotReplaceItsOriginalIntent()
    {
        var plan = ConflictingPlan();
        var action = new MafExecutionAction(plan.Workflow.Definition.Steps[0], plan.Plan.Items[0]);
        var original = await SpawnAsync(plan, action);
        var saved = (await _checkpoints.ReadLatestAsync(CancellationToken.None))!;
        // The deterministic child ID is already claimed: changing the prompt is not a replay.
        var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
            SpawnAsync(plan, action, new string('b', 64)));
        Assert.Equal("maf_execution_frontier_stale", failure.Code);
        Assert.Equal(original.CommandId, (await SpawnAsync(plan, action)).CommandId);
        Assert.Equal((1L, 1L), await CountChildrenAndSpawnsAsync());
        var retained = (await _checkpoints.ReadLatestAsync(CancellationToken.None))!;
        Assert.Equal(saved.Info, retained.Info);
        Assert.Equal(saved.State.Revision, retained.State.Revision);
        Assert.Equal(saved.State.PendingDispatches[action.WorkItem!.Id],
            retained.State.PendingDispatches[action.WorkItem.Id]);
    }

    private MafExecutionCheckpointStore CreateCheckpoints(long fence) =>
        new(new PostgresMafCheckpointStore(fixture.DataSource, _schema, null),
            new MafCheckpointBinding(_root, _actor, fence, "maf-test-sdk", "model:provider/alpha", null));

    private static WorkPlanSnapshot ConflictingPlan()
    {
        var workflow = WorkflowTestData.Snapshot(WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn, WorkflowTestData.Open("implement", 0, [])));
        var result = WorkPlanValidator.ValidateAndSnapshot(
            workflow, WorkflowTestData.Plan(
                WorkflowTestData.Item("first", outputs: ["src/shared.cs"]),
                WorkflowTestData.Item("second", outputs: ["src/shared.cs"])),
            WorkflowTestData.SelectionContext());
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        return result.Value!;
    }

    private async Task<SpawnedSession> SpawnAsync(
        WorkPlanSnapshot plan, MafExecutionAction action, string? promptHash = null)
    {
        var item = action.WorkItem!;
        var child = MafExecutionPlanner.CreateChildSessionId(_root, plan.Plan.Id, item.Id);
        var intent = new MafExecutionDispatchIntent(
            item.Id, child, MafExecutionPlanner.CreateDispatchMessageId(_root, plan.Plan.Id, item.Id),
            promptHash ?? new string('a', 64))
        {
            EgressReceipt = new RuntimeEgressVerificationReceipt(
                1, new EnvironmentGenerationFence(
                    new EnvironmentOwnerIdentity("tenant-1", _root.ProjectId, _root.RunId, "environment-1"), 1),
                1, "selection-1", new string('c', 64))
        };
        // The production callback executes under the same root lock as spawn and outbox insertion.
        return await _owner.SpawnSessionAsync(
            _actor, _root, new SpawnSessionRequest(
                child, CoordinationSessionKind.ChildWork, child, item.Task),
            100, 32, CancellationToken.None,
            persistSpawnedInTransaction: async (connection, transaction, spawned, token) =>
            {
                Assert.Equal(child, spawned.Node.Identity.SessionId);
                var latest = await _checkpoints.ReadLatestInTransactionAsync(connection, transaction, token);
                if (latest?.State.PendingDispatches.TryGetValue(item.Id, out var existing) != true ||
                    existing != intent)
                    MafExecutionPlanner.RequireReadyDispatchAction(
                        plan, latest?.State.Progress ?? MafExecutionProgress.Empty,
                        latest?.State.FixedWorkAssociations, action);
                var next = CoordinationEndpoints.PrepareRunningCheckpoint(
                    latest, plan.Plan.Id, 1, item.Id, null, intent);
                if (next is not null)
                    _ = await _checkpoints.AppendInTransactionAsync(
                        connection, transaction, Guid.NewGuid().ToString("N"), latest?.Info, next, token);
            });
    }

    private async Task<(long Children, long Spawns)> CountChildrenAndSpawnsAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT (SELECT count(*) FROM "{_schema}".coordination_sessions
                    WHERE run_id = @run AND parent_session_id = @root),
                   (SELECT count(*) FROM "{_schema}".outbox_events
                    WHERE event_type = 'orchestrator.session.spawn_requested'
                      AND stream_id = @stream)
            """, connection);
        command.Parameters.AddWithValue("run", _root.RunId);
        command.Parameters.AddWithValue("root", _root.SessionId);
        command.Parameters.AddWithValue("stream",
            $"coordination/{_root.ProjectId}/{_root.RunId}/{_root.SessionId}");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
