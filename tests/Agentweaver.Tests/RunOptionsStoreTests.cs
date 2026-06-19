using FluentAssertions;
using Agentweaver.AgentRuntime;
using Agentweaver.Domain;

namespace Agentweaver.Tests;

/// <summary>Unit tests for <see cref="InMemoryRunOptionsStore"/> (per-run options source of truth).</summary>
public sealed class RunOptionsStoreTests
{
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
    public void Clear_RemovesEntry_RevertsToDefaults()
    {
        var store = new InMemoryRunOptionsStore();
        store.Set("r1", new RunOptions(AutoApproveTools: true, Autopilot: true));
        store.Clear("r1");
        var opts = store.Get("r1");
        opts.AutoApproveTools.Should().BeFalse();
        opts.Autopilot.Should().BeFalse();
    }
}
