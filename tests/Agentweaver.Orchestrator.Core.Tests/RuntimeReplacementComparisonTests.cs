using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class RuntimeReplacementComparisonTests(
    CoordinationPostgresFixture fixture) : IAsyncLifetime
{
    private const string Tenant = "tenant-1";
    private const string Project = "project-1";
    private readonly string _schema = "runtime_compare_" + Guid.NewGuid().ToString("N");
    private readonly CoordinationActor _actor = new(
        "https://identity.example/", Guid.NewGuid().ToString("D"));
    private CoordinationOwnerStore _owner = null!;
    private CoordinatorDecisionOwnerStore _decisions = null!;
    private CoordinatorRunSelectionContextStore _contexts = null!;
    private AuthorizedRunSelection _selection = null!;
    private P1ProjectSetupReceiptSnapshot _receipt = null!;

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        var catalog = ProviderCatalog.Create([], [], []).Value!;
        _contexts = new CoordinatorRunSelectionContextStore(
            fixture.DataSource, _schema, catalog, new ProviderResolver(catalog), []);
        _decisions = new CoordinatorDecisionOwnerStore(
            fixture.DataSource, _schema, _contexts, TimeProvider.System);
        _owner = new CoordinationOwnerStore(
            fixture.DataSource, _schema, TimeProvider.System, _decisions);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ReplacementReadsAcceptedDispatchAndStoredCheckpointResultWithoutClaimingEffectProof()
    {
        var requestRow = await CreateAndAcceptP1RunAsync();
        var p1 = await _owner.ReadCurrentP1InvocationBindingAsync(
            _actor, _selection, CancellationToken.None);
        var root = new SessionIdentity(Project, requestRow.RunId, requestRow.RootSessionId!);
        const string workPlanId = "work-plan";
        const string associationId = "association";
        const string resultText = "Stored child result";
        var childSessionId = "child-" + Guid.NewGuid().ToString("N");
        var spawned = await _owner.SpawnSessionAsync(
            _actor, root,
            new SpawnSessionRequest(
                childSessionId, CoordinationSessionKind.ChildWork, childSessionId, "Complete accepted work"),
            10, 10, CancellationToken.None);

        var checkpointBinding = new MafCheckpointBinding(
            root, _actor, p1.ExecutionFence, MafExecutionCheckpointStore.CurrentSdkVersion,
            "model:provider-alpha", null);
        var checkpointStore = new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(fixture.DataSource, _schema, null), checkpointBinding);
        var state = new MafExecutionCheckpoint(
            1, workPlanId, p1.RootStateVersion,
            MafExecutionProgress.Empty with
            {
                WorkItems = ImmutableDictionary<string, MafExecutionTaskStatus>.Empty
                    .WithComparers(StringComparer.Ordinal)
                    .Add(associationId, MafExecutionTaskStatus.Succeeded)
            })
        {
            Results = ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(associationId, resultText)
        };
        var checkpoint = await checkpointStore.AppendAsync(
            "checkpoint-" + Guid.NewGuid().ToString("N"), null, state, CancellationToken.None);
        var presenting = await _owner.AdvanceTurnBoundaryAsync(
            _actor, spawned.Node.Identity, p1.ExecutionFence, spawned.Node.StateVersion, CancellationToken.None);
        var active = await _owner.CompleteTurnBoundaryAsync(
            _actor, spawned.Node.Identity, p1.ExecutionFence, spawned.Node.StateVersion,
            presenting.StateVersion, null, CancellationToken.None);
        _ = await _owner.FinishTurnAsync(
            _actor, spawned.Node.Identity,
            new FinishTurnRequest(p1.ExecutionFence, active.StateVersion, LogicalTurnCompletion.Completed),
            CancellationToken.None);

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var registrations = new RuntimeRegistrationStore(fixture.DataSource, _schema, TimeProvider.System);
        var originalBinding = RuntimeBinding(p1, requestRow.RunId, requestRow.RootSessionId!, "placement-original");
        var original = await registrations.RegisterAsync(
            originalBinding, expiresAt, CancellationToken.None);
        var replacementBinding = originalBinding with { PlacementUid = "placement-replacement" };
        var replacement = await registrations.ReplaceAsync(
            original, Guid.NewGuid(), replacementBinding, expiresAt, CancellationToken.None);
        var dispatchId = Guid.NewGuid();
        await InsertDispatchAsync(
            p1, root, spawned.Node.Identity.SessionId, dispatchId, workPlanId, associationId,
            checkpoint.Info.CheckpointId, replacementBinding, original);

        var options = new OrchestratorOptions(
            _actor.Issuer, "orchestrator", "https://projects.example/", "projects",
            "https://events.example/", "events", _schema);
        var grants = new ExecutableActionGrantOwnerStore(
            fixture.DataSource, options, TimeProvider.System, null!, new HttpContextAccessor());
        var request = new RuntimeReplacementComparisonRequest(
            replacement.RuntimeInstanceId, replacement.Revision, p1.ExecutionFence,
            dispatchId, p1.SetupInvocationId.ToString("D"));

        var result = await grants.ReadReplacementComparisonBlockerAsync(
            replacement, request, p1, _selection, CancellationToken.None);
        Assert.Equal("blocked", result.Status);
        Assert.Equal(original.RuntimeInstanceId, result.OriginalRuntimeInstanceId);
        Assert.Equal(original.Revision, result.OriginalRegistrationRevision);
        Assert.Equal(replacement.RuntimeInstanceId, result.CurrentRuntimeInstanceId);
        Assert.Equal(replacement.Revision, result.CurrentRegistrationRevision);
        Assert.Equal(checkpoint.Info.CheckpointId, result.ResultCheckpointId);
        Assert.Equal(checkpoint.State.Revision, result.ResultCheckpointRevision);
        Assert.NotNull(result.CheckpointParent);
        Assert.Equal(checkpoint.HasParentMetadata, result.CheckpointParent.HasParentMetadata);
        Assert.Equal(checkpoint.ParentCheckpointId, result.CheckpointParent.ParentCheckpointId);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(resultText))),
            result.ResultDigest!.Sha256);
        Assert.Equal(Encoding.UTF8.GetByteCount(resultText), result.ResultDigest.Utf8ByteLength);
        Assert.Contains("host.events.effect-receipt", result.MissingProvenance);
        Assert.Contains("orchestrator.no-duplicate-send-event-effect", result.MissingProvenance);

        var coreInvocation = await grants.ReadReplacementComparisonBlockerAsync(
            replacement,
            request with { InvocationId = p1.CoreInvocationId.ToString("D") },
            p1, _selection, CancellationToken.None);
        Assert.Equal(result.DispatchId, coreInvocation.DispatchId);

        var retry = await grants.ReadReplacementComparisonBlockerAsync(
            replacement, request, p1, _selection, CancellationToken.None);
        Assert.Equal(result.ResultCheckpointId, retry.ResultCheckpointId);
        Assert.Equal(result.ResultDigest, retry.ResultDigest);
        Assert.Equal(result.MissingProvenance, retry.MissingProvenance);
        Assert.Equal(1L, await CountAsync("maf_execution_dispatches"));

        var missingResultDispatchId = Guid.NewGuid();
        await InsertDispatchAsync(
            p1, root, spawned.Node.Identity.SessionId, missingResultDispatchId, workPlanId,
            associationId + "-missing-result", checkpoint.Info.CheckpointId, replacementBinding, original);
        var missingResult = await grants.ReadReplacementComparisonBlockerAsync(
            replacement,
            request with { DispatchId = missingResultDispatchId },
            p1, _selection, CancellationToken.None);
        Assert.Equal("blocked", missingResult.Status);
        Assert.Null(missingResult.ResultDigest);
        Assert.Contains("orchestrator.result-hash-binding", missingResult.MissingProvenance);

        var invented = request with { InvocationId = Guid.NewGuid().ToString("D") };
        var inventedError = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            grants.ReadReplacementComparisonBlockerAsync(
                replacement, invented, p1, _selection, CancellationToken.None));
        Assert.Equal("runtime_replacement_comparison_conflict", inventedError.Code);

        var foreignBinding = p1 with { ActorSubject = Guid.NewGuid().ToString("D") };
        var foreignError = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            grants.ReadReplacementComparisonBlockerAsync(
                replacement, request, foreignBinding, _selection, CancellationToken.None));
        Assert.Equal("runtime_replacement_comparison_conflict", foreignError.Code);

        var foreignTenant = p1 with { TenantId = "foreign-tenant" };
        var tenantError = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            grants.ReadReplacementComparisonBlockerAsync(
                replacement, request, foreignTenant, _selection, CancellationToken.None));
        Assert.Equal("runtime_replacement_comparison_conflict", tenantError.Code);

        using var changedSnapshot = JsonDocument.Parse(
            """{"modelSelection":{"reference":"model:other"}}""");
        var changedSelection = _selection with
        {
            Selection = _selection.Selection with { Snapshot = changedSnapshot.RootElement.Clone() }
        };
        var selectionError = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            grants.ReadReplacementComparisonBlockerAsync(
                replacement, request, p1, changedSelection, CancellationToken.None));
        Assert.Equal("runtime_replacement_comparison_conflict", selectionError.Code);

        var changedFence = request with { ExecutionFence = request.ExecutionFence + 1 };
        var fenceError = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            grants.ReadReplacementComparisonBlockerAsync(
                replacement, changedFence, p1, _selection, CancellationToken.None));
        Assert.Equal("runtime_replacement_comparison_conflict", fenceError.Code);

        var absentDispatch = request with { DispatchId = Guid.NewGuid() };
        var absentError = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            grants.ReadReplacementComparisonBlockerAsync(
                replacement, absentDispatch, p1, _selection, CancellationToken.None));
        Assert.Equal("runtime_replacement_comparison_conflict", absentError.Code);

        var revokedDuringProjectsWait = 0;
        using var projectsHttp = new HttpClient(new ProjectsHandler(
            _selection,
            async () =>
            {
                if (Interlocked.Exchange(ref revokedDuringProjectsWait, 1) == 0)
                    _ = await registrations.RevokeAsync(
                        replacement.RuntimeInstanceId, replacement.Revision, CancellationToken.None);
            }));
        var projects = new ProjectsRunSelectionClient(
            projectsHttp, options, new ReviewedRemoteToolSnapshotStore(fixture.DataSource, _schema));
        using var environmentHttp = new HttpClient(new OwnerHttpHandler(
            requestMessage => OwnerResponse(requestMessage, RuntimePlacement(replacement))));
        var registrationOwner = new RuntimeRegistrationOwner(
            options, projects, _owner, _decisions, _contexts,
            new PostgresMafCheckpointStore(fixture.DataSource, _schema, null),
            registrations,
            new RuntimeEnvironmentContextClient(
                environmentHttp, new(new Uri("https://environment.example/"))),
            TimeProvider.System);
        var actionOwner = new RuntimeActionOwner(
            registrationOwner, projects, grants,
            null!, null!, null!, null!, null!, null!, options, _owner);
        var contextServices = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(
                new OwnerAuthentication(DateTimeOffset.UtcNow.AddMinutes(5)))
            .BuildServiceProvider();
        var context = RuntimeHttpContext(contextServices, _actor, Project, requestRow.RunId, options);
        var revokedError = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            actionOwner.CompareReplacementAsync(context, request, CancellationToken.None));
        Assert.Equal("runtime_registration_unavailable", revokedError.Code);
        Assert.Equal(1, revokedDuringProjectsWait);
    }

    private async Task<CoordinationOwnerStore.ProjectRunRequestRow> CreateAndAcceptP1RunAsync()
    {
        _receipt = new(
            Guid.NewGuid(), _actor.Issuer, _actor.Subject, Tenant, Guid.NewGuid(), 1,
            Project, _actor.Subject, 1, 1, Guid.NewGuid(), 1, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var request = new CreateProjectRunRequest(
            "Start from P1 setup", 1, P1SetupInvocationId: _receipt.InvocationId);
        var row = await _owner.CreateProjectRunAsync(
            _actor, Tenant, Project, "compare-p1", request, CancellationToken.None);
        _selection = Selection(row.RunId);

        Task Bind(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CoordinationOwnerStore.ProjectRunRequestRow pending,
            CancellationToken token) =>
            _owner.AcceptP1InteractiveRootInTransactionAsync(
                connection, transaction, _actor, _selection, pending, _receipt, token);

        _ = await _owner.StartProjectRunAsync(
            _actor, Tenant, Project, row.RunId, (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask, CancellationToken.None, acceptRootInTransaction: Bind);
        return row;
    }

    private RuntimeBinding RuntimeBinding(
        P1InvocationBinding p1, string runId, string rootSessionId, string placementUid) =>
        new(_actor.Issuer, _actor.Subject, Tenant, Project, runId, rootSessionId,
            RuntimeActionContract.CoordinatorRootStepId, "turn-1", 1, 1, 1, "context-v1",
            p1.AcceptedSelectionHash, p1.ExecutionFence,
            "environment-1", placementUid, 1, "profile-1",
            new Uri("https://runtime.example/configure"),
            new Uri("https://runtime.example/observe"))
        {
            EnvironmentCurrentFencingGeneration = 1,
            EnvironmentProviderFencingGeneration = 1,
            ModelSelectionReference = "model:provider-alpha",
            PlacementProviderId = "sandbox-provider",
            EnvironmentLifecycleGeneration = 1,
            EnvironmentLeaseRevision = 1,
            Image = new SandboxImageIdentity("sha256:" + new string('a', 64), "linux/amd64", 1024)
        };

    private async Task InsertDispatchAsync(
        P1InvocationBinding p1,
        SessionIdentity root,
        string childSessionId,
        Guid dispatchId,
        string workPlanId,
        string associationId,
        string checkpointId,
        RuntimeBinding binding,
        RuntimeRegistration original)
    {
        var runtimeBindingHash = RuntimeContractValidation.Hash(
            JsonSerializer.SerializeToUtf8Bytes(binding));
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".maf_execution_dispatches
                (project_id, run_id, dispatch_id, root_session_id, child_session_id,
                 work_plan_id, association_id, checkpoint_store_name, checkpoint_id,
                 checkpoint_revision, decision_state_version, execution_fence,
                 accepted_selection_hash, prompt_hash, request_hash, runtime_instance_id,
                 registration_revision, runtime_binding_hash, sdk_source_receipt_hash,
                 model_selection_reference, selected_model_id, is_cost_producing, dispatch_state)
            VALUES
                (@project, @run, @dispatch, @root, @child, @plan, @association,
                 'coordinator-execution', @checkpoint, 1, @decision, @fence,
                 @selection, @prompt, @request, @runtime, @registration,
                 @bindingHash, @sourceHash, 'model:provider-alpha', 'provider-alpha',
                 false, 'completed')
            """, connection);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, root.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, root.RunId);
        command.Parameters.AddWithValue("dispatch", NpgsqlDbType.Uuid, dispatchId);
        command.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, root.SessionId);
        command.Parameters.AddWithValue("child", NpgsqlDbType.Varchar, childSessionId);
        command.Parameters.AddWithValue("plan", NpgsqlDbType.Varchar, workPlanId);
        command.Parameters.AddWithValue("association", NpgsqlDbType.Varchar, associationId);
        command.Parameters.AddWithValue("checkpoint", NpgsqlDbType.Varchar, checkpointId);
        command.Parameters.AddWithValue("decision", NpgsqlDbType.Bigint, p1.RootStateVersion);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, p1.ExecutionFence);
        command.Parameters.AddWithValue("selection", NpgsqlDbType.Char, p1.AcceptedSelectionHash);
        command.Parameters.AddWithValue("prompt", NpgsqlDbType.Char, new string('a', 64));
        command.Parameters.AddWithValue("request", NpgsqlDbType.Char, new string('b', 64));
        command.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, original.RuntimeInstanceId);
        command.Parameters.AddWithValue("registration", NpgsqlDbType.Bigint, original.Revision);
        command.Parameters.AddWithValue("bindingHash", NpgsqlDbType.Char, runtimeBindingHash);
        command.Parameters.AddWithValue("sourceHash", NpgsqlDbType.Char, new string('c', 64));
        await command.ExecuteNonQueryAsync();
    }

    private AuthorizedRunSelection Selection(string runId)
    {
        using var runSnapshot = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            projectId = Project,
            runId,
            projectRevision = 1,
            projectConfigurationRevision = 1,
            platformRuntimeRevision = 1,
            contextRevision = "context-v1",
            source = "projects-config",
            modelSelection = new { reference = "model:provider-alpha" },
            projectConfiguration = new
            {
                blueprintWorkflowReferences = Array.Empty<object>(),
                agentCharters = Array.Empty<object>(),
                casting = Array.Empty<object>()
            }
        }));
        var selection = new EffectiveRunSelection(
            Project, runId, 1, 1, 1, "context-v1", runSnapshot.RootElement.Clone());
        return new(selection, new ProjectsAuthorizationContext(
            1, _actor.Issuer, _actor.Subject, Tenant, 1, Project, runId,
            [new ProjectsAuthority("project", Project,
                [
                    new ProjectsPermissionGrant("acceptRunSelection", 1),
                    new ProjectsPermissionGrant("readRunSelection", 1)
                ])]));
    }

    private static EnvironmentRuntimeBootstrapContext RuntimePlacement(RuntimeRegistration registration)
    {
        var binding = registration.Binding;
        var owner = new RuntimeOwnerContext(
            1, binding.ActorIssuer, binding.ActorId, binding.TenantId, binding.ProjectId, binding.RunId,
            binding.SessionId, binding.AgentId, binding.ModelSelectionReference!, binding.TurnId,
            binding.ProjectRevision, binding.ProjectConfigurationRevision, binding.PlatformRuntimeRevision,
            binding.ContextRevision, binding.AcceptedSelectionHash, binding.ExecutionFence, 1, 1, 1)
        {
            WorkflowStepId = binding.WorkflowStepId,
            ModelCredentialReference = binding.ModelCredentialReference,
            ModelSourceMode = binding.ModelSourceMode,
            ReasoningEffort = binding.ReasoningEffort,
            ModelBindingPin = binding.ModelBindingPin,
            ModelConnectionId = binding.ModelConnectionId,
            ModelConnectionScope = binding.ModelConnectionScope,
            MaxModelTurns = binding.MaxModelTurns,
            MaxToolCalls = binding.MaxToolCalls,
            MaxPromptTokens = binding.MaxPromptTokens,
            MaxRevisionAttempts = binding.MaxRevisionAttempts,
            CopilotSoftCreditLimit = binding.CopilotSoftCreditLimit,
            CopilotHardCreditLimit = binding.CopilotHardCreditLimit
        };
        return new(1, binding.TenantId, binding.ProjectId, binding.RunId, binding.EnvironmentId,
            binding.EnvironmentLifecycleGeneration, binding.EnvironmentCurrentFencingGeneration,
            binding.EnvironmentProviderFencingGeneration, binding.EnvironmentLeaseRevision,
            registration.ExpiresAt,
            new(ProviderSeam.Sandbox, binding.PlacementProviderId!, binding.PlacementUid,
                binding.PlacementGeneration),
            new(Guid.NewGuid()), new("placement"), binding.ProfileId,
            binding.ConfigureEndpoint, binding.ObservationEndpoint)
        {
            RuntimeOwnerContext = owner,
            Image = binding.Image
        };
    }

    private static HttpContext RuntimeHttpContext(
        IServiceProvider services, CoordinationActor actor, string projectId, string runId,
        OrchestratorOptions options)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new(new ClaimsIdentity(
            [
                new("sub", actor.Subject), new("iss", actor.Issuer),
                new("aud", options.ProjectsAudience), new("project_id", projectId),
                new("run_id", runId), new("scope", "api.read projects.orchestrator")
            ], "replacement-comparison-test"))
        };
        context.Request.Headers.Authorization = "Bearer controlled-owner";
        context.Request.Headers["X-Agentweaver-Tenant"] = Tenant;
        return context;
    }

    private sealed class OwnerAuthentication(DateTimeOffset expiresAt) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                context.User, new AuthenticationProperties { ExpiresUtc = expiresAt },
                "replacement-comparison-test")));
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

    private sealed class OwnerHttpHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class ProjectsHandler(
        AuthorizedRunSelection selection,
        Func<Task> onSelection) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath.EndsWith("/selection", StringComparison.Ordinal))
                await onSelection().ConfigureAwait(false);
            object body = request.RequestUri!.AbsolutePath == "/api/authorization/context"
                ? selection.Authorization
                : selection.Selection.Snapshot;
            return OwnerResponse(request, body);
        }
    }

    private static readonly JsonSerializerOptions OwnerJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static HttpResponseMessage OwnerResponse(HttpRequestMessage request, object body) => new(HttpStatusCode.OK)
    {
        RequestMessage = request,
        Content = JsonContent.Create(body, body.GetType(), options: OwnerJson),
        Headers = { CacheControl = new() { NoStore = true } }
    };

    private async Task<long> CountAsync(string table)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{_schema}\".{table}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
