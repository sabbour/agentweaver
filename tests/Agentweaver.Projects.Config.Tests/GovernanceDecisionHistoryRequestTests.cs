using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class GovernanceDecisionHistoryRequestTests
{
    [Fact]
    public void PageSizeUsesDefaultAndAcceptsInclusiveBounds()
    {
        Assert.Equal(20, ProjectGovernanceDecisionStore.ParseHistoryPageSize(null));
        Assert.Equal(1, ProjectGovernanceDecisionStore.ParseHistoryPageSize("1"));
        Assert.Equal(100, ProjectGovernanceDecisionStore.ParseHistoryPageSize("100"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("101")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 2")]
    [InlineData("x")]
    [InlineData("2147483648")]
    public void PageSizeRejectsInvalidValues(string value)
    {
        var exception = Assert.Throws<ProjectConfigException>(() =>
            ProjectGovernanceDecisionStore.ParseHistoryPageSize(value));
        Assert.Equal("governance_decision_history_request_invalid", exception.Code);
    }

    [Fact]
    public void BeforeSequenceAllowsNullAndCanonicalPositiveInt64()
    {
        Assert.Null(ProjectGovernanceDecisionStore.ParseBeforeSequence(null));
        Assert.Equal(1L, ProjectGovernanceDecisionStore.ParseBeforeSequence("1"));
        Assert.Equal(long.MaxValue, ProjectGovernanceDecisionStore.ParseBeforeSequence("9223372036854775807"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData(" 1")]
    [InlineData("9223372036854775808")]
    public void BeforeSequenceRejectsInvalidValues(string value)
    {
        var exception = Assert.Throws<ProjectConfigException>(() =>
            ProjectGovernanceDecisionStore.ParseBeforeSequence(value));
        Assert.Equal("governance_decision_history_request_invalid", exception.Code);
    }

    [Theory]
    [InlineData(null, 64)]
    [InlineData("", 64)]
    [InlineData("   ", 64)]
    [InlineData(" skill", 64)]
    [InlineData("skill ", 64)]
    public void ResourceFilterRejectsMissingOrUntrimmedValues(string? value, int maxLength)
    {
        var exception = Assert.Throws<ProjectConfigException>(() =>
            ProjectGovernanceDecisionStore.ParseRequiredResourceFilter(value, maxLength));
        Assert.Equal("governance_decision_history_request_invalid", exception.Code);
    }

    [Fact]
    public void ResourceFilterRejectsOverlongValues()
    {
        var exception = Assert.Throws<ProjectConfigException>(() =>
            ProjectGovernanceDecisionStore.ParseRequiredResourceFilter(new string('a', 65), 64));
        Assert.Equal("governance_decision_history_request_invalid", exception.Code);
    }
}
