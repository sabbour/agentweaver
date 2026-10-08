using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class SourceControlMergeContractsTests
{
    private const string Actor = "owner-1";
    private const string IntentId = "merge-intent-1";

    [Fact]
    public void CreatesMergeIntentFromCurrentAcceptedApprovalAndExactMergeStep()
    {
        var acceptedRun = AcceptedRun();
        var pin = RepositoryPin(acceptedRun);
        var (state, assembly, receipt) = ApprovedMerge();
        var pullRequest = PullRequest();

        var intent = SourceControlMergeIntent.Create(
            IntentId,
            acceptedRun,
            pin,
            state,
            assembly,
            receipt,
            pullRequest,
            SourceControlMergeMethod.Squash,
            DateTimeOffset.Parse("2026-10-07T08:00:00Z"));

        Assert.Equal(IntentId, intent.IntentId);
        Assert.Equal(acceptedRun, intent.AcceptedRun);
        Assert.Equal(pin, intent.RepositoryPin);
        Assert.Equal("workflow.core", intent.WorkflowId);
        Assert.Equal("revision-1", intent.DefinitionRevision);
        Assert.Equal("plan-1", intent.WorkPlanId);
        Assert.Equal("merge", intent.WorkflowStepId);
        Assert.Equal(receipt, intent.ApprovalReceipt);
        Assert.Equal(pin.Repository, intent.Repository);
        Assert.Equal("github-api-v1", pin.ApiCredential.Secret.Id);
        Assert.Equal(SourceControlSecretPurposes.Api, pin.ApiCredential.Purpose);
        Assert.Equal("github-checkout-v1", pin.CheckoutCredential!.Secret.Id);
        Assert.Equal(SourceControlSecretPurposes.Checkout, pin.CheckoutCredential.Purpose);
        Assert.Equal("github-webhook-v1", pin.WebhookCredential!.Secret.Id);
        Assert.Equal(SourceControlSecretPurposes.Webhook, pin.WebhookCredential.Purpose);
        Assert.Equal(12, intent.PullRequestNumber);
        Assert.Equal(pullRequest.HeadSha, intent.ExpectedHeadSha);
        Assert.Equal(pullRequest.BaseSha, intent.ExpectedBaseSha);
        Assert.Equal(SourceControlMergeMethod.Squash, intent.Method);
    }

    [Fact]
    public void PersistsPreApprovalRequestThenFinalizesOnlyWithItsTypedReceipt()
    {
        var acceptedRun = AcceptedRun();
        var pin = RepositoryPin(acceptedRun);
        var (current, assembly) = MergeReadyForApproval();
        const string approvalRequestId = "approval-request-1";
        var request = SourceControlMergeIntentRequest.Create(
            IntentId,
            approvalRequestId,
            acceptedRun,
            pin,
            sourceStateVersion: 7,
            current,
            assembly,
            PullRequest(),
            SourceControlMergeMethod.Rebase,
            DateTimeOffset.Parse("2026-10-07T08:00:00Z"));

        var approval = CoordinatorDecisionFlow.RequestApproval(
            current, approvalRequestId, IntentId, Actor);
        var answer = approval.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            approval.Value!.RequestId,
            Actor,
            acceptedRun.Fence,
            CoordinatorGateChoices.Approve,
            null));
        var intent = request.Approve(answer.State, answer.Receipt!);

        Assert.Equal(7, request.SourceStateVersion);
        Assert.Equal(approvalRequestId, request.ApprovalRequestId);
        Assert.Equal(IntentId, intent.IntentId);
        Assert.Equal(approval.Value.RequestId, intent.ApprovalReceipt.RequestId);
        Assert.Equal("merge", intent.WorkflowStepId);
        Assert.Equal(SourceControlMergeMethod.Rebase, intent.Method);
    }

    [Fact]
    public void RejectsApprovalNotPresentInCurrentDecisionState()
    {
        var acceptedRun = AcceptedRun();
        var pin = RepositoryPin(acceptedRun);
        var (state, assembly, receipt) = ApprovedMerge();

        Assert.Throws<InvalidOperationException>(() => SourceControlMergeIntent.Create(
            IntentId,
            acceptedRun,
            pin,
            state,
            assembly,
            receipt with { RequestId = "different-approval" },
            PullRequest(),
            SourceControlMergeMethod.Squash,
            DateTimeOffset.Parse("2026-10-07T08:00:00Z")));
    }

    [Fact]
    public void RejectsApprovalForDifferentIntentOrActor()
    {
        var acceptedRun = AcceptedRun();
        var pin = RepositoryPin(acceptedRun);
        var (state, assembly, receipt) = ApprovedMerge();

        Assert.Throws<InvalidOperationException>(() => SourceControlMergeIntent.Create(
            "another-intent",
            acceptedRun,
            pin,
            state,
            assembly,
            receipt,
            PullRequest(),
            SourceControlMergeMethod.Squash,
            DateTimeOffset.Parse("2026-10-07T08:00:00Z")));
        Assert.Throws<InvalidOperationException>(() => SourceControlMergeIntent.Create(
            IntentId,
            acceptedRun,
            pin,
            state,
            assembly,
            receipt with { ActorId = "other-actor" },
            PullRequest(),
            SourceControlMergeMethod.Squash,
            DateTimeOffset.Parse("2026-10-07T08:00:00Z")));
    }

    [Fact]
    public void RejectsStaleRunPinAndNonOpenOrChangedPullRequest()
    {
        var acceptedRun = AcceptedRun();
        var pin = RepositoryPin(acceptedRun);
        var (state, assembly, receipt) = ApprovedMerge();

        Assert.Throws<InvalidOperationException>(() => SourceControlMergeIntent.Create(
            IntentId,
            AcceptedRun(acceptedRun.Fence + 1),
            pin,
            state,
            assembly,
            receipt,
            PullRequest(),
            SourceControlMergeMethod.Squash,
            DateTimeOffset.Parse("2026-10-07T08:00:00Z")));
        Assert.Throws<ArgumentException>(() => SourceControlMergeIntent.Create(
            IntentId,
            acceptedRun,
            pin,
            state,
            assembly,
            receipt,
            PullRequest() with { State = "closed" },
            SourceControlMergeMethod.Squash,
            DateTimeOffset.Parse("2026-10-07T08:00:00Z")));
        Assert.Throws<ArgumentException>(() => SourceControlMergeIntent.Create(
            IntentId,
            acceptedRun,
            pin,
            state,
            assembly,
            receipt,
            PullRequest() with { HeadSha = "changed" },
            SourceControlMergeMethod.Squash,
            DateTimeOffset.Parse("2026-10-07T08:00:00Z")));
    }

    [Fact]
    public void RepositoryPinRequiresAcceptedRunSourceControlResourceReadBinding()
    {
        var acceptedRun = AcceptedRun();
        var providerBinding = PinnedSourceControlBinding("different-run", []);

        Assert.Throws<ArgumentException>(() => new SourceControlRepositoryPin(
            "pin-1",
            acceptedRun,
            providerBinding,
            new SourceControlRepositoryIdentity("octo", "repo"),
            ApiCredential(),
            CheckoutCredential(),
            WebhookCredential(),
            123,
            "main",
            isPrivate: true,
            DateTimeOffset.Parse("2026-10-07T08:00:00Z")));
    }

    private static SourceControlAcceptedRunBinding AcceptedRun(long fence = 4) =>
        new(
            "https://issuer.test",
            Actor,
            "tenant-1",
            "project-1",
            "run-1",
            "session-1",
            new string('A', 64),
            1,
            1,
            1,
            "context-1",
            fence);

    private static SourceControlRepositoryPin RepositoryPin(
        SourceControlAcceptedRunBinding acceptedRun) =>
        new(
            "source-pin-1",
            acceptedRun,
            PinnedSourceControlBinding(
                acceptedRun.RunId,
                [SourceControlCapabilities.RepositoryRead, SourceControlCapabilities.Merge]),
            new SourceControlRepositoryIdentity("octo", "repo"),
            ApiCredential(),
            CheckoutCredential(),
            WebhookCredential(),
            123,
            "main",
            isPrivate: true,
            DateTimeOffset.Parse("2026-10-07T08:00:00Z"));

    private static SourceControlCredentialReference ApiCredential() =>
        new(new SecretRef("github-api-v1", "version-1"), SourceControlSecretPurposes.Api);

    private static SourceControlCredentialReference CheckoutCredential() =>
        new(new SecretRef("github-checkout-v1", "version-1"), SourceControlSecretPurposes.Checkout);

    private static SourceControlCredentialReference WebhookCredential() =>
        new(new SecretRef("github-webhook-v1", "version-1"), SourceControlSecretPurposes.Webhook);

    private static PinnedProviderBinding PinnedSourceControlBinding(
        string runId,
        string[] negotiatedCapabilities)
    {
        var advertised = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            SourceControlCapabilities.RepositoryRead,
            SourceControlCapabilities.Merge);
        var registration = new ProviderRegistration(
            new ProviderDescriptor(
                ProviderSeam.SourceControl,
                "github",
                new Version(1, 0, 0),
                1,
                ProviderHostingPattern.InProcess,
                advertised),
            true,
            "options-r1",
            1);
        var catalog = ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.SourceControl, "github")],
            []);
        Assert.True(catalog.IsSuccess);
        var resolver = new ProviderResolver(catalog.Value!);
        var resolution = resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.SourceControl,
            null,
            new Version(1, 0, 0),
            1,
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal)));
        Assert.True(resolution.IsSuccess);
        var capabilities = negotiatedCapabilities.ToImmutableHashSet(StringComparer.Ordinal);
        var pinned = resolver.Pin(
            runId,
            resolution.Value!.Candidate!,
            "repo:123",
            new ResourceNegotiation(
                new ProviderResourceRef(ProviderSeam.SourceControl, "github", "repo:123", 1),
                capabilities));
        Assert.True(pinned.IsSuccess);
        return pinned.Value!;
    }

    private static (CoordinatorDecisionState State,
        CoordinatorAssemblyRequestSnapshot Assembly,
        CoordinatorGateDecisionReceipt Receipt) ApprovedMerge()
    {
        var (current, assembly) = MergeReadyForApproval();
        var approval = CoordinatorDecisionFlow.RequestApproval(
            current,
            "approval-request-1",
            IntentId,
            Actor);
        var approved = approval.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            approval.Value!.RequestId,
            Actor,
            current.Fence,
            CoordinatorGateChoices.Approve,
            null));
        Assert.True(approved.IsAccepted);
        return (approved.State, assembly, approved.Receipt!);
    }

    private static (CoordinatorDecisionState State, CoordinatorAssemblyRequestSnapshot Assembly)
        MergeReadyForApproval()
    {
        var workflow = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Fixed("setup", 0, [], ["docs/plan.md"]),
            WorkflowTestData.Open("implement", 1, ["setup"]),
            WorkflowTestData.Platform("merge", 2, ["implement"], WorkflowPlatformGate.Merge));
        var catalog = new AuthorizedWorkflowCatalog(workflow, [workflow]);
        var outcome = new CoordinatorOutcomeSpecification(
            "outcome-1",
            "Implement the requested change.",
            "The requested change is available.",
            "Only requested workflow behavior changes.",
            "Existing provider pins remain authoritative.",
            []);
        var proposedOutcome = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            CoordinatorDecisionState.Create(4),
            outcome,
            "outcome-approval-1",
            Actor);
        var confirmedOutcome = proposedOutcome.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            proposedOutcome.State.PendingGate!.RequestId,
            Actor,
            4,
            CoordinatorGateChoices.Approve,
            null)).State;
        var selected = CoordinatorDecisionFlow.SelectWorkflow(
            confirmedOutcome,
            catalog,
            requestedWorkflowId: null,
            firstUseRequestId: null,
            authorizedActorId: null);
        var plan = CoordinatorDecisionFlow.ProposeWorkPlan(
            selected.State,
            WorkflowTestData.Plan(WorkflowTestData.Item("implementation")),
            WorkflowTestData.SelectionContext(),
            "plan-approval-1",
            Actor);
        var confirmedPlan = plan.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            plan.State.PendingGate!.RequestId,
            Actor,
            4,
            CoordinatorGateChoices.Approve,
            null)).State;
        var assembly = CoordinatorDecisionFlow.RequestAssembly(
            confirmedPlan,
            new CoordinatorAssemblyRequest(
                "assembly-request-1",
                workflow.Id,
                workflow.Revision,
                "plan-1",
                "merge",
                WorkflowPlatformGate.Merge));
        Assert.True(assembly.IsSuccess);
        return (assembly.State!, assembly.Value!);
    }

    private static SourceControlPullRequest PullRequest() =>
        new(
            12,
            new Uri("https://github.com/octo/repo/pull/12"),
            "open",
            "feature",
            new string('a', 40),
            "main",
            new string('b', 40),
            Merged: false,
            SourceControlPullRequestDisposition.Created);
}
