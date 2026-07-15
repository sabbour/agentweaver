using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;

namespace Agentweaver.Tests.Mcp;

public sealed class McpRunTaskTests
{
    [Fact]
    public async Task RunTask_HappyPath_ReturnsArtifactsInline()
    {
        var statusCalls = 0;
        var tools = CreateRunTools((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/projects/proj-1/orchestrations")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { runId = "run-1" })
                });
            }

            if (request.Method == HttpMethod.Get && path == "/api/runs/run-1")
            {
                statusCalls++;
                if (statusCalls == 1)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new { run_id = "run-1", status = "in_progress" })
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { run_id = "run-1", status = "merged", result = "ok" })
                });
            }

            if (request.Method == HttpMethod.Get && path == "/api/runs/run-1/files")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[] { new { path = "README.md", change_type = "modified" } })
                });
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });

        var result = await tools.RunTaskAsync("proj-1", "Ship it", workflow_id: null, model_id: null, start_mode: "direct", timeout_seconds: 5, poll_interval_seconds: 1, CancellationToken.None);

        result.RunId.Should().Be("run-1");
        result.Status.Should().Be("merged");
        result.Artifacts.Should().NotBeNull();
        result.Artifacts![0].GetProperty("path").GetString().Should().Be("README.md");
    }

    [Fact]
    public async Task RunTask_AwaitingConfirmation_ReturnsNextAction()
    {
        var tools = CreateRunTools((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/projects/proj-1/orchestrations")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { runId = "run-2" })
                });
            }

            if (request.Method == HttpMethod.Get && path == "/api/runs/run-2")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        run_id = "run-2",
                        status = "in_progress",
                        coordinator_status = "awaiting_confirmation"
                    })
                });
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });

        var result = await tools.RunTaskAsync("proj-1", "Plan it", workflow_id: null, model_id: null, start_mode: "defineOutcome", timeout_seconds: 5, poll_interval_seconds: 1, CancellationToken.None);

        result.Status.Should().Be("awaiting_confirmation");
        result.ReviewPrompt.Should().Contain("coordinator_outcome_spec_get");
    }

    [Fact]
    public async Task RunTask_Timeout_ReturnsPartialState()
    {
        var tools = CreateRunTools((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/projects/proj-1/orchestrations")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { runId = "run-3" })
                });
            }

            if (request.Method == HttpMethod.Get && path == "/api/runs/run-3")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { run_id = "run-3", status = "in_progress" })
                });
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });

        var result = await tools.RunTaskAsync("proj-1", "Wait", workflow_id: null, model_id: null, start_mode: "direct", timeout_seconds: 1, poll_interval_seconds: 1, CancellationToken.None);

        result.RunId.Should().Be("run-3");
        result.Status.Should().Be("timed_out");
        result.Hint.Should().Contain("run_status");
        // #339: the timed_out response must still carry an artifacts array (empty), not omit it.
        result.Artifacts.Should().NotBeNull();
        result.Artifacts.Should().BeEmpty();
    }

    private static RunTools CreateRunTools(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var httpClient = new HttpClient(new DelegatingHandlerStub(handler))
        {
            BaseAddress = new Uri("http://localhost/")
        };

        var apiClient = new AgentweaverApiClient(httpClient, new McpConfig("http://localhost", "test-api-key"));
        return new RunTools(apiClient);
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
