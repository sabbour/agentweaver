using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
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
