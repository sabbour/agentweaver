using System.Net;
using System.Text;
using System.Text.Json;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;
using FluentAssertions;

namespace Agentweaver.Tests.Mcp;

public sealed class McpSandboxPolicyToolsTests
{
    [Fact]
    public async Task SandboxPolicyGet_with_run_id_passes_through_safe_effective_permission_projection()
    {
        const string runId = "00000000-0000-0000-0000-000000001397";
        const string responseJson =
            """
            {
              "run_id": "00000000-0000-0000-0000-000000001397",
              "binding": { "binding_id": "epb-safe", "version": "sha256:safe" },
              "configured_policy": { "allowed_operations": ["workspace.read", "workspace.write"] },
              "effective_policy": { "allowed_operations": ["workspace.read"] },
              "overrides": { "is_narrowed": true, "removed_operations": ["workspace.write"] },
              "current_revocation": { "active": true, "removed_since_launch": ["network.access"] },
              "coverage": [{ "operation": "workspace.read", "allowed": true }],
              "latest_denial": {
                "reason_code": "operation_not_allowed",
                "reason": "Operation 'workspace.write' was not allowed by the effective binding."
              }
            }
            """;
        var handler = new CapturingHandler(responseJson);
        var api = new AgentweaverApiClient(
            new HttpClient(handler),
            new McpConfig("http://localhost", "test-token"));
        var tools = new SandboxPolicyTools(api);

        var result = await tools.SandboxPolicyGetAsync(
            repository_path: @"C:\must-not-be-used",
            run_id: runId,
            ct: CancellationToken.None);

        handler.Path.Should().Be($"/api/runs/{runId}/effective-permissions");
        using var expected = JsonDocument.Parse(responseJson);
        using var actual = JsonDocument.Parse(result);
        JsonElement.DeepEquals(actual.RootElement, expected.RootElement).Should().BeTrue();
        result.Should().NotContain("must-not-be-used");
    }

    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });
        }
    }
}
