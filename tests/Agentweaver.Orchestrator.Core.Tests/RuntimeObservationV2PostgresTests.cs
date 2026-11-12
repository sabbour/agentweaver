using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Persistence.Postgres;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed partial class RuntimeNumericLimitsPostgresTests
{
    [Fact]
    public async Task RuntimeObservationV2CursorPinsThePageAndExactLookupFindsAttemptFacts()
    {
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var attemptId = Guid.NewGuid();
        var store = new RuntimeObservationV2Store(fixture.DataSource, _schema);
        var scope = new RuntimeObservationV2ReadScope(
            _actor.Issuer, _actor.Subject, _selection.Authorization.TenantId,
            _root.ProjectId, _root.RunId, _root.SessionId, _root.SessionId,
            selectionHash, _selection.Selection.ContextRevision, 1, 1, new string('a', 64));
        var initialFacts = new[]
            {
                ModelReservationFact(_root, selectionHash, Guid.NewGuid()),
                ModelReservationFact(_root, selectionHash, Guid.NewGuid()),
                ModelReservationFact(_root, selectionHash, attemptId),
                ModelReservationFact(_root, selectionHash, attemptId),
                ModelReservationFact(_root, selectionHash, attemptId),
                CreditGuardFact(_root, selectionHash, 1, 5m, new string('a', 64))
            };
        await AppendFactsAsync(store, initialFacts);

        var firstPage = await store.ReadAsync(scope, 2, null, null, default);
        Assert.Equal(2, firstPage.History.Items.Count);
        Assert.NotNull(firstPage.History.NextCursor);
        var snapshotWatermark = firstPage.History.HighWatermark;
        Assert.Equal(1, firstPage.CreditGuard.Value?.GuardRevision);
        Assert.NotNull(firstPage.RecordedBindingFact?.RuntimeInstanceId);
        Assert.Equal(1, firstPage.RecordedBindingFact?.RegistrationRevision);

        var laterFact = ModelReservationFact(_root, selectionHash, Guid.NewGuid());
        var laterGuard = CreditGuardFact(_root, selectionHash, 2, 9m, new string('b', 64));
        await AppendFactsAsync(store, [laterFact, laterGuard]);
        var secondPage = await store.ReadAsync(
            scope, 2, firstPage.History.NextCursor, null, default);

        Assert.Equal(snapshotWatermark, secondPage.History.HighWatermark);
        Assert.Equal(2, secondPage.History.Items.Count);
        Assert.DoesNotContain(secondPage.History.Items, item => item.FactId == laterFact.FactId);
        Assert.Equal(1, secondPage.CreditGuard.Value?.GuardRevision);
        Assert.Equal(5m, secondPage.CreditGuard.Value?.CopilotAicFloor);
        var authorityChanged = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            store.ReadAsync(
                scope with { AuthorizationHash = new string('b', 64) },
                2, firstPage.History.NextCursor, null, default));
        Assert.Equal("runtime_observation_v2_cursor_stale", authorityChanged.Code);

        var exact = await store.ReadAsync(scope, 2, null, attemptId, default);
        Assert.True(exact.History.ExactAttempt);
        Assert.Equal(3, exact.History.Items.Count);
        Assert.All(exact.History.Items, item => Assert.Equal(attemptId, item.AttemptId));
        Assert.Equal(6, exact.Totals.ModelSlotsReserved.Value);
        Assert.Equal(2, exact.CreditGuard.Value?.GuardRevision);

        var missing = await store.ReadAsync(scope, 100, null, Guid.NewGuid(), default);
        Assert.Empty(missing.History.Items);
        Assert.Equal("attempt_evidence_not_retained", missing.History.ExactUnavailableReason);
    }

    [Fact]
    public async Task RuntimeObservationV2PagesMoreThanOneHundredFactsWithoutGaps()
    {
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var store = new RuntimeObservationV2Store(fixture.DataSource, _schema);
        var scope = new RuntimeObservationV2ReadScope(
            _actor.Issuer, _actor.Subject, _selection.Authorization.TenantId,
            _root.ProjectId, _root.RunId, _root.SessionId, _root.SessionId,
            selectionHash, _selection.Selection.ContextRevision, 1, 1, new string('a', 64));
        await AppendFactsAsync(store, Enumerable.Range(0, 101)
            .Select(_ => ModelReservationFact(_root, selectionHash, Guid.NewGuid()))
            .ToArray());

        var firstPage = await store.ReadAsync(scope, 100, null, null, default);
        var secondPage = await store.ReadAsync(scope, 100, firstPage.History.NextCursor, null, default);

        Assert.Equal(100, firstPage.History.Items.Count);
        Assert.NotNull(firstPage.History.NextCursor);
        Assert.Equal(2, secondPage.History.Items.Count);
        Assert.Null(secondPage.History.NextCursor);
        Assert.Equal(firstPage.History.HighWatermark, secondPage.History.HighWatermark);
        Assert.Empty(firstPage.History.Items.Select(item => item.FactId)
            .Intersect(secondPage.History.Items.Select(item => item.FactId)));
        Assert.Equal(101, firstPage.History.Items.Concat(secondPage.History.Items)
            .Count(item => item.Kind == RuntimeObservationFactKind.ModelTurnReserved));
    }

    [Fact]
    public async Task RuntimeObservationV2BoundsOversizedExactLookupAndReturnsHistoryCursor()
    {
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var attemptId = Guid.NewGuid();
        var store = new RuntimeObservationV2Store(fixture.DataSource, _schema);
        var scope = new RuntimeObservationV2ReadScope(
            _actor.Issuer, _actor.Subject, _selection.Authorization.TenantId,
            _root.ProjectId, _root.RunId, _root.SessionId, _root.SessionId,
            selectionHash, _selection.Selection.ContextRevision, 1, 1, new string('a', 64));
        await AppendFactsAsync(store, Enumerable.Range(0, 101)
            .Select(_ => ModelReservationFact(_root, selectionHash, attemptId))
            .ToArray());

        var exact = await store.ReadAsync(scope, 100, null, attemptId, default);
        Assert.Equal(100, exact.History.Items.Count);
        Assert.Equal("attempt_history_exceeds_bounded_aggregate", exact.History.ExactUnavailableReason);
        Assert.NotNull(exact.History.NextCursor);

        var firstHistoryPage = await store.ReadAsync(scope, 100, exact.History.NextCursor, null, default);
        var secondHistoryPage = await store.ReadAsync(
            scope, 100, firstHistoryPage.History.NextCursor, null, default);
        Assert.Equal(100, firstHistoryPage.History.Items.Count(item => item.AttemptId == attemptId));
        Assert.Single(secondHistoryPage.History.Items, item => item.AttemptId == attemptId);
        Assert.Null(secondHistoryPage.History.NextCursor);
        Assert.Null(firstHistoryPage.History.ExactUnavailableReason);
    }

    [Fact]
    public async Task RuntimeObservationV2RootIncludesChildrenButChildHistoryIsNarrowed()
    {
        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var child = new SessionIdentity(_root.ProjectId, _root.RunId, "child-session");
        var store = new RuntimeObservationV2Store(fixture.DataSource, _schema);
        var rootScope = new RuntimeObservationV2ReadScope(
            _actor.Issuer, _actor.Subject, _selection.Authorization.TenantId,
            _root.ProjectId, _root.RunId, _root.SessionId, _root.SessionId,
            selectionHash, _selection.Selection.ContextRevision, 1, 1, new string('a', 64));
        var childScope = rootScope with { SessionId = child.SessionId, SessionFence = 1 };
        await AppendFactsAsync(store,
        [
            ModelReservationFact(_root, selectionHash, Guid.NewGuid()),
            ModelReservationFact(child, selectionHash, Guid.NewGuid())
        ]);

        var root = await store.ReadAsync(rootScope, 100, null, null, default);
        var childRead = await store.ReadAsync(childScope, 100, null, null, default);

        Assert.Contains(root.History.Items, item => item.SessionId == child.SessionId);
        Assert.All(childRead.History.Items, item => Assert.Equal(child.SessionId, item.SessionId));
        Assert.Equal(2, root.Totals.ModelSlotsReserved.Value);
        Assert.Equal(2, childRead.Totals.ModelSlotsReserved.Value);
    }

    [Fact]
    public async Task RuntimeObservationV2ExactLookupReadsLegacyCreditDenialsAndMarksMissingNoSendProofUnavailable()
    {
        await RegisterPositiveHardCreditAsync(0);
        await RegisterPositiveHardCreditAsync(1);
        Assert.Null(await PrepareAsync(0, PricedSnapshot(0, 0m, 0)));
        Assert.Null(await PrepareAsync(1, PricedSnapshot(1, 0m, 0)));

        var firstDenial = LegacyCreditDenial(0);
        var secondDenial = LegacyCreditDenial(1);
        await AppendLegacyCreditEvidenceAsync(firstDenial, includeNoSend: true);
        await AppendLegacyCreditEvidenceAsync(secondDenial, includeNoSend: false);

        var selectionHash = CoordinationOwnerStore.HashSelection(_selection.Selection);
        var store = new RuntimeObservationV2Store(fixture.DataSource, _schema);
        var scope = new RuntimeObservationV2ReadScope(
            _actor.Issuer, _actor.Subject, _selection.Authorization.TenantId,
            _root.ProjectId, _root.RunId, _root.SessionId, _root.SessionId,
            selectionHash, _selection.Selection.ContextRevision, 1, 1, new string('a', 64));
        var withNoSend = await store.ReadAsync(
            scope, 100, null, firstDenial.DispatchId, default);
        var withoutNoSend = await store.ReadAsync(
            scope, 100, null, secondDenial.DispatchId, default);

        var withNoSendFact = Assert.Single(withNoSend.History.Items,
            item => item.Kind == RuntimeObservationFactKind.NativeNoSend);
        var withoutNoSendFact = Assert.Single(withoutNoSend.History.Items,
            item => item.Kind == RuntimeObservationFactKind.Denied);
        Assert.Equal(RuntimeObservationSectionState.Available, withNoSendFact.NativeNoSend.State);
        Assert.Equal(RuntimeObservationSectionState.Unavailable, withoutNoSendFact.NativeNoSend.State);
        Assert.Equal("native_witness_missing",
            withoutNoSendFact.NativeNoSend.Reason);
    }

    private RuntimeNativeCreditDenialReceipt LegacyCreditDenial(int index)
    {
        var registration = _registrations[index];
        var dispatchId = _intents[index].MessageId;
        var registrationHash = RuntimeContractValidation.RegistrationHash(registration);
        var sourceHash = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes($"legacy-source-{index}"));
        var checkpointHash = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes($"legacy-checkpoint-{index}"));
        var digest = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes($"legacy-ledger-{index}"));
        var now = DateTimeOffset.UtcNow;
        return RuntimeNativeDenialContract.SealDenial(new(
            1, registration.RuntimeInstanceId, registration.Revision, registration.Binding.ExecutionFence,
            _grants[index].GrantId, dispatchId, RuntimeCreditLimitDenialStage.NativeTurnBegin,
            RuntimeCreditLimitDenialCode.HardLimitReached, registrationHash, sourceHash,
            RuntimeNativeDenialContract.DispatchHash(
                dispatchId, registrationHash, sourceHash, checkpointHash, "prepared", 1),
            checkpointHash, 5m, 5m, 1, digest,
            RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes("AIC")),
            1, 2, "prepared", 1, "prepared", 1, now, string.Empty));
    }

    private async Task AppendLegacyCreditEvidenceAsync(
        RuntimeNativeCreditDenialReceipt denial,
        bool includeNoSend)
    {
        var events = new List<OutboxEvent>();
        var denialEventId = Guid.NewGuid();
        events.Add(new OutboxEvent(
            denialEventId,
            $"coordination/{_root.ProjectId}/{_root.RunId}/native-credit-denials",
            $"legacy-credit-denial/{denialEventId:D}",
            "orchestrator.native_turn.credit_denied", 1,
            JsonSerializer.SerializeToElement(denial, LegacyEvidenceJson),
            denial.RecordedAt));
        if (includeNoSend)
        {
            var cursorHash = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes("legacy-empty-cursor"));
            var receipt = RuntimeNativeDenialContract.SealNoSend(new(
                1, denial.RuntimeInstanceId, denial.RegistrationRevision, denial.DispatchId,
                denial.CanonicalPayloadHash, denial.RegistrationHash, denial.SourceHash,
                RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes("legacy-native-session")),
                cursorHash, cursorHash, 0, true, DateTimeOffset.UtcNow, string.Empty));
            var noSendEventId = Guid.NewGuid();
            events.Add(new OutboxEvent(
                noSendEventId,
                $"coordination/{_root.ProjectId}/{_root.RunId}/native-no-sends",
                $"legacy-native-no-send/{noSendEventId:D}",
                "orchestrator.native_turn.no_send", 1,
                JsonSerializer.SerializeToElement(receipt, LegacyEvidenceJson),
                receipt.VerifiedAt));
        }

        var outbox = new PostgresOutbox(fixture.DataSource, _schema);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var item in events)
            await outbox.EnqueueAsync(connection, transaction, item, default);
        await transaction.CommitAsync();
    }

    private static readonly JsonSerializerOptions LegacyEvidenceJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private async Task AppendFactsAsync(
        RuntimeObservationV2Store store,
        IReadOnlyList<RuntimeObservationV2Fact> facts)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var fact in facts)
            await store.AppendAsync(
                connection, transaction, _root.ProjectId, _root.RunId, fact, default);
        await transaction.CommitAsync();
    }

    private static RuntimeObservationV2Fact ModelReservationFact(
        SessionIdentity root,
        string selectionHash,
        Guid attemptId)
    {
        var dispatchId = Guid.NewGuid();
        return new RuntimeObservationV2Fact(
            Guid.NewGuid(), null, RuntimeObservationFactKind.ModelTurnReserved,
            root.ProjectId, root.RunId, attemptId, dispatchId, root.SessionId, 1,
            Guid.NewGuid(), 1, selectionHash.ToLowerInvariant(), 1,
            RuntimeObservationLimitKind.ModelTurns, 4, null, "count", "copilotRun",
            "modelAdmission", null, null, 1, DateTimeOffset.UtcNow,
            new(RuntimeObservationSectionState.Available, null, dispatchId, new string('a', 64), null, null),
            RuntimeObservationEvidenceLink.Unavailable("native_send_not_proven"),
            RuntimeObservationEvidenceLink.Unavailable("not_applicable"),
            RuntimeObservationEvidenceLink.Unavailable("native_tool_execution_not_observed")).Validate();
    }

    private static RuntimeObservationV2Fact CreditGuardFact(
        SessionIdentity root,
        string selectionHash,
        long revision,
        decimal floor,
        string digest)
    {
        var factId = Guid.NewGuid();
        return new RuntimeObservationV2Fact(
            factId, null, RuntimeObservationFactKind.CreditGuardChanged,
            root.ProjectId, root.RunId, null, null, root.SessionId, 1,
            Guid.NewGuid(), 1, selectionHash.ToLowerInvariant(), revision,
            null, null, floor, "nanoAiu", "copilotRun", "guardUpdate",
            revision == 1 ? "soft_warning_or_floor_update" : "hard_limit_reached",
            null, null, DateTimeOffset.UtcNow,
            new(RuntimeObservationSectionState.Available, null, factId, digest, null, null),
            RuntimeObservationEvidenceLink.Unavailable("not_a_native_attempt"),
            RuntimeObservationEvidenceLink.Unavailable("not_applicable"),
            RuntimeObservationEvidenceLink.Unavailable("not_observed"))
        {
            AccountedThroughLedgerEventCount = revision,
            SoftWarningEmitted = true,
            HardCapExhausted = revision > 1
        }.Validate();
    }
}
