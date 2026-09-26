using System.Net;
using System.Text;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;
using FluentAssertions;

namespace Agentweaver.Tests.Mcp;

public sealed class McpRunExecutionIdentityTests
{
    [Fact]
    public async Task Run_execution_identity_passes_through_the_safe_projection()
    {
        const string runId = "00000000-0000-0000-0000-000000001404";
        const string responseJson =
            """
            {
              "evidence_state": "complete",
              "descriptor": {
                "descriptor_id": "execution-safe",
                "principal_ref": "principal-safe"
              },
              "decisions": []
            }
            """;
        var handler = new CapturingHandler(responseJson);
        var tools = new RunTools(new AgentweaverApiClient(
            new HttpClient(handler),
            new McpConfig("http://localhost", "test-token")));

        var result = await tools.RunExecutionIdentityAsync(runId);

        handler.Path.Should().Be($"/api/runs/{runId}/execution-identity");
        result.EvidenceState.Should().Be("complete");
        result.Additional.Should().ContainKey("descriptor");
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
