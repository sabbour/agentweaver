using System.Text.Json;
using Agentweaver.AgentRuntime.Workflow;
using FluentAssertions;

namespace Agentweaver.Tests.Workflow;

public sealed class StructuredRunFailureTerminalTests
{
    public static TheoryData<string, string, string, string> CredentialDiagnostics => new()
    {
        {
            "request failed; Authorization: Bearer bearer-value-123, retry scheduled",
            "bearer-value-123",
            "request failed",
            "retry scheduled"
        },
        {
            "request failed; AUTHORIZATION : Basic QWxhZGRpbjpvcGVuIHNlc2FtZQ==; retry scheduled",
            "QWxhZGRpbjpvcGVuIHNlc2FtZQ==",
            "request failed",
            "retry scheduled"
        },
        {
            "request failed: https://example.test/callback?access_token=query-token-456&operation=deploy",
            "query-token-456",
            "request failed",
            "operation=deploy"
        },
        {
            "request failed; \"CLIENT_SECRET\" : \"client secret value\"; tenant=example",
            "client secret value",
            "request failed",
            "tenant=example"
        },
        {
            "request failed; x-API-key=api-key-value-789; attempt=3",
            "api-key-value-789",
            "request failed",
            "attempt=3"
        },
        {
            "request failed; Server=db;User Id=runner;Password=p@ss-value;Database=agentweaver",
            "p@ss-value",
            "request failed",
            "Database=agentweaver"
        },
        {
            "request failed: https://storage.test/blob?sv=2025-01-05&SIG=abc%2Bdef%3D&se=2030-01-01",
            "abc%2Bdef%3D",
            "request failed",
            "se=2030-01-01"
        },
        {
            "request failed; token=legacy-token-value; reason=transport",
            "legacy-token-value",
            "request failed",
            "reason=transport"
        },
        {
            "request failed; secret:'legacy secret value'; reason=transport",
            "legacy secret value",
            "request failed",
            "reason=transport"
        },
    };

    [Theory]
    [MemberData(nameof(CredentialDiagnostics))]
    public void CreateInternalError_RedactsCredentialsAndPreservesContext(
        string diagnostic,
        string secret,
        string contextBefore,
        string contextAfter)
    {
        var runEvent = StructuredRunFailureTerminal.CreateInternalError("Turn failed.", diagnostic);

        var persistedDiagnostic = ReadDiagnostic(runEvent.Payload);
        persistedDiagnostic.Should().NotContain(secret);
        persistedDiagnostic.Should().Contain("[REDACTED]");
        persistedDiagnostic.Should().Contain(contextBefore);
        persistedDiagnostic.Should().Contain(contextAfter);
    }

    [Fact]
    public void CreateInternalError_RedactsMultilineMixedCaseDiagnostics()
    {
        var secrets = new[]
        {
            "bearer-multiline",
            "YmFzaWMtdXNlcjpiYXNpYy1wYXNz",
            "access-multiline",
            "client-multiline",
            "api-multiline",
            "password-multiline",
            "signature-multiline",
        };
        var diagnostic = string.Join(
            "\r\n",
            "deployment failed for workflow 522",
            $"authorization: bearer {secrets[0]}",
            $"Authorization: BASIC {secrets[1]}",
            $"callback=https://example.test/?ACCESS_TOKEN={secrets[2]}&stage=validate",
            $"client-secret='{secrets[3]}'; tenant=example",
            $"Api_Key : {secrets[4]}, attempt=2",
            $"Server=db;PWD={secrets[5]};Database=agentweaver",
            $"sas=https://storage.test/blob?sv=1&sig={secrets[6]}&se=2",
            "retry remains available");

        var runEvent = StructuredRunFailureTerminal.CreateInternalError("Turn failed.", diagnostic);

        var persistedDiagnostic = ReadDiagnostic(runEvent.Payload);
        foreach (var secret in secrets)
            persistedDiagnostic.Should().NotContain(secret);
        persistedDiagnostic.Should().Contain("deployment failed for workflow 522");
        persistedDiagnostic.Should().Contain("stage=validate");
        persistedDiagnostic.Should().Contain("tenant=example");
        persistedDiagnostic.Should().Contain("attempt=2");
        persistedDiagnostic.Should().Contain("Database=agentweaver");
        persistedDiagnostic.Should().Contain("se=2");
        persistedDiagnostic.Should().Contain("retry remains available");
        persistedDiagnostic.Should().NotContain("\r");
        persistedDiagnostic.Should().NotContain("\n");
    }

    private static string ReadDiagnostic(object payload) =>
        JsonSerializer.SerializeToElement(payload).GetProperty("diagnostic").GetString()!;
}
