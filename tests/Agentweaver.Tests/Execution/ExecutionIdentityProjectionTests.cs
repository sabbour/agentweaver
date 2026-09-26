using System.Text.Json;
using Agentweaver.Api.Execution;
using Agentweaver.Domain;
using FluentAssertions;

namespace Agentweaver.Tests.Execution;

public sealed class ExecutionIdentityProjectionTests
{
    [Fact]
    public void Create_builds_distinct_root_child_and_retry_lineage()
    {
        var root = CreateRun("00000000-0000-0000-0000-000000001404", "Coordinator");
        var rootDescriptor = ExecutionIdentityDescriptor.Create(root);
        var child = CreateRun(
            "00000000-0000-0000-0000-000000001405",
            "Tank",
            parentRunId: root.Id.ToString(),
            subtaskId: "42");
        var childDescriptor = ExecutionIdentityDescriptor.Create(child);
        var retry = CreateRun(
            "00000000-0000-0000-0000-000000001406",
            "Tank",
            retriedFrom: child.Id.ToString());
        var retryDescriptor = ExecutionIdentityDescriptor.Create(retry);

        rootDescriptor.DescriptorId.Should().NotBe(childDescriptor.DescriptorId);
        childDescriptor.ParentDescriptorId.Should().Be(rootDescriptor.DescriptorId);
        childDescriptor.InitiatingPrincipalId.Should().Be(rootDescriptor.InitiatingPrincipalId);
        childDescriptor.AgentAssignmentId.Should().NotBe(rootDescriptor.AgentAssignmentId);
        retryDescriptor.RetryOfDescriptorId.Should().Be(childDescriptor.DescriptorId);
    }

    [Fact]
    public void Create_changes_identity_for_a_new_lifecycle_attempt()
    {
        var run = CreateRun("00000000-0000-0000-0000-000000001407", "Tank");

        var first = ExecutionIdentityDescriptor.Create(run);
        var second = ExecutionIdentityDescriptor.Create(run with { LifecycleGeneration = 2 });

        second.DescriptorId.Should().NotBe(first.DescriptorId);
        second.Attempt.Should().Be(2);
    }

    [Fact]
    public void Project_emits_safe_linked_evidence_and_labels_unknowns()
    {
        var run = CreateRun("00000000-0000-0000-0000-000000001408", "Tank") with
        {
            RepositoryPath = @"C:\secret\customer-repository",
            SandboxBackend = "agenthost",
            SandboxClaimName = "claim-safe",
            SandboxPodName = "pod-safe",
            SandboxNamespace = "namespace-safe",
        };
        var descriptor = ExecutionIdentityDescriptor.Create(run);
        var binding = EffectivePermissionBinding.Create(
            run.Id.ToString(),
            run.LifecycleGeneration,
            "current-project-sandbox-policy",
            $"project:{run.ProjectId}",
            new SandboxPolicy { RepositoryPath = run.RepositoryPath });
        var events = new[]
        {
            new RunEvent(1, EventTypes.PermissionBindingBound, binding),
            new RunEvent(2, EventTypes.ToolCall, new
            {
                callId = "call-safe",
                name = "run_command",
                arguments = new { command = "do-not-leak", token = "do-not-leak" },
            }),
            new RunEvent(3, "tool.approval_resolved", new
            {
                requestId = "call-safe",
                approved = false,
                decidedBy = "private-user",
                arguments = "do-not-leak",
            }),
            new RunEvent(4, EventTypes.ToolError, new
            {
                callId = "missing-call",
                name = "web_fetch",
                error = "do-not-leak",
            }),
        };

        var projection = ExecutionIdentityProjector.Project(run, descriptor, events);

        projection.EvidenceState.Should().Be("complete");
        projection.PermissionBinding!.BindingId.Should().Be(binding.BindingId);
        projection.Backend!.Kind.Should().Be("agenthost");
        projection.Decisions.Should().ContainSingle(item =>
            item.ToolCallId == "call-safe" && item.Outcome == "denied");
        projection.Decisions.Should().ContainSingle(item =>
            item.ToolCallId == "missing-call" && item.CorrelationState == "unmatched");

        var json = JsonSerializer.Serialize(projection);
        json.Should().NotContain("do-not-leak");
        json.Should().NotContain("customer-repository");
        json.Should().NotContain("private-user");
    }

    [Fact]
    public void Project_preserves_the_binding_recorded_with_a_permission_denial()
    {
        var run = CreateRun("00000000-0000-0000-0000-000000001413", "Tank");
        var descriptor = ExecutionIdentityDescriptor.Create(run);
        var launch = EffectivePermissionBinding.Create(
            run.Id.ToString(), 1, "launch-policy", $"project:{run.ProjectId}",
            new SandboxPolicy { RepositoryPath = run.RepositoryPath });
        var denied = launch with
        {
            BindingId = "epb-denial",
            Version = "sha256:denial",
            Source = "effective-policy",
        };
        var events = new[]
        {
            new RunEvent(1, EventTypes.PermissionBindingBound, launch),
            new RunEvent(2, EventTypes.RunDegraded, new
            {
                reason = $"Operation denied: '{EffectivePermissionOperations.WorkspaceWrite}'",
                callId = "call-denied",
                permissionBindingId = denied.BindingId,
                permissionBindingVersion = denied.Version,
                permissionSource = denied.Source,
            }),
        };

        var projection = ExecutionIdentityProjector.Project(run, descriptor, events, launch);

        projection.Decisions.Should().ContainSingle();
        projection.Decisions[0].PermissionBindingId.Should().Be(denied.BindingId);
        projection.Decisions[0].PermissionBindingVersion.Should().Be(denied.Version);
        projection.Decisions[0].PermissionBindingSource.Should().Be(denied.Source);
    }

    [Fact]
    public void Project_reports_missing_legacy_descriptor_explicitly()
    {
        var run = CreateRun("00000000-0000-0000-0000-000000001409", "Tank");

        var projection = ExecutionIdentityProjector.Project(run, descriptor: null, events: []);

        projection.EvidenceState.Should().Be("missing_legacy_descriptor");
        projection.Descriptor.Should().BeNull();
        projection.PermissionBinding.Should().BeNull();
        projection.Decisions.Should().BeEmpty();
    }

    [Fact]
    public void Project_limits_decisions_to_the_descriptor_attempt_and_tolerates_reused_call_ids()
    {
        var run = CreateRun("00000000-0000-0000-0000-000000001410", "Tank") with
        {
            LifecycleGeneration = 2,
        };
        var descriptor = ExecutionIdentityDescriptor.Create(run);
        var events = new[]
        {
            new RunEvent(3, EventTypes.PermissionBindingBound, EffectivePermissionBinding.Create(
                run.Id.ToString(),
                2,
                "current-project-sandbox-policy",
                $"project:{run.ProjectId}",
                new SandboxPolicy { RepositoryPath = run.RepositoryPath })),
            new RunEvent(1, EventTypes.ToolCall, new { callId = "shared", name = "old_tool" },
                descriptor.CreatedAt.AddMinutes(-1)),
            new RunEvent(2, EventTypes.ToolError, new { callId = "shared" },
                descriptor.CreatedAt.AddSeconds(-30)),
            new RunEvent(4, EventTypes.ToolCall, new { callId = "shared", name = "new_tool" },
                descriptor.CreatedAt.AddSeconds(1)),
            new RunEvent(5, EventTypes.ToolResult, new { callId = "shared" },
                descriptor.CreatedAt.AddSeconds(2)),
        };

        var projection = ExecutionIdentityProjector.Project(run, descriptor, events);

        projection.Decisions.Should().ContainSingle();
        projection.Decisions.Single().ToolName.Should().Be("new_tool");
        projection.Decisions.Single().Outcome.Should().Be("succeeded");
    }

    [Fact]
    public void Project_uses_sequence_attempt_boundary_when_timestamps_are_out_of_order()
    {
        var run = CreateRun("00000000-0000-0000-0000-000000001411", "Tank") with
        {
            LifecycleGeneration = 2,
        };
        var descriptor = ExecutionIdentityDescriptor.Create(run);
        var binding = EffectivePermissionBinding.Create(
            run.Id.ToString(), 2, "current-project-sandbox-policy",
            $"project:{run.ProjectId}", new SandboxPolicy { RepositoryPath = run.RepositoryPath });
        var events = new[]
        {
            new RunEvent(1, EventTypes.ToolCall, new { callId = "old", name = "old_tool" },
                descriptor.CreatedAt.AddHours(1)),
            new RunEvent(2, EventTypes.PermissionBindingBound, binding,
                descriptor.CreatedAt.AddHours(-1)),
            new RunEvent(3, EventTypes.ToolCall, new { callId = "new", name = "new_tool" },
                descriptor.CreatedAt.AddHours(-2)),
            new RunEvent(4, EventTypes.ToolError, new { callId = "new" },
                descriptor.CreatedAt.AddHours(-3)),
        };

        var projection = ExecutionIdentityProjector.Project(run, descriptor, events);

        projection.Decisions.Should().ContainSingle();
        projection.Decisions[0].ToolCallId.Should().Be("new");
    }

    [Fact]
    public void Project_labels_missing_call_ids_as_synthetic()
    {
        var run = CreateRun("00000000-0000-0000-0000-000000001412", "Tank");
        var descriptor = ExecutionIdentityDescriptor.Create(run);
        var events = new[] { new RunEvent(7, EventTypes.ToolError, new { error = "hidden" }) };

        var projection = ExecutionIdentityProjector.Project(run, descriptor, events);

        projection.Decisions.Should().ContainSingle();
        projection.Decisions[0].ToolCallId.Should().Be("event-7");
        projection.Decisions[0].CorrelationState.Should().Be("synthetic");
    }

    private static Run CreateRun(
        string runId,
        string agentName,
        string? parentRunId = null,
        string? subtaskId = null,
        string? retriedFrom = null) =>
        new()
        {
            Id = RunId.Parse(runId),
            RepositoryPath = @"C:\repo",
            OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "test",
            SubmittingUser = "principal-123",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.Parse("2026-09-26T12:00:00Z"),
            ProjectId = ProjectId.Parse("00000000-0000-0000-0000-000000001400"),
            AgentName = agentName,
            ParentRunId = parentRunId,
            SubtaskId = subtaskId,
            RetriedFrom = retriedFrom,
        };
}
