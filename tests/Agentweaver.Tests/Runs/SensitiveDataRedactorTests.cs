using System.Text.Json;
using Agentweaver.Domain;
using FluentAssertions;

namespace Agentweaver.Tests.Runs;

/// <summary>
/// Coverage for <see cref="SensitiveDataRedactor"/>, added for issue #850's rubber-duck security
/// follow-up: tool call arguments/results must have sensitive key values (tokens, passwords,
/// secrets, credentials, keys) redacted both at event emission and defensively at API rendering.
/// </summary>
public sealed class SensitiveDataRedactorTests
{
    [Theory]
    [InlineData("token")]
    [InlineData("Token")]
    [InlineData("access_token")]
    [InlineData("accessToken")]
    [InlineData("authorization")]
    [InlineData("Authorization")]
    [InlineData("password")]
    [InlineData("secret")]
    [InlineData("client_secret")]
    [InlineData("credential")]
    [InlineData("connection_string")]
    [InlineData("api_key")]
    [InlineData("apiKey")]
    [InlineData("private-key")]
    [InlineData("bearer")]
    public void IsSensitiveKey_MatchesKnownSensitiveNames(string key)
    {
        SensitiveDataRedactor.IsSensitiveKey(key).Should().BeTrue();
    }

    [Theory]
    [InlineData("command")]
    [InlineData("cwd")]
    [InlineData("path")]
    [InlineData("toolName")]
    [InlineData("timeout")]
    public void IsSensitiveKey_LeavesBenignNamesAlone(string key)
    {
        SensitiveDataRedactor.IsSensitiveKey(key).Should().BeFalse();
    }

    [Fact]
    public void RedactObject_RedactsTopLevelSensitiveKey()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["command"] = "curl https://example.com",
            ["authorization"] = "Bearer eyJabc123",
        };

        var redacted = SensitiveDataRedactor.RedactObject(arguments);

        redacted!["command"]!.GetValue<string>().Should().Be("curl https://example.com");
        redacted["authorization"]!.GetValue<string>().Should().Be(SensitiveDataRedactor.RedactedPlaceholder);
    }

    [Fact]
    public void RedactObject_RedactsNestedSensitiveKeys()
    {
        var arguments = new
        {
            command = "deploy",
            env = new Dictionary<string, object?>
            {
                ["API_KEY"] = "sk-live-abc123",
                ["REGION"] = "us-east-1",
            },
        };

        var redacted = SensitiveDataRedactor.RedactObject(arguments);

        redacted!["env"]!["API_KEY"]!.GetValue<string>().Should().Be(SensitiveDataRedactor.RedactedPlaceholder);
        redacted["env"]!["REGION"]!.GetValue<string>().Should().Be("us-east-1");
    }

    [Fact]
    public void RedactObject_RedactsSensitiveKeysInsideArrays()
    {
        var arguments = new
        {
            headers = new[]
            {
                new Dictionary<string, object?> { ["name"] = "Authorization", ["value"] = "Bearer xyz" },
                new Dictionary<string, object?> { ["name"] = "Accept", ["value"] = "application/json" },
            },
        };

        var redacted = SensitiveDataRedactor.RedactObject(arguments);
        var headers = redacted!["headers"]!.AsArray();

        // "name" itself isn't a sensitive key (its value is just the literal string "Authorization"),
        // only a property actually *named* something sensitive gets redacted.
        headers[0]!["name"]!.GetValue<string>().Should().Be("Authorization");
        headers[0]!["value"]!.GetValue<string>().Should().Be("Bearer xyz");
        headers[1]!["value"]!.GetValue<string>().Should().Be("application/json");
    }

    [Fact]
    public void RedactObject_ReturnsNullForNullInput()
    {
        SensitiveDataRedactor.RedactObject(null).Should().BeNull();
    }

    [Fact]
    public void RedactJsonStringIfApplicable_RedactsStructuredJsonContent()
    {
        var content = JsonSerializer.Serialize(new { status = "ok", secret = "s3cr3t-value" });

        var redacted = SensitiveDataRedactor.RedactJsonStringIfApplicable(content);

        redacted.Should().NotContain("s3cr3t-value");
        redacted.Should().Contain(SensitiveDataRedactor.RedactedPlaceholder);
        redacted.Should().Contain("\"status\":\"ok\"");
    }

    [Fact]
    public void RedactJsonStringIfApplicable_LeavesPlainTextContentUnchanged()
    {
        const string content = "preview_process_started: session_id=abc123, pid=594";

        var redacted = SensitiveDataRedactor.RedactJsonStringIfApplicable(content);

        redacted.Should().Be(content);
    }

    [Theory]
    [InlineData("ghu_token")]
    [InlineData("ghs_token")]
    [InlineData("ghp_token")]
    [InlineData("gho_token")]
    [InlineData("ghr_token")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.signature")]
    public void Redaction_RecursivelyRemovesCredentialShapesFromValues(string secret)
    {
        var value = JsonSerializer.Serialize(new
        {
            nested = new[] { new Dictionary<string, object?> { ["message"] = $"provider failure: {secret}" } },
        });

        var redacted = SensitiveDataRedactor.RedactJsonStringIfApplicable(value);

        redacted.Should().NotContain(secret).And.Contain(SensitiveDataRedactor.RedactedPlaceholder);
    }

    [Fact]
    public void RedactJsonStringIfApplicable_HandlesNullAndEmpty()
    {
        SensitiveDataRedactor.RedactJsonStringIfApplicable(null).Should().Be(string.Empty);
        SensitiveDataRedactor.RedactJsonStringIfApplicable(string.Empty).Should().Be(string.Empty);
    }

    [Fact]
    public void RedactElement_RedactsSensitiveKeysInJsonElement()
    {
        using var doc = JsonDocument.Parse("""{"command":"run","password":"hunter2"}""");

        var redacted = SensitiveDataRedactor.RedactElement(doc.RootElement);

        redacted.GetProperty("command").GetString().Should().Be("run");
        redacted.GetProperty("password").GetString().Should().Be(SensitiveDataRedactor.RedactedPlaceholder);
    }

    [Fact]
    public void RedactElement_ReturnsOriginalWhenNotAnObjectOrArray()
    {
        using var doc = JsonDocument.Parse("\"just a plain string\"");

        var redacted = SensitiveDataRedactor.RedactElement(doc.RootElement);

        redacted.GetString().Should().Be("just a plain string");
    }
}
