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
}
