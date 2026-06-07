using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scaffolder.Api.Agent.Governance;
using Xunit;

namespace Scaffolder.Api.Tests.Unit;

/// <summary>
/// T075: Integration test for secrets scrubbing.
/// Validates that SecretsScrubbingFilter redacts known secret patterns.
/// </summary>
public sealed class SecretsScrubIntegrationTests
{
    private readonly SecretsScrubbingFilter _filter =
        new(NullLogger<SecretsScrubbingFilter>.Instance);

    [Theory]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig")]
    [InlineData("ghp_1234567890abcdefghij")]
    [InlineData("github_pat_11ABCDEFG0_1234567890ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefgh")]
    [InlineData("password: mysecretpassword123")]
    [InlineData("api_key: abc123def456ghi789jkl")]
    public void Scrub_DetectsAndRedactsSecretPatterns(string secretInput)
    {
        var result = _filter.Scrub(secretInput, "test");
        result.Should().Contain("[REDACTED]",
            because: $"the input '{secretInput}' contains a detectable secret pattern");
        result.Should().NotBe(secretInput,
            because: "scrubbed output must differ from input when a secret is found");
    }

    [Theory]
    [InlineData("Hello, this is a normal message.")]
    [InlineData("The file was written successfully.")]
    [InlineData("Run completed after 3 steps.")]
    public void Scrub_CleanInput_ReturnsUnchanged(string cleanInput)
    {
        var result = _filter.Scrub(cleanInput, "test");
        result.Should().Be(cleanInput, "clean inputs must not be modified");
    }

    [Fact]
    public void Scrub_EmptyString_ReturnsEmpty()
    {
        _filter.Scrub(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Scrub_MultipleSecrets_RedactsAll()
    {
        var input = "token: ghp_abc123456789012345 and password: mysecret123";
        var result = _filter.Scrub(input, "test");
        result.Should().Contain("[REDACTED]");
        // The original secret values should not appear verbatim
        result.Should().NotContain("ghp_abc123456789012345");
    }
}
