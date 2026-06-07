using FluentAssertions;
using Scaffolder.AgentRuntime.Safety;
using Scaffolder.Domain;

namespace Scaffolder.Tests.Security;

/// <summary>
/// Verifies FR-025: content-safety checks are applied before model-generated
/// content reaches any client. Tests use the real ContentSafetyChecker; no mocks.
/// </summary>
public sealed class ContentSafetyCheckerTests
{
    private readonly ContentSafetyChecker _checker = new();

    [Fact]
    public void NormalText_PassesSafetyCheck()
    {
        var (safe, reason) = _checker.Check("This is a normal agent response.", ModelSource.GitHubCopilot);

        safe.Should().BeTrue(because: "ordinary text must pass the safety check");
        reason.Should().BeNull();
    }

    [Fact]
    public void TextWithNullByte_FailsSafetyCheck()
    {
        var (safe, reason) = _checker.Check("data\0injected", ModelSource.GitHubCopilot);

        safe.Should().BeFalse(because: "null bytes are a control-character injection risk");
        reason.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("ghp_abc123")]
    [InlineData("sk-abc123")]
    [InlineData("github_pat_abc")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    public void TextWithCredentialPattern_FailsSafetyCheck(string content)
    {
        var (safe, reason) = _checker.Check(content, ModelSource.GitHubCopilot);

        safe.Should().BeFalse(
            because: $"content matching credential pattern '{content}' must be blocked (FR-025, FR-026)");
        reason.Should().NotBeNullOrWhiteSpace();
    }
}
