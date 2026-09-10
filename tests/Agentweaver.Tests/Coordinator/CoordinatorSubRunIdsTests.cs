using Agentweaver.Api.Sandbox;
using FluentAssertions;

namespace Agentweaver.Tests.Coordinator;

public sealed class CoordinatorSubRunIdsTests
{
    [Theory]
    [InlineData("-coordinator-draft")]
    [InlineData("-coordinator-decompose")]
    [InlineData("-coordinator-orchestrate")]
    public void StripSyntheticSuffix_ReturnsOwningRunId(string suffix)
    {
        const string runId = "e14667fe-25cf-4476-8da4-e3b44d9ed289";

        CoordinatorSubRunIds.StripSyntheticSuffix(runId + suffix).Should().Be(runId);
    }
}
