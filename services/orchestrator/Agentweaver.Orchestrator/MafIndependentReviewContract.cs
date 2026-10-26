using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Agentweaver.SourceControl;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

/// <summary>Builds a review of the exact completed ancestors, never an agent-authored approval.</summary>
internal static class MafIndependentReviewContract
{
    private const int MaximumEvidenceSummaryLength = 262_144;

    internal static void RequireGateOrder(WorkflowDefinition workflow)
    {
        // A review must follow every declared build/test gate; a content workflow need not have one.
        foreach (var review in workflow.Steps.Where(step =>
                     step.PlatformGate == WorkflowPlatformGate.IndependentReview))
        {
            var ancestors = Ancestors(workflow, review);
            if (workflow.Steps.Any(step => step.PlatformGate == WorkflowPlatformGate.BuildTest &&
                                           !ancestors.Contains(step.Id)))
                throw Conflict("maf_execution_review_order_invalid");
        }
        // Every review must precede merge; a parallel review may not be silently skipped.
        foreach (var merge in workflow.Steps.Where(step => step.PlatformGate == WorkflowPlatformGate.Merge))
            if (workflow.Steps.Any(step => step.PlatformGate == WorkflowPlatformGate.IndependentReview &&
                                           !Ancestors(workflow, merge).Contains(step.Id)))
                throw Conflict("maf_execution_review_order_invalid");
    }

    internal static MafExecutionReviewIntent CreateIntent(
        Guid operationId, SessionIdentity identity, string checkpointId, long revision,
        WorkPlanSnapshot plan, WorkflowStepDefinition step, MafExecutionCheckpoint state,
        long decisionVersion, string authorizedActorId, long fence, string selectionHash)
    {
        // Canonicalize all completed ancestor results and platform receipts before creating the request.
        var ancestors = Ancestors(plan.Workflow.Definition, step);
        // No partial or missing predecessor can be presented to the human as complete evidence.
        foreach (var ancestor in plan.Workflow.Definition.Steps.Where(item => ancestors.Contains(item.Id)))
        {
            if (ancestor.PlatformGate == WorkflowPlatformGate.BuildTest &&
                (!state.BuildTestIntents.TryGetValue(ancestor.Id, out var buildIntent) ||
                 !state.BuildTestReceipts.TryGetValue(ancestor.Id, out var buildReceipt) ||
                 buildReceipt.ValidateFor(buildIntent) != MafExecutionTaskStatus.Succeeded) ||
                ancestor.PlatformGate == WorkflowPlatformGate.OpenPullRequest &&
                (!state.OpenPullRequestIntents.TryGetValue(ancestor.Id, out var prIntent) ||
                 !state.OpenPullRequestReceipts.TryGetValue(ancestor.Id, out var prReceipt) ||
                 MafOpenPullRequestContract.ValidateReceipt(prIntent, prReceipt) != MafExecutionTaskStatus.Succeeded) ||
                ancestor.Mode == WorkflowStepMode.Open &&
                plan.Plan.Items.Any(item => item.WorkflowStepId == ancestor.Id &&
                                            !state.Results.ContainsKey(item.Id)) ||
                ancestor.Mode == WorkflowStepMode.Fixed &&
                !state.FixedWorkAssociations.Values.Any(item =>
                    item.WorkPlanId == plan.Plan.Id && item.StepId == ancestor.Id &&
                    state.Results.ContainsKey(item.AssociationId)))
                throw Conflict("maf_execution_review_evidence_invalid");
        }
        var build = state.BuildTestReceipts
            .Where(pair => ancestors.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new
            {
                stepId = pair.Key, pair.Value.OperationId, pair.Value.OperationEvidenceSha256,
                pair.Value.OutputSha256, pair.Value.WorkspaceCapture,
                workspaceSource = state.BuildTestIntents[pair.Key].EffectiveWorkspaceSource
            }).ToArray();
        var pullRequests = state.OpenPullRequestReceipts
            .Where(pair => ancestors.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new
            {
                stepId = pair.Key, pair.Value.OperationId, pair.Value.PullRequestNumber,
                pair.Value.HeadSha, pair.Value.RequestFingerprint,
                state.OpenPullRequestIntents[pair.Key].SourceControlPinId,
                state.OpenPullRequestIntents[pair.Key].Repository,
                workspace = state.OpenPullRequestIntents[pair.Key].Workspace
            }).ToArray();
        var work = state.Results
            .Where(pair => plan.Plan.Items.Any(item => item.Id == pair.Key &&
                                                        ancestors.Contains(item.WorkflowStepId)) ||
                           state.FixedWorkAssociations.TryGetValue(pair.Key, out var fixedWork) &&
                           fixedWork.WorkPlanId == plan.Plan.Id &&
                           ancestors.Contains(fixedWork.StepId))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new
            {
                associationId = pair.Key,
                resultSha256 = Convert.ToHexStringLower(SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(pair.Value)))
            }).ToArray();
        // An empty review would be a no-op gate, not a human assessment of completed work.
        if (build.Length == 0 && pullRequests.Length == 0 && work.Length == 0)
            throw Conflict("maf_execution_review_evidence_invalid");
        var evidence = JsonSerializer.Serialize(new
        {
            planId = plan.Plan.Id,
            workflowId = plan.Workflow.Definition.Id,
            definitionRevision = plan.Workflow.Definition.Revision,
            stepId = step.Id,
            build,
            pullRequests,
            work
        });
        // The prompt carries exact source IDs and digests; its hash binds the human answer to those bytes.
        var intent = new MafExecutionReviewIntent(
            operationId, identity, checkpointId, revision, plan.Plan.Id,
            plan.Workflow.Definition.Id, plan.Workflow.Definition.Revision, step.Id,
            decisionVersion, authorizedActorId, fence, selectionHash,
            "maf-review-" + operationId.ToString("N"),
            Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(evidence))),
            evidence);
        if (!IsValidIntent(intent))
            throw Conflict("maf_execution_review_evidence_invalid");
        return intent;
    }

    internal static bool IsValidIntent(MafExecutionReviewIntent? intent)
    {
        // A checkpoint must preserve both the owner scope and the SHA-256 of the exact human-facing evidence.
        return intent is not null &&
        intent.OperationId != Guid.Empty &&
        intent.Identity != default &&
        ValidId(intent.Identity.ProjectId) && ValidId(intent.Identity.RunId) &&
        ValidId(intent.Identity.SessionId) && ValidId(intent.CheckpointId) &&
        ValidId(intent.WorkPlanId) && ValidId(intent.WorkflowId) &&
        ValidId(intent.DefinitionRevision) && ValidId(intent.StepId) &&
        ValidId(intent.AuthorizedActorId) &&
        intent.CheckpointRevision > 0 && intent.DecisionStateVersion > 0 &&
        intent.ExecutionFence > 0 && IsHash(intent.AcceptedSelectionHash) &&
        intent.ApprovalRequestId == "maf-review-" + intent.OperationId.ToString("N") &&
        intent.EvidenceSummary is { Length: > 0 and <= MaximumEvidenceSummaryLength } &&
        IsHash(intent.EvidenceHash) &&
        string.Equals(intent.EvidenceHash,
            Convert.ToHexStringLower(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(intent.EvidenceSummary))), StringComparison.Ordinal);
    }

    internal static MafExecutionTaskStatus ValidateReceipt(
        MafExecutionReviewIntent intent, MafReviewTerminalReceipt receipt)
    {
        // Only the exact owner request, actor, fence and evidence may complete this gate.
        if (receipt.OperationId != intent.OperationId ||
            receipt.ApprovalRequestId != intent.ApprovalRequestId ||
            receipt.EvidenceHash != intent.EvidenceHash ||
            receipt.ActorId.Length is < 1 or > 128 ||
            receipt.ActorId.Any(char.IsControl) ||
            receipt.Fence != intent.ExecutionFence ||
            receipt.Reason != (receipt.ChoiceId == CoordinatorGateChoices.Approve
                ? "approved" : "rejected") ||
            receipt.ChoiceId is not (CoordinatorGateChoices.Approve or CoordinatorGateChoices.Reject))
            throw new ArgumentException("The review decision does not match its intent.", nameof(receipt));
        return receipt.ChoiceId == CoordinatorGateChoices.Approve
            ? MafExecutionTaskStatus.Succeeded
            : MafExecutionTaskStatus.Failed;
    }

    internal static MafReviewTerminalReceipt? ReadDecision(
        MafExecutionReviewIntent intent, CoordinatorDecisionCurrentState current, string actorId)
    {
        // Exactly one decision advance is allowed: an unrelated gate or changed authority fails closed.
        if (current.State.Fence != intent.ExecutionFence ||
            current.SelectionHash != intent.AcceptedSelectionHash ||
            current.State.ConfirmedWorkPlan?.Plan.Id != intent.WorkPlanId ||
            current.State.SelectedWorkflow?.Definition.Id != intent.WorkflowId ||
            current.State.SelectedWorkflow.Definition.Revision != intent.DefinitionRevision ||
            current.StateVersion < intent.DecisionStateVersion ||
            current.StateVersion > intent.DecisionStateVersion + 2)
            throw Conflict("maf_execution_review_binding_changed");
        if (actorId != intent.AuthorizedActorId)
            throw Conflict("maf_execution_review_decision_conflict");
        if (current.StateVersion == intent.DecisionStateVersion)
        {
            // A saved intent may precede the decision owner's first write after a crash.
            if (current.State.PendingGate is not null)
                throw Conflict("maf_execution_review_decision_conflict");
            return null;
        }
        if (current.State.PendingGate is { } pending)
        {
            if (current.StateVersion != intent.DecisionStateVersion + 1 ||
                pending.RequestId != intent.ApprovalRequestId ||
                pending.SubjectId != intent.EvidenceHash ||
                pending.Kind != CoordinatorGateKind.Approval ||
                pending.AuthorizedActorId != actorId ||
                pending.Fence != intent.ExecutionFence)
                throw Conflict("maf_execution_review_decision_conflict");
            return null;
        }
        if (current.StateVersion != intent.DecisionStateVersion + 2)
            throw Conflict("maf_execution_review_decision_conflict");
        var matches = current.State.DecisionReceipts.Where(receipt =>
            receipt.Kind == CoordinatorGateKind.Approval &&
            receipt.RequestId == intent.ApprovalRequestId &&
            receipt.SubjectId == intent.EvidenceHash &&
            receipt.ActorId == actorId &&
            receipt.Fence == intent.ExecutionFence &&
            receipt.FreeformAnswer is null &&
            receipt.ChoiceId is CoordinatorGateChoices.Approve or CoordinatorGateChoices.Reject).ToArray();
        if (matches.Length != 1)
            throw Conflict("maf_execution_review_decision_conflict");
        return new MafReviewTerminalReceipt(intent.OperationId, intent.ApprovalRequestId,
            intent.EvidenceHash, matches[0].ActorId, matches[0].Fence,
            matches[0].ChoiceId!, matches[0].ChoiceId == CoordinatorGateChoices.Approve
                ? "approved" : "rejected");
    }

    internal static bool AllowsDecisionVersionAdvance(
        MafExecutionCheckpoint checkpoint,
        CoordinatorDecisionCurrentState current,
        long recordedVersion)
    {
        // Fixed work and completed BuildTest evidence may span only durable review transitions.
        if (recordedVersion < 1 || current.StateVersion < recordedVersion)
            return false;
        // An unchanged owner version needs no review proof and may still have an unrelated pending gate.
        if (recordedVersion == current.StateVersion)
            return true;
        var version = recordedVersion;
        while (version < current.StateVersion)
        {
            // Each version step must be explained by one immutable review intent for the same accepted plan.
            var matches = checkpoint.ReviewIntents.Values
                .Where(intent => intent.DecisionStateVersion == version).ToArray();
            if (matches.Length != 1)
                return false;
            var intent = matches[0];
            if (!IsValidIntent(intent) ||
                current.State.Fence != intent.ExecutionFence ||
                current.SelectionHash != intent.AcceptedSelectionHash ||
                current.State.ConfirmedWorkPlan?.Plan.Id != intent.WorkPlanId ||
                current.State.SelectedWorkflow?.Definition.Id != intent.WorkflowId ||
                current.State.SelectedWorkflow.Definition.Revision != intent.DefinitionRevision)
                return false;

            if (current.StateVersion == version + 1)
            {
                // A pending review is the only permitted one-version advance; it cannot already have a checkpoint receipt.
                return !checkpoint.ReviewReceipts.ContainsKey(intent.StepId) &&
                    current.State.PendingGate is { } pending &&
                    pending.RequestId == intent.ApprovalRequestId &&
                    pending.SubjectId == intent.EvidenceHash &&
                    pending.Kind == CoordinatorGateKind.Approval &&
                    pending.AuthorizedActorId == intent.AuthorizedActorId &&
                    pending.Fence == intent.ExecutionFence &&
                    !current.State.DecisionReceipts.Any(receipt =>
                        receipt.RequestId == intent.ApprovalRequestId);
            }
            if (current.StateVersion < version + 2)
                return false;

            // The owner's exact answer accounts for both the request and terminal transition versions.
            var receipts = current.State.DecisionReceipts.Where(receipt =>
                receipt.RequestId == intent.ApprovalRequestId).ToArray();
            if (receipts.Length != 1 ||
                receipts[0].Kind != CoordinatorGateKind.Approval ||
                receipts[0].SubjectId != intent.EvidenceHash ||
                receipts[0].ActorId != intent.AuthorizedActorId ||
                receipts[0].Fence != intent.ExecutionFence ||
                receipts[0].FreeformAnswer is not null ||
                receipts[0].ChoiceId is not (CoordinatorGateChoices.Approve or CoordinatorGateChoices.Reject))
                return false;
            var receipt = new MafReviewTerminalReceipt(
                intent.OperationId, intent.ApprovalRequestId, intent.EvidenceHash,
                receipts[0].ActorId, receipts[0].Fence, receipts[0].ChoiceId!,
                receipts[0].ChoiceId == CoordinatorGateChoices.Approve ? "approved" : "rejected");
            if (checkpoint.ReviewReceipts.TryGetValue(intent.StepId, out var recorded) &&
                recorded != receipt)
                return false;
            version = checked(version + 2);
        }

        // Do not accept a version jump, even if a matching review receipt exists in the state history.
        return version == current.StateVersion && current.State.PendingGate is null;
    }

    internal static bool MatchesBuildTestWorkspaceSource(
        SandboxBuildTestWorkspaceSource source,
        GitWorkspaceCaptureDocument current)
    {
        // The canonical manifest digest covers file names, modes, and content hashes, while the tree hash binds Git identity.
        var manifest = current.Manifest;
        return source.IsValid() &&
            GitWorkspaceCapturePackage.Hash(current.ManifestBytes) == current.ManifestSha256 &&
            manifest.WorkspaceId == source.WorkspaceId &&
            manifest.RunId == source.Identity.RunId &&
            manifest.RepositoryId == source.SourceControlResource.ResourceId &&
            manifest.ResourceGeneration == source.SourceControlResource.Generation &&
            manifest.WorkspaceIncarnationId == source.WorkspaceIncarnationId &&
            manifest.BranchName == source.BranchName &&
            manifest.BaseSha == source.BaseSha.ToLowerInvariant() &&
            manifest.OutputTreeSha == source.BaselineTreeSha &&
            current.ManifestSha256 == source.BaselineManifestSha256 &&
            current.ManifestBytes.LongLength == source.BaselineManifestByteLength;
    }

    internal static HashSet<string> Ancestors(WorkflowDefinition workflow, WorkflowStepDefinition step)
    {
        // Walk transitive dependencies so a distant build or PR cannot escape the review evidence.
        var byId = workflow.Steps.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Visit(WorkflowStepDefinition current)
        {
            foreach (var dependency in current.DependsOn)
                if (seen.Add(dependency) && byId.TryGetValue(dependency, out var ancestor))
                    Visit(ancestor);
        }
        Visit(step);
        return seen;
    }

    private static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool ValidId(string? value) =>
        value is { Length: > 0 and <= 128 } && !value.Any(char.IsControl);

    private static CoordinationException Conflict(string code) =>
        new(code, StatusCodes.Status409Conflict);
}
