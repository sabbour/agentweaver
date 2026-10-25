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
        var first = await owner.BeginOpenPullRequestOperationAsync(
            source.Pin, intent.Identity, intent.OperationId, fingerprint, CancellationToken.None);
        var pendingReplay = await owner.BeginOpenPullRequestOperationAsync(
            source.Pin, intent.Identity, intent.OperationId, fingerprint, CancellationToken.None);
        Assert.Equal(new(true, null), first);
        Assert.Equal(new(false, null), pendingReplay);

        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await owner.CompleteOpenPullRequestOperationInTransactionAsync(
                connection, transaction, intent.Identity, intent.OperationId, source.Pin.PinId,
                fingerprint, 42, CancellationToken.None);
            await transaction.CommitAsync();
        }
        var completedReplay = await owner.BeginOpenPullRequestOperationAsync(
            source.Pin, intent.Identity, intent.OperationId, fingerprint, CancellationToken.None);
        Assert.Equal(new(false, 42), completedReplay);

        var changedFingerprint = MafOpenPullRequestContract.OwnerRequestFingerprint(
            intent.Identity, intent.Request with { Title = "changed" });
        var changed = await Assert.ThrowsAsync<CoordinationException>(() =>
            owner.BeginOpenPullRequestOperationAsync(
                source.Pin, intent.Identity, intent.OperationId, changedFingerprint, CancellationToken.None));
        Assert.Equal("source_control_run_binding_changed", changed.Code);
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
        var capabilities = ImmutableHashSet.Create(StringComparer.Ordinal,
            SourceControlCapabilities.RepositoryRead,
            SourceControlCapabilities.RepositoryCheckout,
            SourceControlCapabilities.PullRequestRead,
            SourceControlCapabilities.PullRequestWrite);
        var catalog = ProviderCatalog.Create(
            [new ProviderRegistration(new ProviderDescriptor(
                ProviderSeam.SourceControl, "github", new(1, 0, 0), 1,
                ProviderHostingPattern.InProcess, capabilities), true, "source-v1", 1)],
            [new ProviderSelection(ProviderSeam.SourceControl, "github")], []).Value!;
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
        Func<long>? currentDecisionVersion = null)
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
                return Task.CompletedTask;
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
        public int PullRequestRequests { get; private set; }
        public int ProviderCreates { get; private set; }
        public bool CloseOnReplay { get; set; }
        public bool FailPullRequest { get; set; }
        public List<Guid> OperationIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/diff", StringComparison.Ordinal))
            {
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
                return Response(payload);
            }

            Assert.EndsWith("/source-control/sessions/root/pull-requests", path, StringComparison.Ordinal);
            using var body = await JsonDocument.ParseAsync(
                await request.Content!.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var operationId = body.RootElement.GetProperty("executionBinding")
                .GetProperty("operationId").GetGuid();
            OperationIds.Add(operationId);
            PullRequestRequests++;
            if (FailPullRequest)
                return Response(
                    """{"error":"source_control_run_binding_changed"}""",
                    HttpStatusCode.Conflict);
            var created = ProviderCreates == 0;
            if (created)
                ProviderCreates++;
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
            return Response(payloadBody);
        }

        private static HttpResponseMessage Response(
            string payload,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            var response = new HttpResponseMessage(statusCode)
            {
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
