using FluentAssertions;
using Agentweaver.AgentRuntime;
using Agentweaver.Domain;

namespace Agentweaver.Tests;

/// <summary>Unit tests for <see cref="InMemoryRunOptionsStore"/> (per-run options source of truth).</summary>
public sealed class RunOptionsStoreTests
{
    [Theory]
    [InlineData("web_fetch", true)]
    [InlineData("start_preview", false)]
    [InlineData("run_command", false)]
    [InlineData("secret_read", false)]
    public void ApprovalPolicy_AutoApprovesOnlyRepositoryDefinedSafeTools(string toolName, bool expected)
    {
        var policy = new RunApprovalPolicy(AutoApproveTools: true);

        policy.AllowsAutoApproval(toolName).Should().Be(expected);
    }

    [Fact]
    public void ApprovalPolicy_DirectDefaultsOff_WhileHeartbeatUsesProjectDefaults()
    {
        RunApprovalPolicy.ForDirectRun(null, null).Should().Be(new RunApprovalPolicy());
        RunApprovalPolicy.ForBacklogPickup(autoApproveTools: true, autopilot: true)
            .Should().Be(new RunApprovalPolicy(AutoApproveTools: true, Autopilot: true));
    }

    [Fact]
    public void Get_UnknownRun_ReturnsDefaultsBothOff()
    {
        var store = new InMemoryRunOptionsStore();
        var opts = store.Get("nope");
        opts.AutoApproveTools.Should().BeFalse();
        opts.Autopilot.Should().BeFalse();
    }

    [Fact]
    public void Set_ThenGet_RoundTripsBothFlags()
    {
        var store = new InMemoryRunOptionsStore();
        store.Set("r1", new RunOptions(AutoApproveTools: true, Autopilot: true));
        var opts = store.Get("r1");
        opts.AutoApproveTools.Should().BeTrue();
        opts.Autopilot.Should().BeTrue();
    }

    [Fact]
    public void SetAutoApproveTools_PreservesAutopilot()
    {
        var store = new InMemoryRunOptionsStore();
        store.Set("r1", new RunOptions(AutoApproveTools: false, Autopilot: true));
        store.SetAutoApproveTools("r1", true);
        var opts = store.Get("r1");
        opts.AutoApproveTools.Should().BeTrue();
        opts.Autopilot.Should().BeTrue("toggling one flag must not clear the other");
    }

    [Fact]
    public void SetAutopilot_PreservesAutoApprove()
    {
        var store = new InMemoryRunOptionsStore();
        store.Set("r1", new RunOptions(AutoApproveTools: true, Autopilot: false));
        store.SetAutopilot("r1", true);
        var opts = store.Get("r1");
        opts.Autopilot.Should().BeTrue();
        opts.AutoApproveTools.Should().BeTrue();
    }

    [Fact]
    public void SetAutopilot_UnknownRun_CreatesEntry()
    {
        var store = new InMemoryRunOptionsStore();
        store.SetAutopilot("fresh", true);
        store.Get("fresh").Autopilot.Should().BeTrue();
    }

    [Fact]
    public void Clear_RemovesRuntimeEntry_AndRetainsLaunchPolicy()
    {
        var store = new InMemoryRunOptionsStore();
        store.Set("r1", new RunOptions(AutoApproveTools: true, Autopilot: true));
        store.Clear("r1");
        var opts = store.Get("r1");
        opts.AutoApproveTools.Should().BeTrue();
        opts.Autopilot.Should().BeTrue();
    }

    [Fact]
    public void LaunchPolicy_IsImmutableAndSurvivesLiveOptionCleanup()
    {
        var store = new InMemoryRunOptionsStore();
        var sourceUpdatedAt = DateTimeOffset.Parse("2026-09-10T08:30:00Z");
        var captured = store.CaptureLaunchPolicy(
            "r1",
            new RunOptions(AutoApproveTools: true, Autopilot: true),
            17,
            "backlog_pickup",
            sourceUpdatedAt);

        store.Set("r1", captured.Options);
        store.SetAutoApproveTools("r1", false);
        store.Clear("r1");
        var replay = store.CaptureLaunchPolicy(
            "r1",
            captured.Options,
            17,
            "backlog_pickup",
            sourceUpdatedAt);

        replay.Should().Be(captured);
        store.GetLaunchPolicy("r1").Should().Be(captured);
    }

    [Fact]
    public void LaunchPolicy_RejectsConflictingRecapture()
    {
        var store = new InMemoryRunOptionsStore();
        store.CaptureLaunchPolicy("r1", new RunOptions(AutoApproveTools: true), 30, "backlog_pickup");

        var act = () => store.CaptureLaunchPolicy("r1", new RunOptions(), 30, "interactive");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*run_policy_snapshot_conflict*");
    }
}
