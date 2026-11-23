using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed partial class RuntimeNumericLimitsPostgresTests
{
    [Fact]
    public async Task ApprovedNativeSuspensionIssuesFreshGrantForTheExactRetriedTurn()
    {
        Assert.Null(await PrepareAsync(0));
        var request = ModelAction(0, _intents[0].MessageId) with
        {
            EventId = _intents[0].MessageId
        };
        var first = await IssueAsync(0, request);
        var store = ApprovalStore();
        var retained = Retained(request);

        var pending = await store.RequireOrSuspendAsync(
            _actor, _selection, _registrations[0], request, retained, default);
        Assert.Equal(RuntimeNativeApprovalDisposition.Pending, pending.Disposition);
        await ApproveAsync(pending.RequestId);
        var approved = await store.RequireOrSuspendAsync(
            _actor, _selection, _registrations[0], request, retained, default);
        Assert.Equal(RuntimeNativeApprovalDisposition.Approved, approved.Disposition);

        var retry = await IssueAsync(0, request, approvedNativeHandoff: true);

        Assert.Equal(first.GrantId, retry.GrantId);
        Assert.Equal("1", first.Revision);
        Assert.Equal("2", retry.Revision);
        Assert.Equal("Current", (await GrantStore().GetCurrentAsync(retry, default)).Status.ToString());
    }

    [Fact]
    public async Task ApprovalHandoffIsClaimedOnceUnderCurrentDecisionVersion()
    {
        Assert.Null(await PrepareAsync(0));
        var request = ModelAction(0, _intents[0].MessageId) with
        {
            EventId = _intents[0].MessageId
        };
        _ = await IssueAsync(0, request);
        var store = ApprovalStore();
        var retained = Retained(request);
        var pending = await store.RequireOrSuspendAsync(
            _actor, _selection, _registrations[0], request, retained, default);
        await ApproveAsync(pending.RequestId);
        _ = await store.RequireOrSuspendAsync(
            _actor, _selection, _registrations[0], request, retained, default);
        await store.RequireApprovedHandoffAsync(
            _actor, _selection, _registrations[0], request, default);

        var attempts = await Task.WhenAll(
            ClaimAsync(store, request),
            ClaimAsync(store, request));

        Assert.Single(attempts, item => item is null);
        Assert.Single(attempts, item =>
            item == "runtime_native_policy_approval_already_consumed");
    }

    [Fact]
    public async Task ApprovalClaimRejectsDecisionVersionChangedAfterHandoffValidation()
    {
        Assert.Null(await PrepareAsync(0));
        var request = ModelAction(0, _intents[0].MessageId) with
        {
            EventId = _intents[0].MessageId
        };
        _ = await IssueAsync(0, request);
        var store = ApprovalStore();
        var retained = Retained(request);
        var pending = await store.RequireOrSuspendAsync(
            _actor, _selection, _registrations[0], request, retained, default);
        await ApproveAsync(pending.RequestId);
        _ = await store.RequireOrSuspendAsync(
            _actor, _selection, _registrations[0], request, retained, default);
        await store.RequireApprovedHandoffAsync(
            _actor, _selection, _registrations[0], request, default);

        var current = await _decisions.ReadCurrentAsync(_actor, _root, _selection, default);
        var requestId = "later-approval-" + Guid.NewGuid().ToString("N");
        var transition = CoordinatorDecisionFlow.RequestApproval(
            current.State, requestId, "later-subject-" + Guid.NewGuid().ToString("N"), _actor.Subject);
        await _decisions.PersistTransitionAsync(
            _actor, _root, _selection, current.StateVersion,
            "later-" + Guid.NewGuid().ToString("N"), requestId, "approval.request",
            CoordinatorDecisionOwnerStore.ComputeCommandHash(new { current.StateVersion, requestId }),
            transition.State!, true, transition.Issues, null, null, transition.Value, default);

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            store.ClaimAsync(_actor, _selection, _registrations[0], request, default));
        Assert.Equal("runtime_native_policy_approval_stale", failure.Code);
    }

    private RuntimeNativeApprovalOwnerStore ApprovalStore() =>
        new(
            fixture.DataSource,
            _schema,
            _decisions,
            new CoordinationOwnerStore(fixture.DataSource, _schema));

    private RuntimeNativePolicyGuardrailsResult Retained(RuntimeActionRequest request)
    {
        var policyInput = JsonSerializer.SerializeToElement(new
        {
            policy_target = new
            {
                value = new
                {
                    approval = new
                    {
                        turn_id = _registrations[0].Binding.TurnId,
                        arguments_digest = request.InputHash
                    }
                }
            }
        });
        var source = new RuntimeNativePolicySourceReference(
            "project", _root.ProjectId, "native-policy", 1, new string('a', 64));
        return new(
            "binding-" + Guid.NewGuid().ToString("N"),
            [new RuntimeNativePolicyRetainedEvaluation(source, new string('b', 64), policyInput, policyInput)],
            null!,
            [],
            []);
    }

    private async Task ApproveAsync(string requestId)
    {
        var current = await _decisions.ReadCurrentAsync(_actor, _root, _selection, default);
        var answer = new CoordinatorGateAnswer(
            requestId, _actor.Subject, current.State.Fence, CoordinatorGateChoices.Approve, null);
        var transition = current.State.ApplyGateAnswer(answer);
        Assert.Equal(CoordinatorGateAnswerStatus.Accepted, transition.Status);
        await _decisions.PersistTransitionAsync(
            _actor,
            _root,
            _selection,
            current.StateVersion,
            "answer-" + Guid.NewGuid().ToString("N"),
            requestId,
            "gate.answer",
            CoordinatorDecisionOwnerStore.ComputeCommandHash(new
            {
                current.StateVersion,
                requestId,
                answer.ChoiceId
            }),
            transition.State,
            transition.IsAccepted,
            transition.Issues,
            null,
            null,
            transition.Receipt,
            default);
    }

    private async Task<string?> ClaimAsync(
        RuntimeNativeApprovalOwnerStore store,
        RuntimeActionRequest request)
    {
        try
        {
            await store.ClaimAsync(
                _actor, _selection, _registrations[0], request, default);
            return null;
        }
        catch (RuntimeAuthorizationException exception)
        {
            return exception.Code;
        }
    }
}
