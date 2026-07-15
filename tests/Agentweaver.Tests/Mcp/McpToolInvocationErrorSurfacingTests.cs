using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agentweaver.Tests.Mcp;

/// <summary>
/// End-to-end regression test for #347. Unlike <see cref="McpCoordinatorErrorsTests"/>, which calls
/// the tool method directly, this drives the tool through the real MCP SDK invocation pipeline
/// (<see cref="McpServerTool.InvokeAsync"/>) so the exception travels through Microsoft.Extensions.AI's
/// <c>AIFunction.InvokeAsync</c> exactly as it does at runtime. That pipeline only forwards an
/// exception's message to the client when the exception is (or unwraps to) a
/// <see cref="McpException"/>; otherwise it collapses to the opaque
/// "An error occurred invoking '&lt;tool&gt;'." string reported in #347. This test therefore proves the
/// real client-visible <see cref="CallToolResult"/> carries the backend detail.
/// </summary>
public sealed class McpToolInvocationErrorSurfacingTests
{
    [Fact]
    public async Task CoordinatorStart_OptionalModelIdOmitted_BackendError_SurfacesRealDetail()
    {
        // This is the exact #347 scenario: the client calls coordinator_start WITHOUT the optional
        // model_id. If model_id is declared required in the tool schema, Microsoft.Extensions.AI's
        // argument binding throws a non-McpException before the method runs, which the MCP SDK
        // collapses to the opaque "An error occurred invoking 'coordinator_start'." wrapper.
        var tool = BuildTool<CoordinatorTools>(
            nameof(CoordinatorTools.CoordinatorStartAsync),
            (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new
                {
                    error = "no_team",
                    message = "This project has no team. Cast a team before starting a coordinator run."
                })
            }));

        var result = await InvokeAsync(tool, "coordinator_start", new()
        {
            ["project_id"] = JsonDoc("proj-1"),
            ["goal"] = JsonDoc("Ship the thing"),
            // model_id intentionally omitted - it is documented as optional.
        });

        result.IsError.Should().BeTrue();
        var text = TextOf(result);

        // #347 regression guard: the client must NOT receive the opaque wrapper with no detail...
        text.Should().NotBe("An error occurred invoking 'coordinator_start'.");
        // ...it must carry the real backend detail instead.
        text.Should().Contain("no team");
    }

    [Fact]
    public async Task CoordinatorStart_AllArgsProvided_BackendError_SurfacesRealDetail()
    {
        var tool = BuildTool<CoordinatorTools>(
            nameof(CoordinatorTools.CoordinatorStartAsync),
            (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = JsonContent.Create(new { error = "model_id is not allowed." })
            }));

        var result = await InvokeAsync(tool, "coordinator_start", new()
        {
            ["project_id"] = JsonDoc("proj-1"),
            ["goal"] = JsonDoc("Ship the thing"),
            ["model_id"] = JsonDoc("bad-model"),
        });

        result.IsError.Should().BeTrue();
        var text = TextOf(result);
        text.Should().NotBe("An error occurred invoking 'coordinator_start'.");
        text.Should().Contain("model_id is not allowed.");
    }

    [Fact]
    public async Task CoordinatorSteer_OptionalTargetOmitted_BackendConflict_SurfacesRealDetail()
    {
        // coordinator_steer documents target_child_run_id as optional ("omit to broadcast").
        // Omitting it must not swallow the backend error behind the opaque wrapper.
        var tool = BuildTool<CoordinatorTools>(
            nameof(CoordinatorTools.CoordinatorSteerAsync),
            (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new { error = "Run is not in a steerable state (current state: 'completed')." })
            }));

        var result = await InvokeAsync(tool, "coordinator_steer", new()
        {
            ["run_id"] = JsonDoc("run-1"),
            ["kind"] = JsonDoc("redirect"),
            ["instruction"] = JsonDoc("focus on tests"),
            // target_child_run_id intentionally omitted - documented as optional.
        });

        result.IsError.Should().BeTrue();
        var text = TextOf(result);
        text.Should().NotBe("An error occurred invoking 'coordinator_steer'.");
        text.Should().Contain("steerable state");
    }

    // ---- helpers ----

    private static JsonElement JsonDoc(string value) =>
        JsonSerializer.SerializeToElement(value);

    private static string TextOf(CallToolResult result) =>
        string.Join(
            "\n",
            result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static async Task<CallToolResult> InvokeAsync(
        McpServerTool tool, string toolName, Dictionary<string, JsonElement> arguments)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var server = new StubMcpServer(services);
        var request = new RequestContext<CallToolRequestParams>(server)
        {
            Params = new CallToolRequestParams { Name = toolName, Arguments = arguments }
        };
        return await tool.InvokeAsync(request, CancellationToken.None);
    }

    private static McpServerTool BuildTool<TTools>(
        string methodName,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        where TTools : class
    {
        var apiClient = CreateApiClient(handler);
        var instance = Activator.CreateInstance(typeof(TTools), apiClient)
            ?? throw new InvalidOperationException($"Could not construct {typeof(TTools).Name}.");
        var method = typeof(TTools).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Method {methodName} not found on {typeof(TTools).Name}.");
        return McpServerTool.Create(method, instance, options: null);
    }

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

    /// <summary>Minimal <see cref="IMcpServer"/> sufficient to drive <see cref="McpServerTool.InvokeAsync"/>.</summary>
    private sealed class StubMcpServer(IServiceProvider services) : IMcpServer
    {
        public IServiceProvider? Services { get; } = services;
        public ClientCapabilities? ClientCapabilities => null;
        public Implementation? ClientInfo => null;
        public McpServerOptions ServerOptions { get; } = new();
        public LoggingLevel? LoggingLevel => null;
        public string? SessionId => null;

        public Task RunAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncDisposable RegisterNotificationHandler(string method, Func<JsonRpcNotification, CancellationToken, ValueTask> handler) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
