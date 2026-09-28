using System.Text;
using Agentweaver.Api.Execution;
using Agentweaver.Domain;
using FluentAssertions;

namespace Agentweaver.Tests.Execution;

public sealed class ExecutionManifestInventoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");

    [Fact]
    public void Valid_pins_and_retained_output_bind_existing_references_without_claiming_mutable_inputs()
    {
        var yaml = "id: workflow\n";
        var digest = "sha256:" + RunOutputRevision.Sha256(Encoding.UTF8.GetBytes(yaml));
        var policy = new RunApprovalPolicySnapshot(new RunApprovalPolicy(false, false), "direct", Now);
        var run = NewRun() with
        {
            ExecutableWorkflowPinRequired = true,
            ExecutableWorkflowManifestSchemaVersion = ExecutableWorkflowPin.CurrentSchemaVersion,
            ExecutableWorkflowDefinitionId = "workflow",
            ExecutableWorkflowSource = "library",
            ExecutableWorkflowContentDigest = digest,
            ExecutableWorkflowDefinitionYaml = yaml,
            ExecutableWorkflowPinnedAt = Now,
            AgentCharter = "Private charter",
            CurrentOutputRevisionId = "revision-1",
            TreeHash = "tree-1",
        };
        run = run.WithApprovalPolicySnapshot(policy);
        var tree = RunOutputTree.Encode([new RunOutputTree.File("file.txt", 33188, "hello"u8.ToArray())]);
        var diff = RunOutputRevision.EncodeDiff("diff");
        var revision = new RunOutputRevision("revision-1", RunOutputRevision.CurrentSchemaVersion,
            run.Id, 1, digest, false, "tree-1", RunOutputRevision.Sha256(diff),
            null, diff, Now, treeContent: tree, treeContentSha256: RunOutputRevision.Sha256(tree));

        var inventory = ExecutionManifestInventory.Create(run, ExecutionIdentityDescriptor.Create(run), revision,
            launchPermissionBinding: new ExecutionPermissionBindingSummary("binding-1", "sha256:binding", "launch", 1));

        inventory.SchemaVersion.Should().Be(1);
        inventory.Compatibility.Should().Be("partial");
        inventory.Inputs.Should().Contain(x => x.Name == "executable_workflow" && x.Binding == "bound"
            && x.Digest == digest && x.Reference == $"run:{run.Id}:executable_workflow_pin");
        inventory.Inputs.Should().Contain(x => x.Name == "effective_graph" && x.Binding == "bound");
        inventory.Inputs.Should().Contain(x => x.Name == "agent_charter" && x.Binding == "bound"
            && x.Digest == "sha256:" + RunOutputRevision.Sha256(Encoding.UTF8.GetBytes(run.AgentCharter!)));
        inventory.Inputs.Should().Contain(x => x.Name == "approval_policy" && x.Binding == "bound");
        inventory.Inputs.Should().Contain(x => x.Name == "launch_permission_binding" && x.Binding == "bound");
        inventory.Inputs.Should().Contain(x => x.Name == "review_diff" && x.Binding == "bound");
        inventory.Inputs.Should().Contain(x => x.Name == "produced_output" && x.Binding == "bound"
            && x.Digest == RunOutputRevision.Sha256(tree));
        foreach (var name in new[] { "blueprint", "team", "skills", "resources", "capability_policy", "source_revision", "knowledge" })
            inventory.Inputs.Should().Contain(x => x.Name == name && x.Binding == "current_state");
        System.Text.Json.JsonSerializer.Serialize(inventory).Should().NotContain("Private charter");
    }

    [Fact]
    public void Legacy_or_incomplete_manifest_is_typed_unavailable_not_a_success_shaped_fallback()
    {
        var run = NewRun() with { CurrentOutputRevisionId = "missing" };

        var inventory = ExecutionManifestInventory.Create(run, null);

        inventory.Compatibility.Should().Be("unavailable");
        inventory.Inputs.Should().Contain(x => x.Name == "executable_workflow"
            && x.Binding == "unavailable" && x.Reason == "missing_workflow_pin");
        inventory.Inputs.Should().Contain(x => x.Name == "produced_output"
            && x.Binding == "unavailable" && x.Reason == "incomplete_or_mismatched_output_manifest");
    }

    [Fact]
    public void Backlog_execution_input_reports_exact_source_and_composite_bindings()
    {
        var run = NewRun() with
        {
            ExecutionInputRequired = true,
            ExecutionInputSourceCommitHash = "source-commit",
            ExecutionInputCommitHash = "materialized-commit",
            ExecutionInputCompositeId = "sha256:composite",
        };

        var inventory = ExecutionManifestInventory.Create(run, null);

        inventory.Inputs.Should().Contain(x => x.Name == "source_revision"
            && x.Binding == "bound" && x.Reference == "git:commit:source-commit");
        inventory.Inputs.Should().Contain(x => x.Name == "prerequisite_outputs"
            && x.Binding == "bound" && x.Reference == "git:commit:materialized-commit"
            && x.Digest == "sha256:composite");
    }

    [Fact]
    public void Required_but_unbound_execution_input_is_unavailable()
    {
        var run = NewRun() with { ExecutionInputRequired = true };

        var inventory = ExecutionManifestInventory.Create(run, null);

        inventory.Compatibility.Should().Be("unavailable");
        inventory.Inputs.Should().Contain(x => x.Name == "source_revision"
            && x.Binding == "unavailable" && x.Reason == "execution_input_unbound");
        inventory.Inputs.Should().Contain(x => x.Name == "prerequisite_outputs"
            && x.Binding == "unavailable" && x.Reason == "execution_input_unbound");
    }

    [Fact]
    public void Retained_no_change_and_collective_candidate_schemas_are_reported_as_bound()
    {
        var yaml = "id: workflow\n";
        var digest = "sha256:" + RunOutputRevision.Sha256(Encoding.UTF8.GetBytes(yaml));
        var tree = RunOutputTree.Encode([new RunOutputTree.File("file.txt", 33188, "hello"u8.ToArray())]);
        var baseRun = NewRun() with
        {
            ExecutableWorkflowPinRequired = true,
            ExecutableWorkflowManifestSchemaVersion = ExecutableWorkflowPin.CurrentSchemaVersion,
            ExecutableWorkflowDefinitionId = "workflow",
            ExecutableWorkflowSource = "library",
            ExecutableWorkflowContentDigest = digest,
            ExecutableWorkflowDefinitionYaml = yaml,
            ExecutableWorkflowPinnedAt = Now,
            TreeHash = "tree",
        };
        var noChange = new RunOutputRevision(
            "no-change", RunOutputRevision.NoChangeSchemaVersion, baseRun.Id, 1,
            digest, false, "tree", RunOutputRevision.Sha256([]), null, [], Now,
            outputKind: "no_change", mergedCommitHash: "commit", acceptedNoChange: true,
            treeContent: tree, treeContentSha256: RunOutputRevision.Sha256(tree));
        var candidateDiff = RunOutputRevision.EncodeDiff("diff --git a/file.txt b/file.txt\n");
        var candidate = new RunOutputRevision(
            "candidate", RunOutputRevision.CollectiveCandidateSchemaVersion, baseRun.Id, 1,
            digest, false, "tree", RunOutputRevision.Sha256(candidateDiff), null, candidateDiff, Now,
            outputKind: "collective", workPlanId: "7",
            treeContent: tree, treeContentSha256: RunOutputRevision.Sha256(tree));

        foreach (var revision in new[] { noChange, candidate })
        {
            var run = baseRun with { CurrentOutputRevisionId = revision.RevisionId };
            var inventory = ExecutionManifestInventory.Create(
                run, ExecutionIdentityDescriptor.Create(run), revision);
            inventory.Inputs.Should().Contain(x => x.Name == "produced_output"
                && x.Binding == "bound" && x.Digest == RunOutputRevision.Sha256(tree));
        }
    }

    [Fact]
    public void Unsupported_or_tampered_workflow_schema_cannot_be_reported_as_bound()
    {
        var run = NewRun() with
        {
            ExecutableWorkflowManifestSchemaVersion = 99,
            ExecutableWorkflowDefinitionId = "workflow",
            ExecutableWorkflowSource = "library",
            ExecutableWorkflowDefinitionYaml = "changed",
            ExecutableWorkflowContentDigest = "sha256:old",
            ExecutableWorkflowPinnedAt = Now,
        };

        var inventory = ExecutionManifestInventory.Create(run, ExecutionIdentityDescriptor.Create(run));

        inventory.Compatibility.Should().Be("unavailable");
        inventory.Inputs.Should().Contain(x => x.Name == "executable_workflow"
            && x.Binding == "unavailable" && x.Reason == "invalid_or_unsupported_workflow_pin");
        inventory.Inputs.Should().Contain(x => x.Name == "effective_graph" && x.Binding == "unavailable");
    }

    [Fact]
    public void Diff_only_output_and_missing_launch_evidence_cannot_pass_as_complete_content()
    {
        var run = NewRun() with { CurrentOutputRevisionId = "old-revision", TreeHash = "tree-1" };
        var diff = RunOutputRevision.EncodeDiff("diff");
        var revision = new RunOutputRevision("old-revision", RunOutputRevision.CurrentSchemaVersion,
            run.Id, 1, null, true, "tree-1", RunOutputRevision.Sha256(diff),
            null, diff, Now);

        var inventory = ExecutionManifestInventory.Create(run, ExecutionIdentityDescriptor.Create(run), revision);

        inventory.Compatibility.Should().Be("unavailable");
        inventory.Inputs.Should().Contain(x => x.Name == "produced_output"
            && x.Binding == "unavailable" && x.Reason == "incomplete_or_mismatched_output_manifest");
        inventory.Inputs.Should().Contain(x => x.Name == "approval_policy"
            && x.Binding == "unavailable" && x.Reason == "missing_launch_policy_snapshot");
        inventory.Inputs.Should().Contain(x => x.Name == "launch_permission_binding"
            && x.Binding == "unavailable" && x.Reason == "missing_attempt_permission_binding");
    }

    private static Run NewRun() => new()
    {
        Id = RunId.Parse("00000000-0000-0000-0000-000000001396"),
        RepositoryPath = @"C:\repo",
        OriginatingBranch = "dev",
        ModelSource = ModelSource.GitHubCopilot,
        Task = "test",
        SubmittingUser = "user",
        Status = RunStatus.InProgress,
        StartedAt = Now,
    };
}
