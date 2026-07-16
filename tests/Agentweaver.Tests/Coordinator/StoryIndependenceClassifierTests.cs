using System.Diagnostics;
using FluentAssertions;
using Agentweaver.Api.Coordinator;

namespace Agentweaver.Tests.Coordinator;

public sealed class StoryIndependenceClassifierTests
{
    [Fact]
    public void ParseResult_ReadsStructuredJson()
    {
        var result = CopilotStoryIndependenceClassifier.ParseResult(
            """{"is_independent_deliverable":true,"independence_rationale":"Standalone pipeline service."}""");

        result.Should().NotBeNull();
        result!.IsIndependentDeliverable.Should().BeTrue();
        result.IndependenceRationale.Should().Be("Standalone pipeline service.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"is_independent_deliverable\":\"yes\",\"independence_rationale\":\"bad\"}")]
    [InlineData("{\"is_independent_deliverable\":true}")]
    [InlineData("{\"independence_rationale\":\"missing bool\"}")]
    public void ParseResult_ReturnsNull_WhenContractIsInvalid(string? response)
    {
        CopilotStoryIndependenceClassifier.ParseResult(response).Should().BeNull();
    }

    [Fact]
    public void BuildPrompt_IncludesComponentAndRemainder()
    {
        var prompt = CopilotStoryIndependenceClassifier.BuildPrompt(new StoryIndependenceClassificationContext(
            RunId: "run-1",
            ProjectId: "proj-1",
            SubmittingUser: "alice",
            DesiredOutcome: "Create an online shopping experience",
            Scope: "Storefront plus services",
            Assumptions: null,
            ComponentStories:
            [
                new StoryComponentInput("storefront", "Create storefront", "Build the storefront", []),
            ],
            OtherStories:
            [
                new StoryComponentInput("pipeline-service", "Create pipeline service", "Build ingestion service", []),
            ]));

        prompt.Should().Contain("TARGET COMPONENT:")
            .And.Contain("REMAINDER OF DECOMPOSITION:")
            .And.Contain("storefront")
            .And.Contain("pipeline-service")
            .And.Contain("\"is_independent_deliverable\"");
    }

    [Fact]
    public async Task RunWithTimeoutAsync_StalledModelTurn_FailsClosed()
    {
        static async Task<string?> Stalled(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return null;
        }

        var timedOut = false;
        var stopwatch = Stopwatch.StartNew();
        var result = await CopilotStoryIndependenceClassifier.RunWithTimeoutAsync(
            Stalled,
            TimeSpan.FromMilliseconds(200),
            CancellationToken.None,
            onTimeout: () => timedOut = true);
        stopwatch.Stop();

        result.Should().BeNull();
        timedOut.Should().BeTrue();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }
}
