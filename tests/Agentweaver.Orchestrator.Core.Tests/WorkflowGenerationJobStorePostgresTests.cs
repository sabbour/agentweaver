using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class WorkflowGenerationJobStorePostgresTests(CoordinationPostgresFixture fixture) : IAsyncLifetime
{
    private const string ProjectId = "workflow-generation-project";
    private const string RunId = "workflow-generation-run";
    private const string TenantId = "workflow-generation-tenant";
    private const string ActorIssuer = "https://identity.example/";
    private const string ActorId = "6294c6de-f698-43d2-8dcb-d29cb289e671";
    private readonly string _schema = "workflow_generation_" + Guid.NewGuid().ToString("N");
    private WorkflowGenerationJobStore _store = null!;

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        _store = new WorkflowGenerationJobStore(fixture.DataSource, _schema);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".accepted_runs
                (project_id, run_id, accepted_selection, accepted_selection_hash,
                 accepted_by_issuer, accepted_by_subject, execution_state)
            VALUES (@project, @run, jsonb_build_object(), repeat('a', 64), @issuer, @actor, 'active')
            """, connection);
        command.Parameters.AddWithValue("project", ProjectId);
        command.Parameters.AddWithValue("run", RunId);
        command.Parameters.AddWithValue("issuer", ActorIssuer);
        command.Parameters.AddWithValue("actor", ActorId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ConcurrentRetryAndLostAcceptanceReplyReuseOneJobAndNativeRequest()
    {
        var create = Create("same-key");
        // Each production request proposes fresh candidate IDs for the same logical request.
        var candidates = Enumerable.Range(0, 8).Select(_ => WithFreshCandidateIds(create)).ToArray();
        Assert.Equal(candidates.Length, candidates.Select(candidate => candidate.JobId).Distinct().Count());
        Assert.Equal(candidates.Length, candidates.Select(candidate => candidate.NativeRequestId).Distinct().Count());

        Task<WorkflowGenerationCreateResult[]> attempts;
        await using (var blocker = await fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await blocker.BeginTransactionAsync())
        {
            await using (var lockRun = new NpgsqlCommand($"""
                SELECT 1 FROM "{_schema}".accepted_runs
                WHERE project_id = @project AND run_id = @run FOR UPDATE
                """, blocker, transaction))
            {
                lockRun.Parameters.AddWithValue("project", ProjectId);
                lockRun.Parameters.AddWithValue("run", RunId);
                await lockRun.ExecuteNonQueryAsync();
            }

            attempts = Task.WhenAll(candidates.Select(candidate =>
                _store.CreateOrGetAsync(candidate, CancellationToken.None)));
            try
            {
                await WaitForBlockedJobInsertsAsync(blocker.ProcessID, minimum: 2);
                Assert.False(attempts.IsCompleted);
            }
            finally
            {
                await transaction.RollbackAsync();
                await Task.WhenAny(attempts);
            }
        }
        var responses = await attempts;

        var winner = Assert.Single(responses
            .Select(response => (response.Job.JobId, response.Job.NativeRequestId))
            .Distinct());
        Assert.Contains(candidates, candidate =>
            candidate.JobId == winner.JobId && candidate.NativeRequestId == winner.NativeRequestId);
        Assert.Single(responses, response => response.Disposition == WorkflowGenerationCreateDisposition.Created);
        Assert.Equal(7, responses.Count(response =>
            response.Disposition == WorkflowGenerationCreateDisposition.Existing));
        Assert.Equal(winner, Assert.Single(await ReadPersistedJobIdsAsync("same-key")));

        var lostReplyRetry = await _store.CreateOrGetAsync(WithFreshCandidateIds(create), CancellationToken.None);
        Assert.Equal(WorkflowGenerationCreateDisposition.Existing, lostReplyRetry.Disposition);
        Assert.Equal(winner.JobId, lostReplyRetry.Job.JobId);
        Assert.Equal(winner.NativeRequestId, lostReplyRetry.Job.NativeRequestId);
        Assert.Equal(winner, Assert.Single(await ReadPersistedJobIdsAsync("same-key")));
    }

    private static WorkflowGenerationJobCreate WithFreshCandidateIds(WorkflowGenerationJobCreate create) =>
        create with { JobId = Guid.NewGuid(), NativeRequestId = Guid.NewGuid() };

    private async Task WaitForBlockedJobInsertsAsync(int blockerProcessId, int minimum)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var observer = await fixture.DataSource.OpenConnectionAsync(deadline.Token);
        while (true)
        {
            await using var blocked = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity
                WHERE @blocker = ANY(pg_blocking_pids(pid))
                  AND query LIKE @table
                """, observer);
            blocked.Parameters.AddWithValue("blocker", blockerProcessId);
            blocked.Parameters.AddWithValue("table", $"%\"{_schema}\".workflow_generation_jobs%");
            if ((long)(await blocked.ExecuteScalarAsync(deadline.Token))! >= minimum)
                return;
            await Task.Delay(20, deadline.Token);
        }
    }

    private async Task<List<(Guid JobId, Guid NativeRequestId)>> ReadPersistedJobIdsAsync(string idempotencyKey)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT job_id, native_request_id FROM "{_schema}".workflow_generation_jobs
            WHERE tenant_id = @tenant AND project_id = @project AND actor_issuer = @issuer
              AND actor_id = @actor AND idempotency_key = @key
            """, connection);
        command.Parameters.AddWithValue("tenant", TenantId);
        command.Parameters.AddWithValue("project", ProjectId);
        command.Parameters.AddWithValue("issuer", ActorIssuer);
        command.Parameters.AddWithValue("actor", ActorId);
        command.Parameters.AddWithValue("key", idempotencyKey);
        var rows = new List<(Guid JobId, Guid NativeRequestId)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetGuid(0), reader.GetGuid(1)));
        return rows;
    }

    [Fact]
    public async Task ChangedPayloadConflictsAndForeignOwnerLookupDoesNotFindJob()
    {
        var create = Create("same-key");
        var job = (await _store.CreateOrGetAsync(create, CancellationToken.None)).Job;
        var changed = create with
        {
            Prompt = "A different workflow request.",
            PromptSha256 = WorkflowGenerationJobStore.Hash("A different workflow request."),
            RequestFingerprint = WorkflowGenerationJobStore.Hash("different request")
        };

        var conflict = await _store.CreateOrGetAsync(changed, CancellationToken.None);

        Assert.Equal(WorkflowGenerationCreateDisposition.Conflict, conflict.Disposition);
        Assert.Equal(job.JobId, conflict.Job.JobId);
        Assert.Null(await _store.ReadByIdempotencyKeyAsync(
            TenantId, ProjectId, ActorIssuer, "another-actor", "same-key", CancellationToken.None));
        Assert.Null(await _store.ReadByIdempotencyKeyAsync(
            TenantId, "another-project", ActorIssuer, ActorId, "same-key", CancellationToken.None));
    }

    [Fact]
    public async Task StatusAndCancellationKeepAdmittedWorkUnresolvedUntilItQuiesces()
    {
        var failedJob = (await _store.CreateOrGetAsync(Create("failed"), CancellationToken.None)).Job;
        var running = await _store.TryBeginExecutionAsync(ProjectId, failedJob.JobId, CancellationToken.None);
        Assert.Equal("running", running!.Status);
        await _store.FailAsync(
            ProjectId, failedJob.JobId, "workflow_generation_failed", "The request failed.", true,
            CancellationToken.None);
        var failedRead = await _store.ReadAsync(ProjectId, failedJob.JobId, CancellationToken.None);
        Assert.Equal("failed", failedRead!.Status);
        Assert.Equal("workflow_generation_failed",
            WorkflowGenerationExecution.CreateEnvelope(failedRead).Failure!.Code);

        var queued = (await _store.CreateOrGetAsync(Create("queued"), CancellationToken.None)).Job;
        var admitted = (await _store.CreateOrGetAsync(Create("admitted"), CancellationToken.None)).Job;
        await _store.TryBeginExecutionAsync(ProjectId, admitted.JobId, CancellationToken.None);
        Assert.True(await _store.MarkSendAdmittedAsync(ProjectId, admitted.JobId, CancellationToken.None));
        await CancelRunAsync();

        var queuedRead = await _store.ReadAsync(ProjectId, queued.JobId, CancellationToken.None);
        var admittedRead = await _store.ReadAsync(ProjectId, admitted.JobId, CancellationToken.None);
        Assert.Equal("cancelled", queuedRead!.Status);
        Assert.Equal("quiesced", queuedRead.ExecutionPhase);
        Assert.Equal("cancelled", admittedRead!.Status);
        Assert.Equal("send-admitted", admittedRead.ExecutionPhase);
        Assert.True(await _store.HasUnresolvedNativeWorkAsync(ProjectId, RunId, CancellationToken.None));
        Assert.False(WorkflowGenerationExecution.CanEnqueue(admittedRead));

        await _store.MarkQuiescedAsync(ProjectId, admitted.JobId, CancellationToken.None);
        Assert.False(await _store.HasUnresolvedNativeWorkAsync(ProjectId, RunId, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationPreventsReadmissionOfAnIndeterminateJob()
    {
        var job = (await _store.CreateOrGetAsync(Create("uncertain-cancel"), CancellationToken.None)).Job;
        await _store.TryBeginExecutionAsync(ProjectId, job.JobId, CancellationToken.None);
        Assert.True(await _store.MarkSendAdmittedAsync(ProjectId, job.JobId, CancellationToken.None));
        await _store.FailAsync(
            ProjectId, job.JobId, "workflow_generation_outcome_indeterminate", "The outcome is not known.",
            quiesced: false, CancellationToken.None);

        await CancelRunAsync();

        var cancelled = await _store.ReadAsync(ProjectId, job.JobId, CancellationToken.None);
        Assert.Equal("cancelled", cancelled!.Status);
        Assert.Equal("send-admitted", cancelled.ExecutionPhase);
        Assert.False(WorkflowGenerationExecution.CanEnqueue(cancelled));
        Assert.False(await _store.MarkSendAdmittedAsync(ProjectId, job.JobId, CancellationToken.None));
        Assert.True(await _store.HasUnresolvedNativeWorkAsync(ProjectId, RunId, CancellationToken.None));
    }

    [Fact]
    public async Task CleanupWorkerReturnsAndLeavesTheCancelledAdmittedJobAndIntentPending()
    {
        var job = (await _store.CreateOrGetAsync(Create("cleanup"), CancellationToken.None)).Job;
        await _store.TryBeginExecutionAsync(ProjectId, job.JobId, CancellationToken.None);
        Assert.True(await _store.MarkSendAdmittedAsync(ProjectId, job.JobId, CancellationToken.None));
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".runtime_run_start_intents
                (tenant_id, project_id, run_id, root_session_id, accepted_selection_hash,
                 execution_fence, intent_id, bootstrap_operation_id, consume_operation_id,
                 exchange_operation_id, state, current_step, cleanup_obligation)
            VALUES (@tenant, @project, @run, 'root', repeat('a', 64), 1,
                    @intent, @bootstrap, @consume, @exchange, 'cleanup_pending', 'cleanup', true)
            """, connection))
        {
            insert.Parameters.AddWithValue("tenant", TenantId);
            insert.Parameters.AddWithValue("project", ProjectId);
            insert.Parameters.AddWithValue("run", RunId);
            insert.Parameters.AddWithValue("intent", Guid.NewGuid());
            insert.Parameters.AddWithValue("bootstrap", Guid.NewGuid());
            insert.Parameters.AddWithValue("consume", Guid.NewGuid());
            insert.Parameters.AddWithValue("exchange", Guid.NewGuid());
            await insert.ExecuteNonQueryAsync();
        }
        await CancelRunAsync();

        var startStore = new CoordinationRuntimeRunStartStore(fixture.DataSource, _schema);
        var credential = new SecretCredential("cleanup-test", DateTimeOffset.UtcNow.AddMinutes(1));
        var continuation = new RuntimeRunStartCleanupContinuation(
            startStore, null!, null!, _store,
            new RuntimeRunStartOptions(new Uri("https://environment.test/"), new Uri("https://broker.test/")),
            null!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await continuation.ContinueAsync(
                new RuntimeRunStartCleanupWork(
                    ProjectId, RunId, TenantId, new CoordinationActor(ActorIssuer, ActorId),
                    new RuntimeActorAuthorization(credential, TenantId)),
                deadline.Token);
        }
        finally
        {
            credential.Invalidate();
        }

        var pending = await startStore.FindCleanupIntentAsync(TenantId, ProjectId, RunId, CancellationToken.None);
        Assert.NotNull(pending);
        Assert.Equal("cleanup_pending", pending.State);
        Assert.True(pending.CleanupObligation);
        var cancelled = await _store.ReadAsync(ProjectId, job.JobId, CancellationToken.None);
        Assert.Equal("cancelled", cancelled!.Status);
        Assert.Equal("send-admitted", cancelled.ExecutionPhase);
        Assert.True(await _store.HasUnresolvedNativeWorkAsync(ProjectId, RunId, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationHoldingRunLockRejectsConcurrentJobAcceptance()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var cancel = new NpgsqlCommand($"""
            SELECT 1 FROM "{_schema}".accepted_runs
            WHERE project_id = @project AND run_id = @run FOR UPDATE;
            UPDATE "{_schema}".accepted_runs
            SET execution_state = 'cancelled', execution_fence = execution_fence + 1,
                cancelled_at = clock_timestamp(), completed_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run;
            INSERT INTO "{_schema}".project_run_cancellations
                (tenant_id, project_id, run_id, actor_issuer, actor_subject,
                 idempotency_key_hash, request_fingerprint, result)
            VALUES (@tenant, @project, @run, @issuer, @actor, repeat('b', 64), repeat('c', 64),
                    jsonb_build_object('state', 'cancelled', 'alreadyTerminal', false));
            """, connection, transaction))
        {
            cancel.Parameters.AddWithValue("tenant", TenantId);
            cancel.Parameters.AddWithValue("project", ProjectId);
            cancel.Parameters.AddWithValue("run", RunId);
            cancel.Parameters.AddWithValue("issuer", ActorIssuer);
            cancel.Parameters.AddWithValue("actor", ActorId);
            await cancel.ExecuteNonQueryAsync();
        }

        var create = Create("concurrent-cancel");
        var acceptance = _store.CreateOrGetAsync(create, CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var observer = await fixture.DataSource.OpenConnectionAsync(deadline.Token))
        {
            while (true)
            {
                await using var blocked = new NpgsqlCommand("""
                    SELECT EXISTS (
                        SELECT 1 FROM pg_stat_activity
                        WHERE @blocker = ANY(pg_blocking_pids(pid))
                          AND query LIKE '%workflow_generation_jobs%')
                    """, observer);
                blocked.Parameters.AddWithValue("blocker", connection.ProcessID);
                if ((bool)(await blocked.ExecuteScalarAsync(deadline.Token))!)
                    break;
                await Task.Delay(20, deadline.Token);
            }
        }
        Assert.False(acceptance.IsCompleted);
        await transaction.CommitAsync();

        var failure = await Assert.ThrowsAsync<CoordinationException>(() => acceptance);
        Assert.Equal("workflow_generation_run_unavailable", failure.Code);
        Assert.Equal(409, failure.StatusCode);
        Assert.Null(await _store.ReadAsync(ProjectId, create.JobId, CancellationToken.None));
    }

    [Fact]
    public async Task ValidDefinitionWithoutPersistedNativeCompletionCannotBeCompleted()
    {
        var job = (await _store.CreateOrGetAsync(Create("completion"), CancellationToken.None)).Job;
        await _store.TryBeginExecutionAsync(ProjectId, job.JobId, CancellationToken.None);
        Assert.True(await _store.MarkSendAdmittedAsync(ProjectId, job.JobId, CancellationToken.None));
        var result = CreateValidResult(job);

        Assert.True(WorkflowGenerationJobStore.HasValidResult(job, result));
        var completed = WorkflowGenerationExecution.CreateEnvelope(job with
        {
            Status = "completed",
            ExecutionPhase = "quiesced",
            Result = result
        });
        Assert.Equal("completed", completed.Status);
        Assert.Equal(result.ArtifactId, completed.Artifact!.ArtifactId);
        Assert.EndsWith("/result", completed.ResultUrl, StringComparison.Ordinal);
        Assert.False(WorkflowGenerationExecution.CanEnqueue(job with
        {
            Status = "completed",
            ExecutionPhase = "quiesced",
            Result = result
        }));
        var equivalentEvidence = result.NativeCompletion with
        {
            UsageEventIds = ImmutableArray.CreateRange(result.NativeCompletion.UsageEventIds.ToArray())
        };
        Assert.NotEqual(result.NativeCompletion, equivalentEvidence);
        Assert.True(WorkflowGenerationJobStore.HasSameCompletionEvidence(
            result.NativeCompletion, equivalentEvidence));
        Assert.False(await _store.HasPersistedCompletionAsync(job, result, CancellationToken.None));
        Assert.False(await _store.CompleteAsync(ProjectId, job.JobId, result, CancellationToken.None));
        Assert.Equal("running", (await _store.ReadAsync(ProjectId, job.JobId, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ReconciledCompletionClearsThePersistedIndeterminateFailure()
    {
        var job = (await _store.CreateOrGetAsync(Create("reconciled"), CancellationToken.None)).Job;
        await _store.TryBeginExecutionAsync(ProjectId, job.JobId, CancellationToken.None);
        Assert.True(await _store.MarkSendAdmittedAsync(ProjectId, job.JobId, CancellationToken.None));
        await _store.FailAsync(
            ProjectId, job.JobId, "workflow_generation_outcome_indeterminate", "The outcome is not known.",
            quiesced: false, CancellationToken.None);
        var failed = await _store.ReadAsync(ProjectId, job.JobId, CancellationToken.None);
        Assert.Equal("workflow_generation_outcome_indeterminate", failed!.FailureCode);
        Assert.True(WorkflowGenerationExecution.CanEnqueue(failed));

        var completion = CreateCompletion(job);
        var ledgerReceipt = RuntimeRootTurnCompletionLedgerReceipt.Create(completion, DateTimeOffset.UtcNow);
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await new RuntimeExecutionEvidenceLedgerStore(_schema).AppendRootTurnCompletionAsync(
                connection, transaction, ledgerReceipt, CancellationToken.None);
            await transaction.CommitAsync();
        }
        var result = CreateValidResult(job, completion.ToEvidence());
        Assert.True(await _store.CompleteAsync(ProjectId, job.JobId, result, CancellationToken.None));

        var completed = await _store.ReadAsync(ProjectId, job.JobId, CancellationToken.None);
        Assert.NotNull(completed);
        Assert.Equal("completed", completed.Status);
        Assert.Equal("quiesced", completed.ExecutionPhase);
        Assert.Null(completed.FailureCode);
        Assert.Null(completed.FailureMessage);
        var envelope = WorkflowGenerationExecution.CreateEnvelope(completed);
        Assert.Equal(result.ArtifactId, envelope.Artifact!.ArtifactId);
        Assert.Null(envelope.Failure);
        Assert.True(await _store.HasPersistedCompletionAsync(completed, completed.Result, CancellationToken.None));
    }

    [Fact]
    public async Task TerminalReconciliationRequiresCurrentAuthorityAndExactPersistedNativeProof()
    {
        var exact = (await _store.CreateOrGetAsync(Create("exact-proof"), CancellationToken.None)).Job;
        var foreign = (await _store.CreateOrGetAsync(Create("foreign-proof"), CancellationToken.None)).Job;
        foreach (var job in new[] { exact, foreign })
        {
            await _store.TryBeginExecutionAsync(ProjectId, job.JobId, CancellationToken.None);
            Assert.True(await _store.MarkSendAdmittedAsync(ProjectId, job.JobId, CancellationToken.None));
            await _store.FailAsync(ProjectId, job.JobId, "primary_failure", "Keep this failure.",
                quiesced: false, CancellationToken.None);
        }
        var authorityChecks = 0;
        Task Current(CancellationToken _)
        {
            authorityChecks++;
            return Task.CompletedTask;
        }
        await _store.ReconcileTerminalQuiescenceAsync(ProjectId, RunId, Current, CancellationToken.None);
        Assert.Equal(0, authorityChecks);
        Assert.True(await _store.HasUnresolvedNativeWorkAsync(ProjectId, RunId, CancellationToken.None));

        await InsertRootAdmissionsAsync(exact, foreign);
        foreach (var job in new[] { exact, foreign })
        {
            var source = job == foreign ? job with { RuntimeInstanceId = Guid.NewGuid() } : job;
            var receipt = RuntimeRootTurnCompletionLedgerReceipt.Create(CreateCompletion(source), DateTimeOffset.UtcNow);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await new RuntimeExecutionEvidenceLedgerStore(_schema).AppendRootTurnCompletionAsync(
                connection, transaction, receipt, CancellationToken.None);
            await transaction.CommitAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.ReconcileTerminalQuiescenceAsync(ProjectId, RunId,
                _ => throw new InvalidOperationException("authority revoked"), CancellationToken.None));
        Assert.Equal("send-admitted", (await _store.ReadAsync(ProjectId, exact.JobId, CancellationToken.None))!.ExecutionPhase);
        await _store.ReconcileTerminalQuiescenceAsync(ProjectId, RunId, Current, CancellationToken.None);
        var reconciled = await _store.ReadAsync(ProjectId, exact.JobId, CancellationToken.None);
        var rejected = await _store.ReadAsync(ProjectId, foreign.JobId, CancellationToken.None);
        Assert.Equal(1, authorityChecks);
        Assert.Equal("failed", reconciled!.Status);
        Assert.Equal("primary_failure", reconciled.FailureCode);
        Assert.Equal("quiesced", reconciled.ExecutionPhase);
        Assert.Equal("send-admitted", rejected!.ExecutionPhase);
        Assert.True(await _store.HasUnresolvedNativeWorkAsync(ProjectId, RunId, CancellationToken.None));
    }

    [Fact]
    public async Task TerminalReconciliationAcceptsExactSealedNoSendProof()
    {
        var job = (await _store.CreateOrGetAsync(Create("no-send-proof"), CancellationToken.None)).Job;
        await _store.TryBeginExecutionAsync(ProjectId, job.JobId, CancellationToken.None);
        Assert.True(await _store.MarkSendAdmittedAsync(ProjectId, job.JobId, CancellationToken.None));
        await CancelRunAsync();
        await InsertRootAdmissionsAsync(job);
        var hash = new string('b', 64);
        var proof = RuntimeNumericLimitNativeNoSendReceipt.Seal(new(
            1, Guid.NewGuid(), ProjectId, RunId, job.SessionId, job.SessionId,
            job.RuntimeInstanceId, job.RegistrationRevision, job.ExecutionFence,
            job.NativeRequestId, job.NativeRequestId, hash, Guid.NewGuid(), hash, hash, hash,
            Guid.NewGuid(), 1, hash, hash, hash, 0, true, DateTimeOffset.UtcNow, string.Empty));
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".maf_runtime_evidence_receipts
                (project_id, run_id, receipt_id, receipt_kind, attempt_id, payload_hash, receipt_json, created_at)
            VALUES (@project, @run, @receipt, 'numeric-limit-native-no-send', @request,
                    @hash, @json::jsonb, @created)
            """, connection))
        {
            insert.Parameters.AddWithValue("project", ProjectId);
            insert.Parameters.AddWithValue("run", RunId);
            insert.Parameters.AddWithValue("receipt", proof.ReceiptId);
            insert.Parameters.AddWithValue("request", job.NativeRequestId);
            insert.Parameters.AddWithValue("hash", proof.CanonicalPayloadHash);
            insert.Parameters.AddWithValue("json", JsonSerializer.Serialize(proof, JsonOptions));
            insert.Parameters.AddWithValue("created", proof.RecordedAt);
            await insert.ExecuteNonQueryAsync();
        }
        await _store.ReconcileTerminalQuiescenceAsync(
            ProjectId, RunId, _ => Task.CompletedTask, CancellationToken.None);
        var reconciled = await _store.ReadAsync(ProjectId, job.JobId, CancellationToken.None);
        Assert.Equal("cancelled", reconciled!.Status);
        Assert.Equal("quiesced", reconciled.ExecutionPhase);
        Assert.False(await _store.HasUnresolvedNativeWorkAsync(ProjectId, RunId, CancellationToken.None));
    }

    private async Task InsertRootAdmissionsAsync(params WorkflowGenerationJob[] jobs)
    {
        var decisionId = Guid.NewGuid();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var roots = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".coordination_sessions
                (project_id, run_id, session_id, root_session_id, node_kind, writer_issuer, writer_subject, execution_fence)
            VALUES (@project, @run, 'root', 'root', 'coordinator', @issuer, @actor, 1);
            INSERT INTO "{_schema}".coordinator_decisions
                (project_id, run_id, session_id, request_id, decision_id, actor_issuer, actor_subject,
                 execution_fence, state_version, action_kind, idempotency_key, command_hash, decision_state, decision)
            VALUES (@project, @run, 'root', 'proof', @decision, @issuer, @actor,
                    1, 1, 'proof', 'proof', repeat('a', 64), 'accepted', jsonb_build_object())
            """, connection))
        {
            roots.Parameters.AddWithValue("project", ProjectId);
            roots.Parameters.AddWithValue("run", RunId);
            roots.Parameters.AddWithValue("issuer", ActorIssuer);
            roots.Parameters.AddWithValue("actor", ActorId);
            roots.Parameters.AddWithValue("decision", decisionId);
            await roots.ExecuteNonQueryAsync();
        }
        foreach (var job in jobs)
        {
            await using var admission = new NpgsqlCommand($"""
                INSERT INTO "{_schema}".maf_root_model_turn_admissions
                    (project_id, run_id, root_session_id, request_id, actor_issuer, actor_id, tenant_id,
                     accepted_selection_hash, prompt_hash, execution_fence, decision_id, decision_state_version)
                VALUES (@project, @run, @session, @request, @issuer, @actor, @tenant,
                        @selection, @prompt, @fence, @decision, @version)
                """, connection);
            admission.Parameters.AddWithValue("project", job.ProjectId);
            admission.Parameters.AddWithValue("run", job.RunId);
            admission.Parameters.AddWithValue("session", job.SessionId);
            admission.Parameters.AddWithValue("request", job.NativeRequestId);
            admission.Parameters.AddWithValue("issuer", job.ActorIssuer);
            admission.Parameters.AddWithValue("actor", job.ActorId);
            admission.Parameters.AddWithValue("tenant", job.TenantId);
            admission.Parameters.AddWithValue("selection", job.AcceptedSelectionHash);
            admission.Parameters.AddWithValue("prompt", job.PromptSha256);
            admission.Parameters.AddWithValue("fence", job.ExecutionFence);
            admission.Parameters.AddWithValue("decision", decisionId);
            admission.Parameters.AddWithValue("version", job.ExpectedDecisionStateVersion);
            await admission.ExecuteNonQueryAsync();
        }
    }

    private WorkflowGenerationJobCreate Create(string idempotencyKey)
    {
        const string prompt = "Create a safe workflow.";
        return new WorkflowGenerationJobCreate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ProjectId,
            RunId,
            "root",
            TenantId,
            ActorIssuer,
            ActorId,
            idempotencyKey,
            WorkflowGenerationJobStore.Hash(idempotencyKey),
            prompt,
            WorkflowGenerationJobStore.Hash(prompt),
            1,
            1,
            new string('a', 64),
            1,
            Guid.NewGuid(),
            1,
            "model:provider-alpha");
    }

    private async Task CancelRunAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".project_run_cancellations
                (tenant_id, project_id, run_id, actor_issuer, actor_subject,
                 idempotency_key_hash, request_fingerprint, result)
            VALUES (@tenant, @project, @run, @issuer, @actor, repeat('b', 64), repeat('c', 64),
                    jsonb_build_object('state', 'cancelled', 'alreadyTerminal', false))
            """, connection);
        command.Parameters.AddWithValue("tenant", TenantId);
        command.Parameters.AddWithValue("project", ProjectId);
        command.Parameters.AddWithValue("run", RunId);
        command.Parameters.AddWithValue("issuer", ActorIssuer);
        command.Parameters.AddWithValue("actor", ActorId);
        await command.ExecuteNonQueryAsync();
    }

    private static WorkflowGenerationJobResult CreateValidResult(
        WorkflowGenerationJob job,
        RuntimeRootTurnCompletionEvidence? actualEvidence = null)
    {
        var definition = WorkflowTestData.Definition(WorkflowDefinitionOrigin.Generated);
        var definitionJson = JsonSerializer.Serialize(definition, JsonOptions);
        var definitionHash = WorkflowGenerationJobStore.Hash(definitionJson);
        var artifactHash = WorkflowGenerationJobStore.Hash(
            $"{job.JobId:D}\0{definition.Id}\0{definition.Revision}\0{definitionHash}");
        var evidenceHash = new string('d', 64);
        var material = new RuntimeMaterialAcknowledgmentEvidence(
            Guid.NewGuid(), 1, SessionMaterialKind.TurnContent, evidenceHash, evidenceHash);
        var evidence = new RuntimeRootTurnCompletionEvidence(
            1,
            job.NativeRequestId,
            job.PromptSha256,
            evidenceHash,
            job.RuntimeInstanceId,
            job.RegistrationRevision,
            evidenceHash,
            job.ExecutionFence,
            job.AcceptedSelectionHash,
            evidenceHash,
            evidenceHash,
            Guid.NewGuid(),
            1,
            job.ModelSelectionReference,
            "model",
            "byok",
            null,
            null,
            new SandboxImageIdentity("sha256:" + evidenceHash, "linux/amd64", 1),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            evidenceHash,
            evidenceHash,
            evidenceHash,
            1,
            ImmutableArray.Create(Guid.NewGuid()),
            0,
            0,
            material,
            material with { Kind = SessionMaterialKind.TurnContent },
            material with { Kind = SessionMaterialKind.SdkCache },
            evidenceHash,
            evidenceHash,
            1,
            true);
        return new WorkflowGenerationJobResult(
            job.JobId,
            Guid.ParseExact(artifactHash[..32], "N"),
            definition,
            definitionHash,
            actualEvidence ?? evidence,
            job.ConfigurationRevision,
            job.AcceptedSelectionHash);
    }

    internal static RuntimeRootTurnCompletionReceipt CreateCompletion(
        WorkflowGenerationJob job,
        RuntimeRegistration? actualRegistration = null,
        RuntimeGrantReceipt? actualGrant = null,
        string? actualOutput = null)
    {
        var binding = actualRegistration?.Binding ?? new RuntimeBinding(
            job.ActorIssuer, job.ActorId, job.TenantId, job.ProjectId, job.RunId, job.SessionId,
            RuntimeActionContract.CoordinatorRootStepId, "root-turn", 1, job.ConfigurationRevision, 1,
            "context-v1", job.AcceptedSelectionHash, job.ExecutionFence, "environment", "placement", 1,
            "profile", new Uri("https://runtime.test/runtime/v1/configure"),
            new Uri("https://runtime.test/runtime/v1/observe"))
        {
            ModelSelectionReference = job.ModelSelectionReference,
            ModelSourceMode = ModelSourceMode.HostedCopilot,
            PlacementProviderId = "sandbox",
            EnvironmentLifecycleGeneration = 1,
            EnvironmentLeaseRevision = 1,
            EnvironmentCurrentFencingGeneration = 1,
            EnvironmentProviderFencingGeneration = 1,
            Image = new SandboxImageIdentity("sha256:" + new string('a', 64), "linux/amd64", 1)
        };
        var registration = actualRegistration ?? new RuntimeRegistration(
            job.RuntimeInstanceId, job.RegistrationRevision, binding, RuntimeRegistrationState.Active,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var source = new SdkSessionFacts(
            job.RuntimeInstanceId, RuntimeContractValidation.NativeSessionId(binding), "sdk-1", "runtime-1",
            job.ModelSelectionReference, "model", new string('c', 64), 1,
            "hosted-copilot", SdkMeterSources.CopilotNanoAiu, job.AcceptedSelectionHash,
            job.RegistrationRevision);
        var grant = actualGrant ?? new RuntimeGrantReceipt(
            Guid.NewGuid(), job.RuntimeInstanceId, job.RegistrationRevision, 1, job.ActorIssuer,
            RuntimeCredentialPurpose.Observe, binding.ObservationEndpoint, RuntimeCredentialState.Active,
            new string('d', 64), registration.ExpiresAt, DateTimeOffset.UtcNow);
        var definition = WorkflowTestData.Definition(WorkflowDefinitionOrigin.Generated);
        var output = actualOutput ?? JsonSerializer.Serialize(definition, JsonOptions);
        var cursor = "native-cursor";
        var cursorHash = WorkflowGenerationJobStore.Hash(cursor);
        var native = new RuntimeNativeTurnObservation(
            source, Guid.NewGuid().ToString("D"), "native-turn", Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), DateTimeOffset.UtcNow, cursor, WorkflowGenerationJobStore.Hash(output), null);
        var assistantId = new Guid(SHA256.HashData(
            Encoding.UTF8.GetBytes($"agentweaver:a2a:assistant:{job.NativeRequestId:N}")).AsSpan(0, 16));

        SessionMaterialAcknowledgment Material(Guid eventId, long position, SessionMaterialKind kind, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var material = new SessionMaterialBinding(
                1, kind, job.TenantId, RuntimeContractValidation.Hash(bytes), job.RuntimeInstanceId,
                job.RegistrationRevision, job.ExecutionFence, job.AcceptedSelectionHash,
                source.SdkVersion, source.RuntimeVersion, source.ModelSelectionReference, source.ModelId);
            return new SessionMaterialAcknowledgment(
                1, new SessionIdentity(job.ProjectId, job.RunId, job.SessionId), eventId, position,
                new SessionObjectReference(
                    new ObjectKey($"material/{eventId:N}"), SessionMaterialValidation.Purpose(kind), bytes.Length)
                {
                    Material = material
                });
        }

        return new RuntimeRootTurnCompletionReceipt(
            1, job.NativeRequestId, job.PromptSha256, registration, source, grant, native,
            new string('e', 64), cursorHash, new string('f', 64), 1,
            Material(job.NativeRequestId, 1, SessionMaterialKind.TurnContent, job.Prompt),
            Material(assistantId, 2, SessionMaterialKind.TurnContent, output),
            Material(Guid.NewGuid(), 3, SessionMaterialKind.SdkCache, "cache"),
            0, 0, cursor, cursorHash, cursorHash, 0, true).Validate();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
