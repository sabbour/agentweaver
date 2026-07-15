using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Agentweaver.Tests.Mcp;

/// <summary>
/// Guards the MCP run-workflow tool contract regressions fixed in #337, #338, and #339:
/// declared output schemas, optional input parameters, and structured error surfacing.
/// </summary>
public sealed class McpToolSchemaTests
{
    // ---- #337: run-workflow tools must declare an outputSchema matching runtime shape ----

    [Fact]
    public void RunSubmit_DeclaresOutputSchema_WithRunIdAndStatus()
    {
        var schema = OutputSchema(nameof(RunTools.RunSubmitAsync));
        var props = Properties(schema);

        props.Should().ContainKey("run_id");
        props.Should().ContainKey("status");
        TypeOf(props["run_id"]).Should().Contain("string");
        TypeOf(props["status"]).Should().Contain("string");
    }

    [Fact]
    public void RunStatus_DeclaresOutputSchema_WithStatus()
    {
        var schema = OutputSchema(nameof(RunTools.RunStatusAsync));
        var props = Properties(schema);

        props.Should().ContainKey("status");
        TypeOf(props["status"]).Should().Contain("string");
    }

    [Fact]
    public void RunShowArtifacts_DeclaresOutputSchema_WithArtifactsArray()
    {
        var schema = OutputSchema(nameof(RunTools.RunShowArtifactsAsync));
        var props = Properties(schema);

        props.Should().ContainKey("artifacts");
        TypeOf(props["artifacts"]).Should().Contain("array");
    }

    [Fact]
    public void RunTask_DeclaresOutputSchema_WithRunIdStatusAndArtifactsArray()
    {
        var schema = OutputSchema(nameof(RunTools.RunTaskAsync));
        var props = Properties(schema);

        props.Should().ContainKey("run_id");
        props.Should().ContainKey("status");
        props.Should().ContainKey("artifacts");
        TypeOf(props["run_id"]).Should().Contain("string");
        TypeOf(props["status"]).Should().Contain("string");
        TypeOf(props["artifacts"]).Should().Contain("array");
    }

    /// <summary>
    /// Regression test for #341: run_task's outputSchema must NOT emit the JSON Schema boolean
    /// <c>true</c> for the <c>run</c> property. The boolean <c>true</c> is technically valid
    /// JSON Schema ("any value is valid") but causes the MCP TypeScript SDK's Zod-based
    /// tools/list validator to throw, breaking ALL tools for any SDK-based MCP client.
    /// </summary>
    [Fact]
    public void RunTask_OutputSchema_RunProperty_IsNotBooleanTrue()
    {
        var schema = OutputSchema(nameof(RunTools.RunTaskAsync));
        var props = Properties(schema);

        props.Should().ContainKey("run", because: "run_task always embeds the run object for terminal/gate responses");

        var runSchema = props["run"];

        // The schema must NOT be the JSON Schema boolean `true` (ValueKind == True).
        // That literal-boolean form is what the broken generator emits when it can't reflect
        // the type; the TypeScript MCP SDK's Zod validator rejects it with ZodError at
        // tools[N].outputSchema.properties.run.
        runSchema.ValueKind.Should().NotBe(JsonValueKind.True,
            because: "JSON Schema boolean `true` for `run` breaks the MCP TypeScript SDK's Zod validation");

        // The schema must be an object (not null, not a primitive) representing a JSON Schema node.
        runSchema.ValueKind.Should().Be(JsonValueKind.Object,
            because: "run must be described by a proper JSON Schema object (e.g. {\"type\":\"object\"} or $ref)");

        // The schema must declare type as object (or object|null for nullable).
        var types = TypeOf(runSchema);
        types.Should().Contain("object",
            because: "the `run` property holds the API run object so its schema must include type:object");
    }

    // ---- #338: functionally-optional parameters must not be marked required ----

    [Fact]
    public void RunSubmit_InputSchema_MarksLegacyFieldsOptional()
    {
        var required = RequiredInputs(nameof(RunTools.RunSubmitAsync));

        required.Should().Contain("project_id");
        required.Should().Contain("task");
        required.Should().NotContain("agent_name");
        required.Should().NotContain("base_branch");
        required.Should().NotContain("model_source");
    }

    [Fact]
    public void RunTask_InputSchema_MarksTuningFieldsOptional()
    {
        var required = RequiredInputs(nameof(RunTools.RunTaskAsync));

        required.Should().Contain("project_id");
        required.Should().Contain("task");
        required.Should().NotContain("workflow_id");
        required.Should().NotContain("model_id");
        required.Should().NotContain("start_mode");
        required.Should().NotContain("timeout_seconds");
        required.Should().NotContain("poll_interval_seconds");
    }

    [Fact]
    public async Task RunSubmit_WorksWhenOptionalParamsOmitted()
    {
        var tools = CreateRunTools((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/projects/proj-1/orchestrations")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { runId = "run-9" })
                });
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });

        // Only the two required arguments are supplied; the optional ones use their defaults.
        var result = await tools.RunSubmitAsync("proj-1", "Ship it");

        result.RunId.Should().Be("run-9");
        result.Status.Should().Be("submitted");
        result.StartMode.Should().Be("direct");
    }

    // ---- functional shape checks for the pass-through tools ----

    [Fact]
    public async Task RunShowArtifacts_WrapsFilesUnderArtifactsArray()
    {
        var tools = CreateRunTools((request, _) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/runs/run-1/files");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[]
                {
                    new { path = "README.md", change_type = "modified" }
                })
            });
        });

        var result = await tools.RunShowArtifactsAsync("run-1");

        result.Artifacts.Should().HaveCount(1);
        result.Artifacts[0].GetProperty("path").GetString().Should().Be("README.md");
    }

    [Fact]
    public async Task RunStatus_PreservesFullRunObject()
    {
        var tools = CreateRunTools((request, _) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/runs/run-1");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { run_id = "run-1", status = "in_progress", coordinator_status = "planning" })
            });
        });

        var result = await tools.RunStatusAsync("run-1");

        result.Status.Should().Be("in_progress");
        // Extra fields survive round-trip via extension data, so no run detail is dropped.
        result.Additional.Should().ContainKey("run_id");
        result.Additional.Should().ContainKey("coordinator_status");
        result.Additional!["coordinator_status"].GetString().Should().Be("planning");
    }

    // ---- #339: tool-call exceptions surface the real message + hint, not a generic wrapper ----

    [Fact]
    public void McpApiException_IsAnMcpException_SoTheSdkForwardsItsMessage()
    {
        // The MCP SDK only appends the exception message to the tool-call error content when
        // the thrown exception is a ModelContextProtocol.McpException; otherwise it collapses to
        // "An error occurred invoking '<tool>'." Deriving from McpException is the fix for #339.
        typeof(McpApiException).Should().BeAssignableTo<McpException>();
    }

    [Fact]
    public async Task RunShowArtifacts_ArtifactsNotReady_SurfacesMessageAndHint()
    {
        var tools = CreateRunTools((request, _) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/runs/run-1/files");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new { error = "worktree not available" })
            });
        });

        var act = () => tools.RunShowArtifactsAsync("run-1");

        var ex = await act.Should().ThrowAsync<McpApiException>();

        // It is an McpException, so the SDK surfaces ex.Message (this JSON) to the client.
        ex.Which.Should().BeAssignableTo<McpException>();
        ex.Which.Error.Should().Be("Run artifacts are not ready yet.");
        ex.Which.Hint.Should().Contain("run_status");

        using var payload = JsonDocument.Parse(ex.Which.Message);
        payload.RootElement.GetProperty("error").GetString().Should().Be("Run artifacts are not ready yet.");
        payload.RootElement.GetProperty("hint").GetString().Should().Contain("run_status");
    }

    // ---- helpers ----

    private static JsonElement OutputSchema(string methodName)
    {
        var tool = BuildTool(methodName);
        tool.ProtocolTool.OutputSchema.Should().NotBeNull($"{methodName} must declare an outputSchema");
        return tool.ProtocolTool.OutputSchema!.Value;
    }

    private static HashSet<string> RequiredInputs(string methodName)
    {
        var schema = BuildTool(methodName).ProtocolTool.InputSchema;
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            return required.EnumerateArray().Select(e => e.GetString()!).ToHashSet();
        return new HashSet<string>();
    }

    private static McpServerTool BuildTool(string methodName)
    {
        var method = typeof(RunTools).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Method {methodName} not found on RunTools.");
        var instance = new RunTools(CreateApiClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        return McpServerTool.Create(method, instance, options: null);
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement schema)
    {
        schema.TryGetProperty("properties", out var props).Should().BeTrue("output schema must expose properties");
        return props.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
    }

    private static IEnumerable<string> TypeOf(JsonElement propertySchema)
    {
        if (!propertySchema.TryGetProperty("type", out var type))
            return Array.Empty<string>();
        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : new[] { type.GetString()! };
    }

    private static RunTools CreateRunTools(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(CreateApiClient(handler));

    private static AgentweaverApiClient CreateApiClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var httpClient = new HttpClient(new DelegatingHandlerStub(handler))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        return new AgentweaverApiClient(httpClient, new McpConfig("http://localhost", "test-api-key"));
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
