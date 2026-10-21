using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class IssueSyncTests
{
    [Theory]
    [InlineData(30, 1, 5)]
    [InlineData(86400, 100, 120)]
    public void SettingsAcceptExactBounds(int interval, int items, int seconds) =>
        new IssueSyncSettings(true, Guid.NewGuid(), interval, items, seconds).Validate();

    [Theory]
    [InlineData(29, 1, 5)]
    [InlineData(86401, 1, 5)]
    [InlineData(30, 0, 5)]
    [InlineData(30, 101, 5)]
    [InlineData(30, 1, 4)]
    [InlineData(120, 1, 121)]
    [InlineData(30, 1, 31)]
    public void SettingsRejectValuesOutsideBounds(int interval, int items, int seconds) =>
        Assert.Throws<ArgumentException>(() => new IssueSyncSettings(true, Guid.NewGuid(), interval, items, seconds).Validate());

    [Fact]
    public void MergeReconcilesEachFieldWithDeterministicLocalWinOnlyOnChangedFields()
    {
        var baseline = new SourceControlIssueContent(1, "old", "old body", false);
        Assert.Equal(new(1, "local title", "remote body", true), IssueSyncEngine.Merge(
            baseline with { Title = "local title" },
            baseline with { Title = "remote title", Body = "remote body", Closed = true }, baseline));
        Assert.Equal(baseline, IssueSyncEngine.Merge(baseline, baseline, baseline));
    }
}
