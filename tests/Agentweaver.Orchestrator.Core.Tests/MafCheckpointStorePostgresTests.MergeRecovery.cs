using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Microsoft.Agents.AI.Workflows;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed partial class MafCheckpointStorePostgresTests
{
    [Fact]
    public async Task MergeIntentAndTerminalReceiptProjectAcrossFreshPostgresStores()
    {
        var source = CreateCurrentSourceControlRun();
        var workflow = WorkflowTestData.Snapshot(WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("implement", 0, []),
            WorkflowTestData.Platform("merge", 1, ["implement"], WorkflowPlatformGate.Merge)));
        var validatedPlan = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            WorkflowTestData.Plan(WorkflowTestData.Item("implementation")),
            WorkflowTestData.SelectionContext());
        Assert.True(validatedPlan.IsValid,
            string.Join("; ", validatedPlan.Issues.Select(issue => issue.Message)));
        var plan = validatedPlan.Value!;
        var initialProgress = MafExecutionProgress.Empty with
        {
            NonModelSteps = MafExecutionProgress.Empty.NonModelSteps.Add(
                "merge", MafExecutionTaskStatus.Pending)
        };
        var initial = await _executionStore.AppendAsync(
            "merge-initial",
            null,
            new MafExecutionCheckpoint(1, plan.Plan.Id, 1, initialProgress),
            CancellationToken.None);
        var intent = MafMergeContract.CreateIntent(
            Guid.NewGuid(),
            _binding.Identity,
            "merge-intent",
            2,
            plan.Plan.Id,
            workflow.Definition.Id,
            workflow.Definition.Revision,
            "merge",
            1,
            _binding.ExecutionFence,
            CoordinationOwnerStore.HashSelection(_selection.Selection),
            source.Pin,
            42,
            new string('a', 40),
            SourceControlMergeMethod.Rebase);
        var runningState = CoordinationEndpoints.PrepareMergeCheckpoint(initial, intent)!;
        _ = await _executionStore.AppendAsync(
            intent.CheckpointId, initial.Info, runningState, CancellationToken.None);

        // A new checkpoint store observes the durable intent and projects it as retryable, not completed.
        var recoveredStore = CreateRecoveryStore();
        var recovered = await recoveredStore.ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.True(MafExecutionCheckpointContract.MergeIntentsMatch(
            intent, recovered.State.MergeIntents["merge"]));
        Assert.Equal(MafExecutionTaskStatus.Running,
            recovered.State.Progress.NonModelSteps["merge"]);
        var intentRecovery = MafExecutionRecoveryProjector.Project(
            recovered, plan, intent.DecisionStateVersion, true, null);
        var projectedIntent = Assert.Single(intentRecovery.PlatformGateOperations);
        Assert.Equal(WorkflowPlatformGate.Merge, projectedIntent.Gate);
        Assert.Equal(intent.OperationId, projectedIntent.OperationId);
        Assert.Equal("intentOnly", projectedIntent.EvidenceState);
        Assert.Equal("mayRepeat", projectedIntent.EffectRecoveryState);

        // A saved Merge operation is unavailable when the accepted plan changes its gate type.
        var mismatchedWorkflow = WorkflowTestData.Snapshot(WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("implement", 0, []),
            WorkflowTestData.Platform(
                "merge", 1, ["implement"], WorkflowPlatformGate.OpenPullRequest)));
        var mismatchedPlanResult = WorkPlanValidator.ValidateAndSnapshot(
            mismatchedWorkflow,
            WorkflowTestData.Plan(WorkflowTestData.Item("implementation")),
            WorkflowTestData.SelectionContext());
        Assert.True(mismatchedPlanResult.IsValid,
            string.Join("; ", mismatchedPlanResult.Issues.Select(issue => issue.Message)));
        var mismatchedRecovery = MafExecutionRecoveryProjector.Project(
            recovered, mismatchedPlanResult.Value, intent.DecisionStateVersion, true, null);
        Assert.Equal("unavailable", mismatchedRecovery.State);
        Assert.Equal("maf_execution_checkpoint_progress_mismatch", mismatchedRecovery.UnavailableCode);

        var receipt = new MafMergeTerminalReceipt(
            intent.OperationId,
            intent.OwnerIntentId,
            intent.PullRequestNumber,
            intent.ExpectedHeadSha,
            new string('b', 40));
        var completedState = CoordinationEndpoints.PrepareMergeCompletionCheckpoint(
            recovered, intent, receipt)!;
        _ = await recoveredStore.AppendAsync(
            "merge-terminal", recovered.Info, completedState, CancellationToken.None);
        var afterRestart = await CreateRecoveryStore().ReadLatestAsync(CancellationToken.None);
        Assert.NotNull(afterRestart);
        Assert.Equal(MafExecutionTaskStatus.Succeeded,
            afterRestart.State.Progress.NonModelSteps["merge"]);
        Assert.Equal(receipt, afterRestart.State.MergeReceipts["merge"]);
        var receiptRecovery = MafExecutionRecoveryProjector.Project(
            afterRestart, plan, intent.DecisionStateVersion, true, null);
        var projectedReceipt = Assert.Single(receiptRecovery.PlatformGateOperations);
        Assert.Equal("terminalReceiptRecorded", projectedReceipt.EvidenceState);
        Assert.Null(projectedReceipt.EffectRecoveryState);
        Assert.Equal(
            "terminalReceiptRecorded",
            Assert.Single(receiptRecovery.Steps, step => step.StepId == "merge").EffectRecoveryState);
    }

    private MafExecutionCheckpointStore CreateRecoveryStore() =>
        new(new PostgresMafCheckpointStore(_fixture.DataSource, _schema, null), _binding);
}
