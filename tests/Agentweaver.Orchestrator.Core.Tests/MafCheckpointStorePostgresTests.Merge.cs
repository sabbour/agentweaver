using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed partial class MafCheckpointStorePostgresTests
{
    [Fact]
    public async Task PostgresMergeApprovalPendingKeepsCheckpointRunningAndNoReceipt()
    {
        var (_, intent, source) = await PersistMergeIntentAsync();
        var owner = new MergeOwnerHandler(intent) { ApprovalPending = true };
        var (executor, client) = CreateMergeExecutor(intent, source, owner);
        using (client)
        {
            var outcome = await executor.ExecuteAsync(intent, CancellationToken.None);

            Assert.Equal(MafMergeExecutionOutcome.AwaitingApproval, outcome);
            Assert.Equal(intent.OwnerIntentId, owner.LastIntentId);
            Assert.Equal(intent.ApprovalRequestId, owner.LastApprovalRequestId);
            Assert.Equal(0, owner.MergeEffects);
            var latest = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(latest);
            Assert.Equal(MafExecutionTaskStatus.Running, latest.State.Progress.NonModelSteps["merge"]);
            Assert.Empty(latest.State.MergeReceipts);
        }
    }

    [Fact]
    public async Task PostgresMergeRecoveryAfterCheckpointRollbackReusesOwnerResultWithoutSecondMerge()
    {
        var (_, intent, source) = await PersistMergeIntentAsync();
        var owner = new MergeOwnerHandler(intent);
        var (executor, client) = CreateMergeExecutor(intent, source, owner);
        using (client)
        {
            await SetMergeTerminalCheckpointFailureAsync(true);
            try
            {
                await Assert.ThrowsAsync<PostgresException>(() =>
                    executor.ExecuteAsync(intent, CancellationToken.None));
            }
            finally
            {
                await SetMergeTerminalCheckpointFailureAsync(false);
            }

            var afterRollback = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(afterRollback);
            Assert.Equal(MafExecutionTaskStatus.Running, afterRollback.State.Progress.NonModelSteps["merge"]);
            Assert.Empty(afterRollback.State.MergeReceipts);
            Assert.Equal(1, owner.MergeEffects);
            Assert.Equal(1, owner.PrepareRequests);

            Assert.Equal(
                MafMergeExecutionOutcome.Completed,
                await executor.ExecuteAsync(intent, CancellationToken.None));
            var recovered = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(recovered);
            Assert.Equal(MafExecutionTaskStatus.Succeeded, recovered.State.Progress.NonModelSteps["merge"]);
            Assert.Equal(new string('b', 40), recovered.State.MergeReceipts["merge"].MergeSha);
            Assert.Equal(1, owner.MergeEffects);
            Assert.Equal(2, owner.PrepareRequests);
            Assert.Equal(1, owner.ExecuteRequests);

            Assert.Equal(
                MafMergeExecutionOutcome.NoProgress,
                await executor.ExecuteAsync(intent, CancellationToken.None));
            Assert.Equal(1, owner.MergeEffects);
            Assert.Equal(2, owner.PrepareRequests);
            Assert.Equal(1, owner.ExecuteRequests);
        }
    }

    [Fact]
    public async Task PostgresMergeRejectsChangedOwnerHeadBeforeCallingExecuteRoute()
    {
        var (_, intent, source) = await PersistMergeIntentAsync();
        var owner = new MergeOwnerHandler(intent)
        {
            PreparedHeadSha = new string('d', 40)
        };
        var (executor, client) = CreateMergeExecutor(intent, source, owner);
        using (client)
        {
            var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
                executor.ExecuteAsync(intent, CancellationToken.None));

            Assert.Equal("source_control_merge_response_invalid", failure.Code);
            Assert.Equal(0, owner.ExecuteRequests);
            var latest = await _executionStore.ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(latest);
            Assert.Equal(MafExecutionTaskStatus.Running, latest.State.Progress.NonModelSteps["merge"]);
            Assert.Empty(latest.State.MergeReceipts);
        }
    }

    private async Task<(MafExecutionCheckpointSnapshot Running,
        MafExecutionMergeIntent Intent,
        SourceControlRunContext Source)> PersistMergeIntentAsync()
    {
        // Start from a real PostgreSQL checkpoint so restart and rollback behavior exercise the production store.
        var source = CreateCurrentSourceControlRun();
        var initialProgress = MafExecutionProgress.Empty with
        {
            NonModelSteps = MafExecutionProgress.Empty.NonModelSteps.Add(
                "merge", MafExecutionTaskStatus.Pending)
        };
        var initial = await _executionStore.AppendAsync(
            "merge-initial",
            null,
            new(1, "plan-1", 1, initialProgress),
            CancellationToken.None);
        var intent = MafMergeContract.CreateIntent(
            Guid.Parse("c252ff87-659a-45dc-a307-41369cbe61f7"),
            _binding.Identity,
            "merge-intent",
            2,
            "plan-1",
            "workflow.core",
            "revision-1",
            "merge",
            1,
            _binding.ExecutionFence,
            CoordinationOwnerStore.HashSelection(_selection.Selection),
            source.Pin,
            42,
            new string('a', 40),
            SourceControlMergeMethod.Rebase);
        var next = CoordinationEndpoints.PrepareMergeCheckpoint(initial, intent)!;
        var running = await _executionStore.AppendAsync(
            intent.CheckpointId, initial.Info, next, CancellationToken.None);
        return (running, intent, source);
    }

    private (MafMergeExecutor Executor, HttpClient Client) CreateMergeExecutor(
        MafExecutionMergeIntent intent,
        SourceControlRunContext source,
        MergeOwnerHandler owner)
    {
        // Only the checkpoint and owner HTTP boundary are real here; the test isolates merge recovery semantics.
        var client = new HttpClient(owner) { Timeout = TimeSpan.FromSeconds(2) };
        var sourceControl = new MafSourceControlOwnerClient(
            client, new MafSourceControlOwnerOptions(new Uri("https://orchestrator.example/")));
        var executor = new MafMergeExecutor(
            _executionStore,
            sourceControl,
            _fixture.DataSource,
            _ => Task.FromResult(new RuntimeActorAuthorization(
                new SecretCredential("test-owner-token", DateTimeOffset.UtcNow.AddMinutes(1)),
                "tenant-1")),
            (currentIntent, _) =>
            {
                Assert.Equal(intent.Identity, currentIntent.Identity);
                Assert.Equal(source.Pin.PinId, currentIntent.SourceControlPinId);
                return Task.CompletedTask;
            },
            (_, _, _) => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (_, _) => { });
        return (executor, client);
    }

    private async Task SetMergeTerminalCheckpointFailureAsync(bool enabled)
    {
        // Fail only the terminal-receipt append so the test can recover an already-applied owner effect.
        var trigger = $"\"{_schema}\".fail_merge_terminal";
        var table = $"\"{_schema}\".maf_workflow_checkpoints";
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        var sql = enabled
            ? $"""
                CREATE FUNCTION {trigger}() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF position('"mergeSha"' in NEW.payload::text) > 0 THEN
                        RAISE EXCEPTION 'intentional Merge checkpoint failure';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER fail_merge_terminal
                    BEFORE INSERT ON {table}
                    FOR EACH ROW EXECUTE FUNCTION {trigger}();
                """
            : $"""
                DROP TRIGGER IF EXISTS fail_merge_terminal ON {table};
                DROP FUNCTION IF EXISTS {trigger}();
                """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class MergeOwnerHandler(MafExecutionMergeIntent intent) : HttpMessageHandler
    {
        public bool ApprovalPending { get; init; }
        public string? PreparedHeadSha { get; init; }
        public int PrepareRequests { get; private set; }
        public int ExecuteRequests { get; private set; }
        public int MergeEffects { get; private set; }
        public string? LastIntentId { get; private set; }
        public string? LastApprovalRequestId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // An exact idempotency replay returns the durable merge result instead of invoking the effect again.
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(HttpMethod.Post, request.Method);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/merge-intents", StringComparison.Ordinal))
            {
                PrepareRequests++;
                using var body = await JsonDocument.ParseAsync(
                    await request.Content!.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
                Assert.Equal(intent.IdempotencyKey, body.RootElement.GetProperty("idempotencyKey").GetString());
                Assert.Equal(intent.ExpectedHeadSha, body.RootElement.GetProperty("expectedHeadSha").GetString());
                LastIntentId = intent.OwnerIntentId;
                LastApprovalRequestId = intent.ApprovalRequestId;
                if (ApprovalPending)
                    return Response(JsonSerializer.Serialize(new
                    {
                        intentId = intent.OwnerIntentId,
                        approvalRequestId = intent.ApprovalRequestId,
                        state = "approval_pending"
                    }), request, HttpStatusCode.Accepted);

                var state = MergeEffects == 0 ? "approved" : "merged";
                return Response(JsonSerializer.Serialize(new
                {
                    intentId = intent.OwnerIntentId,
                    approvalRequestId = intent.ApprovalRequestId,
                    state,
                    repository = intent.Repository.FullName,
                    pullRequestNumber = intent.PullRequestNumber,
                    headBranch = "feature",
                    headSha = PreparedHeadSha ?? intent.ExpectedHeadSha,
                    baseBranch = "main",
                    baseSha = new string('c', 40),
                    method = intent.Method,
                    approvalDecisionId = Guid.Parse("daab7d19-85f9-4e68-9bc0-7d3533f355e6"),
                    approvalStateVersion = intent.DecisionStateVersion,
                    grantReference = new { kind = "merge-approval" },
                    mergeSha = state == "merged" ? new string('b', 40) : null,
                    lastFailureCode = (string?)null
                }), request);
            }

            Assert.EndsWith("/merge-intents/" + intent.OwnerIntentId + "/execute",
                path, StringComparison.Ordinal);
            ExecuteRequests++;
            MergeEffects++;
            return Response(JsonSerializer.Serialize(new
            {
                intentId = intent.OwnerIntentId,
                state = "merged",
                mergeSha = new string('b', 40),
                failureCode = (string?)null
            }), request);
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
}
