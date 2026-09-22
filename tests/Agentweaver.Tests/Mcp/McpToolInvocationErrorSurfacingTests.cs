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

    [Fact]
    public async Task ProjectCreate_OptionalBlueprintOmitted_Succeeds()
    {
        // Regression guard for #418: blueprint is documented as optional ("Inline blueprint object
        // to apply at creation ... exclusive with blueprint_id"), but the C# parameter previously had
        // no default value. Microsoft.Extensions.AI's reflection-based argument binding treats any
        // parameter without a C# default as required, so a client omitting blueprint (the normal,
        // documented case) got its argument binding rejected before ProjectCreateAsync ever ran -
        // collapsed by the MCP SDK to the opaque "An error occurred invoking 'project_create'."
        // wrapper (the same class of bug as #347), never reaching the real API call below.
        var tool = BuildTool<ProjectTools>(
            nameof(ProjectTools.ProjectCreateAsync),
            (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new { project_id = "proj-9", name = "demo" })
            }));

        var result = await InvokeAsync(tool, "project_create", new()
        {
            ["name"] = JsonDoc("demo"),
            ["working_directory"] = JsonDoc("/tmp/demo"),
            // blueprint intentionally omitted - documented as optional.
        });

        result.IsError.Should().NotBeTrue();
        var text = TextOf(result);
        text.Should().NotBe("An error occurred invoking 'project_create'.");
        text.Should().Contain("proj-9");
    }

    [Fact]
    public async Task MemoryGet_BackendError_ReturnsProtocolErrorWithActionableDetail()
    {
        var tool = BuildTool<MemoryTools>(
            nameof(MemoryTools.MemoryGetAsync),
            (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(new
                {
                    error = "memory_not_found",
                    message = "Memory entry 42 was not found.",
                    hint = "Call memory_list to find a valid memory entry."
                })
            }));

        var result = await InvokeAsync(tool, "memory_get", new()
        {
            ["project_id"] = JsonDoc("proj-1"),
            ["agent_name"] = JsonDoc("link"),
            ["memory_id"] = JsonDoc("42"),
        });

        result.IsError.Should().BeTrue();
        var text = TextOf(result);
        using var payload = ErrorPayloadOf(text);
        payload.RootElement.GetProperty("error").GetString().Should().Be("memory_not_found");
        payload.RootElement.GetProperty("message").GetString().Should().Be("Memory entry 42 was not found.");
        payload.RootElement.GetProperty("hint").GetString()
            .Should().Be("Call memory_list to find a valid memory entry.");
        text.Should().NotContain("memory_get failed:");
    }

    [Fact]
    public async Task MemoryRecord_BackendError_ReturnsProtocolErrorWithActionableDetail()
    {
        var tool = BuildTool<MemoryTools>(
            nameof(MemoryTools.MemoryAddAsync),
            (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new
                {
                    error = "memory_conflict",
                    message = "The memory entry conflicts with existing state.",
                    hint = "Refresh memory_list and retry."
                })
            }));

        var result = await InvokeAsync(tool, "memory_record", new()
        {
            ["project_id"] = JsonDoc("proj-1"),
            ["agent_name"] = JsonDoc("link"),
            ["type"] = JsonDoc("learning"),
            ["content"] = JsonDoc("Preserve MCP protocol errors."),
        });

        result.IsError.Should().BeTrue();
        var text = TextOf(result);
        using var payload = ErrorPayloadOf(text);
        payload.RootElement.GetProperty("error").GetString().Should().Be("memory_conflict");
        payload.RootElement.GetProperty("message").GetString()
            .Should().Be("The memory entry conflicts with existing state.");
        payload.RootElement.GetProperty("hint").GetString().Should().Be("Refresh memory_list and retry.");
        text.Should().NotContain("memory_record failed:");
    }

    [Fact]
    public async Task MemoryReadAndWrite_Success_PreserveResultContent()
    {
        var readTool = BuildTool<MemoryTools>(
            nameof(MemoryTools.MemoryGetAsync),
            (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = 42, content = "Protocol errors stay errors." })
            }));
        var writeTool = BuildTool<MemoryTools>(
            nameof(MemoryTools.MemoryAddAsync),
            (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new { id = 43, content = "Cancellation stays cancellation." })
            }));

        var readResult = await InvokeAsync(readTool, "memory_get", new()
        {
            ["project_id"] = JsonDoc("proj-1"),
            ["agent_name"] = JsonDoc("link"),
            ["memory_id"] = JsonDoc("42"),
        });
        var writeResult = await InvokeAsync(writeTool, "memory_record", new()
        {
            ["project_id"] = JsonDoc("proj-1"),
            ["agent_name"] = JsonDoc("link"),
            ["type"] = JsonDoc("learning"),
            ["content"] = JsonDoc("Cancellation stays cancellation."),
        });

        readResult.IsError.Should().NotBeTrue();
        TextOf(readResult).Should().Contain("Protocol errors stay errors.");
        writeResult.IsError.Should().NotBeTrue();
        TextOf(writeResult).Should().Contain("Cancellation stays cancellation.");
    }

    [Fact]
    public async Task MemoryGet_CancelledCall_RemainsCancelled()
    {
        var tool = BuildTool<MemoryTools>(
            nameof(MemoryTools.MemoryGetAsync),
            async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("Unreachable.");
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => InvokeAsync(tool, "memory_get", new()
        {
            ["project_id"] = JsonDoc("proj-1"),
            ["agent_name"] = JsonDoc("link"),
            ["memory_id"] = JsonDoc("42"),
        }, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task MemoryGet_UnexpectedFailure_UsesStandardProtocolError()
    {
        var tool = BuildTool<MemoryTools>(
            nameof(MemoryTools.MemoryGetAsync),
            (_, _) => throw new InvalidOperationException("socket pipeline failed"));

        var result = await InvokeAsync(tool, "memory_get", new()
        {
            ["project_id"] = JsonDoc("proj-1"),
            ["agent_name"] = JsonDoc("link"),
            ["memory_id"] = JsonDoc("42"),
        });

        result.IsError.Should().BeTrue();
        var text = TextOf(result);
        text.Should().Contain("The MCP tool failed before Agentweaver returned a response.");
        text.Should().Contain("Retry once.");
        text.Should().NotContain("memory_get failed:");
    }

    // ---- helpers ----

    private static JsonElement JsonDoc(string value) =>
        JsonSerializer.SerializeToElement(value);

    private static string TextOf(CallToolResult result) =>
        string.Join(
            "\n",
            result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static JsonDocument ErrorPayloadOf(string text) =>
        JsonDocument.Parse(text[text.IndexOf('{')..]);

    private static async Task<CallToolResult> InvokeAsync(
        McpServerTool tool,
        string toolName,
        Dictionary<string, JsonElement> arguments,
        CancellationToken cancellationToken = default)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var server = new StubMcpServer(services);
        var request = new RequestContext<CallToolRequestParams>(server)
        {
            Params = new CallToolRequestParams { Name = toolName, Arguments = arguments }
        };
        return await tool.InvokeAsync(request, cancellationToken);
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
