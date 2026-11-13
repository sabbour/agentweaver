using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed partial class MafCheckpointStorePostgresTests
{
    [Fact]
    public async Task OpenPullRequestIntentAndTerminalReceiptCommitAndRecoverAtomically()
    {
        var (initial, intent, _) = await PersistOpenPullRequestIntentAsync();
        var recovered = await _executionStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal(initial.Info, recovered.Info);
        Assert.Equal(MafExecutionTaskStatus.Running,
            recovered.State.Progress.NonModelSteps["publish"]);
        Assert.True(MafExecutionCheckpointContract.OpenPullRequestIntentsMatch(
            intent, recovered.State.OpenPullRequestIntents["publish"]));
        Assert.Null(CoordinationEndpoints.PrepareOpenPullRequestCheckpoint(recovered, intent));

        var pullRequest = new SourceControlPullRequest(
            42,
            new Uri("https://github.com/octo/repo/pull/42"),
            "open",
            intent.Request.HeadBranch,
            intent.Request.ExpectedHeadSha,
            intent.Request.BaseBranch,
            intent.Request.ExpectedBaseSha,
            false,
            SourceControlPullRequestDisposition.Created);
        var receipt = MafOpenPullRequestContract.CreateReceipt(intent, pullRequest);
        var completed = CoordinationEndpoints.PrepareOpenPullRequestCompletionCheckpoint(
            recovered, intent, receipt)!;
        var terminal = await _executionStore.AppendAsync(
            "open-pr-terminal", recovered.Info, completed, CancellationToken.None);
        var afterRecovery = await new MafExecutionCheckpointStore(
                new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null), _binding)
            .ReadLatestAsync(CancellationToken.None);

        Assert.NotNull(afterRecovery);
        Assert.Equal(terminal.Info, afterRecovery.Info);
        Assert.Equal(MafExecutionTaskStatus.Succeeded,
            afterRecovery.State.Progress.NonModelSteps["publish"]);
        Assert.Equal(receipt, afterRecovery.State.OpenPullRequestReceipts["publish"]);
        Assert.Null(CoordinationEndpoints.PrepareOpenPullRequestCompletionCheckpoint(
            afterRecovery, intent, receipt));
    }

    [Fact]
    public async Task ChangedOwnerOrDecisionAndSourceControlFailureLeavePersistedIntentUncompleted()
    {
        var (_, intent, source) = await PersistOpenPullRequestIntentAsync();
        var changedPin = WithPinId(source.Pin, "replacement-pin");
        var ownerFailure = Assert.Throws<CoordinationException>(() =>
            MafOpenPullRequestContract.RequireCurrentSource(
                intent, source.Actor, intent.Identity, "tenant-1", intent.DecisionStateVersion,
                intent.ExecutionFence, intent.AcceptedSelectionHash, changedPin));
        var decisionFailure = Assert.Throws<CoordinationException>(() =>
            MafOpenPullRequestContract.RequireCurrentSource(
                intent, source.Actor, intent.Identity, "tenant-1", intent.DecisionStateVersion + 1,
                intent.ExecutionFence, intent.AcceptedSelectionHash, source.Pin));
        Assert.Equal("source_control_run_binding_changed", ownerFailure.Code);
        Assert.Equal("source_control_run_binding_changed", decisionFailure.Code);

        using var client = new HttpClient(new FixedOwnerResponseHandler())
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        var owner = new MafSourceControlOwnerClient(
            client, new MafSourceControlOwnerOptions(new Uri("https://orchestrator.example/")));
        var runtimeActor = new RuntimeActorAuthorization(
            new SecretCredential("test-owner-token", DateTimeOffset.UtcNow.AddMinutes(1)), "tenant-1");
        var sourceControlFailure = await Assert.ThrowsAsync<CoordinationException>(() =>
            owner.CreateOrReusePullRequestAsync(
                runtimeActor, intent.Identity, intent.Request, CancellationToken.None));
        Assert.Equal("source_control_run_binding_changed", sourceControlFailure.Code);

        var latest = await _executionStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(MafExecutionTaskStatus.Running,
            latest.State.Progress.NonModelSteps["publish"]);
        Assert.Empty(latest.State.OpenPullRequestReceipts);
    }

    [Fact]
    public async Task PostgresDispatchPersistsSuccessAndTerminalReplayDoesNotCallOwnerAgain()
    {
        var (_, intent, source) = await PersistOpenPullRequestIntentAsync();
        var owner = new DispatchOwnerHandler(intent);
        var (executor, client) = CreateDispatchExecutor(intent, source, owner);
        using (client)
        {
            Assert.True(await executor.ExecuteAsync(intent, CancellationToken.None));
            var completed = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(completed);
            Assert.Equal(MafExecutionTaskStatus.Succeeded,
                completed.State.Progress.NonModelSteps["publish"]);
            Assert.Equal(42, completed.State.OpenPullRequestReceipts["publish"].PullRequestNumber);
            Assert.Equal(1, owner.ProviderCreates);

            Assert.False(await executor.ExecuteAsync(intent, CancellationToken.None));
            Assert.Equal(1, owner.ProviderCreates);
            Assert.Equal(1, owner.PullRequestRequests);
        }
    }

    [Fact]
    public async Task RecoveredCommittedReplayUsesCurrentBindingAndSkipsSeparateDiffRequest()
    {
        var (_, intent, source) = await PersistOpenPullRequestIntentAsync();
        var binding = intent.Request.ExecutionBinding! with
        {
            DecisionStateVersion = intent.DecisionStateVersion + 1,
            ExecutionFence = intent.ExecutionFence + 1
        };
        var recoveredIntent = intent with
        {
            DecisionStateVersion = binding.DecisionStateVersion,
            ExecutionFence = binding.ExecutionFence,
            Request = intent.Request with { ExecutionBinding = binding }
        };
        var owner = new DispatchOwnerHandler(intent) { ReturnExistingPullRequest = true };
        var (executor, client) = CreateDispatchExecutor(
            intent, source, owner, authorizedIntent: _ => recoveredIntent);
        using (client)
        {
            Assert.True(await executor.ExecuteAsync(intent, CancellationToken.None));
            Assert.Equal(0, owner.DiffRequests);
            Assert.Equal(0, owner.ProviderCreates);
            Assert.Equal(1, owner.PullRequestRequests);
            Assert.Equal(binding.ExecutionFence, owner.LastExecutionFence);

            var completed = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(completed);
            Assert.Equal(MafExecutionTaskStatus.Succeeded,
                completed.State.Progress.NonModelSteps["publish"]);
            Assert.Equal(42, completed.State.OpenPullRequestReceipts["publish"].PullRequestNumber);
        }
    }

    [Fact]
    public async Task ConcurrentRecoveredCommittedReplaysWriteOneReceiptWithoutCreatingAnotherPullRequest()
    {
        var (before, intent, source) = await PersistOpenPullRequestIntentAsync();
        var binding = intent.Request.ExecutionBinding! with
        {
            DecisionStateVersion = intent.DecisionStateVersion + 1,
            ExecutionFence = intent.ExecutionFence + 1
        };
        var recoveredIntent = intent with
        {
            DecisionStateVersion = binding.DecisionStateVersion,
            ExecutionFence = binding.ExecutionFence,
            Request = intent.Request with { ExecutionBinding = binding }
        };
        var owner = new DispatchOwnerHandler(intent) { ReturnExistingPullRequest = true };
        var (executor, client) = CreateDispatchExecutor(
            intent, source, owner, authorizedIntent: _ => recoveredIntent);
        using (client)
        {
            var results = await Task.WhenAll(
                executor.ExecuteAsync(intent, CancellationToken.None),
                executor.ExecuteAsync(intent, CancellationToken.None));

            Assert.All(results, result => Assert.True(result));
            Assert.Equal(0, owner.DiffRequests);
            Assert.Equal(0, owner.ProviderCreates);
            Assert.Equal(2, owner.PullRequestRequests);
            Assert.All(owner.ExecutionFences, fence => Assert.Equal(binding.ExecutionFence, fence));

            var completed = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(completed);
            Assert.Equal(before.State.Revision + 1, completed.State.Revision);
            Assert.Equal(MafExecutionTaskStatus.Succeeded,
                completed.State.Progress.NonModelSteps["publish"]);
            Assert.Single(completed.State.OpenPullRequestReceipts);
        }
    }

    [Fact]
    public async Task PostgresDispatchSourceControlFailureLeavesPersistedIntentIncomplete()
    {
        var (_, intent, source) = await PersistOpenPullRequestIntentAsync();
        var owner = new DispatchOwnerHandler(intent) { FailPullRequest = true };
        var (executor, client) = CreateDispatchExecutor(intent, source, owner);
        using (client)
        {
            var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
                executor.ExecuteAsync(intent, CancellationToken.None));
            Assert.Equal("source_control_run_binding_changed", failure.Code);
            Assert.Equal(1, owner.PullRequestRequests);
            Assert.Equal(0, owner.ProviderCreates);
            var latest = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(latest);
            Assert.Equal(MafExecutionTaskStatus.Running,
                latest.State.Progress.NonModelSteps["publish"]);
            Assert.Empty(latest.State.OpenPullRequestReceipts);
        }
    }

    [Fact]
    public async Task PostgresDispatchRecoversOwnerCreateAfterCheckpointRollbackWithSameOperation()
    {
        var (_, intent, source) = await PersistOpenPullRequestIntentAsync();
        var owner = new DispatchOwnerHandler(intent);
        var (executor, client) = CreateDispatchExecutor(intent, source, owner);
        using (client)
        {
            await SetPullRequestTerminalCheckpointFailureAsync(true);
            try
            {
                await Assert.ThrowsAsync<PostgresException>(() =>
                    executor.ExecuteAsync(intent, CancellationToken.None));
            }
            finally
            {
                await SetPullRequestTerminalCheckpointFailureAsync(false);
            }

            var afterRollback = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(afterRollback);
            Assert.Equal(MafExecutionTaskStatus.Running,
                afterRollback.State.Progress.NonModelSteps["publish"]);
            Assert.Empty(afterRollback.State.OpenPullRequestReceipts);
            Assert.Equal(1, owner.ProviderCreates);

            Assert.True(await executor.ExecuteAsync(intent, CancellationToken.None));
            var recovered = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(recovered);
            Assert.Equal(MafExecutionTaskStatus.Succeeded,
                recovered.State.Progress.NonModelSteps["publish"]);
            Assert.Equal(1, owner.ProviderCreates);
            Assert.Equal(2, owner.PullRequestRequests);
            Assert.All(owner.OperationIds, operationId => Assert.Equal(intent.OperationId, operationId));

            Assert.False(await executor.ExecuteAsync(intent, CancellationToken.None));
            Assert.Equal(1, owner.ProviderCreates);
            Assert.Equal(2, owner.PullRequestRequests);
        }
    }

    [Fact]
    public async Task OpenPullRequestGateIntentIsProjectedAndResumedWithSameOperationAfterRestart()
    {
        var (_, intent, source) = await PersistOpenPullRequestIntentAsync();
        _executionStore = new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null), _binding);
        var recovered = await _executionStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(recovered);

        var workflow = WorkflowTestData.Snapshot(WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("implement", 0, []),
            WorkflowTestData.Platform(
                "publish", 1, ["implement"], WorkflowPlatformGate.OpenPullRequest)));
        var validPlan = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            WorkflowTestData.Plan(WorkflowTestData.Item("implementation")),
            WorkflowTestData.SelectionContext());
        Assert.True(validPlan.IsValid, string.Join("; ", validPlan.Issues.Select(issue => issue.Message)));
        var recovery = MafExecutionRecoveryProjector.Project(
            recovered, validPlan.Value, intent.DecisionStateVersion, true, null);

        var operation = Assert.Single(recovery.PlatformGateOperations);
        Assert.Equal(WorkflowPlatformGate.OpenPullRequest, operation.Gate);
        Assert.Equal(intent.OperationId, operation.OperationId);
        Assert.Equal("intentOnly", operation.EvidenceState);
        Assert.Equal("mayRepeat", operation.EffectRecoveryState);

        // A fresh executor resumes the saved operation ID instead of creating another pull request.
        var owner = new DispatchOwnerHandler(intent);
        var (executor, client) = CreateDispatchExecutor(intent, source, owner);
        using (client)
        {
            Assert.True(await executor.ExecuteAsync(intent, CancellationToken.None));
            Assert.Equal(1, owner.ProviderCreates);
            Assert.All(owner.OperationIds, operationId => Assert.Equal(intent.OperationId, operationId));
        }
    }

    [Fact]
    public async Task PostgresDispatchRecoveryAfterPullRequestClosureFailsClosedWithoutSecondCreate()
    {
        var (_, intent, source) = await PersistOpenPullRequestIntentAsync();
        var owner = new DispatchOwnerHandler(intent);
        var (executor, client) = CreateDispatchExecutor(intent, source, owner);
        using (client)
        {
            await SetPullRequestTerminalCheckpointFailureAsync(true);
            try
            {
                await Assert.ThrowsAsync<PostgresException>(() =>
                    executor.ExecuteAsync(intent, CancellationToken.None));
            }
            finally
            {
                await SetPullRequestTerminalCheckpointFailureAsync(false);
            }

            owner.CloseOnReplay = true;
            var closed = await Assert.ThrowsAsync<CoordinationException>(() =>
                executor.ExecuteAsync(intent, CancellationToken.None));
            Assert.Equal("source_control_pull_request_response_invalid", closed.Code);
            var recovered = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(recovered);
            Assert.Equal(MafExecutionTaskStatus.Running,
                recovered.State.Progress.NonModelSteps["publish"]);
            Assert.Empty(recovered.State.OpenPullRequestReceipts);
            Assert.Equal(1, owner.ProviderCreates);
            Assert.Equal(2, owner.PullRequestRequests);
            Assert.All(owner.OperationIds, operationId => Assert.Equal(intent.OperationId, operationId));
        }
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("decision")]
    [InlineData("workspace")]
    public async Task PostgresDispatchRejectsChangedCurrentBindingFromInitiallyValidState(string change)
    {
        var (_, intent, source) = await PersistOpenPullRequestIntentAsync();
        var pin = source.Pin;
        var decisionVersion = intent.DecisionStateVersion;
        var owner = new DispatchOwnerHandler(intent, afterDiff: () =>
        {
            if (change == "owner")
                pin = WithPinId(pin, "replacement-pin");
            if (change == "decision")
                decisionVersion++;
        }, diffHeadSha: change == "workspace" ? new string('d', 40) : null);
        var (executor, client) = CreateDispatchExecutor(
            intent, source with { Pin = source.Pin }, owner,
            () => pin, () => decisionVersion);
        using (client)
        {
            var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
                executor.ExecuteAsync(intent, CancellationToken.None));
            Assert.Equal("source_control_run_binding_changed", failure.Code);
            Assert.Equal(0, owner.PullRequestRequests);
            var latest = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(latest);
            Assert.Equal(MafExecutionTaskStatus.Running,
                latest.State.Progress.NonModelSteps["publish"]);
            Assert.Empty(latest.State.OpenPullRequestReceipts);
        }
    }

    [Fact]
    public async Task SourceControlOwnerOperationIsDurableAndRejectsChangedRequestFingerprint()
    {
        var (_, intent, source) = await PersistOpenPullRequestIntentAsync();
        var catalog = ProviderCatalog.Create([], [], []).Value!;
        var owner = new SourceControlOwnerStore(
            _fixture.DataSource, _schema, catalog, new ProviderResolver(catalog));
        var fingerprint = MafOpenPullRequestContract.OwnerRequestFingerprint(
            intent.Identity, intent.Request);
        var effectFingerprint = MafOpenPullRequestContract.EffectRequestFingerprint(
            intent.Identity, intent.Request);
        var authorizationFingerprint = MafOpenPullRequestContract.OriginalAuthorizationFingerprint(
            intent.Identity, source.Pin.AcceptedRun.TenantId, source.Pin.AcceptedRun.Issuer,
            source.Pin.AcceptedRun.Subject, intent.Request.ExecutionBinding!);
        var first = await owner.BeginOpenPullRequestOperationAsync(
            source.Pin, intent.Identity, intent.OperationId, fingerprint, effectFingerprint,
            authorizationFingerprint, CancellationToken.None);
        var pendingReplay = await owner.BeginOpenPullRequestOperationAsync(
            source.Pin, intent.Identity, intent.OperationId, fingerprint, effectFingerprint,
            authorizationFingerprint, CancellationToken.None);
        Assert.True(first.IsNew);
        Assert.Null(first.PullRequestNumber);
        Assert.Equal("pending", first.EffectState);
        Assert.False(pendingReplay.IsNew);
        Assert.Null(pendingReplay.PullRequestNumber);

        var binding = intent.Request.ExecutionBinding!;
        var refreshedBinding = binding with
        {
            DecisionStateVersion = binding.DecisionStateVersion + 1,
            ExecutionFence = binding.ExecutionFence + 1
        };
        var refreshedRequest = intent.Request with { ExecutionBinding = refreshedBinding };
        var refreshedAuthorizationFingerprint =
            MafOpenPullRequestContract.OriginalAuthorizationFingerprint(
                intent.Identity, source.Pin.AcceptedRun.TenantId, source.Pin.AcceptedRun.Issuer,
                source.Pin.AcceptedRun.Subject, refreshedBinding);
        var pendingReauthorization = await Assert.ThrowsAsync<CoordinationException>(() =>
            owner.BeginOpenPullRequestOperationAsync(
                source.Pin,
                intent.Identity,
                intent.OperationId,
                MafOpenPullRequestContract.OwnerRequestFingerprint(intent.Identity, refreshedRequest),
                effectFingerprint,
                refreshedAuthorizationFingerprint,
                CancellationToken.None,
                requireCommittedReplay: true));
        Assert.Equal("source_control_run_binding_changed", pendingReauthorization.Code);

        var resultFingerprint = MafOpenPullRequestContract.EffectResultFingerprint(
            intent.Identity, intent.Request.ExecutionBinding!, source.Pin.Repository,
            new SourceControlPullRequest(
                42,
                new Uri("https://github.com/octo/repo/pull/42"),
                "open",
                intent.Request.HeadBranch,
                intent.Request.ExpectedHeadSha,
                intent.Request.BaseBranch,
                intent.Request.ExpectedBaseSha,
                false,
                SourceControlPullRequestDisposition.Created));
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await owner.CompleteOpenPullRequestOperationInTransactionAsync(
                connection, transaction, intent.Identity, intent.OperationId, source.Pin.PinId,
                fingerprint, resultFingerprint, 42, CancellationToken.None);
            await transaction.CommitAsync();
        }
        var completedReplay = await owner.BeginOpenPullRequestOperationAsync(
            source.Pin, intent.Identity, intent.OperationId, fingerprint, effectFingerprint,
            authorizationFingerprint, CancellationToken.None);
        Assert.False(completedReplay.IsNew);
        Assert.Equal(42, completedReplay.PullRequestNumber);
        Assert.Equal("committed", completedReplay.EffectState);
        Assert.Equal(resultFingerprint, completedReplay.ResultFingerprint);

        var changedResult = await Assert.ThrowsAsync<CoordinationException>(async () =>
        {
            await using var connection = await _fixture.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await owner.CompleteOpenPullRequestOperationInTransactionAsync(
                connection, transaction, intent.Identity, intent.OperationId, source.Pin.PinId,
                fingerprint, new string('f', 64), 43, CancellationToken.None);
            await transaction.CommitAsync();
        });
        Assert.Equal("source_control_run_binding_changed", changedResult.Code);

        var changedFingerprint = MafOpenPullRequestContract.OwnerRequestFingerprint(
            intent.Identity, intent.Request with { Title = "changed" });
        var changedEffectFingerprint = MafOpenPullRequestContract.EffectRequestFingerprint(
            intent.Identity, intent.Request with { Title = "changed" });
        var changed = await Assert.ThrowsAsync<CoordinationException>(() =>
            owner.BeginOpenPullRequestOperationAsync(
                source.Pin, intent.Identity, intent.OperationId, changedFingerprint, changedEffectFingerprint,
                authorizationFingerprint, CancellationToken.None));
        Assert.Equal("source_control_run_binding_changed", changed.Code);

        Assert.Equal(effectFingerprint,
            MafOpenPullRequestContract.EffectRequestFingerprint(intent.Identity, refreshedRequest));
        var changedBinding = await Assert.ThrowsAsync<CoordinationException>(() =>
            owner.BeginOpenPullRequestOperationAsync(
                source.Pin,
                intent.Identity,
                intent.OperationId,
                MafOpenPullRequestContract.OwnerRequestFingerprint(intent.Identity, refreshedRequest),
                effectFingerprint,
                refreshedAuthorizationFingerprint,
                CancellationToken.None));
        Assert.Equal("source_control_run_binding_changed", changedBinding.Code);

        var refreshedReplay = await owner.BeginOpenPullRequestOperationAsync(
            source.Pin,
            intent.Identity,
            intent.OperationId,
            MafOpenPullRequestContract.OwnerRequestFingerprint(intent.Identity, refreshedRequest),
            effectFingerprint,
            refreshedAuthorizationFingerprint,
            CancellationToken.None,
            requireCommittedReplay: true);
        Assert.False(refreshedReplay.IsNew);
        Assert.Equal(42, refreshedReplay.PullRequestNumber);
        Assert.Equal(resultFingerprint, refreshedReplay.ResultFingerprint);

        await using var verify = await _fixture.DataSource.OpenConnectionAsync();
        await using var verifyOperation = new NpgsqlCommand($"""
            SELECT request_fingerprint, effect_fingerprint, original_authorization_fingerprint
            FROM "{_schema}".source_control_pull_request_operations
            WHERE project_id = @project AND run_id = @run AND session_id = @session
              AND operation_id = @operation
            """, verify);
        verifyOperation.Parameters.AddWithValue("project", intent.Identity.ProjectId);
        verifyOperation.Parameters.AddWithValue("run", intent.Identity.RunId);
        verifyOperation.Parameters.AddWithValue("session", intent.Identity.SessionId);
        verifyOperation.Parameters.AddWithValue("operation", intent.OperationId);
        await using var verifyReader = await verifyOperation.ExecuteReaderAsync();
        Assert.True(await verifyReader.ReadAsync());
        Assert.Equal(fingerprint, verifyReader.GetString(0).TrimEnd());
        Assert.Equal(effectFingerprint, verifyReader.GetString(1).TrimEnd());
        Assert.Equal(authorizationFingerprint, verifyReader.GetString(2).TrimEnd());
    }

    [Fact]
    public async Task OpenPullRequestEffectIdentityDoesNotChangeWithAuthorizationFence()
    {
        var (_, intent, _) = await PersistOpenPullRequestIntentAsync();
        var binding = intent.Request.ExecutionBinding!;
        var refreshedRequest = intent.Request with
        {
            ExecutionBinding = binding with
            {
                DecisionStateVersion = binding.DecisionStateVersion + 1,
                ExecutionFence = binding.ExecutionFence + 1
            }
        };

        Assert.Equal(
            MafOpenPullRequestContract.EffectRequestFingerprint(intent.Identity, intent.Request),
            MafOpenPullRequestContract.EffectRequestFingerprint(intent.Identity, refreshedRequest));
        Assert.NotEqual(
            MafOpenPullRequestContract.OwnerRequestFingerprint(intent.Identity, intent.Request),
            MafOpenPullRequestContract.OwnerRequestFingerprint(intent.Identity, refreshedRequest));
        Assert.NotEqual(
            MafOpenPullRequestContract.OriginalAuthorizationFingerprint(
                intent.Identity, "tenant-1", "issuer", "subject", binding),
            MafOpenPullRequestContract.OriginalAuthorizationFingerprint(
                intent.Identity, "tenant-1", "issuer", "subject", refreshedRequest.ExecutionBinding!));
    }

    private async Task<(MafExecutionCheckpointSnapshot Running,
        MafExecutionOpenPullRequestIntent Intent,
        SourceControlRunContext Source)>
        PersistOpenPullRequestIntentAsync()
    {
        var source = CreateCurrentSourceControlRun();
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var initialProgress = MafExecutionProgress.Empty with
        {
            NonModelSteps = MafExecutionProgress.Empty.NonModelSteps.Add(
                "publish", MafExecutionTaskStatus.Pending)
        };
        var initial = await _executionStore.AppendAsync(
            "open-pr-initial",
            null,
            new(1, "plan-1", 1, initialProgress),
            CancellationToken.None);
        var workspace = new SourceControlPullRequestWorkspaceLocator(
            "workspace",
            Guid.Parse("5956a121-5556-4c16-848c-0d4a2937966d"),
            new string('a', 40),
            "feature/agent-change",
            new string('c', 40),
            source.Pin.DefaultBranch);
        var intent = MafOpenPullRequestContract.CreateIntent(
            Guid.Parse("c252ff87-659a-45dc-a307-41369cbe61f7"),
            _binding.Identity,
            "open-pr-intent",
            2,
            "plan-1",
            "publish",
            1,
            _binding.ExecutionFence,
            selectionHash,
            source.Pin,
            workspace,
            1);
        var next = CoordinationEndpoints.PrepareOpenPullRequestCheckpoint(initial, intent)!;
        var running = await _executionStore.AppendAsync(
            intent.CheckpointId, initial.Info, next, CancellationToken.None);
        return (running, intent, source);
    }

    private SourceControlRunContext CreateCurrentSourceControlRun()
    {
        var catalog = CreateChildRecoverySourceControlCatalog(out var capabilities);
        var resolver = new ProviderResolver(catalog);
        var candidate = resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.SourceControl, null, new(1, 0, 0), 1,
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal))).Value!.Candidate!;
        var resource = new ProviderResourceRef(ProviderSeam.SourceControl, "github", "repository", 1);
        var providerBinding = resolver.Pin(
            _binding.Identity.RunId, candidate, resource.ResourceId,
            new ResourceNegotiation(resource, capabilities)).Value!;
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var acceptedRun = new SourceControlAcceptedRunBinding(
            _actor.Issuer,
            _actor.Subject,
            _selection.Authorization.TenantId,
            _binding.Identity.ProjectId,
            _binding.Identity.RunId,
            _binding.Identity.SessionId,
            selectionHash,
            _selection.Selection.ProjectRevision,
            _selection.Selection.ProjectConfigurationRevision,
            _selection.Selection.PlatformRuntimeRevision,
            _selection.Selection.ContextRevision,
            _binding.ExecutionFence);
        var pin = new SourceControlRepositoryPin(
            SourceControlOwnerStore.CreateRepositoryPinId(acceptedRun),
            acceptedRun,
            providerBinding,
            new("octo", "repo"),
            new(new SecretRef("api", "1"), SourceControlSecretPurposes.Api),
            new(new SecretRef("checkout", "1"), SourceControlSecretPurposes.Checkout),
            null,
            123,
            "main",
            false,
            DateTimeOffset.UnixEpoch);
        return new(_actor, _binding.Identity, _selection, pin, 1);
    }

    private static ProviderCatalog CreateChildRecoverySourceControlCatalog(
        out ImmutableHashSet<string> capabilities)
    {
        capabilities = ImmutableHashSet.Create(StringComparer.Ordinal,
            SourceControlCapabilities.RepositoryRead,
            SourceControlCapabilities.RepositoryCheckout,
            SourceControlCapabilities.PullRequestRead,
            SourceControlCapabilities.PullRequestWrite);
        return ProviderCatalog.Create(
            [new ProviderRegistration(new ProviderDescriptor(
                ProviderSeam.SourceControl, "github", new(1, 0, 0), 1,
                ProviderHostingPattern.InProcess, capabilities), true, "source-v1", 1)],
            [new ProviderSelection(ProviderSeam.SourceControl, "github")], []).Value!;
    }

    private async Task<string> CommitOpenPullRequestOperationAsync(
        MafExecutionOpenPullRequestIntent intent,
        SourceControlRepositoryPin pin)
    {
        var catalog = ProviderCatalog.Create([], [], []).Value!;
        var owner = new SourceControlOwnerStore(
            _fixture.DataSource, _schema, catalog, new ProviderResolver(catalog));
        var requestFingerprint = MafOpenPullRequestContract.OwnerRequestFingerprint(
            intent.Identity, intent.Request);
        var effectFingerprint = MafOpenPullRequestContract.EffectRequestFingerprint(
            intent.Identity, intent.Request);
        var authorizationFingerprint = MafOpenPullRequestContract.OriginalAuthorizationFingerprint(
            intent.Identity,
            pin.AcceptedRun.TenantId,
            pin.AcceptedRun.Issuer,
            pin.AcceptedRun.Subject,
            intent.Request.ExecutionBinding!);
        var claim = await owner.BeginOpenPullRequestOperationAsync(
            pin,
            intent.Identity,
            intent.OperationId,
            requestFingerprint,
            effectFingerprint,
            authorizationFingerprint,
            CancellationToken.None);
        Assert.True(claim.IsNew);
        var pullRequest = new SourceControlPullRequest(
            42,
            new Uri("https://github.com/octo/repo/pull/42"),
            "open",
            intent.Request.HeadBranch,
            intent.Request.ExpectedHeadSha,
            intent.Request.BaseBranch,
            intent.Request.ExpectedBaseSha,
            false,
            SourceControlPullRequestDisposition.Created);
        var resultFingerprint = MafOpenPullRequestContract.EffectResultFingerprint(
            intent.Identity, intent.Request.ExecutionBinding!, pin.Repository, pullRequest);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await owner.CompleteOpenPullRequestOperationInTransactionAsync(
            connection,
            transaction,
            intent.Identity,
            intent.OperationId,
            pin.PinId,
            requestFingerprint,
            resultFingerprint,
            pullRequest.Number,
            CancellationToken.None);
        await transaction.CommitAsync();
        return resultFingerprint;
    }

    private static SourceControlRepositoryPin WithPinId(
        SourceControlRepositoryPin pin,
        string pinId) =>
        new(
            pinId,
            pin.AcceptedRun,
            pin.ProviderBinding,
            pin.Repository,
            pin.ApiCredential,
            pin.CheckoutCredential,
            pin.WebhookCredential,
            pin.ProviderRepositoryId,
            pin.DefaultBranch,
            pin.IsPrivate,
            pin.PinnedAt,
            pin.GitHubAppBinding);

    private (MafOpenPullRequestExecutor Executor, HttpClient Client) CreateDispatchExecutor(
        MafExecutionOpenPullRequestIntent intent,
        SourceControlRunContext source,
        DispatchOwnerHandler owner,
        Func<SourceControlRepositoryPin>? currentPin = null,
        Func<long>? currentDecisionVersion = null,
        Func<MafExecutionOpenPullRequestIntent, MafExecutionOpenPullRequestIntent>? authorizedIntent = null)
    {
        currentPin ??= () => source.Pin;
        currentDecisionVersion ??= () => intent.DecisionStateVersion;
        var client = new HttpClient(owner) { Timeout = TimeSpan.FromSeconds(2) };
        var sourceControl = new MafSourceControlOwnerClient(
            client, new MafSourceControlOwnerOptions(new Uri("https://orchestrator.example/")));
        var execution = new MafOpenPullRequestExecutor(
            _executionStore,
            sourceControl,
            _fixture.DataSource,
            _ => Task.FromResult(new RuntimeActorAuthorization(
                new SecretCredential("test-owner-token", DateTimeOffset.UtcNow.AddMinutes(1)),
                "tenant-1")),
            (currentIntent, _) =>
            {
                MafOpenPullRequestContract.RequireCurrentSource(
                    currentIntent, source.Actor, currentIntent.Identity, "tenant-1",
                    currentDecisionVersion(), currentIntent.ExecutionFence,
                    currentIntent.AcceptedSelectionHash, currentPin());
                return Task.FromResult(authorizedIntent?.Invoke(currentIntent) ?? currentIntent);
            },
            async (_, _, token) =>
            {
                var latest = await _executionStore.ReadLatestAsync(token);
                Assert.NotNull(latest);
                Assert.Equal(MafExecutionTaskStatus.Running,
                    latest.State.Progress.NonModelSteps["publish"]);
            },
            () => false,
            () => { },
            _ => { });
        return (execution, client);
    }

    private async Task SetPullRequestTerminalCheckpointFailureAsync(bool enabled)
    {
        var trigger = $"\"{_schema}\".fail_open_pull_request_terminal";
        var table = $"\"{_schema}\".maf_workflow_checkpoints";
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        var sql = enabled
            ? $"""
                CREATE FUNCTION {trigger}() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF position('"pullRequestNumber"' in NEW.payload::text) > 0 THEN
                        RAISE EXCEPTION 'intentional OpenPullRequest checkpoint failure';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER fail_open_pull_request_terminal
                    BEFORE INSERT ON {table}
                    FOR EACH ROW EXECUTE FUNCTION {trigger}();
                """
            : $"""
                DROP TRIGGER IF EXISTS fail_open_pull_request_terminal ON {table};
                DROP FUNCTION IF EXISTS {trigger}();
                """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class DispatchOwnerHandler(
        MafExecutionOpenPullRequestIntent intent,
        Action? afterDiff = null,
        string? diffHeadSha = null)
        : HttpMessageHandler
    {
        private int _pullRequestRequests;
        private int _diffRequests;
        private int _providerCreates;

        public int PullRequestRequests => Volatile.Read(ref _pullRequestRequests);
        public int DiffRequests => Volatile.Read(ref _diffRequests);
        public int ProviderCreates => Volatile.Read(ref _providerCreates);
        public bool ReturnExistingPullRequest { get; set; }
        public long? LastExecutionFence { get; private set; }
        public bool CloseOnReplay { get; set; }
        public bool FailPullRequest { get; set; }
        public ConcurrentBag<long> ExecutionFences { get; } = [];
        public ConcurrentBag<Guid> OperationIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/diff", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _diffRequests);
                afterDiff?.Invoke();
                var payload = JsonSerializer.Serialize(new
                {
                    workspaceId = intent.Workspace.WorkspaceId,
                    baseSha = intent.Workspace.BaseSha,
                    headSha = diffHeadSha ?? intent.Workspace.HeadSha,
                    status = string.Empty,
                    patch = string.Empty,
                    workspaceIncarnationId = intent.Workspace.WorkspaceIncarnationId
                });
                return Response(payload, request);
            }

            Assert.EndsWith("/source-control/sessions/root/pull-requests", path, StringComparison.Ordinal);
            using var body = await JsonDocument.ParseAsync(
                await request.Content!.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var operationId = body.RootElement.GetProperty("executionBinding")
                .GetProperty("operationId").GetGuid();
            var executionFence = body.RootElement.GetProperty("executionBinding")
                .GetProperty("executionFence").GetInt64();
            LastExecutionFence = executionFence;
            ExecutionFences.Add(executionFence);
            OperationIds.Add(operationId);
            Interlocked.Increment(ref _pullRequestRequests);
            if (FailPullRequest)
                return Response(
                    """{"error":"source_control_run_binding_changed"}""",
                    request,
                    HttpStatusCode.Conflict);
            var created = !ReturnExistingPullRequest &&
                Interlocked.CompareExchange(ref _providerCreates, 1, 0) == 0;
            var payloadBody = JsonSerializer.Serialize(new
            {
                number = 42,
                htmlUrl = "https://github.com/octo/repo/pull/42",
                state = CloseOnReplay && !created ? "closed" : "open",
                headBranch = intent.Request.HeadBranch,
                headSha = intent.Request.ExpectedHeadSha,
                baseBranch = intent.Request.BaseBranch,
                baseSha = intent.Request.ExpectedBaseSha,
                merged = false,
                disposition = created ? "created" : "reused",
                title = intent.Request.Title
            });
            return Response(payloadBody, request);
        }

        private static HttpResponseMessage Response(
            string payload,
            HttpRequestMessage request,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return response;
        }
    }

    private sealed class FixedOwnerResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/source-control/sessions/root/pull-requests",
                request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var response = new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                RequestMessage = request,
                Content = new StringContent(
                    """{"error":"source_control_run_binding_changed"}""",
                    Encoding.UTF8,
                    "application/json")
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        }
    }
}
