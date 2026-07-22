using FluentAssertions;
using Agentweaver.Api.Coordinator;

namespace Agentweaver.Tests.Coordinator;

public sealed class AssemblyGateCodeClassifierTests
{
    [Theory]
    [InlineData("""{"produces_code": true}""", true)]
    [InlineData("""{"produces_code": false}""", false)]
    [InlineData("```json\n{\"produces_code\": false}\n```", false)]
    public void ParseResult_ReadsStructuredBoolean(string response, bool expected)
    {
        CopilotAssemblyGateCodeClassifier.ParseResult(response).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""{"produces_code": "maybe"}""")]
    [InlineData("""{"different": true}""")]
    public void ParseResult_AmbiguousResponseReturnsNull(string? response)
    {
        CopilotAssemblyGateCodeClassifier.ParseResult(response).Should().BeNull();
    }

    [Fact]
    public async Task RunWithTimeoutAsync_TimeoutReturnsNull()
    {
        var result = await CopilotAssemblyGateCodeClassifier.RunWithTimeoutAsync(
            async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return """{"produces_code": false}""";
            },
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        result.Should().BeNull();
    }
}
