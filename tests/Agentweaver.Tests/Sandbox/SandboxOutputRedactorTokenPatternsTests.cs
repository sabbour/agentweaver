using FluentAssertions;
using Agentweaver.SandboxExec;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// Coverage for the credential/token patterns <see cref="SandboxOutputRedactor"/> scrubs, added in
/// support of GitHub issue #528 (AgentHost tool-call failure telemetry must never leak secrets to
/// Application Insights). These are on top of the pre-existing <c>ghp_</c>/<c>gho_</c>/AWS/PEM/etc.
/// patterns already covered elsewhere.
/// </summary>
public sealed class SandboxOutputRedactorTokenPatternsTests
{
    [Theory]
    [InlineData("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    [InlineData("gho_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    [InlineData("ghu_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    [InlineData("ghr_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    public void Redact_ScrubsAllGitHubTokenPrefixes(string token)
    {
        var redacted = SandboxOutputRedactor.Default.Redact($"response body echoed token={token} back");

        redacted.Should().NotContain(token);
        redacted.Should().Contain("[REDACTED]");
    }

    [Fact]
    public void Redact_ScrubsJwtShapedStrings()
    {
        const string jwt =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";

        var redacted = SandboxOutputRedactor.Default.Redact($"Authorization: Bearer {jwt}");

        redacted.Should().NotContain(jwt);
    }

    [Fact]
    public void Redact_ScrubsAuthorizationHeaderStyleValues()
    {
        var redacted = SandboxOutputRedactor.Default.Redact(
            "Authorization: Bearer some-opaque-token-value-here\nX-Api-Key: my-secret-key-value");

        redacted.Should().NotContain("some-opaque-token-value-here");
        redacted.Should().NotContain("my-secret-key-value");
    }
}
