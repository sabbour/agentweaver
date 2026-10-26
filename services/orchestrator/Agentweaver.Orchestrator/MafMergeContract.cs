using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Agents.AI.Workflows;

namespace Agentweaver.Orchestrator;

internal sealed record MafMergeOwnerIntent(
    string IntentId,
    string State,
    string? ApprovalRequestId,
    string? Repository,
    long? PullRequestNumber,
    string? HeadSha,
    SourceControlMergeMethod? Method,
    Guid? ApprovalDecisionId,
    long? ApprovalStateVersion,
    bool HasGrantReference,
    string? MergeSha,
    string? FailureCode);

internal enum MafMergeExecutionOutcome
{
    NoProgress,
    AwaitingApproval,
    Completed
}

/// <summary>Builds and validates checkpointed Source Control merge-gate data.</summary>
internal static class MafMergeContract
{
    internal static bool IsValidInput(SourceControlMergeStepInput? input)
    {
        // A pull-request number and head must be supplied together; the workflow may derive both from an upstream receipt.
        if (input is null || !Enum.IsDefined(input.Method) ||
            (input.PullRequestNumber is null) != (input.ExpectedHeadSha is null))
            return false;
        return input.PullRequestNumber is null ||
            input.PullRequestNumber > 0 && IsGitSha(input.ExpectedHeadSha);
    }

    internal static (long PullRequestNumber, string ExpectedHeadSha) ResolvePullRequest(
        WorkflowDefinition workflow,
        WorkflowStepDefinition step,
        MafExecutionCheckpoint checkpoint,
        SourceControlMergeStepInput input)
    {
        // Resolve only actual workflow ancestors so unrelated PR receipts cannot be merged accidentally.
        var ancestorIds = OpenPullRequestAncestorIds(workflow, step);
        if (ancestorIds.IsEmpty)
        {
            if (input.PullRequestNumber is not { } number || input.ExpectedHeadSha is not { } headSha)
                throw InvalidMergeInput();
            return (number, headSha);
        }

        var receipts = ancestorIds.Select(stepId =>
        {
            if (!checkpoint.OpenPullRequestIntents.TryGetValue(stepId, out var intent) ||
                !checkpoint.OpenPullRequestReceipts.TryGetValue(stepId, out var receipt) ||
                MafOpenPullRequestContract.ValidateReceipt(intent, receipt) != MafExecutionTaskStatus.Succeeded)
                throw InvalidMergeInput();
            return (PullRequestNumber: receipt.PullRequestNumber, HeadSha: receipt.HeadSha);
        });
        if (input.PullRequestNumber is { } suppliedNumber &&
            input.ExpectedHeadSha is { } suppliedHeadSha)
            receipts = receipts.Where(receipt =>
                receipt.PullRequestNumber == suppliedNumber &&
                string.Equals(receipt.HeadSha, suppliedHeadSha, StringComparison.OrdinalIgnoreCase));
        var matches = receipts.ToArray();
        if (matches.Length != 1)
            throw InvalidMergeInput();
        return matches[0];
    }

    internal static MafExecutionMergeIntent CreateIntent(
        Guid operationId,
        SessionIdentity identity,
        string checkpointId,
        long checkpointRevision,
        string workPlanId,
        string workflowId,
        string definitionRevision,
        string stepId,
        long decisionStateVersion,
        long executionFence,
        string acceptedSelectionHash,
        SourceControlRepositoryPin pin,
        long pullRequestNumber,
        string expectedHeadSha,
        SourceControlMergeMethod method)
    {
        // Save the owner-derived identity before the call so a retry cannot create a second approval or merge.
        var idempotencyKey = "maf-merge-" + operationId.ToString("N");
        var ownerIntentId = SourceControlOwnerStore.CreateIntentId(identity, idempotencyKey);
        var intent = new MafExecutionMergeIntent(
            operationId,
            idempotencyKey,
            ownerIntentId,
            SourceControlOwnerStore.CreateApprovalRequestId(ownerIntentId),
            identity,
            checkpointId,
            checkpointRevision,
            workPlanId,
            workflowId,
            definitionRevision,
            stepId,
            decisionStateVersion,
            executionFence,
            acceptedSelectionHash,
            pin.PinId,
            pin.Repository,
            pullRequestNumber,
            expectedHeadSha,
            method);
        if (!IsValidIntent(intent))
            throw new CoordinationException(
                "source_control_merge_request_invalid", StatusCodes.Status400BadRequest);
        return intent;
    }

    internal static bool IsValidIntent(MafExecutionMergeIntent? intent)
    {
        // Reject any checkpoint data that could redirect the request or change its replay identity.
        return intent is not null &&
            intent.OperationId != Guid.Empty &&
            string.Equals(
                intent.IdempotencyKey, "maf-merge-" + intent.OperationId.ToString("N"),
                StringComparison.Ordinal) &&
            IsMetadataValue(intent.OwnerIntentId, 128) &&
            IsMetadataValue(intent.ApprovalRequestId, 256) &&
            intent.Identity != default &&
            IsMetadataValue(intent.Identity.ProjectId, 256) &&
            IsMetadataValue(intent.Identity.RunId, 256) &&
            IsMetadataValue(intent.Identity.SessionId, 256) &&
            IsMetadataValue(intent.CheckpointId, 128) &&
            intent.CheckpointRevision > 0 &&
            IsMetadataValue(intent.WorkPlanId, 128) &&
            IsMetadataValue(intent.WorkflowId, 128) &&
            IsMetadataValue(intent.DefinitionRevision, 128) &&
            IsMetadataValue(intent.StepId, 128) &&
            intent.DecisionStateVersion > 0 &&
            intent.ExecutionFence > 0 &&
            IsHash(intent.AcceptedSelectionHash) &&
            IsMetadataValue(intent.SourceControlPinId, 128) &&
            IsRepository(intent.Repository) &&
            intent.PullRequestNumber > 0 &&
            IsGitSha(intent.ExpectedHeadSha) &&
            Enum.IsDefined(intent.Method) &&
            string.Equals(
                intent.OwnerIntentId,
                SourceControlOwnerStore.CreateIntentId(intent.Identity, intent.IdempotencyKey),
                StringComparison.Ordinal) &&
            string.Equals(
                intent.ApprovalRequestId,
                SourceControlOwnerStore.CreateApprovalRequestId(intent.OwnerIntentId),
                StringComparison.Ordinal);
    }

    internal static void RequireCurrentSource(
        MafExecutionMergeIntent intent,
        CoordinationActor actor,
        SessionIdentity identity,
        string tenantId,
        long decisionStateVersion,
        CoordinatorDecisionState decision,
        long executionFence,
        string acceptedSelectionHash,
        SourceControlRepositoryPin pin,
        long? ownerApprovalStateVersion = null)
    {
        // Approval may advance the decision version once; the owner must bind that receipt to the current version.
        var accepted = pin.AcceptedRun;
        var matchesBinding =
            intent.Identity == identity &&
            intent.ExecutionFence == executionFence &&
            intent.DecisionStateVersion <= decisionStateVersion &&
            string.Equals(intent.AcceptedSelectionHash, acceptedSelectionHash, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(intent.SourceControlPinId, pin.PinId, StringComparison.Ordinal) &&
            intent.Repository == pin.Repository &&
            decision.Fence == executionFence &&
            decision.SelectedWorkflow?.Definition.Id == intent.WorkflowId &&
            decision.SelectedWorkflow.Definition.Revision == intent.DefinitionRevision &&
            decision.ConfirmedWorkPlan?.Plan.Id == intent.WorkPlanId &&
            accepted.ProjectId == identity.ProjectId &&
            accepted.RunId == identity.RunId &&
            accepted.RootSessionId == identity.SessionId &&
            accepted.Issuer == actor.Issuer &&
            accepted.Subject == actor.Subject &&
            accepted.TenantId == tenantId &&
            accepted.Fence == executionFence &&
            string.Equals(accepted.AcceptedSelectionHash, acceptedSelectionHash, StringComparison.OrdinalIgnoreCase);
        var decisionAdvanced = decisionStateVersion != intent.DecisionStateVersion;
        if (!matchesBinding ||
            decisionAdvanced &&
                (!HasCurrentApproval(intent, actor.Subject, decision) ||
                 ownerApprovalStateVersion is { } approvalVersion &&
                    approvalVersion != decisionStateVersion))
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
    }

    internal static MafMergeTerminalReceipt CreateReceipt(
        MafExecutionMergeIntent intent,
        MafMergeOwnerIntent prepared,
        MafMergeOwnerIntent executed)
    {
        // Combine the owner's approved request identity with its terminal execution result.
        ValidatePreparedOwnerIntent(intent, prepared);
        if (!string.Equals(executed.IntentId, intent.OwnerIntentId, StringComparison.Ordinal) ||
            executed.State != "merged" ||
            !IsGitSha(executed.MergeSha))
            throw InvalidOwnerResult();
        if (executed.FailureCode is not null)
            throw UncertainOwnerResult();
        if (prepared.State == "merged" &&
            !string.Equals(prepared.MergeSha, executed.MergeSha, StringComparison.OrdinalIgnoreCase))
            throw InvalidOwnerResult();
        return new(
            intent.OperationId,
            intent.OwnerIntentId,
            intent.PullRequestNumber,
            intent.ExpectedHeadSha,
            executed.MergeSha!);
    }

    internal static void ValidatePreparedOwnerIntent(
        MafExecutionMergeIntent intent,
        MafMergeOwnerIntent prepared)
    {
        // Verify the exact head and approval evidence before the owner is asked to perform the merge effect.
        if (prepared.State == "merged" && prepared.FailureCode is not null)
            throw UncertainOwnerResult();
        if (!MatchesOwnerRequest(intent, prepared) ||
            prepared.State is not ("approved" or "merge_started" or "merged") ||
            prepared.FailureCode is not null ||
            prepared.ApprovalDecisionId is null ||
            prepared.ApprovalStateVersion is null ||
            prepared.ApprovalStateVersion < intent.DecisionStateVersion ||
            prepared.State != "merged" && !prepared.HasGrantReference)
            throw InvalidOwnerResult();
    }

    internal static MafExecutionTaskStatus ValidateReceipt(
        MafExecutionMergeIntent intent,
        MafMergeTerminalReceipt receipt)
    {
        // A durable terminal status is valid only for the exact saved operation, PR, and expected head.
        if (receipt.OperationId != intent.OperationId ||
            !string.Equals(receipt.OwnerIntentId, intent.OwnerIntentId, StringComparison.Ordinal) ||
            receipt.PullRequestNumber != intent.PullRequestNumber ||
            !string.Equals(receipt.ExpectedHeadSha, intent.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase) ||
            !IsGitSha(receipt.MergeSha))
            throw new ArgumentException("The Merge receipt does not match its immutable intent.", nameof(receipt));
        return MafExecutionTaskStatus.Succeeded;
    }

    internal static string ResultSummary(MafMergeTerminalReceipt receipt) =>
        JsonSerializer.Serialize(new
        {
            kind = "merge",
            pullRequestNumber = receipt.PullRequestNumber,
            mergeSha = receipt.MergeSha
        });

    internal static bool IsGitSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static ImmutableHashSet<string> OpenPullRequestAncestorIds(
        WorkflowDefinition workflow,
        WorkflowStepDefinition step)
    {
        // Walk the confirmed dependency graph transitively and keep only prior Source Control PR gates.
        var steps = workflow.Steps.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var openPullRequestIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        void Visit(WorkflowStepDefinition current)
        {
            foreach (var dependencyId in current.DependsOn)
            {
                if (!visited.Add(dependencyId) || !steps.TryGetValue(dependencyId, out var dependency))
                    continue;
                if (dependency.Mode == WorkflowStepMode.Platform &&
                    dependency.PlatformGate == WorkflowPlatformGate.OpenPullRequest)
                    openPullRequestIds.Add(dependency.Id);
                Visit(dependency);
            }
        }
        Visit(step);
        return openPullRequestIds.ToImmutable();
    }

    // Every owner response must identify the exact persisted intent and immutable PR revision.
    private static bool MatchesOwnerRequest(MafExecutionMergeIntent intent, MafMergeOwnerIntent owner) =>
        string.Equals(owner.IntentId, intent.OwnerIntentId, StringComparison.Ordinal) &&
        string.Equals(owner.ApprovalRequestId, intent.ApprovalRequestId, StringComparison.Ordinal) &&
        string.Equals(owner.Repository, intent.Repository.FullName, StringComparison.OrdinalIgnoreCase) &&
        owner.PullRequestNumber == intent.PullRequestNumber &&
        string.Equals(owner.HeadSha, intent.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase) &&
        owner.Method == intent.Method;

    // Only a completed typed approval by the accepted actor can advance the original decision version.
    private static bool HasCurrentApproval(
        MafExecutionMergeIntent intent,
        string actorId,
        CoordinatorDecisionState decision) =>
        decision.PendingGate is null &&
        decision.DecisionReceipts.Any(receipt =>
            receipt.Kind == CoordinatorGateKind.Approval &&
            receipt.RequestId == intent.ApprovalRequestId &&
            receipt.SubjectId == intent.OwnerIntentId &&
            receipt.ChoiceId == CoordinatorGateChoices.Approve &&
            receipt.FreeformAnswer is null &&
            receipt.ActorId == actorId &&
            receipt.Fence == intent.ExecutionFence);

    // Repository identity is validated by bounded owner/name components before its full name is used.
    private static bool IsRepository(SourceControlRepositoryIdentity? repository) =>
        repository is not null &&
        IsMetadataValue(repository.Owner, 100) &&
        IsMetadataValue(repository.Name, 100);

    // Persisted identifiers must stay bounded and cannot carry control characters into owner routes.
    private static bool IsMetadataValue(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    // Selection hashes are SHA-256 hex values, unlike provider commit IDs which allow SHA-1 too.
    private static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static CoordinationException InvalidMergeInput() =>
        new("source_control_merge_request_invalid", StatusCodes.Status400BadRequest);

    private static CoordinationException InvalidOwnerResult() =>
        new("source_control_merge_response_invalid", StatusCodes.Status502BadGateway);

    private static CoordinationException UncertainOwnerResult() =>
        new("source_control_merge_outcome_uncertain", StatusCodes.Status502BadGateway);
}
