using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class RuntimeNumericLimitsPostgresTests(CoordinationPostgresFixture fixture) : IAsyncLifetime
{
    private readonly string _schema = "numeric_" + Guid.NewGuid().ToString("N");
    private readonly CoordinationActor _actor = new("https://identity.example/", Guid.NewGuid().ToString("D"));
    private readonly RuntimeRegistration[] _registrations = new RuntimeRegistration[2];
    private readonly RuntimeGrantReceipt[] _grants = new RuntimeGrantReceipt[2];
    private readonly SdkSessionFacts[] _sources = new SdkSessionFacts[2];
    private readonly RuntimeA2ASendRequest[] _messages = new RuntimeA2ASendRequest[2];
    private readonly MafExecutionDispatchIntent[] _intents = new MafExecutionDispatchIntent[2];
    private AuthorizedRunSelection _selection = null!;
    private OrchestratorOptions _options = null!;
    private SessionIdentity _root;
    private MafExecutionCheckpointSnapshot _checkpoint = null!;
    private HttpClient _projectsHttp = null!;
    private HttpContext _context = null!;
    private CoordinatorDecisionOwnerStore _decisions = null!;

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        var run = Guid.NewGuid().ToString("N");
        var snapshot = JsonSerializer.SerializeToElement(new
        {
            projectId = "numeric-project", runId = run, projectRevision = 1,
            projectConfigurationRevision = 1, platformRuntimeRevision = 1, contextRevision = "context-1",
            modelSelection = new { reference = "model:selected" }
        });
        _selection = new(new("numeric-project", run, 1, 1, 1, "context-1", snapshot),
            new(1, _actor.Issuer, _actor.Subject, "tenant-1", 1, "numeric-project", run,
                [new("project", "numeric-project", [new("acceptRunSelection", 1)])]));
        _options = new(_actor.Issuer, "orchestrator", "https://projects.test/", "projects",
            "https://events.test/", "events", _schema);
        _context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", _actor.Subject), new Claim("aud", _options.ProjectsAudience),
                new Claim("project_id", _selection.Selection.ProjectId), new Claim("run_id", run),
                new Claim("scope", "api.read projects.orchestrator")
            ], "controlled-owner"))
        };
        _context.Request.Headers.Authorization = "Bearer controlled-owner";
        _context.Request.Headers["X-Agentweaver-Tenant"] = "tenant-1";
        _projectsHttp = new HttpClient(new ProjectsHandler(_selection));
        var coordination = new CoordinationOwnerStore(fixture.DataSource, _schema);
        var accepted = await coordination.AcceptRootAsync(_actor, _selection, "root", default);
        _root = new(_selection.Selection.ProjectId, run, accepted.RootSessionId);
        var catalog = ProviderCatalog.Create([], [], []).Value!;
        var contexts = new CoordinatorRunSelectionContextStore(
            fixture.DataSource, _schema, catalog, new ProviderResolver(catalog), []);
        _decisions = new(fixture.DataSource, _schema, contexts);
        var decision = await _decisions.InitializeRootAsync(_actor, _root, _selection, default);
        for (var index = 0; index < _registrations.Length; index++)
        {
            var child = await coordination.RegisterChildAsync(
                _actor, _root, $"child-{index}", 10, 10, default);
            var binding = new RuntimeBinding(_actor.Issuer, _actor.Subject, "tenant-1",
                _root.ProjectId, _root.RunId, child.Identity.SessionId, "agent-1", "turn-1",
                1, 1, 1, "context-1", RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(snapshot.GetRawText())),
                accepted.ExecutionFence, "environment-1", $"placement-{index}", 1, "profile-1",
                new("https://runtime.test/runtime/v1/configure"), new("https://runtime.test/observation"))
            {
                WorkflowStepId = "implement", ModelSelectionReference = "model:selected",
                ModelSourceMode = ModelSourceMode.HostedCopilot,
                EnvironmentCurrentFencingGeneration = 1, EnvironmentProviderFencingGeneration = 1,
                MaxModelTurns = 1, MaxToolCalls = 1
            };
            await RegisterSourceAsync(index, binding);
        }
        // The fixture supplies active owner turns, not a fabricated native send or usage result.
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"UPDATE \"{_schema}\".coordination_sessions SET turn_state = 'active' WHERE parent_session_id IS NOT NULL",
            connection))
            Assert.Equal(2, await command.ExecuteNonQueryAsync());
        await WriteCheckpointAsync(accepted.ExecutionFence, decision.StateVersion, "numeric-checkpoint", _intents);
    }

    public async Task DisposeAsync()
    {
        _projectsHttp?.Dispose();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task NativeSendReservationSurvivesRestartAndReplayWithoutResendingOrRefunding()
    {
        Assert.True(await PrepareAsync(0));
        Assert.True(await PrepareAsync(0));
        Assert.Equal(1, await CountAsync("maf_execution_dispatches"));
        var admission = await BeginAsync(0);
        Assert.NotNull(admission);
        Assert.Equal(_intents[0].MessageId, admission.MessageId);
        Assert.Equal("runtime_native_turn_indeterminate",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => BeginAsync(0))).Code);
        Assert.Equal("runtime_native_turn_indeterminate",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => PrepareAsync(0))).Code);
        Assert.Equal("runtime_model_turn_limit_exhausted",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => PrepareAsync(1))).Code);
        Assert.Equal(1, await CountAsync("maf_execution_dispatches"));
    }

    [Fact]
    public async Task ConcurrentNativeRuntimesCannotReserveBeyondTheWholeRunModelCap()
    {
        var admitted = await Task.WhenAll(
            TryReserveAsync(() => PrepareAsync(0), "runtime_model_turn_limit_exhausted"),
            TryReserveAsync(() => PrepareAsync(1), "runtime_model_turn_limit_exhausted"));
        Assert.Single(admitted, value => value);
        Assert.Equal(1, await CountAsync("maf_execution_dispatches"));
    }

    [Fact]
    public async Task ModelGrantRequiresTheExactPreparedDispatchRuntimeAndPrompt()
    {
        Assert.True(await PrepareAsync(0));
        var request = Action(0, toolInvocation: false) with
        {
            ActionId = "model.turn", DispatchId = _intents[0].MessageId, InputHash = _intents[0].PromptHash
        };
        await IssueAsync(0, request);
        foreach (var changed in new[]
        {
            request with { EventId = Guid.NewGuid(), DispatchId = Guid.NewGuid() },
            request with { EventId = Guid.NewGuid(), InputHash = new string('b', 64) },
            request with
            {
                EventId = Guid.NewGuid(), RuntimeInstanceId = _registrations[1].RuntimeInstanceId
            }
        })
        {
            var index = changed.RuntimeInstanceId == _registrations[0].RuntimeInstanceId ? 0 : 1;
            Assert.Equal("runtime_model_turn_admission_required",
                (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => IssueAsync(index, changed))).Code);
        }
        Assert.Equal(1, await CountAsync("executable_action_grants"));
    }

    [Fact]
    public async Task PermissionCheckDoesNotDebitAndExactToolRetrySurvivesRestartAtTheCap()
    {
        var permission = Action(0, toolInvocation: false);
        await IssueAsync(0, permission);
        Assert.Equal(0, await CountToolReservationsAsync());
        var invocation = Action(0, toolInvocation: true);
        var issued = await IssueAsync(0, invocation);
        Assert.Equal(issued, await IssueAsync(0, invocation));
        await GrantStore().RequireToolInvocationReservationAsync(_registrations[0], invocation, default);
        Assert.Equal(1, await CountToolReservationsAsync());
        Assert.Equal("runtime_tool_call_limit_exhausted",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                IssueAsync(0, invocation with { EventId = Guid.NewGuid() }))).Code);
        Assert.Equal("runtime_tool_invocation_admission_required",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                GrantStore().RequireToolInvocationReservationAsync(
                    _registrations[0], Action(0, toolInvocation: true), default))).Code);
        Assert.Equal(1, await CountToolReservationsAsync());
        Assert.Equal(2, await CountAsync("executable_action_grants"));
    }

    [Fact]
    public async Task ConcurrentToolRuntimesReserveOnlyOneWholeRunInvocation()
    {
        async Task<bool> ReserveAsync(int index)
        {
            await IssueAsync(index, Action(index, toolInvocation: true));
            return true;
        }
        var admitted = await Task.WhenAll(
            TryReserveAsync(() => ReserveAsync(0), "runtime_tool_call_limit_exhausted"),
            TryReserveAsync(() => ReserveAsync(1), "runtime_tool_call_limit_exhausted"));
        Assert.Single(admitted, value => value);
        Assert.Equal(1, await CountToolReservationsAsync());
        Assert.Equal(1, await CountAsync("executable_action_grants"));
    }

    [Fact]
    public async Task ReusedToolEventCannotChangeItsInputOrBecomeAPermissionOnlyRequest()
    {
        var invocation = Action(0, toolInvocation: true);
        await IssueAsync(0, invocation);
        foreach (var changed in new[]
        {
            invocation with { InputHash = new string('b', 64) },
            invocation with { IsToolInvocation = false }
        })
            Assert.Equal("runtime_action_conflict",
                (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => IssueAsync(0, changed))).Code);
        Assert.Equal(1, await CountToolReservationsAsync());
    }

    [Theory]
    [InlineData("model")]
    [InlineData("tool")]
    public async Task WholeRunQuotaSurvivesOwnerRecoveryAndANewRuntimeUnderANewFence(string kind)
    {
        if (kind == "model")
            Assert.True(await PrepareAsync(0));
        else
            await IssueAsync(0, Action(0, toolInvocation: true));
        var coordination = new CoordinationOwnerStore(fixture.DataSource, _schema, TimeProvider.System, _decisions);
        var status = await coordination.ReadOwnerRunStatusAsync(_actor, _root.ProjectId, _root.RunId, default);
        var child = new SessionIdentity(_root.ProjectId, _root.RunId, _registrations[0].Binding.SessionId);
        var failed = await coordination.ReportRunFailureAsync(_actor, child,
            new(status.ExecutionFence, status.StateVersion, 1, "numeric-failure",
                OwnerRunFailureState.Indeterminate, "runtime_lost", "numeric-attempt"), default, _selection);
        var recovered = await coordination.RecoverRunExecutionAsync(_actor, _root.ProjectId, _root.RunId,
            new(failed.ExecutionFence, failed.RunStateVersion, "numeric-recovery",
                "operator_recovery", "numeric-recovery"), default, _selection);
        Assert.True(recovered.ExecutionFence > _registrations[1].Binding.ExecutionFence);
        await RegisterSourceAsync(1, _registrations[1].Binding with { ExecutionFence = recovered.ExecutionFence });
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"UPDATE \"{_schema}\".coordination_sessions SET turn_state = 'active' WHERE session_id = 'child-1'",
            connection))
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        var current = await _decisions.InitializeRootAsync(_actor, _root, _selection, default);
        await WriteCheckpointAsync(recovered.ExecutionFence, current.StateVersion,
            "numeric-recovered-checkpoint", [_intents[1]]);
        if (kind == "model")
        {
            Assert.Equal("runtime_model_turn_limit_exhausted",
                (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => PrepareAsync(1))).Code);
            Assert.Equal(1, await CountAsync("maf_execution_dispatches"));
        }
        else
        {
            Assert.Equal("runtime_tool_call_limit_exhausted",
                (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                    IssueAsync(1, Action(1, toolInvocation: true)))).Code);
            Assert.Equal(1, await CountToolReservationsAsync());
        }
    }

    private async Task RegisterSourceAsync(int index, RuntimeBinding binding)
    {
        var registration = await new RuntimeRegistrationStore(fixture.DataSource, _schema, TimeProvider.System)
            .RegisterAsync(binding, DateTimeOffset.UtcNow.AddHours(1), default);
        _registrations[index] = registration;
        var grant = new RuntimeGrantReceipt(Guid.NewGuid(), registration.RuntimeInstanceId, registration.Revision,
            1, _actor.Issuer, RuntimeCredentialPurpose.Observe, binding.ObservationEndpoint,
            RuntimeCredentialState.Active, new string('a', 64), registration.ExpiresAt, DateTimeOffset.UtcNow);
        _grants[index] = grant;
        var source = new SdkSessionFacts(registration.RuntimeInstanceId,
            RuntimeContractValidation.NativeSessionId(binding), "1.0.18", "1.0.79", "model:selected",
            "controlled-model", new string('c', 64), 1, "hosted-copilot", SdkMeterSources.CopilotNanoAiu,
            binding.AcceptedSelectionHash, registration.Revision);
        _sources[index] = source;
        var store = SourceStore();
        await store.ExecuteLockedAsync(registration.RuntimeInstanceId, async (connection, transaction, token) =>
        {
            var receipt = await store.RegisterWithinTransactionAsync(
                connection, transaction, registration, grant, source, token);
            await transaction.CommitAsync(token);
            return receipt;
        }, default);
        var prompt = $"Execute work item {index}.";
        var intent = new MafExecutionDispatchIntent($"item-{index}", binding.SessionId,
            Guid.NewGuid(), RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(prompt)));
        _intents[index] = intent;
        _messages[index] = new(new("message", intent.MessageId, source.SdkSessionId, "user",
            [new("text", prompt)], new(new(1, registration, grant.GrantId, grant.Revision,
                RuntimeCredentialPurpose.Observe), AddressedMessageDeliveryMode.Enqueue)));
    }

    private async Task WriteCheckpointAsync(
        long fence, long decisionVersion, string checkpointId, MafExecutionDispatchIntent[] intents)
    {
        var state = new MafExecutionCheckpoint(1, checkpointId, decisionVersion,
            MafExecutionProgress.Empty with
            {
                WorkItems = intents.ToImmutableDictionary(
                    intent => intent.AssociationId, _ => MafExecutionTaskStatus.Running)
            })
        {
            PendingDispatches = intents.ToImmutableDictionary(intent => intent.AssociationId)
        };
        var binding = new MafCheckpointBinding(_root, _actor, fence,
            MafExecutionCheckpointStore.CurrentSdkVersion, "model:selected", null,
            MafExecutionCheckpointContract.StoreName);
        _checkpoint = await new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(fixture.DataSource, _schema, null), binding)
            .AppendAsync(checkpointId, null, state, default);
    }

    private RuntimeUsageSourceStore SourceStore() => new(fixture.DataSource, _options, TimeProvider.System);

    private ExecutableActionGrantOwnerStore GrantStore() => new(
        fixture.DataSource, _options, TimeProvider.System, new ProjectsRunSelectionClient(_projectsHttp, _options),
        new HttpContextAccessor { HttpContext = _context });

    private Task<ExecutableActionGrantReference> IssueAsync(int index, RuntimeActionRequest request) =>
        GrantStore().IssueRuntimeActionGrantAsync(
            _context, _registrations[index], request, default, reserveToolInvocation: request.IsToolInvocation);

    private RuntimeActionRequest Action(int index, bool toolInvocation) =>
        new(1, _registrations[index].RuntimeInstanceId, _registrations[index].Revision,
            _registrations[index].Binding.ExecutionFence, Guid.NewGuid(), "tool.read",
            RuntimeContractValidation.Hash("same tool arguments"u8)) { IsToolInvocation = toolInvocation };

    private Task<bool> PrepareAsync(int index)
    {
        var store = SourceStore();
        return store.ExecuteLockedAsync(_registrations[index].RuntimeInstanceId,
            async (connection, transaction, token) =>
            {
                var prepared = await store.PrepareNativeTurnWithinTransactionAsync(
                    connection, transaction, _root, _checkpoint, _intents[index], _registrations[index],
                    _messages[index], NoCreditSnapshot, token);
                await transaction.CommitAsync(token);
                return prepared;
            }, default);
    }

    private Task<RuntimeNativeTurnAdmissionReceipt?> BeginAsync(int index)
    {
        var store = SourceStore();
        return store.ExecuteLockedAsync(_registrations[index].RuntimeInstanceId,
            async (connection, transaction, token) =>
            {
                var receipt = await store.BeginNativeTurnWithinTransactionAsync(
                    connection, transaction, _registrations[index], _grants[index],
                    new(new(_grants[index].GrantId, _registrations[index].RuntimeInstanceId,
                        _grants[index].Revision, RuntimeCredentialPurpose.Observe,
                        _grants[index].Audience, _grants[index].ConfigurationHash,
                        "controlled-store-only", _grants[index].ExpiresAt, Guid.NewGuid()),
                        _messages[index], _sources[index]), NoCreditSnapshot, token);
                await transaction.CommitAsync(token);
                return receipt;
            }, default);
    }

    private static Task<RuntimeUsageCostSnapshotReceipt> NoCreditSnapshot(
        SdkSessionFacts source, CancellationToken token) =>
        throw new InvalidOperationException("Numeric-only admission must not fabricate a Cost receipt.");

    private static async Task<bool> TryReserveAsync(Func<Task<bool>> reserve, string exhaustedCode)
    {
        try
        {
            return await reserve();
        }
        catch (RuntimeAuthorizationException failure)
        {
            Assert.Equal(exhaustedCode, failure.Code);
            return false;
        }
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{_schema}\".{table}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountToolReservationsAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".outbox_events
            WHERE event_type = 'orchestrator.runtime.tool_invocation_admitted'
            """, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class ProjectsHandler(AuthorizedRunSelection selection) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal("controlled-owner", request.Headers.Authorization?.Parameter);
            Assert.Equal("tenant-1", Assert.Single(request.Headers.GetValues("X-Agentweaver-Tenant")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = request.RequestUri!.AbsolutePath == "/api/authorization/context"
                    ? JsonContent.Create(selection.Authorization) : JsonContent.Create(selection.Selection.Snapshot),
                Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
            });
        }
    }
}
