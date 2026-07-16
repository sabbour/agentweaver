using System.Text;
using FluentAssertions;
using Agentweaver.Api.Webhooks;

namespace Agentweaver.Tests.Webhooks;

/// <summary>
/// Unit tests for <see cref="GitHubWebhookSignatureVerifier"/> (issue #53 follow-up: GitHub webhook
/// receiver). Covers valid signatures, tampered bodies, wrong secrets, and malformed/missing headers —
/// the exact conditions <see cref="Agentweaver.Api.Endpoints.GitHubWebhookEndpoints"/> relies on to
/// reject forged deliveries before ever parsing/trusting the payload.
/// </summary>
public sealed class GitHubWebhookSignatureVerifierTests
{
    private const string Secret = "test-webhook-secret-value";

    [Fact]
    public void Verify_ValidSignature_ReturnsTrue()
    {
        var body = Encoding.UTF8.GetBytes("""{"action":"opened","repository":{"full_name":"acme/demo"}}""");
        var signature = ComputeSignature(Secret, body);

        GitHubWebhookSignatureVerifier.Verify(Secret, body, signature).Should().BeTrue();
    }

    [Fact]
    public void Verify_TamperedBody_ReturnsFalse()
    {
        var originalBody = Encoding.UTF8.GetBytes("""{"action":"opened"}""");
        var signature = ComputeSignature(Secret, originalBody);
        var tamperedBody = Encoding.UTF8.GetBytes("""{"action":"closed"}""");

        GitHubWebhookSignatureVerifier.Verify(Secret, tamperedBody, signature).Should().BeFalse();
    }

    [Fact]
    public void Verify_WrongSecret_ReturnsFalse()
    {
        var body = Encoding.UTF8.GetBytes("""{"action":"opened"}""");
        var signature = ComputeSignature("a-different-secret", body);

        GitHubWebhookSignatureVerifier.Verify(Secret, body, signature).Should().BeFalse();
    }

    [Fact]
    public void Verify_MissingSignatureHeader_ReturnsFalse()
    {
        var body = Encoding.UTF8.GetBytes("""{"action":"opened"}""");

        GitHubWebhookSignatureVerifier.Verify(Secret, body, signatureHeader: null).Should().BeFalse();
        GitHubWebhookSignatureVerifier.Verify(Secret, body, signatureHeader: "").Should().BeFalse();
    }

    [Fact]
    public void Verify_MalformedSignatureHeader_ReturnsFalse()
    {
        var body = Encoding.UTF8.GetBytes("""{"action":"opened"}""");

        // Missing "sha256=" prefix.
        GitHubWebhookSignatureVerifier.Verify(Secret, body, "deadbeef").Should().BeFalse();
        // Not valid hex.
        GitHubWebhookSignatureVerifier.Verify(Secret, body, "sha256=not-hex-zzzz").Should().BeFalse();
        // Wrong length (too short for a 32-byte digest).
        GitHubWebhookSignatureVerifier.Verify(Secret, body, "sha256=abcd").Should().BeFalse();
    }

    [Fact]
    public void Verify_EmptyConfiguredSecret_ReturnsFalse()
    {
        var body = Encoding.UTF8.GetBytes("""{"action":"opened"}""");
        var signature = ComputeSignature(Secret, body);

        GitHubWebhookSignatureVerifier.Verify(secret: null, body, signature).Should().BeFalse();
        GitHubWebhookSignatureVerifier.Verify(secret: "", body, signature).Should().BeFalse();
    }

    private static string ComputeSignature(string secret, byte[] body)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(body);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
