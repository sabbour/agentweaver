using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Agentweaver.Domain;

namespace Agentweaver.Api.Execution;

public sealed record ExecutionManifestInput(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("binding")] string Binding,
    [property: JsonPropertyName("reference")] string? Reference,
    [property: JsonPropertyName("digest")] string? Digest,
    [property: JsonPropertyName("reason")] string? Reason = null);

public sealed record ExecutionManifestInventory(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("compatibility")] string Compatibility,
    [property: JsonPropertyName("inputs")] IReadOnlyList<ExecutionManifestInput> Inputs)
{
    public const int CurrentSchemaVersion = 1;

    public static ExecutionManifestInventory Create(
        Run run, ExecutionIdentityDescriptor? descriptor,
        RunOutputRevision? output = null, string? outputError = null,
        ExecutionPermissionBindingSummary? launchPermissionBinding = null)
    {
        var inputs = new List<ExecutionManifestInput>();
        var incompatible = descriptor is null
            || descriptor.SchemaVersion != ExecutionIdentityDescriptor.CurrentSchemaVersion
            || descriptor.RunId != run.Id.ToString()
            || descriptor.Attempt != run.LifecycleGeneration;

        void Add(string name, string binding, string? reference = null, string? digest = null, string? reason = null) =>
            inputs.Add(new(name, binding, reference, digest, reason));

        if (incompatible)
            Add("execution_descriptor", "unavailable", reason: "missing_or_incompatible_attempt_descriptor");
        else
            Add("execution_descriptor", "bound", $"execution_identity:{descriptor!.DescriptorId}");

        var workflow = run.GetExecutableWorkflowPin();
        var validWorkflow = workflow is not null
            && workflow.ManifestSchemaVersion == ExecutableWorkflowPin.CurrentSchemaVersion
            && string.Equals(
                workflow.ContentDigest,
                "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workflow.DefinitionYaml))).ToLowerInvariant(),
                StringComparison.Ordinal);
        if (validWorkflow)
            Add("executable_workflow", "bound", $"run:{run.Id}:executable_workflow_pin", workflow!.ContentDigest);
        else
        {
            Add("executable_workflow", "unavailable", reason: workflow is null
                ? "missing_workflow_pin" : "invalid_or_unsupported_workflow_pin");
            incompatible = true;
        }
        if (validWorkflow && descriptor is not null
            && !string.Equals(descriptor.ExecutableWorkflowContentDigest, workflow!.ContentDigest, StringComparison.Ordinal))
        {
            incompatible = true;
            Add("descriptor_workflow_binding", "unavailable", reason: "descriptor_workflow_digest_mismatch");
        }

        if (validWorkflow)
            Add("effective_graph", "bound", $"run:{run.Id}:executable_workflow_pin", workflow!.ContentDigest);
        else
            Add("effective_graph", "unavailable", reason: "missing_executable_graph_pin");
        Add("blueprint", "current_state", reason: "no_consumed_blueprint_revision");
        Add("team", "current_state", reason: "no_consumed_team_revision");
        if (!string.IsNullOrWhiteSpace(run.AgentCharter))
            Add("agent_charter", "bound", $"run:{run.Id}:agent_charter",
                "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(run.AgentCharter))).ToLowerInvariant());
        else
            Add("agent_charter", "current_state", reason: "no_persisted_charter");
        Add("skills", "current_state", reason: "no_consumed_skill_revision");
        Add("resources", "current_state", reason: "no_consumed_resource_revision");
        var approval = run.GetApprovalPolicySnapshot();
        if (approval is not null && !string.IsNullOrWhiteSpace(run.ApprovalPolicySnapshotId)
            && string.Equals(
                new RunApprovalPolicySnapshot(approval.Policy, approval.Source, approval.CapturedAt,
                    approval.SettingsUpdatedAt, approval.InheritedFromRunId).SnapshotId,
                run.ApprovalPolicySnapshotId, StringComparison.Ordinal))
            Add("approval_policy", "bound", $"run:{run.Id}:approval_policy_snapshot:{run.ApprovalPolicySnapshotId}");
        else
        {
            Add("approval_policy", "unavailable", reason: "missing_launch_policy_snapshot");
            incompatible = true;
        }
        if (launchPermissionBinding is not null && launchPermissionBinding.Attempt == run.LifecycleGeneration)
            Add("launch_permission_binding", "bound",
                $"run:{run.Id}:permission_binding:{launchPermissionBinding.BindingId}",
                launchPermissionBinding.Version);
        else
        {
            Add("launch_permission_binding", "unavailable", reason: "missing_attempt_permission_binding");
            incompatible = true;
        }
        Add("capability_policy", "current_state", reason: "current_authorization_and_revocation_apply");
        if (run.ExecutionInputRequired)
        {
            if (!string.IsNullOrWhiteSpace(run.ExecutionInputSourceCommitHash)
                && !string.IsNullOrWhiteSpace(run.ExecutionInputCommitHash)
                && !string.IsNullOrWhiteSpace(run.ExecutionInputCompositeId))
            {
                Add("source_revision", "bound", $"git:commit:{run.ExecutionInputSourceCommitHash}");
                Add("prerequisite_outputs", "bound",
                    $"git:commit:{run.ExecutionInputCommitHash}",
                    run.ExecutionInputCompositeId);
            }
            else
            {
                Add("source_revision", "unavailable", reason: "execution_input_unbound");
                Add("prerequisite_outputs", "unavailable", reason: "execution_input_unbound");
                incompatible = true;
            }
        }
        else
        {
            Add("source_revision", "current_state", reason: "consumed_base_commit_not_persisted");
            Add("prerequisite_outputs", "current_state", reason: "no_claimed_prerequisite_snapshot");
        }
        if (!string.IsNullOrWhiteSpace(run.MergedCommitHash))
            Add("published_commit", "bound", $"git:commit:{run.MergedCommitHash}");
        Add("knowledge", "current_state", reason: "no_consumed_knowledge_revision");

        if (output is not null && output.SchemaVersion is
                RunOutputRevision.CurrentSchemaVersion
                or RunOutputRevision.CollectiveSchemaVersion
                or RunOutputRevision.NoChangeSchemaVersion
                or RunOutputRevision.CollectiveCandidateSchemaVersion
            && !output.ManifestIncomplete
            && output.RunId == run.Id
            && output.LifecycleGeneration == run.LifecycleGeneration
            && string.Equals(output.RevisionId, run.CurrentOutputRevisionId, StringComparison.Ordinal)
            && string.Equals(output.TreeHash, run.TreeHash, StringComparison.Ordinal)
            && validWorkflow
            && string.Equals(output.WorkflowDigest, workflow!.ContentDigest, StringComparison.Ordinal))
        {
            Add("review_diff", "bound", $"run:{run.Id}:output_revision:{output.RevisionId}", output.DiffSha256);
            if (output.TreeContentSha256 is not null && output.TreeContent is not null)
                Add("produced_output", "bound", $"run:{run.Id}:output_revision:{output.RevisionId}",
                    output.TreeContentSha256);
            else
            {
                Add("produced_output", "unavailable", reason: "missing_full_file_content");
                incompatible = true;
            }
        }
        else
        {
            Add("produced_output", "unavailable", reason: outputError ??
                (run.CurrentOutputRevisionId is null ? "no_output_revision" : "incomplete_or_mismatched_output_manifest"));
            if (run.CurrentOutputRevisionId is not null)
                incompatible = true;
        }

        return new(CurrentSchemaVersion, incompatible ? "unavailable" : "partial", inputs);
    }
}
