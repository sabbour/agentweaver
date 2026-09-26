using System.Text.Json;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;
using FluentAssertions;

namespace Agentweaver.Tests.Sandbox;

public sealed class EffectivePermissionInspectionProjectorTests
{
    [Fact]
    public void Project_distinguishes_configured_effective_revoked_and_denied_permissions()
    {
        const string runId = "00000000-0000-0000-0000-000000001397";
        var launch = EffectivePermissionBinding.Create(
            runId,
            attempt: 1,
            source: "current-project-sandbox-policy",
            scope: "project:project-1",
            new SandboxPolicy
            {
                RepositoryPath = @"C:\repo",
                AllowedOperations =
                [
                    EffectivePermissionOperations.WorkspaceRead,
                    EffectivePermissionOperations.WorkspaceWrite,
                    EffectivePermissionOperations.NetworkAccess,
                ],
            });
        var configured = launch.Policy with
        {
            NetworkEnabled = false,
            AllowedOperations =
            [
                EffectivePermissionOperations.WorkspaceRead,
                EffectivePermissionOperations.WorkspaceWrite,
            ],
        };
        var current = EffectivePermissionBinding.Create(
            runId,
            attempt: 1,
            source: "current-project-sandbox-policy",
            scope: launch.Scope,
            configured);
        var effective = EffectivePermissionBinding.Intersect(current, launch);
        var events = new[]
        {
            new RunEvent(1, EventTypes.PermissionBindingBound, launch),
            new RunEvent(
                2,
                EventTypes.RunDegraded,
                new
                {
                    toolName = "write_file",
                    reason = $"Operation denied by effective permission binding {effective.BindingId} " +
                             $"({effective.Version}, source={effective.Source}): " +
                             $"'{EffectivePermissionOperations.WorkspaceWrite}' is not allowed.",
                    permissionBindingId = effective.BindingId,
                    permissionBindingVersion = effective.Version,
                    permissionSource = effective.Source,
                    permissionAttempt = effective.Attempt,
                    arguments = new { api_key = "must-not-leak", command = "must-not-leak" },
                },
                DateTimeOffset.Parse("2026-01-02T03:04:05Z")),
        };

        var inspection = EffectivePermissionInspectionProjector.Project(
            runId,
            configured,
            effective,
            events);

        inspection.ConfiguredPolicy.AllowedOperations.Should().Contain(EffectivePermissionOperations.WorkspaceWrite);
        inspection.EffectivePolicy.AllowedOperations.Should().Contain(EffectivePermissionOperations.WorkspaceWrite);
        inspection.CurrentRevocation.Active.Should().BeTrue();
        inspection.CurrentRevocation.RemovedSinceLaunch.Should().Contain(EffectivePermissionOperations.NetworkAccess);
        inspection.LatestDenial.Should().NotBeNull();
        inspection.LatestDenial!.ReasonCode.Should().Be("operation_not_allowed");
        inspection.LatestDenial.Operation.Should().Be(EffectivePermissionOperations.WorkspaceWrite);
        inspection.LatestDenial.ToolName.Should().Be("write_file");
        inspection.Binding.LaunchBindingId.Should().Be(launch.BindingId);

        var json = JsonSerializer.Serialize(inspection);
        json.Should().NotContain("must-not-leak");
        json.Should().NotContain("api_key");
        json.Should().NotContain("arguments");
    }

    [Fact]
    public void Project_reports_parent_narrowing_and_complete_operation_coverage()
    {
        const string runId = "00000000-0000-0000-0000-000000001398";
        var configuredPolicy = new SandboxPolicy { RepositoryPath = @"C:\repo" };
        var parent = EffectivePermissionBinding.Create(
            runId,
            attempt: 1,
            source: "parent",
            scope: "project:project-1",
            configuredPolicy with
            {
                AllowedOperations = [EffectivePermissionOperations.WorkspaceRead],
            });
        var effective = EffectivePermissionBinding.Create(
            runId,
            attempt: 1,
            source: "current-project-sandbox-policy",
            scope: "project:project-1",
            configuredPolicy,
            parent);

        var inspection = EffectivePermissionInspectionProjector.Project(
            runId,
            configuredPolicy,
            effective,
            [new RunEvent(1, EventTypes.PermissionBindingBound, effective)]);

        inspection.Overrides.IsNarrowed.Should().BeTrue();
        inspection.Overrides.ParentRestrictionActive.Should().BeTrue();
        inspection.Overrides.RemovedOperations.Should().Contain(EffectivePermissionOperations.ShellExecute);
        inspection.Coverage.Select(item => item.Operation)
            .Should().BeEquivalentTo(EffectivePermissionOperations.Known);
        inspection.Coverage.Single(item =>
                item.Operation == EffectivePermissionOperations.WorkspaceRead)
            .Allowed.Should().BeTrue();
        inspection.Coverage.Single(item =>
                item.Operation == EffectivePermissionOperations.WorkspaceWrite)
            .Allowed.Should().BeFalse();
    }

    [Fact]
    public void Project_ignores_denials_from_an_earlier_run_attempt()
    {
        const string runId = "00000000-0000-0000-0000-000000001399";
        var policy = new SandboxPolicy { RepositoryPath = @"C:\repo" };
        var oldBinding = EffectivePermissionBinding.Create(
            runId,
            attempt: 1,
            source: "current-project-sandbox-policy",
            scope: "project:project-1",
            policy);
        var currentBinding = EffectivePermissionBinding.Create(
            runId,
            attempt: 2,
            source: "current-project-sandbox-policy",
            scope: "project:project-1",
            policy);
        var events = new[]
        {
            new RunEvent(1, EventTypes.PermissionBindingBound, oldBinding),
            new RunEvent(3, EventTypes.PermissionBindingBound, currentBinding),
            new RunEvent(4, EventTypes.RunDegraded, new
            {
                toolName = "write_file",
                reason = $"Operation denied by effective permission binding {oldBinding.BindingId} " +
                         $"({oldBinding.Version}, source={oldBinding.Source}): " +
                         $"'{EffectivePermissionOperations.WorkspaceWrite}' is not allowed.",
                permissionBindingId = oldBinding.BindingId,
                permissionBindingVersion = oldBinding.Version,
                permissionSource = oldBinding.Source,
            }),
        };

        var inspection = EffectivePermissionInspectionProjector.Project(
            runId,
            policy,
            currentBinding,
            events);

        inspection.LatestDenial.Should().BeNull();
        inspection.Binding.LaunchBindingId.Should().Be(currentBinding.BindingId);
    }

    [Fact]
    public void Project_reports_launch_ceiling_when_it_blocks_a_later_widening()
    {
        const string runId = "00000000-0000-0000-0000-000000001400";
        var configured = new SandboxPolicy { RepositoryPath = @"C:\repo" };
        var launch = EffectivePermissionBinding.Create(
            runId,
            attempt: 1,
            source: "current-project-sandbox-policy",
            scope: "project:project-1",
            configured with
            {
                AllowedOperations = [EffectivePermissionOperations.WorkspaceRead],
            });
        var widened = EffectivePermissionBinding.Create(
            runId,
            attempt: 1,
            source: "current-project-sandbox-policy",
            scope: "project:project-1",
            configured);
        var effective = EffectivePermissionBinding.Intersect(widened, launch);

        var inspection = EffectivePermissionInspectionProjector.Project(
            runId,
            configured,
            effective,
            [new RunEvent(1, EventTypes.PermissionBindingBound, launch)]);

        inspection.Overrides.LaunchCeilingActive.Should().BeTrue();
        inspection.Overrides.ParentRestrictionActive.Should().BeFalse();
        inspection.Binding.ParentBindingId.Should().BeNull();
        inspection.EffectivePolicy.AllowedOperations.Should().Equal(
            EffectivePermissionOperations.WorkspaceRead);
    }
}
