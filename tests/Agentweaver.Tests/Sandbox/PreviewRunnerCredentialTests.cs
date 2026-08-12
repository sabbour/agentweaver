using Agentweaver.Api.Auth;
using Agentweaver.Api.Sandbox.Preview;
using FluentAssertions;

namespace Agentweaver.Tests.Sandbox;

public sealed class PreviewRunnerCredentialTests
{
    [Theory]
    [InlineData("run-123")]
    [InlineData("run/with:unsafe?characters")]
    public void SecretKey_IsDeterministicAndKeyVaultSafe(string runId)
    {
        var first = PreviewRunnerCredential.SecretKey(runId);
        var second = PreviewRunnerCredential.SecretKey(runId);
        var keyVaultName = KeyVaultSecretStore.SanitizeKey(first);

        second.Should().Be(first);
        keyVaultName.Should().MatchRegex("^[0-9a-zA-Z-]+$");
        keyVaultName.Length.Should().BeLessThanOrEqualTo(127);
    }

    [Fact]
    public void SecretKey_LongRunId_RemainsWithinKeyVaultLimit()
    {
        var keyVaultName = KeyVaultSecretStore.SanitizeKey(
            PreviewRunnerCredential.SecretKey(new string('x', 500)));

        keyVaultName.Length.Should().Be(127);
    }

    [Fact]
    public void Mint_ReturnsDistinctHighEntropyUrlSafeCredentials()
    {
        var first = PreviewRunnerCredential.Mint();
        var second = PreviewRunnerCredential.Mint();

        first.Should().NotBe(second);
        first.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        second.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
    }
}
