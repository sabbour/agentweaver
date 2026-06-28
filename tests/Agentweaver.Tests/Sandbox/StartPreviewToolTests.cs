using System.Net;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Agentweaver.AgentRuntime;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// Unit tests for the agent-initiated <c>start_preview</c> tool produced by
/// <see cref="AgentweaverApiTools"/>. The tool is run-scoped: it is only present when a non-empty
/// runId is captured in the closure, it POSTs <c>{ "target_port": N }</c> to
/// <c>api/runs/{runId}/sandbox/preview</c>, and it returns the <c>preview_url</c> from the response.
/// A capturing fake handler asserts the path/body without a real server.
/// </summary>
public sealed class StartPreviewToolTests
{
    private const string ProjectId = "test-project-id";
    private const string AgentName = "tank";
    private const string RunId = "run-abc-123";

    private static AIFunction GetStartPreview(CapturingHandler handler, string? runId = RunId)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var tools = AgentweaverApiTools.Build(ProjectId, AgentName, "http://localhost", null, http, runId: runId);
        return tools.Single(t => t.Name == "start_preview");
    }

    [Fact]
    public void StartPreview_IsPresent_WhenRunIdSupplied()
    {
        var http = new HttpClient(new CapturingHandler(HttpStatusCode.OK, "{}"))
            { BaseAddress = new Uri("http://localhost/") };
        var tools = AgentweaverApiTools.Build(ProjectId, AgentName, "http://localhost", null, http, runId: RunId);

        tools.Select(t => t.Name).Should().Contain("start_preview");
    }

    [Fact]
    public void StartPreview_IsAbsent_WhenRunIdMissing()
    {
        var http = new HttpClient(new CapturingHandler(HttpStatusCode.OK, "{}"))
            { BaseAddress = new Uri("http://localhost/") };

        var withoutRun = AgentweaverApiTools.Build(ProjectId, AgentName, "http://localhost", null, http);
        var withEmptyRun = AgentweaverApiTools.Build(ProjectId, AgentName, "http://localhost", null, http, runId: "");

        withoutRun.Select(t => t.Name).Should().NotContain("start_preview",
            because: "the tool is run-scoped and must not be offered without a bound runId");
        withEmptyRun.Select(t => t.Name).Should().NotContain("start_preview");
    }

    [Fact]
    public void StartPreview_Signature_ExposesOnlyPort()
    {
        var schema = GetStartPreview(new CapturingHandler(HttpStatusCode.OK, "{}")).JsonSchema.GetRawText();

        schema.Should().Contain("port", because: "the model supplies the port to expose");
        schema.Should().NotContain("target_port", because: "the wire DTO name must not leak into the tool schema");
        schema.Should().NotContain("runId", because: "runId is server-bound in the closure, never a model argument");
    }

    [Fact]
    public async Task StartPreview_PostsTargetPort_ToRunScopedPath()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK,
            """{"session_id":"tok","target_port":3000,"preview_url":"https://preview.example.com/p/tok"}""");
        var tool = GetStartPreview(handler);

        await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["port"] = 3000 }));

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastPath.Should().Be($"/api/runs/{RunId}/sandbox/preview");
        handler.LastBody.Should().Contain("\"target_port\":3000",
            because: "the new DTO binds snake_case target_port");
    }

    [Fact]
    public async Task StartPreview_OnSuccess_ReturnsPreviewUrl()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK,
            """{"session_id":"tok","target_port":3000,"preview_url":"https://preview.example.com/p/tok"}""");
        var tool = GetStartPreview(handler);

        var result = (await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["port"] = 3000 })))?.ToString() ?? "";

        result.Should().Contain("https://preview.example.com/p/tok",
            because: "the tool returns the approved preview URL back to the agent");
        result.Should().NotContain("failed:");
    }

    [Fact]
    public async Task StartPreview_OnDenied_ReturnsErrorStringWithoutThrowing()
    {
        var handler = new CapturingHandler(HttpStatusCode.Forbidden,
            """{"error":"Preview approval was denied or timed out."}""");
        var tool = GetStartPreview(handler);

        var act = async () => await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["port"] = 3000 }));
        await act.Should().NotThrowAsync();

        var result = (await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["port"] = 3000 })))?.ToString() ?? "";
        result.Should().Contain("start_preview failed:");
        result.Should().Contain("403");
    }
}

/// <summary>Fake handler that records the last request's method, path and body.</summary>
internal sealed class CapturingHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _body;

    public HttpMethod? LastMethod { get; private set; }
    public string? LastPath { get; private set; }
    public string LastBody { get; private set; } = string.Empty;

    public CapturingHandler(HttpStatusCode statusCode, string body)
    {
        _statusCode = statusCode;
        _body = body;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastMethod = request.Method;
        LastPath = request.RequestUri?.AbsolutePath;
        if (request.Content is not null)
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
