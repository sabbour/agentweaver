using System.Net;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentTools;
using Agentweaver.SandboxExec;
using Agentweaver.SandboxFs;
using Agentweaver.AgentRuntime;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// Unit tests for <see cref="PreviewPublishTool"/>, which builds the agent-initiated
/// <c>start_preview</c> tool. The tool POSTs <c>{ "target_port": N }</c> to
/// <c>api/runs/{runId}/sandbox/preview</c> and returns the <c>preview_url</c> from the response.
/// A capturing fake handler asserts the path/body without a real server.
/// </summary>
/// <remarks>
/// As of GitHub issue #334, <c>start_preview</c> is built exclusively via
/// <see cref="PreviewPublishTool.Build"/> and registered by
/// <c>PreviewRunnerToolProvider</c> (apps/Agentweaver.AgentHost/PreviewRunner.cs) — see
/// <c>PreviewRunnerToolProviderStartPreviewTests</c> in the Preview test folder for the
/// end-to-end lifecycle coverage. It is deliberately no longer part of
/// <see cref="AgentweaverApiTools.Build"/>, which used to gate it behind both
/// <c>projectId</c>/<c>agentName</c> being non-empty — a gate sandboxed subtask agents don't
/// satisfy, which is exactly what caused the dead end in #334.
/// </remarks>
public sealed class StartPreviewToolTests
{
    private const string ProjectId = "test-project-id";
    private const string AgentName = "tank";
    private const string RunId = "run-abc-123";

    private static AIFunction GetStartPreview(CapturingHandler handler, string runId = RunId)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return PreviewPublishTool.Build("http://localhost", null, runId, http);
    }

    [Fact]
    public void BuildSessionConfigTools_NeverIncludesStartPreview_RegardlessOfProjectAgentPresence()
    {
        using var workspace = new TempWorkspace();
        var context = new SandboxToolContext(
            AgentId: "qa-engineer",
            WorkingDirectory: workspace.Path,
            SandboxRoot: workspace.Path,
            Executor: SandboxExecutorFactory.CreatePassthrough(),
            FileTools: new SandboxedFileTools(workspace.Path),
            SearchTools: new SandboxedSearchTools(workspace.Path),
            Redactor: SandboxOutputRedactor.Default,
            Options: new SandboxToolOptions(ShellEnabled: false),
            Logger: NullLogger.Instance,
            RunId: RunId);

        var withProject = CopilotAIAgent.BuildSessionConfigTools(
            context, ProjectId, AgentName, "http://localhost", apiKey: null);
        var withoutProject = CopilotAIAgent.BuildSessionConfigTools(
            context, projectId: null, agentName: AgentName, apiBaseUrl: "http://localhost", apiKey: null);

        // start_preview is registered only by PreviewRunnerToolProvider (issue #334) — never by the
        // Agentweaver API tool set, so its presence must not depend on projectId/agentName at all.
        withProject.Select(t => t.Name).Should().NotContain("start_preview");
        withoutProject.Select(t => t.Name).Should().NotContain("start_preview");
    }

    [Fact]
    public void StartPreview_Signature_ExposesPortAndSessionId()
    {
        var schema = GetStartPreview(new CapturingHandler(HttpStatusCode.OK, "{}")).JsonSchema.GetRawText();

        schema.Should().Contain("port", because: "the model supplies the port to expose");
        schema.Should().NotContain("target_port", because: "the wire DTO name must not leak into the tool schema");
        schema.Should().Contain("session_id", because: "the observed process session is required to prove liveness before publication");`r`n        schema.Should().NotContain("runId", because: "runId is server-bound in the closure, never a model argument");
    }

    [Fact]
    public async Task StartPreview_PostsTargetPort_ToRunScopedPath()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK,
            """{"session_id":"tok","target_port":3000,"preview_url":"https://preview.example.com/p/tok"}""");
        var tool = GetStartPreview(handler);

        await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["port"] = 3000, ["session_id"] = "preview-session-1" }));

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
            new AIFunctionArguments(new Dictionary<string, object?> { ["port"] = 3000, ["session_id"] = "preview-session-1" })))?.ToString() ?? "";

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
            new AIFunctionArguments(new Dictionary<string, object?> { ["port"] = 3000, ["session_id"] = "preview-session-1" }));
        await act.Should().NotThrowAsync();

        var result = (await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["port"] = 3000, ["session_id"] = "preview-session-1" })))?.ToString() ?? "";
        result.Should().Contain("start_preview failed:");
        result.Should().Contain("403");
    }

    [Fact]
    public async Task StartPreview_OnFailure_LogsStructuredFailureEvent()
    {
        // GitHub issue #528: AgentHost pods are recycled shortly after a run completes, so a failed
        // tool call must leave a durable, queryable telemetry trail — this asserts the structured
        // fields (tool name, run id, port, status code) actually get logged, not just returned to
        // the model.
        var handler = new CapturingHandler(HttpStatusCode.Forbidden,
            """{"error":"Preview approval was denied or timed out."}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var logger = new CapturingLogger();

        var tool = PreviewPublishTool.Build("http://localhost", null, RunId, http, logger: logger);
        await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["port"] = 3000, ["session_id"] = "preview-session-1" }));

        logger.HasEntryMatching(LogLevel.Warning, "start_preview").Should().BeTrue(
            because: "a non-success HTTP response must produce a durable log entry naming the tool");
        logger.HasEntryContaining(RunId).Should().BeTrue(
            because: "the run id is required to correlate the failure back to a specific run");
        logger.HasEntryContaining("port=3000").Should().BeTrue(
            because: "the target port is required context to root-cause a start_preview failure");
        logger.HasEntryContaining("statusCode=403").Should().BeTrue(
            because: "the HTTP status code is required to distinguish e.g. 403 (denied) from 5xx (server error)");
    }

    [Fact]
    public async Task StartPreview_OnFailure_DoesNotLogSensitiveDataFromResponseBodyOrApiKey()
    {
        // Regression test: the response body may echo back sensitive values (a leaked token, an
        // Authorization header, etc.) — none of that may reach the durable telemetry sink. Also
        // assert the apiKey used to call the API is never logged, even though it's never part of
        // the response body in this scenario (it's carried as a request header only).
        const string fakeGitHubToken = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        const string fakeApiKey = "super-secret-api-key-do-not-log";
        var handler = new CapturingHandler(HttpStatusCode.Forbidden,
            $$"""{"error":"denied","Authorization":"Bearer {{fakeGitHubToken}}","token":"{{fakeGitHubToken}}"}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var logger = new CapturingLogger();

        var tool = PreviewPublishTool.Build("http://localhost", fakeApiKey, RunId, http, logger: logger);
        await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["port"] = 3000, ["session_id"] = "preview-session-1" }));

        logger.Entries.Should().NotBeEmpty();
        foreach (var entry in logger.Entries)
        {
            entry.Message.Should().NotContain(fakeGitHubToken,
                because: "GitHub tokens echoed in a response body must be redacted before logging");
            entry.Message.Should().NotContain(fakeApiKey,
                because: "the apiKey used to authenticate to the API must never be logged");
            entry.Message.Should().NotContain("Bearer " + fakeGitHubToken,
                because: "Authorization header-shaped values must be redacted before logging");
        }
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

internal sealed class TempWorkspace : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        Directory.GetCurrentDirectory(), ".agentweaver-test-workspaces", Guid.NewGuid().ToString("N"));

    public TempWorkspace() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch
        {
        }
    }
}

