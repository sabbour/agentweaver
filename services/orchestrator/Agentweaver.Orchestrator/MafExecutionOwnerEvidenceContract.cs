using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.SourceControl;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal static class MafExecutionOwnerEvidenceContract
{
    internal const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static JsonElement Serialize(
        MafExecutionOwnerEvidenceBinding root,
        string tenantId,
        ImmutableArray<SourceControlOutputCaptureRecord> captures,
        ImmutableArray<SourceControlMergeIntentSnapshot> mergeIntents)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        MafExecutionOutputWitnessStore.ValidateBinding(
            root.Identity,
            root.ExecutionFence,
            root.Checkpoint,
            root.CurrentDecision.StateVersion,
            root.AcceptedSelectionHash,
            root.OutputSet);
        MafExecutionOutputWitnessStore.ValidateCompletedPlan(
            root.Identity,
            root.ExecutionFence,
            root.WorkPlan,
            root.Checkpoint,
            root.CurrentDecision.StateVersion,
            root.AcceptedSelectionHash,
            root.OutputSet);
        ValidateRootBinding(root);
        _ = MafExecutionOutputWitness.SerializeCanonicalOutputSet(root.OutputSet);

        if (captures.IsDefault || mergeIntents.IsDefault)
            throw InvalidEvidence();

        var outputSet = root.OutputSet;
        var plan = root.WorkPlan;
        var checkpoint = root.Checkpoint;
        var obligationsByPath = outputSet.Obligations
            .GroupBy(obligation => obligation.OutputPath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        if (obligationsByPath.Values.Any(matches => matches.Length != 1))
            throw InvalidEvidence();

        var mergeSteps = plan.Workflow.Definition.Steps
            .Where(step => step.Mode == WorkflowStepMode.Platform &&
                step.PlatformGate == WorkflowPlatformGate.Merge)
            .ToArray();
        var matchingMergeIntents = mergeIntents
            .Where(intent =>
                intent.WorkPlanId == plan.Plan.Id &&
                intent.WorkflowId == plan.Workflow.Definition.Id &&
                intent.DefinitionRevision == plan.Workflow.Definition.Revision)
            .ToArray();

        if (outputSet.Obligations.IsEmpty)
        {
            foreach (var emptyCapture in captures)
            {
                ValidateCapture(emptyCapture, root, tenantId);
                if (!GitWorkspaceCapturePackage.ParseManifest(emptyCapture.ManifestBytes).Files.IsEmpty)
                    throw InvalidEvidence();
            }

            if (mergeSteps.Length != 0 ||
                matchingMergeIntents.Length != 0 ||
                HasRequiredOrCompletedPlatformOperation(plan, checkpoint))
                throw InvalidEvidence();

            return JsonSerializer.SerializeToElement(
                new[]
                {
                    new MafExecutionOwnerEvidenceNoOutputRow(
                        CurrentVersion,
                        "no-output",
                        plan.Plan.Id,
                        outputSet.Digest,
                        checkpoint.State.Revision,
                        root.CurrentDecision.StateVersion)
                },
                JsonOptions);
        }

        if (captures.Length != 1 || mergeSteps.Length > 1 ||
            HasUnsupportedPlatformOperation(plan, checkpoint, mergeSteps.SingleOrDefault()))
            throw InvalidEvidence();

        var capture = captures[0];
        ValidateCapture(capture, root, tenantId);
        var manifest = GitWorkspaceCapturePackage.ParseManifest(capture.ManifestBytes);
        var seenOutputs = new HashSet<(string WorkItemId, string Path)>();
        var captureOutputs = ImmutableArray.CreateBuilder<MafExecutionOwnerEvidenceOutputRow>(
            manifest.Files.Length);
        foreach (var file in manifest.Files)
        {
            if (!WorkflowDefinitionValidator.IsValidOutputPath(file.Path) ||
                file.Sha256 is not { Length: 64 } ||
                !file.Sha256.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f') ||
                file.ByteLength < 0 ||
                !obligationsByPath.TryGetValue(file.Path, out var matches))
                throw InvalidEvidence();
            var obligation = matches[0];
            if (!seenOutputs.Add((obligation.WorkItemId, file.Path)))
                throw InvalidEvidence();
            captureOutputs.Add(new MafExecutionOwnerEvidenceOutputRow(
                obligation.WorkItemId,
                file.Path,
                file.Sha256,
                file.ByteLength,
                MergeIntentId: null));
        }

        if (seenOutputs.Count != outputSet.Obligations.Length ||
            outputSet.Obligations.Any(obligation =>
                !seenOutputs.Contains((obligation.WorkItemId, obligation.OutputPath))))
            throw InvalidEvidence();

        SourceControlMergeIntentSnapshot? selectedMerge = null;
        string? mergeIntentId = null;
        if (mergeSteps.Length == 1)
        {
            var mergeStep = mergeSteps[0];
            var intentsForStep = matchingMergeIntents
                .Where(intent => intent.WorkflowStepId == mergeStep.Id)
                .ToArray();
            if (intentsForStep.Length != 1)
                throw InvalidEvidence();
            selectedMerge = intentsForStep[0];
            ValidateMerge(selectedMerge, root, tenantId, capture.Proof);
            mergeIntentId = selectedMerge.IntentId;
        }
        else if (matchingMergeIntents.Length != 0)
        {
            throw InvalidEvidence();
        }

        var orderedOutputs = captureOutputs
            .Select(output => output with { MergeIntentId = mergeIntentId })
            .OrderBy(output => output.WorkItemId, StringComparer.Ordinal)
            .ThenBy(output => output.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        var rows = new List<JsonElement>
        {
            JsonSerializer.SerializeToElement(
                new MafExecutionOwnerEvidenceCaptureRow(
                    CurrentVersion,
                    "capture",
                    capture.Proof,
                    capture.State,
                    capture.ObjectKey!,
                    capture.EventPosition!.Value,
                    capture.AdmittedAt!.Value,
                    orderedOutputs),
                JsonOptions)
        };
        if (selectedMerge is not null)
        {
            var receipt = selectedMerge.ApprovalReceipt!;
            rows.Add(JsonSerializer.SerializeToElement(
                new MafExecutionOwnerEvidenceMergeRow(
                    CurrentVersion,
                    "merge",
                    selectedMerge.IntentId,
                    selectedMerge.AcceptedRun,
                    selectedMerge.SourceStateVersion,
                    selectedMerge.SourceDecisionId,
                    selectedMerge.SourceRequestId,
                    selectedMerge.WorkPlanId,
                    selectedMerge.WorkflowStepId,
                    selectedMerge.ApprovalRequestId,
                    selectedMerge.State,
                    selectedMerge.ApprovalDecisionId!.Value,
                    selectedMerge.ApprovalStateVersion!.Value,
                    receipt,
                    selectedMerge.MergeSha!),
                JsonOptions));
        }
        return JsonSerializer.SerializeToElement(rows, JsonOptions);
    }

    private static void ValidateRootBinding(MafExecutionOwnerEvidenceBinding root)
    {
        if (root.ExecutionFence < 1 ||
            root.CurrentDecision.StateVersion < 1 ||
            root.CurrentDecision.State.Fence != root.ExecutionFence ||
            root.CurrentDecision.SelectionHash != root.AcceptedSelectionHash ||
            root.CurrentDecision.State.DecisionReceipts.IsDefault ||
            root.Checkpoint.State.Revision < 1 ||
            root.Checkpoint.State.WorkPlanId != root.WorkPlan.Plan.Id ||
            root.Identity.ProjectId.Length is < 1 or > 256 ||
            root.Identity.RunId.Length is < 1 or > 256 ||
            root.Identity.SessionId.Length is < 1 or > 256)
            throw InvalidEvidence();
    }

    private static void ValidateCapture(
        SourceControlOutputCaptureRecord capture,
        MafExecutionOwnerEvidenceBinding root,
        string tenantId)
    {
        try
        {
            ProducedRunCaptureContractValidation.Validate(capture.Proof);
        }
        catch (ArgumentException)
        {
            throw InvalidEvidence();
        }

        var proof = capture.Proof;
        var expectedObjectKey = ProducedRunCaptureContractValidation.CreatePackageReference(proof).Key.Value;
        if (capture.State != "admitted" ||
            capture.ObjectKey != expectedObjectKey ||
            capture.EventPosition is not > 0 ||
            capture.AdmittedAt is null ||
            capture.Proof.Identity != root.Identity ||
            capture.Proof.ActorIssuer != root.Actor.Issuer ||
            capture.Proof.ActorSubject != root.Actor.Subject ||
            capture.Proof.TenantId != tenantId ||
            capture.Proof.AcceptedSelectionHash != root.AcceptedSelectionHash ||
            capture.ManifestBytes.LongLength != proof.ManifestByteLength ||
            capture.PatchBytes.LongLength != proof.PatchByteLength ||
            capture.PackageBytes.LongLength != proof.PackageByteLength ||
            GitWorkspaceCapturePackage.Hash(capture.ManifestBytes) != proof.ManifestSha256 ||
            GitWorkspaceCapturePackage.Hash(capture.PatchBytes) != proof.PatchSha256 ||
            GitWorkspaceCapturePackage.Hash(capture.PackageBytes) != proof.PackageSha256)
            throw InvalidEvidence();

        GitWorkspaceCapturedOutputManifest manifest;
        try
        {
            manifest = GitWorkspaceCapturePackage.ParseManifest(capture.ManifestBytes);
            GitWorkspaceCapturePackage.VerifyPackage(capture.PackageBytes, manifest, proof.PackageSha256);
        }
        catch (ArgumentException)
        {
            throw InvalidEvidence();
        }
        if (manifest.WorkspaceId != proof.WorkspaceId ||
            manifest.RunId != proof.Identity.RunId ||
            manifest.RepositoryId != proof.RepositoryId ||
            manifest.ResourceGeneration != proof.ResourceGeneration ||
            manifest.WorkspaceIncarnationId != proof.WorkspaceIncarnationId ||
            manifest.BranchName != proof.BranchName ||
            !string.Equals(manifest.BaseSha, proof.BaseSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.OutputTreeSha, proof.OutputTreeSha, StringComparison.OrdinalIgnoreCase))
            throw InvalidEvidence();
    }

    private static void ValidateMerge(
        SourceControlMergeIntentSnapshot intent,
        MafExecutionOwnerEvidenceBinding root,
        string tenantId,
        ProducedRunCaptureProof capture)
    {
        var acceptedRun = intent.AcceptedRun;
        var receipt = intent.ApprovalReceipt;
        var pin = intent.Pin;
        if (intent.State != "merged" ||
            intent.MergeSha is not { Length: 40 or 64 } ||
            !intent.MergeSha.All(Uri.IsHexDigit) ||
            intent.SourceStateVersion < 1 ||
            intent.SourceDecisionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(intent.SourceRequestId) ||
            intent.ApprovalDecisionId is null ||
            intent.ApprovalDecisionId == Guid.Empty ||
            intent.ApprovalStateVersion is not > 0 ||
            receipt is null ||
            intent.WorkflowId != root.WorkPlan.Workflow.Definition.Id ||
            intent.DefinitionRevision != root.WorkPlan.Workflow.Definition.Revision ||
            intent.WorkPlanId != root.WorkPlan.Plan.Id ||
            intent.AcceptedRun != pin.AcceptedRun ||
            acceptedRun.Issuer != root.Actor.Issuer ||
            acceptedRun.Subject != root.Actor.Subject ||
            acceptedRun.TenantId != tenantId ||
            acceptedRun.ProjectId != root.Identity.ProjectId ||
            acceptedRun.RunId != root.Identity.RunId ||
            acceptedRun.RootSessionId != root.Identity.SessionId ||
            acceptedRun.AcceptedSelectionHash != root.AcceptedSelectionHash ||
            acceptedRun.Fence != root.ExecutionFence ||
            pin.PinId != capture.SourceControlPinId ||
            pin.ProviderBinding.Resource.ResourceId != capture.RepositoryId ||
            pin.ProviderBinding.Resource.Generation != capture.ResourceGeneration ||
            intent.ApprovalStateVersion != root.CurrentDecision.StateVersion ||
            receipt.Kind != CoordinatorGateKind.Approval ||
            receipt.RequestId != intent.ApprovalRequestId ||
            receipt.SubjectId != intent.IntentId ||
            receipt.ActorId != acceptedRun.Subject ||
            receipt.Fence != acceptedRun.Fence ||
            receipt.ChoiceId != CoordinatorGateChoices.Approve ||
            receipt.FreeformAnswer is not null ||
            !root.CurrentDecision.State.DecisionReceipts.Contains(receipt))
            throw InvalidEvidence();
    }

    private static bool HasRequiredOrCompletedPlatformOperation(
        WorkPlanSnapshot plan,
        MafExecutionCheckpointSnapshot checkpoint) =>
        plan.Workflow.Definition.Steps.Any(step =>
            step.Mode == WorkflowStepMode.Platform &&
            (step.Cardinality.Minimum > 0 ||
             checkpoint.State.Progress.NonModelSteps.GetValueOrDefault(step.Id) != MafExecutionTaskStatus.Pending));

    private static bool HasUnsupportedPlatformOperation(
        WorkPlanSnapshot plan,
        MafExecutionCheckpointSnapshot checkpoint,
        WorkflowStepDefinition? selectedMerge) =>
        plan.Workflow.Definition.Steps.Any(step =>
            step.Mode == WorkflowStepMode.Platform &&
            step.Id != selectedMerge?.Id &&
            (step.Cardinality.Minimum > 0 ||
             checkpoint.State.Progress.NonModelSteps.GetValueOrDefault(step.Id) != MafExecutionTaskStatus.Pending));

    private static CoordinationException InvalidEvidence() =>
        new("maf_execution_output_witness_evidence_invalid", StatusCodes.Status409Conflict);
}

internal sealed record MafExecutionOwnerEvidenceCaptureRow(
    int ContractVersion,
    string Kind,
    ProducedRunCaptureProof Capture,
    string State,
    string ObjectKey,
    long EventPosition,
    DateTimeOffset AdmittedAt,
    ImmutableArray<MafExecutionOwnerEvidenceOutputRow> Outputs);

internal sealed record MafExecutionOwnerEvidenceOutputRow(
    string WorkItemId,
    string Path,
    string Sha256,
    long ByteLength,
    string? MergeIntentId);

internal sealed record MafExecutionOwnerEvidenceMergeRow(
    int ContractVersion,
    string Kind,
    string IntentId,
    SourceControlAcceptedRunBinding AcceptedRun,
    long SourceStateVersion,
    Guid SourceDecisionId,
    string SourceRequestId,
    string WorkPlanId,
    string WorkflowStepId,
    string ApprovalRequestId,
    string State,
    Guid ApprovalDecisionId,
    long ApprovalStateVersion,
    CoordinatorGateDecisionReceipt ApprovalReceipt,
    string MergeSha);

internal sealed record MafExecutionOwnerEvidenceNoOutputRow(
    int ContractVersion,
    string Kind,
    string WorkPlanId,
    string OutputSetSha256,
    long CheckpointRevision,
    long DecisionStateVersion);
