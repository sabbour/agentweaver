using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;
using ModelContextProtocol;

namespace Agentweaver.Tests.Mcp;

/// <summary>
/// Regression tests for #347: MCP <c>coordinator_start</c> (and the other coordinator tools that
/// share the same dispatch path) must surface the real underlying API error detail instead of the
/// opaque MCP wrapper message "An error occurred invoking '&lt;tool&gt;'.".
///
/// The MCP SDK only appends an exception's message to the tool-call error content when the thrown
/// exception derives from <see cref="McpException"/>; any other exception collapses to the opaque
/// generic string. These tests lock in that every coordinator tool threads backend failures through
/// <see cref="McpApiException"/> (an <see cref="McpException"/>) so the actionable detail survives.
/// </summary>
public sealed class McpCoordinatorErrorsTests
{
    // The exact opaque string the MCP SDK emits for a non-McpException. If any assertion below ever
    // matches this, the swallow described in #347 has regressed.
    private const string OpaqueWrapperMessage = "An error occurred invoking 'coordinator_start'.";

    [Fact]
    public async Task CoordinatorStart_BadRequest_SurfacesRealDetailNotOpaqueWrapper()
    {
        var tools = CreateCoordinatorTools((request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().Be("/api/projects/proj-1/orchestrations");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = JsonContent.Create(new { error = "model_id is not allowed." })
            });
        });

        var act = () => tools.CoordinatorStartAsync("proj-1", "Ship the thing", model_id: "bad-model", ct: CancellationToken.None);

        var ex = await act.Should().ThrowAsync<McpApiException>();

        // It is an McpException, so the SDK forwards ex.Message to the client (see SDK
        // AIFunctionMcpServerTool.InvokeAsync) instead of the opaque generic string.
        ex.Which.Should().BeAssignableTo<McpException>();
        ex.Which.StatusCode.Should().Be(400);
        ex.Which.Error.Should().Be("model_id is not allowed.");

        // The real detail is present in the serialized message the client receives...
        using var payload = JsonDocument.Parse(ex.Which.Message);
        payload.RootElement.GetProperty("error").GetString().Should().Be("model_id is not allowed.");
        payload.RootElement.TryGetProperty("hint", out _).Should().BeTrue();

        // ...and it is NOT the opaque wrapper #347 reported.
        ex.Which.Message.Should().NotBe(OpaqueWrapperMessage);
        ex.Which.Message.Should().Contain("model_id is not allowed.");
    }

    [Fact]
    public async Task CoordinatorStart_ProjectNotFound_SurfacesProjectHint()
    {
        // The backend returns 404 with an empty body for a missing project (Results.NotFound()).
        // The mapping must still produce an actionable, project-scoped message rather than swallowing it.
        var tools = CreateCoordinatorTools((request, _) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/projects/proj-404/orchestrations");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var act = () => tools.CoordinatorStartAsync("proj-404", "Do work", model_id: null, ct: CancellationToken.None);

        var ex = await act.Should().ThrowAsync<McpApiException>();
        ex.Which.StatusCode.Should().Be(404);
        ex.Which.Error.Should().Be("Project 'proj-404' not found.");
        ex.Which.Hint.Should().Be("Call project_list to see available projects.");
        ex.Which.Message.Should().NotBe(OpaqueWrapperMessage);
    }

    [Fact]
    public async Task CoordinatorStart_NoTeamConflict_SurfacesConflictDetail()
    {
        var tools = CreateCoordinatorTools((request, _) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/projects/proj-1/orchestrations");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new
                {
                    error = "no_team",
                    message = "This project has no team. Cast a team before starting a coordinator run."
                })
            });
        });

        var act = () => tools.CoordinatorStartAsync("proj-1", "Ship it", model_id: null, ct: CancellationToken.None);

        var ex = await act.Should().ThrowAsync<McpApiException>();
        ex.Which.StatusCode.Should().Be(409);
        // The backend message survives into the surfaced error, not the opaque wrapper.
        ex.Which.Message.Should().Contain("no team");
        ex.Which.Message.Should().NotBe(OpaqueWrapperMessage);
    }

    [Fact]
    public async Task CoordinatorSteer_BackendConflict_SurfacesRealDetail()
    {
        // A second coordinator tool exercising the same dispatch/error-mapping path proves the fix
        // is not specific to coordinator_start.
        var tools = CreateCoordinatorTools((request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().Be("/api/runs/run-1/steer");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new { error = "Run is not in a steerable state (current state: 'completed')." })
            });
        });

        var act = () => tools.CoordinatorSteerAsync(
            "run-1", kind: "redirect", instruction: "focus on tests", target_child_run_id: null, ct: CancellationToken.None);

        var ex = await act.Should().ThrowAsync<McpApiException>();
        ex.Which.Should().BeAssignableTo<McpException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("steerable state");
        ex.Which.Message.Should().NotBe("An error occurred invoking 'coordinator_steer'.");
    }

    private static CoordinatorTools CreateCoordinatorTools(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(CreateApiClient(handler));

    private static AgentweaverApiClient CreateApiClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var httpClient = new HttpClient(new DelegatingHandlerStub(handler))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        return new AgentweaverApiClient(httpClient, new McpConfig("http://localhost", "test-api-key"));
    }

    private sealed class DelegatingHandlerStub(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
