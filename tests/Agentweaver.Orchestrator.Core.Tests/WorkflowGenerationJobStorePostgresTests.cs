using System.Collections.Immutable;
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
    private const string ActorId = "workflow-generation-actor";
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
        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => _store.CreateOrGetAsync(create, CancellationToken.None)));

        var jobIds = responses.Select(response => response.Job.JobId).Distinct().ToArray();
        var nativeRequestIds = responses.Select(response => response.Job.NativeRequestId).Distinct().ToArray();
        Assert.Single(jobIds);
        Assert.Single(nativeRequestIds);
        Assert.Single(responses, response => response.Disposition == WorkflowGenerationCreateDisposition.Created);
        Assert.Equal(7, responses.Count(response =>
            response.Disposition == WorkflowGenerationCreateDisposition.Existing));

        var lostReplyRetry = await _store.CreateOrGetAsync(create, CancellationToken.None);
        Assert.Equal(WorkflowGenerationCreateDisposition.Existing, lostReplyRetry.Disposition);
        Assert.Equal(responses[0].Job.JobId, lostReplyRetry.Job.JobId);
        Assert.Equal(responses[0].Job.NativeRequestId, lostReplyRetry.Job.NativeRequestId);
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
            "model:provider/alpha");
    }

    private async Task CancelRunAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".project_run_cancellations
                (tenant_id, project_id, run_id, actor_issuer, actor_subject,
                 idempotency_key_hash, request_fingerprint, result)
            VALUES (@tenant, @project, @run, @issuer, @actor, repeat('b', 64), repeat('c', 64), jsonb_build_object())
            """, connection);
        command.Parameters.AddWithValue("tenant", TenantId);
        command.Parameters.AddWithValue("project", ProjectId);
        command.Parameters.AddWithValue("run", RunId);
        command.Parameters.AddWithValue("issuer", ActorIssuer);
        command.Parameters.AddWithValue("actor", ActorId);
        await command.ExecuteNonQueryAsync();
    }

    private static WorkflowGenerationJobResult CreateValidResult(WorkflowGenerationJob job)
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
            evidence,
            job.ConfigurationRevision,
            job.AcceptedSelectionHash);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
