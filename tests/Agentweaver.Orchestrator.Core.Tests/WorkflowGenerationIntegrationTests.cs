using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IdentityModel.Tokens.Jwt;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class WorkflowGenerationIntegrationTests(CoordinationPostgresFixture fixture) : IAsyncLifetime
{
    private const string Project = "workflow-project";
    private const string Run = "workflow-run";
    private const string Tenant = "workflow-tenant";
    private const string Issuer = "https://identity.example/";
    private const string Subject = "6294c6de-f698-43d2-8dcb-d29cb289e671";
    private const string Root = "root";
    private readonly string _schema = "workflow_integration_" + Guid.NewGuid().ToString("N");
    private readonly CoordinationActor _actor = new(Issuer, Subject);
    private readonly WorkflowGenerationWorkQueue _queue = new();
    private WebApplication _app = null!;
    private WorkflowGenerationJobStore _jobs = null!;
    private WorkflowGenerationExecutor _executor = null!;
    private HostHandler _host = null!;
    private MaterialHandler _materials = null!;
    private AuthorizedRunSelection _selection = null!;
    private OrchestratorOptions _options = null!;
    private CoordinationOwnerStore _sessions = null!;
    private CoordinatorDecisionOwnerStore _decisions = null!;
    private RuntimeRegistrationOwner _registrationOwner = null!;
    private RuntimeRegistration _registration = null!;
    private ProjectsRunSelectionClient _projects = null!;
    private ProjectsHandler _projectsHandler = null!;
    private string _projectWriterToken = null!;
    private long _decisionVersion;
    private readonly List<HttpClient> _ownerClients = [];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        var snapshot = JsonSerializer.SerializeToElement(new
        {
            projectId = Project,
            runId = Run,
            projectRevision = 1,
            projectConfigurationRevision = 1,
            platformRuntimeRevision = 1,
            contextRevision = "context-v1",
            modelSelection = new
            {
                reference = "model:provider-alpha",
                sourceMode = "hostedCopilot",
                connectionId = "11111111-1111-1111-1111-111111111111",
                reasoningEffort = "high"
            },
            projectConfiguration = new
            {
                blueprintWorkflowReferences = Array.Empty<object>(),
                agentCharters = new[] { new { agentId = "planner-1", role = "planner", charter = "Prepare the plan." } },
                casting = new[] { new { agentId = "planner-1", role = "planner" } }
            },
            runLimits = new { maxChildren = 100, maxConcurrentChildren = 32, maxWallTimeSeconds = 3600 }
        });
        var selection = new AuthorizedRunSelection(
            new EffectiveRunSelection(Project, Run, 1, 1, 1, "context-v1", snapshot),
            new ProjectsAuthorizationContext(1, Issuer, Subject, Tenant, 1, Project, Run,
                [new ProjectsAuthority("project", Project,
                    [new("acceptRunSelection", 1), new("readRunSelection", 1)])]));
        _selection = selection;
        var options = new OrchestratorOptions(
            Issuer, "orchestrator", "https://projects.test/", "projects",
            "https://events.test/", "events", _schema);
        _options = options;
        _projectWriterToken = CreateProjectWriterToken();
        _projectsHandler = new ProjectsHandler(selection, _projectWriterToken);
        var projects = new ProjectsRunSelectionClient(
            Client(_projectsHandler), options,
            new ReviewedRemoteToolSnapshotStore(fixture.DataSource, _schema));
        _projects = projects;
        var catalog = ProviderCatalog.Create([], [], []).Value!;
        var contexts = new CoordinatorRunSelectionContextStore(
            fixture.DataSource, _schema, catalog, new ProviderResolver(catalog), []);
        var decisions = new CoordinatorDecisionOwnerStore(fixture.DataSource, _schema, contexts);
        _decisions = decisions;
        var sessions = new CoordinationOwnerStore(fixture.DataSource, _schema, TimeProvider.System, decisions);
        _sessions = sessions;
        var checkpoints = new PostgresMafCheckpointStore(fixture.DataSource, _schema, null);
        var accepted = await sessions.AcceptRootAsync(_actor, selection, Root, CancellationToken.None);
        var identity = new SessionIdentity(Project, Run, Root);
        _decisionVersion = (await decisions.InitializeRootAsync(_actor, identity, selection, CancellationToken.None))
            .StateVersion;

        using var ownerServices = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(new FixedAuthentication())
            .BuildServiceProvider();
        var ownerContext = new DefaultHttpContext { User = Principal(), RequestServices = ownerServices };
        ownerContext.Request.Headers.Authorization = "Bearer test-token";
        ownerContext.Request.Headers["X-Agentweaver-Tenant"] = Tenant;
        var owner = await CoordinationEndpoints.ReadRuntimeOwnerContextCoreAsync(
            Project, Run, Root, ownerContext, options, projects, sessions, decisions,
            contexts, checkpoints, CancellationToken.None, allowCoordinatorRoot: true);
        var binding = new RuntimeBinding(
            owner.ActorIssuer, owner.ActorId, owner.TenantId, owner.ProjectId, owner.RunId, owner.SessionId,
            owner.AgentId, owner.TurnId, owner.ProjectRevision, owner.ProjectConfigurationRevision,
            owner.PlatformRuntimeRevision, owner.ContextRevision, owner.AcceptedSelectionHash,
            accepted.ExecutionFence, "environment", "placement", 1, "profile",
            new("https://runtime.test/runtime/v1/configure"), new("https://runtime.test/runtime/v1/observe"))
        {
            ModelSelectionReference = owner.ModelSelectionReference,
            ModelSourceMode = owner.ModelSourceMode,
            ModelConnectionId = owner.ModelConnectionId,
            ModelConnectionScope = owner.ModelConnectionScope,
            ReasoningEffort = owner.ReasoningEffort,
            PlacementProviderId = "sandbox",
            EnvironmentLifecycleGeneration = 1,
            EnvironmentLeaseRevision = 1,
            EnvironmentCurrentFencingGeneration = 1,
            EnvironmentProviderFencingGeneration = 1,
            Image = new SandboxImageIdentity("sha256:" + new string('a', 64), "linux/amd64", 1)
        };
        var expires = DateTimeOffset.UtcNow.AddMinutes(10);
        expires = new DateTimeOffset(expires.UtcTicks - expires.UtcTicks % 10, TimeSpan.Zero);
        var registrations = new RuntimeRegistrationStore(fixture.DataSource, _schema, TimeProvider.System);
        var registration = await registrations.RegisterAsync(binding, expires, CancellationToken.None);
        var placement = new EnvironmentRuntimeBootstrapContext(
            1, Tenant, Project, Run, binding.EnvironmentId, 1, 1, 1, 1, registration.ExpiresAt,
            new(ProviderSeam.Sandbox, "sandbox", "placement", 1),
            new(Guid.NewGuid()), new("placement"), "profile", binding.ConfigureEndpoint, binding.ObservationEndpoint)
        {
            RuntimeOwnerContext = owner,
            Image = binding.Image
        };
        var registrationOwner = new RuntimeRegistrationOwner(
            options, projects, sessions, decisions, contexts, checkpoints, registrations,
            new RuntimeEnvironmentContextClient(
                Client(new FixedHandler(request => Response(request, placement))),
                new RuntimeRegistrationOwnerOptions(new Uri("https://environment.test/"))),
            TimeProvider.System);
        _registrationOwner = registrationOwner;
        _registration = registration;
        _jobs = new WorkflowGenerationJobStore(fixture.DataSource, _schema);
        _materials = new MaterialHandler();
        _host = new HostHandler(registration, _materials);
        var events = new EventsAddressedMessageClient(
            Client(_materials), options, new HttpContextAccessor());
        var actionGrants = new ExecutableActionGrantOwnerStore(
            fixture.DataSource, options, TimeProvider.System, projects, new HttpContextAccessor());

        _app = CreateTestApplication(_jobs, _queue, projects);
        await _app.StartAsync();
        _executor = new WorkflowGenerationExecutor(
            _jobs, projects, sessions, decisions, registrationOwner, actionGrants, events,
            new HostClientFactory(_host), TimeProvider.System, _app.Services,
            NullLogger<WorkflowGenerationExecutor>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        foreach (var client in _ownerClients)
            client.Dispose();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task FullQueueReturnsRetryableErrorAndSameRequestEventuallyCompletes()
    {
        var fillerCredential = new SecretCredential("filler", DateTimeOffset.UtcNow.AddMinutes(5));
        var filler = new WorkflowGenerationWork(
            Project, Run, Root, _actor, Principal(), new(fillerCredential, Tenant),
            new SecretCredential("project-filler", DateTimeOffset.UtcNow.AddMinutes(5)), Guid.NewGuid());
        for (var index = 0; index < 64; index++)
            Assert.True(_queue.TryEnqueue(filler));

        using var first = await SubmitAsync("queue-full");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Contains("workflow_generation_queue_full", await first.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var stored = await _jobs.ReadByIdempotencyKeyAsync(
            Tenant, Project, Issuer, Subject, "queue-full", CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal("queued", stored.Status);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var reader = _queue.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        using var retry = await SubmitAsync("queue-full");
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var envelope = await retry.Content.ReadFromJsonAsync<WorkflowGenerationJobEnvelope>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.Equal(stored.JobId, envelope.JobId);
        Assert.Equal("queued", envelope.Status);

        WorkflowGenerationWork? scheduled = null;
        for (var index = 0; index < 64; index++)
        {
            Assert.True(await reader.MoveNextAsync());
            if (reader.Current.JobId == stored.JobId)
                scheduled = reader.Current;
        }
        Assert.NotNull(scheduled);
        fillerCredential.Invalidate();
        filler.ProjectWriterBearer.Invalidate();
        await ExecuteAsync(scheduled);
        using var status = await GetStatusAsync(stored.JobId);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var completed = await status.Content.ReadFromJsonAsync<WorkflowGenerationJobEnvelope>(JsonOptions);
        Assert.True(completed!.Status == "completed",
            $"Status: {completed.Status}; failure: {completed.Failure?.Code}; host requests: {_host.Requests.Count}");
        Assert.NotNull(completed.Artifact);
        Assert.Null(completed.Failure);
        using var resultResponse = await GetResultAsync(stored.JobId);
        var result = await resultResponse.Content.ReadFromJsonAsync<WorkflowGenerationJobResult>(JsonOptions);
        Assert.Equal(HttpStatusCode.OK, resultResponse.StatusCode);
        Assert.Equal(stored.JobId, result!.JobId);
        Assert.Equal(stored.NativeRequestId, result.NativeCompletion.RequestId);
        Assert.Equal(WorkflowDefinitionOrigin.Generated, result.WorkflowDefinition.Origin);
        Assert.Equal(completed.Artifact!.ArtifactId, result.ArtifactId);
        Assert.Equal(1, _host.NativeTurns);
    }

    [Fact]
    public async Task ProjectWriterAuthorityMustMatchTheRunActor()
    {
        _projectsHandler.ProjectWriterAuthority = new ProjectsAuthorizationContext(
            1, Issuer, "another-actor", Tenant, 1, null, null,
            [new ProjectsAuthority("project", Project, [new("writeProjects", 1)])]);

        using var response = await SubmitAsync("writer-mismatch");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await _jobs.ReadByIdempotencyKeyAsync(
            Tenant, Project, Issuer, Subject, "writer-mismatch", CancellationToken.None));
        Assert.Empty(_host.Requests);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("project-writer")]
    public async Task RevokedQueuedCredentialStopsBeforeNativeWork(string credentialName)
    {
        using var submitted = await SubmitAsync($"revoked-{credentialName}");
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var reader = _queue.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        var work = reader.Current;
        if (credentialName == "actor")
            work.Actor.Bearer.Invalidate();
        else
            work.ProjectWriterBearer.Invalidate();

        await ExecuteAsync(work);

        var failed = await _jobs.ReadAsync(Project, work.JobId, CancellationToken.None);
        Assert.Equal("failed", failed!.Status);
        Assert.Equal("quiesced", failed.ExecutionPhase);
        Assert.Equal("workflow_generation_failed", failed.FailureCode);
        Assert.Empty(_host.Requests);
        Assert.Equal(0, _host.NativeTurns);
    }

    [Theory]
    [InlineData("actor", 6)]
    [InlineData("project-writer", 11)]
    public async Task ExpiredQueuedCredentialStopsBeforeNativeWork(string credentialName, int elapsedMinutes)
    {
        var writerToken = CreateProjectWriterToken();
        _projectWriterToken = writerToken;
        _projectsHandler.ProjectWriterToken = writerToken;
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var queue = new WorkflowGenerationWorkQueue();
        await using var app = CreateTestApplication(_jobs, queue, _projects, clock);
        await app.StartAsync();
        var runToken = credentialName == "actor" ? "short-expiry-run-token" : "test-expiry-token";
        using var submitted = await SubmitAsync(app, $"expired-{credentialName}", runToken, writerToken);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var reader = queue.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        var work = reader.Current;
        clock.Advance(TimeSpan.FromMinutes(elapsedMinutes));

        await ExecuteAsync(work);

        var failed = await _jobs.ReadAsync(Project, work.JobId, CancellationToken.None);
        Assert.Equal("failed", failed!.Status);
        Assert.Equal("quiesced", failed.ExecutionPhase);
        Assert.Equal("workflow_generation_failed", failed.FailureCode);
        Assert.Empty(_host.Requests);
        Assert.Equal(0, _host.NativeTurns);
    }

    [Fact]
    public async Task RestartedQueueReattachesOnlyWithFreshCredentials()
    {
        const string key = "restart-reattach";
        using var submitted = await SubmitAsync(key);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var originalEnvelope =
            await submitted.Content.ReadFromJsonAsync<WorkflowGenerationJobEnvelope>(JsonOptions);
        Assert.NotNull(originalEnvelope);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var originalReader = _queue.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await originalReader.MoveNextAsync());
        var originalWork = originalReader.Current;
        var originalJob = await _jobs.ReadAsync(Project, originalWork.JobId, deadline.Token);
        Assert.Equal(originalEnvelope.JobId, originalJob!.JobId);
        originalWork.Actor.Bearer.Invalidate();
        originalWork.ProjectWriterBearer.Invalidate();

        var restartedJobs = new WorkflowGenerationJobStore(fixture.DataSource, _schema);
        var restartedQueue = new WorkflowGenerationWorkQueue();
        await using var restartedApp = CreateTestApplication(restartedJobs, restartedQueue, _projects);
        await restartedApp.StartAsync();
        var freshRunToken = "fresh-run-token";
        var freshProjectWriterToken = CreateProjectWriterToken();
        _projectWriterToken = freshProjectWriterToken;
        _projectsHandler.ProjectWriterToken = freshProjectWriterToken;

        using var retry = await SubmitAsync(
            restartedApp, key, freshRunToken, freshProjectWriterToken);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var retryEnvelope = await retry.Content.ReadFromJsonAsync<WorkflowGenerationJobEnvelope>(JsonOptions);
        Assert.Equal(originalEnvelope.JobId, retryEnvelope!.JobId);
        Assert.Equal(originalJob.NativeRequestId, (await restartedJobs.ReadAsync(
            Project, retryEnvelope.JobId, deadline.Token))!.NativeRequestId);

        await using var restartedReader = restartedQueue.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await restartedReader.MoveNextAsync());
        var reattachedWork = restartedReader.Current;
        try
        {
            Assert.Equal(originalEnvelope.JobId, reattachedWork.JobId);
            Assert.Equal(freshRunToken, reattachedWork.Actor.Bearer.GetValue());
            Assert.Equal(freshProjectWriterToken, reattachedWork.ProjectWriterBearer.GetValue());
            Assert.True(reattachedWork.Actor.Bearer.IsUsable());
            Assert.True(reattachedWork.ProjectWriterBearer.IsUsable());
            Assert.Empty(_host.Requests);
        }
        finally
        {
            reattachedWork.Actor.Bearer.Invalidate();
            reattachedWork.ProjectWriterBearer.Invalidate();
        }
    }

    [Fact]
    public async Task CompletionWriteFailureReconcilesTheSameNativeRequest()
    {
        await SetFailureTriggerAsync(
            "maf_runtime_evidence_receipts", "INSERT",
            "NEW.receipt_kind = 'native-root-turn-completion'");
        var jobId = await SubmitAndExecuteAsync("completion-write");
        var uncertain = await _jobs.ReadAsync(Project, jobId, CancellationToken.None);
        Assert.Equal("failed", uncertain!.Status);
        Assert.Equal("send-admitted", uncertain.ExecutionPhase);
        Assert.Equal("workflow_generation_outcome_indeterminate", uncertain.FailureCode);
        Assert.True(WorkflowGenerationExecution.CanEnqueue(uncertain));
        Assert.Equal(1, _host.NativeTurns);

        await ClearFailureTriggerAsync("maf_runtime_evidence_receipts");
        Assert.Equal(jobId, await SubmitAndExecuteAsync("completion-write"));
        var completed = await _jobs.ReadAsync(Project, jobId, CancellationToken.None);
        Assert.Equal("completed", completed!.Status);
        Assert.Null(completed.FailureCode);
        Assert.Equal(2, _host.Requests.Count);
        Assert.All(_host.Requests, requestId => Assert.Equal(uncertain.NativeRequestId, requestId));
        Assert.Equal(1, _host.NativeTurns);
    }

    [Fact]
    public async Task StoredCompletionSurvivesMaterialReadFailureWithoutAnotherHostSend()
    {
        await SetFailureTriggerAsync("workflow_generation_jobs", "UPDATE", "NEW.status = 'completed'");
        var jobId = await SubmitAndExecuteAsync("material-read");
        var uncertain = await _jobs.ReadAsync(Project, jobId, CancellationToken.None);
        Assert.Equal("workflow_generation_outcome_indeterminate", uncertain!.FailureCode);
        Assert.Equal("send-admitted", uncertain.ExecutionPhase);
        await ClearFailureTriggerAsync("workflow_generation_jobs");

        _materials.FailReads = 1;
        Assert.Equal(jobId, await SubmitAndExecuteAsync("material-read"));
        var unavailable = await _jobs.ReadAsync(Project, jobId, CancellationToken.None);
        Assert.Equal("failed", unavailable!.Status);
        Assert.Equal("send-admitted", unavailable.ExecutionPhase);
        Assert.Equal("workflow_generation_outcome_indeterminate", unavailable.FailureCode);
        Assert.True(WorkflowGenerationExecution.CanEnqueue(unavailable));
        Assert.Single(_host.Requests);

        Assert.Equal(jobId, await SubmitAndExecuteAsync("material-read"));
        using var status = await GetStatusAsync(jobId);
        var completed = await status.Content.ReadFromJsonAsync<WorkflowGenerationJobEnvelope>(JsonOptions);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("completed", completed!.Status);
        Assert.NotNull(completed.Artifact);
        Assert.Null(completed.Failure);
        Assert.Single(_host.Requests);
        Assert.Equal(1, _host.NativeTurns);
    }

    [Fact]
    public async Task InvalidRecordedOutputBecomesTerminalWithoutAnotherNativeTurn()
    {
        _host.Output = "not a workflow definition";
        var jobId = await SubmitAndExecuteAsync("invalid-output");
        var job = await _jobs.ReadAsync(Project, jobId, CancellationToken.None);
        Assert.Equal("failed", job!.Status);
        Assert.Equal("quiesced", job.ExecutionPhase);
        Assert.Equal("workflow_generation_artifact_invalid", job.FailureCode);
        Assert.False(WorkflowGenerationExecution.CanEnqueue(job));

        using var retry = await SubmitAsync("invalid-output");
        var envelope = await retry.Content.ReadFromJsonAsync<WorkflowGenerationJobEnvelope>(JsonOptions);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal(jobId, envelope!.JobId);
        Assert.Equal("workflow_generation_artifact_invalid", envelope.Failure!.Code);
        Assert.Single(_host.Requests);
        Assert.Equal(1, _host.NativeTurns);
    }

    [Fact]
    public async Task CancelledInFlightTurnWithLostResponseStaysUnresolvedAfterSimulatedHostRestart()
    {
        _host.LoseResponse = true;
        using var submitted = await SubmitAsync("cancel-lost-response");
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var envelope = await submitted.Content.ReadFromJsonAsync<WorkflowGenerationJobEnvelope>(JsonOptions);
        Assert.NotNull(envelope);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var reader = _queue.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());

        var cancellation = CancellationSelection();
        var cancelProjects = new ProjectsRunSelectionClient(
            Client(new ProjectsHandler(cancellation)), _options,
            new ReviewedRemoteToolSnapshotStore(fixture.DataSource, _schema));
        var credential = new SecretCredential("cancel-token", DateTimeOffset.UtcNow.AddMinutes(5));
        var actor = new RuntimeActorAuthorization(credential, Tenant);
        var execution = ExecuteAsync(reader.Current);
        try
        {
            // Cancel after the Host admits the turn but before the test releases its lost response.
            await _host.TurnAdmitted.Task.WaitAsync(deadline.Token);
            await _sessions.CancelProjectRunAsync(
                _actor, cancellation, "cancel-lost-response", new CancelProjectRunRequest(),
                async token =>
                {
                    _ = await cancelProjects.ReadSelectionForCancelAsync(
                        actor, _actor, Project, Run, token);
                },
                deadline.Token);
            _host.ReleaseTurn.TrySetResult();
            await execution.WaitAsync(deadline.Token);

            var restartedStore = new WorkflowGenerationJobStore(fixture.DataSource, _schema);
            var cancelled = await restartedStore.ReadAsync(Project, envelope.JobId, deadline.Token);
            Assert.Equal("cancelled", cancelled!.Status);
            Assert.Equal("send-admitted", cancelled.ExecutionPhase);
            Assert.True(await restartedStore.HasUnresolvedNativeWorkAsync(Project, Run, deadline.Token));
            _host.ForgetResponses();

            using var retry = await SubmitAsync("cancel-lost-response");
            var retryEnvelope = await retry.Content.ReadFromJsonAsync<WorkflowGenerationJobEnvelope>(JsonOptions);
            Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
            Assert.Equal(envelope.JobId, retryEnvelope!.JobId);
            Assert.Equal("cancelled", retryEnvelope.Status);
            using var noWork = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await using var emptyQueue = _queue.ReadAllAsync(noWork.Token).GetAsyncEnumerator();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => emptyQueue.MoveNextAsync().AsTask());
            Assert.Single(_host.Requests);
            Assert.Equal(1, _host.NativeTurns);
        }
        finally
        {
            _host.ReleaseTurn.TrySetResult();
            credential.Invalidate();
        }
    }

    [Fact]
    public async Task CancelledJobDefersCleanupUntilItsStoredCompletionIsVerified()
    {
        await SetFailureTriggerAsync("workflow_generation_jobs", "UPDATE", "NEW.status = 'completed'");
        var jobId = await SubmitAndExecuteAsync("cancel-with-proof");
        var job = (await _jobs.ReadAsync(Project, jobId, CancellationToken.None))!;
        await ClearFailureTriggerAsync("workflow_generation_jobs");
        var intentId = Guid.NewGuid();
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".runtime_run_start_intents
                (tenant_id, project_id, run_id, root_session_id, accepted_selection_hash,
                 execution_fence, intent_id, bootstrap_operation_id, consume_operation_id,
                 exchange_operation_id, state, current_step)
            VALUES (@tenant, @project, @run, @root, @hash, @fence, @intent,
                    @bootstrap, @consume, @exchange, 'ready', 'ready')
            """, connection))
        {
            command.Parameters.AddWithValue("tenant", Tenant);
            command.Parameters.AddWithValue("project", Project);
            command.Parameters.AddWithValue("run", Run);
            command.Parameters.AddWithValue("root", Root);
            command.Parameters.AddWithValue("hash", job.AcceptedSelectionHash);
            command.Parameters.AddWithValue("fence", job.ExecutionFence);
            command.Parameters.AddWithValue("intent", intentId);
            command.Parameters.AddWithValue("bootstrap", Guid.NewGuid());
            command.Parameters.AddWithValue("consume", Guid.NewGuid());
            command.Parameters.AddWithValue("exchange", Guid.NewGuid());
            await command.ExecuteNonQueryAsync();
        }

        _materials.PauseOnCache = true;
        using var retry = await SubmitAsync("cancel-with-proof");
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var reader = _queue.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());

        var cancellation = CancellationSelection();
        var cancelProjects = new ProjectsRunSelectionClient(
            Client(new ProjectsHandler(cancellation)), _options,
            new ReviewedRemoteToolSnapshotStore(fixture.DataSource, _schema));
        var credential = new SecretCredential("cancel-token", DateTimeOffset.UtcNow.AddMinutes(5));
        var actor = new RuntimeActorAuthorization(credential, Tenant);
        var environmentCalls = 0;
        var cleanupHandler = new FixedHandler(request =>
        {
            environmentCalls++;
            // Migration 047 installs the terminal guard, so cleanup uses contract version 2.
            return Response(request, new RuntimeRunStartCleanupReceipt(
                2, intentId, Guid.Empty, 0, 0, null, "absent", "cleanup_complete"));
        });
        var startStore = new CoordinationRuntimeRunStartStore(fixture.DataSource, _schema);
        var cleanup = new RuntimeRunStartCleanupContinuation(
            startStore, cancelProjects, _registrationOwner, _jobs,
            new RuntimeRunStartOptions(new Uri("https://environment.test/"), new Uri("https://broker.test/")),
            new HostClientFactory(cleanupHandler));
        var cleanupWork = new RuntimeRunStartCleanupWork(Project, Run, Tenant, _actor, actor);
        var execution = ExecuteAsync(reader.Current);
        try
        {
            await _materials.Paused.Task.WaitAsync(deadline.Token);
            await _sessions.CancelProjectRunAsync(
                _actor, cancellation, "cancel-proof", new CancelProjectRunRequest(),
                async token =>
                {
                    _ = await cancelProjects.ReadSelectionForCancelAsync(
                        actor, _actor, Project, Run, token);
                },
                deadline.Token);
            await cleanup.ContinueAsync(cleanupWork, deadline.Token);
            Assert.Equal(0, environmentCalls);
            Assert.Equal("cleanup_pending",
                (await startStore.FindCleanupIntentAsync(Tenant, Project, Run, deadline.Token))!.State);
            var pendingJob = await _jobs.ReadAsync(Project, jobId, deadline.Token);
            Assert.Equal("cancelled", pendingJob!.Status);
            Assert.Equal("send-admitted", pendingJob.ExecutionPhase);
            _materials.Release.TrySetResult();
            await execution.WaitAsync(deadline.Token);
            var quiesced = await _jobs.ReadAsync(Project, jobId, deadline.Token);
            Assert.Equal("cancelled", quiesced!.Status);
            Assert.Equal("quiesced", quiesced.ExecutionPhase);
            // No placement link ties the intent to this fixture registration, so revoke it before retrying cleanup.
            await _registrationOwner.RevokeAfterPlacementReleaseAsync(_registration, deadline.Token);
            await cleanup.ContinueAsync(cleanupWork, deadline.Token);
            Assert.Equal(1, environmentCalls);
            Assert.Equal("cleanup_complete",
                (await startStore.FindCleanupIntentAsync(Tenant, Project, Run, deadline.Token))!.State);
            Assert.Single(_host.Requests);
            Assert.Equal(1, _host.NativeTurns);
        }
        finally
        {
            _materials.Release.TrySetResult();
            credential.Invalidate();
        }
    }

    private async Task<Guid> SubmitAndExecuteAsync(string key)
    {
        using var response = await SubmitAsync(key);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, body);
        var envelope = JsonSerializer.Deserialize<WorkflowGenerationJobEnvelope>(body, JsonOptions)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var reader = _queue.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(envelope.JobId, reader.Current.JobId);
        await ExecuteAsync(reader.Current);
        return envelope.JobId;
    }

    private async Task SetFailureTriggerAsync(string table, string operation, string condition)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            CREATE FUNCTION "{_schema}".inject_workflow_test_failure() RETURNS trigger
            LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected storage failure'; END $$;
            CREATE TRIGGER workflow_test_failure BEFORE {operation} ON "{_schema}"."{table}"
            FOR EACH ROW WHEN ({condition}) EXECUTE FUNCTION "{_schema}".inject_workflow_test_failure();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task ClearFailureTriggerAsync(string table)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            DROP TRIGGER workflow_test_failure ON "{_schema}"."{table}";
            DROP FUNCTION "{_schema}".inject_workflow_test_failure();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private Task<HttpResponseMessage> SubmitAsync(string key) =>
        SubmitAsync(_app, key, "test-token", _projectWriterToken);

    private async Task<HttpResponseMessage> SubmitAsync(
        WebApplication app,
        string key,
        string runBearer,
        string projectWriterBearer)
    {
        using var client = app.GetTestClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{Project}/workflows/generate")
        {
            Content = JsonContent.Create(
                new GenerateProjectWorkflowRequest(Run, Root, _decisionVersion, "Create a workflow."),
                options: JsonOptions)
        };
        message.Headers.Authorization = new("Bearer", runBearer);
        message.Headers.Add("X-Agentweaver-Tenant", Tenant);
        message.Headers.Add("X-Agentweaver-Project-Authorization", "Bearer " + projectWriterBearer);
        message.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(message);
    }

    private WebApplication CreateTestApplication(
        WorkflowGenerationJobStore jobs,
        WorkflowGenerationWorkQueue queue,
        ProjectsRunSelectionClient projects,
        TimeProvider? timeProvider = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>(
            "test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(_options);
        builder.Services.AddSingleton(projects);
        builder.Services.AddSingleton(_sessions);
        builder.Services.AddSingleton(_decisions);
        builder.Services.AddSingleton(_registrationOwner);
        builder.Services.AddSingleton(jobs);
        builder.Services.AddSingleton(queue);
        builder.Services.AddSingleton(timeProvider ?? TimeProvider.System);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapWorkflowGenerationEndpoints();
        return app;
    }

    private Task<HttpResponseMessage> GetStatusAsync(Guid jobId) => GetJobAsync(jobId, "");

    private Task<HttpResponseMessage> GetResultAsync(Guid jobId) => GetJobAsync(jobId, "/result");

    private AuthorizedRunSelection CancellationSelection() => _selection with
    {
        Authorization = new ProjectsAuthorizationContext(
            1, Issuer, Subject, Tenant, 1, Project, null,
            [new ProjectsAuthority("project", Project, [new("cancelRun", 1)])])
    };

    private async Task<HttpResponseMessage> GetJobAsync(Guid jobId, string suffix)
    {
        using var client = _app.GetTestClient();
        using var message = new HttpRequestMessage(
            HttpMethod.Get, $"/api/projects/{Project}/workflows/generation-jobs/{jobId:D}{suffix}");
        message.Headers.Authorization = new("Bearer", "test-token");
        message.Headers.Add("X-Agentweaver-Tenant", Tenant);
        return await client.SendAsync(message);
    }

    private async Task ExecuteAsync(WorkflowGenerationWork work)
    {
        _host.Job = await _jobs.ReadAsync(Project, work.JobId, CancellationToken.None);
        try
        {
            await _executor.ExecuteAsync(work, CancellationToken.None);
        }
        finally
        {
            work.Actor.Bearer.Invalidate();
            work.ProjectWriterBearer.Invalidate();
        }
    }

    private HttpClient Client(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        _ownerClients.Add(client);
        return client;
    }

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity(
    [
        new Claim("sub", Subject), new Claim("iss", Issuer),
        new Claim("aud", "orchestrator"), new Claim("aud", "projects"), new Claim("aud", "events"),
        new Claim("project_id", Project), new Claim("run_id", Run),
        new Claim("scope", "api.read projects.orchestrator")
    ], "test"));

    private sealed class TestAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                Principal(), new AuthenticationProperties
                {
                    ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(
                        Context.Request.Headers.Authorization == "Bearer short-expiry-run-token" ? 5 : 30)
                },
                Scheme.Name)));
    }

    private sealed class FixedAuthentication : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                context.User, new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) },
                "test")));
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
        public Task SignInAsync(
            HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, object value) => new(HttpStatusCode.OK)
    {
        RequestMessage = request,
        Content = JsonContent.Create(value, value.GetType(), options: JsonOptions),
        Headers = { CacheControl = new() { NoStore = true } }
    };

    private sealed class FixedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(respond(request));
    }

    private static string CreateProjectWriterToken()
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            Issuer,
            "orchestrator",
            [],
            now.AddMinutes(-1),
            now.AddMinutes(10));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class ProjectsHandler(
        AuthorizedRunSelection selection,
        string projectWriterToken = "") : HttpMessageHandler
    {
        public string ProjectWriterToken { get; set; } = projectWriterToken;
        public ProjectsAuthorizationContext ProjectWriterAuthority { get; set; } = new(
            1, Issuer, Subject, Tenant, 1, null, null,
            [new ProjectsAuthority("project", Project, [new("writeProjects", 1)])]);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(Response(
                request,
                request.RequestUri!.AbsolutePath == "/api/authorization/context"
                    ? request.Headers.Authorization?.Parameter == ProjectWriterToken
                        ? ProjectWriterAuthority
                        : selection.Authorization
                    : selection.Selection.Snapshot));
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow = _utcNow.Add(amount);
    }

    private sealed class MaterialHandler : HttpMessageHandler
    {
        public Dictionary<Guid, SessionMaterialReadResult> Results { get; } = [];
        public int FailReads { get; set; }
        public bool PauseOnCache { get; set; }
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (FailReads > 0)
            {
                FailReads--;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    RequestMessage = request,
                    Headers = { CacheControl = new() { NoStore = true } }
                };
            }
            if (PauseOnCache && request.RequestUri!.AbsolutePath.EndsWith("/SdkCache", StringComparison.Ordinal))
            {
                PauseOnCache = false;
                Paused.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            var parts = request.RequestUri!.AbsolutePath.Split('/');
            var id = Guid.Parse(parts[^2]);
            return Response(request, Results[id]);
        }
    }

    private sealed class HostClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class HostHandler(RuntimeRegistration registration, MaterialHandler materials) : HttpMessageHandler
    {
        private readonly Dictionary<Guid, RuntimeA2AResponse> _responses = [];
        public WorkflowGenerationJob? Job { get; set; }
        public int NativeTurns { get; private set; }
        public List<Guid> Requests { get; } = [];
        public bool LoseResponse { get; set; }
        public TaskCompletionSource TurnAdmitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseTurn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Output { get; set; } = JsonSerializer.Serialize(
            WorkflowTestData.Definition(
                WorkflowDefinitionOrigin.Generated, WorkflowTestData.Fixed("setup", 0, [], ["docs/plan.md"])),
            JsonOptions);

        public void ForgetResponses() => _responses.Clear();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var grant = new RuntimeGrantReceipt(
                Guid.Parse("ad450d07-7317-45bc-88aa-df9b9be0e6f4"), registration.RuntimeInstanceId,
                registration.Revision, 1, Issuer, RuntimeCredentialPurpose.Observe,
                registration.Binding.ObservationEndpoint, RuntimeCredentialState.Active,
                new string('d', 64), registration.ExpiresAt, registration.ExpiresAt.AddMinutes(-1));
            if (request.RequestUri!.AbsolutePath.EndsWith("/readiness", StringComparison.Ordinal))
            {
                var phases = Enum.GetValues<SandboxStartupPhase>()
                    .Select(phase => new SandboxStartupPhaseObservation(
                        phase, 1, DateTimeOffset.UtcNow,
                        phase == SandboxStartupPhase.ImageReady ? registration.Binding.Image!.Digest : null,
                        phase == SandboxStartupPhase.ImageReady ? registration.Binding.Image!.CompressedPullBytes : null))
                    .ToImmutableArray();
                return Response(request, new RuntimeHostReadinessReceipt(
                    1, registration.RuntimeInstanceId, registration.Revision,
                    registration.Binding.ExecutionFence, registration.Binding.Image!, phases, grant));
            }

            var send = (await request.Content!.ReadFromJsonAsync<RuntimeA2ASendRequest>(JsonOptions, token))!;
            var requestId = send.Message.MessageId;
            Requests.Add(requestId);
            if (!_responses.TryGetValue(requestId, out var result))
            {
                Assert.NotNull(Job);
                Assert.Equal(Job.NativeRequestId, requestId);
                NativeTurns++;
                var completion = WorkflowGenerationJobStorePostgresTests.CreateCompletion(
                    Job, registration, grant, Output);
                materials.Results[completion.UserMaterial.EventId] =
                    new(completion.UserMaterial, Encoding.UTF8.GetBytes(Job.Prompt));
                materials.Results[completion.AssistantMaterial.EventId] =
                    new(completion.AssistantMaterial, Encoding.UTF8.GetBytes(Output));
                materials.Results[completion.CacheMaterial.EventId] =
                    new(completion.CacheMaterial, Encoding.UTF8.GetBytes("cache"));
                result = new RuntimeA2AResponse(
                    "message", Guid.NewGuid(), RuntimeContractValidation.NativeSessionId(registration.Binding),
                    "agent", [new("text", Output)]) { RootCompletion = completion };
                _responses.Add(requestId, result);
            }
            if (LoseResponse)
            {
                TurnAdmitted.TrySetResult();
                await ReleaseTurn.Task.WaitAsync(token);
                throw new HttpRequestException("The test Host response was lost.");
            }
            return Response(request, result);
        }
    }
}
