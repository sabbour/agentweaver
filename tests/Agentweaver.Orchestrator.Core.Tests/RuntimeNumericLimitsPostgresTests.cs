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
                PlacementProviderId = "sandbox-provider",
                EnvironmentLifecycleGeneration = 1, EnvironmentLeaseRevision = 1,
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
        Assert.Equal(0, await CountRuntimeGrantsAsync());
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
        Assert.Equal(1, await CountRuntimeGrantsAsync());
    }

    [Fact]
    public async Task PositiveHardCreditGrantRequiresPreparedDispatchWithoutAModelTurnLimit()
    {
        await RegisterPositiveHardCreditAsync(0);
        Assert.Null(_registrations[0].Binding.MaxModelTurns);
        var request = ModelAction(0, dispatchId: null);

        Assert.Equal("runtime_model_turn_admission_required",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => IssueAsync(0, request))).Code);
        Assert.Equal(0, await CountRuntimeGrantsAsync());

        Assert.True(await PrepareAsync(0, PricedSnapshot(0, 0m, 0)));
        await IssueAsync(0, request with { DispatchId = _intents[0].MessageId });
        Assert.Equal(1, await CountRuntimeGrantsAsync());
        Assert.Equal("runtime_model_turn_admission_required",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                IssueAsync(0, request with { EventId = Guid.NewGuid(), DispatchId = Guid.NewGuid() }))).Code);
        Assert.Equal(1, await CountRuntimeGrantsAsync());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task ObservedWholeRunCreditFloorDeniesPrepareAtOrAboveHardLimit(int observedCredits)
    {
        await RegisterPositiveHardCreditAsync(0);

        Assert.False(await PrepareAsync(0, PricedSnapshot(0, observedCredits, 1)));
        Assert.Equal(0, await CountAsync("maf_execution_dispatches"));
        var guard = await ReadCreditGuardAsync();
        Assert.Equal(observedCredits, guard.Floor);
        Assert.Equal(1, guard.LedgerEventCount);
        Assert.True(guard.HardCapExhausted);
        Assert.False(await PrepareAsync(0, PricedSnapshot(0, observedCredits, 1)));
        Assert.Equal(0, await CountAsync("maf_execution_dispatches"));
    }

    [Fact]
    public async Task ObservedSoftCreditBoundaryEmitsOneWarningAndLaterTotalsStillAdvanceTheFloor()
    {
        await RegisterPositiveHardCreditAsync(0, softLimit: 3m);
        await RegisterPositiveHardCreditAsync(1, softLimit: 3m);

        var atBoundary = PricedSnapshot(0, 3m, 1);
        var prepared = await Task.WhenAll(
            PrepareAsync(0, atBoundary),
            PrepareAsync(1, PricedSnapshot(1, 3m, 1)));

        Assert.All(prepared, Assert.True);
        Assert.Equal(1, await CountSoftCreditWarningsAsync());
        var boundaryGuard = await ReadCreditGuardAsync();
        Assert.Equal(3m, boundaryGuard.Floor);
        Assert.Equal(1, boundaryGuard.LedgerEventCount);
        Assert.False(boundaryGuard.HardCapExhausted);

        Assert.True(await PrepareAsync(0, atBoundary));
        Assert.Equal(1, await CountSoftCreditWarningsAsync());

        Assert.True(await PrepareAsync(1, PricedSnapshot(1, 4m, 2)));
        var laterGuard = await ReadCreditGuardAsync();
        Assert.Equal(4m, laterGuard.Floor);
        Assert.Equal(2, laterGuard.LedgerEventCount);
        Assert.False(laterGuard.HardCapExhausted);
        Assert.True(laterGuard.OwnerRevision > boundaryGuard.OwnerRevision);
        Assert.Equal(1, await CountSoftCreditWarningsAsync());
    }

    [Fact]
    public async Task BeginRechecksCreditFloorAndLeavesDeniedIntentPrepared()
    {
        await RegisterPositiveHardCreditAsync(0);
        Assert.True(await PrepareAsync(0, PricedSnapshot(0, 4m, 1)));

        Assert.Null(await BeginAsync(0, PricedSnapshot(0, 5m, 2)));
        Assert.Equal("prepared", await ReadDispatchStateAsync(_intents[0].MessageId));
        Assert.Equal(0, await NativeDispatchCountAsync("sending"));
        var guard = await ReadCreditGuardAsync();
        Assert.Equal(5m, guard.Floor);
        Assert.Equal(2, guard.LedgerEventCount);
        Assert.True(guard.HardCapExhausted);
    }

    [Fact]
    public async Task ConcurrentCreditBoundChildrenCannotBothBegin()
    {
        await RegisterPositiveHardCreditAsync(0);
        await RegisterPositiveHardCreditAsync(1);
        var belowLimit = PricedSnapshot(0, 0m, 0);
        Assert.True(await PrepareAsync(0, belowLimit));
        Assert.True(await PrepareAsync(1, PricedSnapshot(1, 0m, 0)));

        var admissions = await Task.WhenAll(
            TryReserveAsync(async () => await BeginAsync(0, belowLimit) is not null,
                "runtime_native_accounting_pending"),
            TryReserveAsync(async () => await BeginAsync(1, PricedSnapshot(1, 0m, 0)) is not null,
                "runtime_native_accounting_pending"));

        Assert.Single(admissions, admitted => admitted);
        Assert.Single(admissions, admitted => !admitted);
        Assert.Equal(1, await NativeDispatchCountAsync("sending"));
    }

    [Theory]
    [InlineData("sending")]
    [InlineData("terminal_pending_accounting")]
    [InlineData("indeterminate")]
    public async Task CreditBoundBeginBlocksWhenAnotherChildIsSendingOrUncertain(string siblingState)
    {
        await RegisterPositiveHardCreditAsync(0);
        await RegisterPositiveHardCreditAsync(1);
        Assert.True(await PrepareAsync(0, PricedSnapshot(0, 0m, 0)));
        Assert.True(await PrepareAsync(1, PricedSnapshot(1, 0m, 0)));
        var first = Assert.IsType<RuntimeNativeTurnAdmissionReceipt>(
            await BeginAsync(0, PricedSnapshot(0, 0m, 0)));
        if (siblingState != "sending")
            await SetDispatchStateAsync(first.MessageId, siblingState);

        Assert.Equal("runtime_native_accounting_pending",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                BeginAsync(1, PricedSnapshot(1, 0m, 0)))).Code);
        Assert.Equal("prepared", await ReadDispatchStateAsync(_intents[1].MessageId));
    }

    [Fact]
    public async Task LateWholeRunUsageBlocksTheNextPrepareWithoutRevokingAnAdmittedTurn()
    {
        await RegisterPositiveHardCreditAsync(0);
        await RegisterPositiveHardCreditAsync(1);
        Assert.True(await PrepareAsync(0, PricedSnapshot(0, 4m, 1)));
        Assert.IsType<RuntimeNativeTurnAdmissionReceipt>(
            await BeginAsync(0, PricedSnapshot(0, 4m, 1)));

        Assert.False(await PrepareAsync(1, PricedSnapshot(1, 5m, 2)));
        Assert.Equal("sending", await ReadDispatchStateAsync(_intents[0].MessageId));
        Assert.Equal(1, await CountAsync("maf_execution_dispatches"));
        var guard = await ReadCreditGuardAsync();
        Assert.Equal(5m, guard.Floor);
        Assert.Equal(2, guard.LedgerEventCount);
        Assert.True(guard.HardCapExhausted);
    }

    [Fact]
    public async Task PositiveCreditDispatchEvidenceStaysPartialAndUnserialized()
    {
        await RegisterPositiveHardCreditAsync(0);
        Assert.True(await PrepareAsync(0, PricedSnapshot(0, 1m, 1)));
        var admission = Assert.IsType<RuntimeNativeTurnAdmissionReceipt>(
            await BeginAsync(0, PricedSnapshot(0, 1m, 1)));
        var observation = NativeObservation(admission, Guid.NewGuid()) with { UsageEventIds = [] };
        var recorded = await RecordNativeAsync(admission, observation);

        var evidence = await ReadDispatchEvidenceAsync(admission.MessageId);
        Assert.Equal("partial", evidence.ReportStatus);
        Assert.False(evidence.HardCapSerialized);
        Assert.Null(evidence.SourceCompletionManifestHash);
        Assert.Equal(recorded.CanonicalPayloadHash, evidence.ReportHash);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("source")]
    public async Task PartialOrForeignCostSnapshotCannotPrepareHardCappedDispatch(string fault)
    {
        await RegisterPositiveHardCreditAsync(0);
        var snapshot = fault switch
        {
            "partial" => PricedSnapshot(0, 1m, 1, isFullyPriced: false),
            "source" => PricedSnapshot(0, 1m, 1, sourceHash: new string('f', 64)),
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => PrepareAsync(0, snapshot));
        Assert.Equal(0, await CountAsync("maf_execution_dispatches"));
    }

    [Fact]
    public async Task ChangedCostRateOrRevokedSourceGrantCannotBeginHardCappedDispatch()
    {
        await RegisterPositiveHardCreditAsync(0);
        Assert.True(await PrepareAsync(0, PricedSnapshot(0, 1m, 1)));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            BeginAsync(0, PricedSnapshot(0, 1m, 1, rateVersion: "2")));
        Assert.Equal("prepared", await ReadDispatchStateAsync(_intents[0].MessageId));

        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            BeginAsync(0, PricedSnapshot(0, 1m, 1), _grants[0] with
            {
                State = RuntimeCredentialState.Revoked,
                Revision = _grants[0].Revision + 1
            }));
        Assert.Equal("prepared", await ReadDispatchStateAsync(_intents[0].MessageId));
    }

    [Fact]
    public async Task CreditFloorAndOccupiedDispatchSlotSurviveOwnerRestartAndReplay()
    {
        await RegisterPositiveHardCreditAsync(0);
        await RegisterPositiveHardCreditAsync(1);
        var belowLimit = PricedSnapshot(0, 4m, 1);
        Assert.True(await PrepareAsync(0, belowLimit));
        Assert.True(await PrepareAsync(1, PricedSnapshot(1, 4m, 1)));
        Assert.IsType<RuntimeNativeTurnAdmissionReceipt>(await BeginAsync(0, belowLimit));
        var beforeReplay = await ReadCreditGuardAsync();

        Assert.Equal("runtime_native_turn_indeterminate",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                BeginAsync(0, belowLimit))).Code);
        Assert.Equal("runtime_native_accounting_pending",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                BeginAsync(1, PricedSnapshot(1, 4m, 1)))).Code);
        Assert.Equal(beforeReplay, await ReadCreditGuardAsync());
        Assert.Equal("sending", await ReadDispatchStateAsync(_intents[0].MessageId));
        Assert.Equal("prepared", await ReadDispatchStateAsync(_intents[1].MessageId));
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
        Assert.Equal(2, await CountRuntimeGrantsAsync());
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
        Assert.Equal(1, await CountRuntimeGrantsAsync());
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
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".coordinator_decisions
                (project_id, run_id, session_id, request_id, decision_id,
                 actor_issuer, actor_subject, execution_fence, state_version,
                 action_kind, idempotency_key, command_hash, decision_state, decision)
            SELECT project_id, run_id, session_id, 'numeric-revision-fixture', @decision,
                   actor_issuer, actor_subject, execution_fence, state_version + 1,
                   action_kind, 'numeric-revision-fixture', command_hash, decision_state,
                   jsonb_set(decision, ARRAY['envelope', 'workPlanRevisionAttempts'], '1'::jsonb)
            FROM "{_schema}".coordinator_decisions
            WHERE project_id = @project AND run_id = @run AND session_id = @session
            ORDER BY state_version DESC LIMIT 1
            """, connection))
        {
            command.Parameters.AddWithValue("project", _root.ProjectId);
            command.Parameters.AddWithValue("run", _root.RunId);
            command.Parameters.AddWithValue("session", _root.SessionId);
            command.Parameters.AddWithValue("decision", Guid.NewGuid());
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        var revisionState = await _decisions.InitializeRootAsync(_actor, _root, _selection, default);
        Assert.Equal(1, revisionState.State.WorkPlanRevisionAttempts);
        await AdvanceNativeCheckpointAsync("numeric-revision-fixture-checkpoint", _checkpoint.State with
        {
            Revision = _checkpoint.State.Revision + 1,
            DecisionStateVersion = revisionState.StateVersion
        });
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
        Assert.Equal(1, (await _decisions.InitializeRootAsync(_actor, _root, _selection, default))
            .State.WorkPlanRevisionAttempts);
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
        Assert.Equal(1, current.State.WorkPlanRevisionAttempts);
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

    [Fact]
    public async Task ConcurrentNativeReportsCannotReuseAnEventFromTheSameSdkSource()
    {
        var admissions = await PrepareNativeAccountingPairAsync();
        var eventId = Guid.NewGuid();
        var observations = admissions.Select(admission => NativeObservation(admission, eventId)).ToArray();
        var reported = await Task.WhenAll(Enumerable.Range(0, 2).Select(index =>
            TryReserveAsync(async () =>
            {
                await RecordNativeAsync(admissions[index], observations[index]);
                return true;
            }, "runtime_native_turn_usage_reused")));
        Assert.Single(reported, value => value);
        Assert.Equal(1, await NativeDispatchCountAsync("terminal_pending_accounting"));
    }

    [Fact]
    public async Task CompetingCompletionsRejectHistoricallyOverlappingNativeReportsUnderTheFinalOwnerLock()
    {
        var admissions = await PrepareNativeAccountingPairAsync();
        var eventId = Guid.NewGuid();
        var observations = admissions.Select(admission => NativeObservation(admission, eventId)).ToArray();
        var recorded = new[]
        {
            await RecordNativeAsync(admissions[0], observations[0]),
            new RuntimeNativeTurnRecordedReceipt(admissions[1], observations[1],
                RuntimeNativeTurnContract.ObservationHash(observations[1]), 3)
        };
        // Retain an overlapping row written before the cross-dispatch check existed.
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            UPDATE "{_schema}".maf_execution_dispatches
            SET dispatch_state = 'terminal_pending_accounting', source_report_status = 'partial',
                source_report_hash = @hash, source_report_json = @report, owner_revision = owner_revision + 1
            WHERE dispatch_id = @message AND dispatch_state = 'sending'
            """, connection))
        {
            command.Parameters.AddWithValue("hash", recorded[1].CanonicalPayloadHash);
            command.Parameters.AddWithValue("report", NpgsqlTypes.NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(observations[1], new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            command.Parameters.AddWithValue("message", admissions[1].MessageId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        var completed = await Task.WhenAll(recorded.Select(receipt =>
            TryReserveAsync(async () =>
            {
                await CompleteNativeAsync(receipt);
                return true;
            }, "runtime_native_turn_usage_reused")));
        Assert.All(completed, value => Assert.False(value));
        Assert.Equal(0, await NativeDispatchCountAsync("completed"));
        Assert.Equal(2, await NativeDispatchCountAsync("terminal_pending_accounting"));
    }

    [Fact]
    public async Task NativeCompletionRechecksTheCurrentCheckpointAfterAnUnlockedObservedSnapshotRead()
    {
        var admissions = await PrepareNativeAccountingPairAsync();
        var recorded = await RecordNativeAsync(admissions[0], NativeObservation(admissions[0], Guid.NewGuid()));
        var store = SourceStore();
        var read = await store.ExecuteLockedAsync(_registrations[0].RuntimeInstanceId,
            (connection, transaction, token) => store.ReadNativeTurnWithinTransactionAsync(
                connection, transaction, _registrations[0], recorded, token), default);
        Assert.Equal(recorded.CanonicalPayloadHash, read.CanonicalPayloadHash);
        await AdvanceNativeCheckpointAsync("after-observed-read", _checkpoint.State with
        {
            Revision = _checkpoint.State.Revision + 1
        });
        Assert.Equal("runtime_native_turn_checkpoint_stale",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => CompleteNativeAsync(recorded))).Code);
        Assert.Equal(0, await NativeDispatchCountAsync("completed"));
    }

    [Fact]
    public async Task ConcurrentExactNativeCompletionsCommitOnceAndReplayTheSameOwnerRevision()
    {
        var admissions = await PrepareNativeAccountingPairAsync();
        var recorded = await RecordNativeAsync(admissions[0], NativeObservation(admissions[0], Guid.NewGuid()));
        var completions = await Task.WhenAll(CompleteNativeAsync(recorded), CompleteNativeAsync(recorded));
        Assert.All(completions, receipt => Assert.Equal(recorded.OwnerRevision + 1, receipt.OwnerRevision));
        Assert.Equal(1, await NativeDispatchCountAsync("completed"));
    }

    private async Task<RuntimeNativeTurnAdmissionReceipt[]> PrepareNativeAccountingPairAsync()
    {
        await RegisterSourceAsync(0, _registrations[0].Binding with { MaxModelTurns = 2 });
        _registrations[1] = _registrations[0];
        _grants[1] = _grants[0];
        _sources[1] = _sources[0];
        _intents[1] = new("item-1", _registrations[0].Binding.SessionId, Guid.NewGuid(), _intents[0].PromptHash);
        _messages[1] = _messages[0] with
        {
            Message = _messages[0].Message with { MessageId = _intents[1].MessageId }
        };
        await AdvanceNativeCheckpointAsync("native-accounting-pair", _checkpoint.State with
        {
            Revision = _checkpoint.State.Revision + 1,
            PendingDispatches = _intents.ToImmutableDictionary(intent => intent.AssociationId)
        });
        Assert.True(await PrepareAsync(0));
        Assert.True(await PrepareAsync(1));
        return [Assert.IsType<RuntimeNativeTurnAdmissionReceipt>(await BeginAsync(0)),
            Assert.IsType<RuntimeNativeTurnAdmissionReceipt>(await BeginAsync(1))];
    }

    private async Task AdvanceNativeCheckpointAsync(string id, MafExecutionCheckpoint state)
    {
        var binding = new MafCheckpointBinding(_root, _actor, _registrations[0].Binding.ExecutionFence,
            MafExecutionCheckpointStore.CurrentSdkVersion, "model:selected", null,
            MafExecutionCheckpointContract.StoreName);
        _checkpoint = await new MafExecutionCheckpointStore(
            new PostgresMafCheckpointStore(fixture.DataSource, _schema, null), binding)
            .AppendAsync(id, _checkpoint.Info, state, default);
    }

    private static RuntimeNativeTurnObservation NativeObservation(
        RuntimeNativeTurnAdmissionReceipt admission, Guid usageEventId) =>
        new(admission.Source, Guid.NewGuid().ToString("D"), "controlled-native-turn", Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(-1),
            "controlled-cursor", new string('d', 64), null) { UsageEventIds = [usageEventId] };

    private Task<RuntimeNativeTurnRecordedReceipt> RecordNativeAsync(
        RuntimeNativeTurnAdmissionReceipt admission, RuntimeNativeTurnObservation observation)
    {
        var store = SourceStore();
        return store.ExecuteLockedAsync(admission.Registration.RuntimeInstanceId,
            async (connection, transaction, token) =>
            {
                var recorded = await store.RecordNativeTurnWithinTransactionAsync(
                    connection, transaction, admission.Registration, _grants[0],
                    new(null!, admission, observation), token);
                await transaction.CommitAsync(token);
                return recorded;
            }, default);
    }

    private Task<RuntimeNativeTurnAccountingReceipt> CompleteNativeAsync(RuntimeNativeTurnRecordedReceipt recorded)
    {
        var registration = recorded.Admission.Registration;
        var scope = registration.Binding;
        var reference = new RuntimeUsageCostReceiptReference(
            recorded.Observation.UsageEventIds[0], new(SdkUsageIdentity.Create(registration.RuntimeInstanceId,
                recorded.Admission.Source.SdkSessionId, recorded.Observation.UsageEventIds[0].ToString("D")),
                new(scope.TenantId, scope.ProjectId, scope.RunId, scope.SessionId, scope.AgentId)
                {
                    TurnId = scope.TurnId
                }, new string('e', 64), CostDisposition.Unpriced, null, null, "controlled-cost", null, null,
                DateTimeOffset.UnixEpoch));
        var request = new RuntimeNativeTurnAccountingRequest(null!, recorded, [reference]);
        var snapshot = new RuntimeUsageCostSnapshotReceipt(1,
            RuntimeUsageSourceReceiptContract.HashSource(registration, recorded.Admission.Source),
            null, new(null, null, CostDisposition.Unpriced, null, "controlled-cost"),
            new(scope.TenantId, scope.ProjectId, scope.RunId, 0, true, [], []))
        {
            DispatchId = recorded.Admission.MessageId, RepresentedReceipts = [reference]
        };
        var store = SourceStore();
        return store.ExecuteLockedAsync(registration.RuntimeInstanceId, async (connection, transaction, token) =>
        {
            var receipt = await store.CompleteNativeTurnWithinTransactionAsync(
                connection, transaction, registration, _grants[0], request, snapshot, token);
            await transaction.CommitAsync(token);
            return receipt;
        }, default);
    }

    private async Task<long> NativeDispatchCountAsync(string state)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{_schema}\".maf_execution_dispatches WHERE dispatch_state = @state", connection);
        command.Parameters.AddWithValue("state", state);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string?> ReadDispatchStateAsync(Guid dispatchId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT dispatch_state FROM "{_schema}".maf_execution_dispatches
            WHERE project_id = @project AND run_id = @run AND dispatch_id = @dispatch
            """, connection);
        command.Parameters.AddWithValue("project", _root.ProjectId);
        command.Parameters.AddWithValue("run", _root.RunId);
        command.Parameters.AddWithValue("dispatch", dispatchId);
        return await command.ExecuteScalarAsync() as string;
    }

    private async Task<(string ReportStatus, bool HardCapSerialized, string? SourceCompletionManifestHash, string ReportHash)>
        ReadDispatchEvidenceAsync(Guid dispatchId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT source_report_status, hard_cap_serialized, source_completion_manifest_hash, source_report_hash
            FROM "{_schema}".maf_execution_dispatches
            WHERE project_id = @project AND run_id = @run AND dispatch_id = @dispatch
            """, connection);
        command.Parameters.AddWithValue("project", _root.ProjectId);
        command.Parameters.AddWithValue("run", _root.RunId);
        command.Parameters.AddWithValue("dispatch", dispatchId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3));
    }

    private async Task<(decimal Floor, long LedgerEventCount, bool HardCapExhausted, long OwnerRevision)>
        ReadCreditGuardAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT copilot_aic_floor, accounted_through_event_position, hard_cap_exhausted, owner_revision
            FROM "{_schema}".maf_execution_run_guards WHERE project_id = @project AND run_id = @run
            """, connection);
        command.Parameters.AddWithValue("project", _root.ProjectId);
        command.Parameters.AddWithValue("run", _root.RunId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetDecimal(0), reader.GetInt64(1), reader.GetBoolean(2), reader.GetInt64(3));
    }

    private async Task SetDispatchStateAsync(Guid dispatchId, string state)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            UPDATE "{_schema}".maf_execution_dispatches
            SET dispatch_state = @state, owner_revision = owner_revision + 1
            WHERE project_id = @project AND run_id = @run AND dispatch_id = @dispatch
            """, connection);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("project", _root.ProjectId);
        command.Parameters.AddWithValue("run", _root.RunId);
        command.Parameters.AddWithValue("dispatch", dispatchId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
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
        fixture.DataSource, _options, TimeProvider.System,
        new ProjectsRunSelectionClient(
            _projectsHttp, _options, new ReviewedRemoteToolSnapshotStore(fixture.DataSource, _schema)),
        new HttpContextAccessor { HttpContext = _context });

    private Task<ExecutableActionGrantReference> IssueAsync(int index, RuntimeActionRequest request) =>
        GrantStore().IssueRuntimeActionGrantAsync(
            _context, _registrations[index], request, default, reserveToolInvocation: request.IsToolInvocation);

    private RuntimeActionRequest Action(int index, bool toolInvocation) =>
        new(1, _registrations[index].RuntimeInstanceId, _registrations[index].Revision,
            _registrations[index].Binding.ExecutionFence, Guid.NewGuid(), "tool.read",
            RuntimeContractValidation.Hash("same tool arguments"u8)) { IsToolInvocation = toolInvocation };

    private RuntimeActionRequest ModelAction(int index, Guid? dispatchId) =>
        new(1, _registrations[index].RuntimeInstanceId, _registrations[index].Revision,
            _registrations[index].Binding.ExecutionFence, Guid.NewGuid(), "model.turn",
            _intents[index].PromptHash) { DispatchId = dispatchId };

    private async Task RegisterPositiveHardCreditAsync(
        int index, decimal hardLimit = 5m, decimal? softLimit = null)
    {
        await RegisterSourceAsync(index, _registrations[index].Binding with
        {
            MaxModelTurns = null,
            MaxToolCalls = null,
            MaxPromptTokens = null,
            CopilotSoftCreditLimit = softLimit,
            CopilotHardCreditLimit = hardLimit
        });
        await AdvanceNativeCheckpointAsync($"positive-credit-{Guid.NewGuid():N}", _checkpoint.State with
        {
            Revision = _checkpoint.State.Revision + 1,
            PendingDispatches = _intents.ToImmutableDictionary(intent => intent.AssociationId)
        });
    }

    private Func<SdkSessionFacts, CancellationToken, Task<RuntimeUsageCostSnapshotReceipt>> PricedSnapshot(
        int index, decimal observedCredits, long ledgerEventCount, bool isFullyPriced = true,
        string? sourceHash = null, string rateVersion = "1")
    {
        var registration = _registrations[index];
        return (source, token) =>
        {
            token.ThrowIfCancellationRequested();
            var card = new CostRateCard("numeric-rate-card", rateVersion, source.MeterSource, "AIC",
                1_000_000_000m,
                ImmutableDictionary<string, decimal>.Empty.WithComparers(StringComparer.Ordinal)
                    .Add(source.ModelId, 1m));
            var binding = new CostBinding(source.MeterSource, "copilot.usage-cost", "1.0.0", 1,
                "numeric-cost-options", "numeric-cost-resource", 1,
                ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal), card);
            var scope = registration.Binding;
            ImmutableArray<UsageAmountTotal> amounts = ledgerEventCount == 0
                ? [] : [new(source.MeterSource, "AIC", observedCredits, ledgerEventCount, 0)];
            ImmutableArray<UsageAgentTotals> agents = ledgerEventCount == 0
                ? [] : [new(scope.AgentId, ledgerEventCount, null, null, null, null, null, null,
                    isFullyPriced, amounts)];
            var totals = new UsageRunTotals(scope.TenantId, scope.ProjectId, scope.RunId,
                ledgerEventCount, isFullyPriced, agents, amounts);
            return Task.FromResult(new RuntimeUsageCostSnapshotReceipt(1,
                sourceHash ?? RuntimeUsageSourceReceiptContract.HashSource(registration, source),
                binding, new(0m, "AIC", CostDisposition.Estimate, card, null), totals));
        };
    }

    private Task<bool> PrepareAsync(
        int index,
        Func<SdkSessionFacts, CancellationToken, Task<RuntimeUsageCostSnapshotReceipt>>? readCostSnapshot = null)
    {
        var store = SourceStore();
        var snapshot = readCostSnapshot ?? NoCreditSnapshot;
        return store.ExecuteLockedAsync(_registrations[index].RuntimeInstanceId,
            async (connection, transaction, token) =>
            {
                var prepared = await store.PrepareNativeTurnWithinTransactionAsync(
                    connection, transaction, _root, _checkpoint, _intents[index], _registrations[index],
                    _messages[index], snapshot, token);
                await transaction.CommitAsync(token);
                return prepared;
            }, default);
    }

    private Task<RuntimeNativeTurnAdmissionReceipt?> BeginAsync(
        int index,
        Func<SdkSessionFacts, CancellationToken, Task<RuntimeUsageCostSnapshotReceipt>>? readCostSnapshot = null,
        RuntimeGrantReceipt? grantOverride = null)
    {
        var store = SourceStore();
        var grant = grantOverride ?? _grants[index];
        var snapshot = readCostSnapshot ?? NoCreditSnapshot;
        return store.ExecuteLockedAsync(_registrations[index].RuntimeInstanceId,
            async (connection, transaction, token) =>
            {
                var receipt = await store.BeginNativeTurnWithinTransactionAsync(
                    connection, transaction, _registrations[index], grant,
                    new(new(grant.GrantId, _registrations[index].RuntimeInstanceId,
                        grant.Revision, RuntimeCredentialPurpose.Observe,
                        grant.Audience, grant.ConfigurationHash,
                        "controlled-store-only", grant.ExpiresAt, Guid.NewGuid()),
                        _messages[index], _sources[index]), snapshot, token);
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

    private async Task<long> CountSoftCreditWarningsAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".outbox_events
            WHERE event_type = 'orchestrator.run.copilot_soft_credit_limit_reached'
            """, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountRuntimeGrantsAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{_schema}\".executable_action_grants WHERE purpose = @purpose", connection);
        command.Parameters.AddWithValue("purpose", RuntimeActionContract.Purpose);
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
