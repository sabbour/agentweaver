using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Agentweaver.AgentRuntime;

namespace Agentweaver.Tests.Memory;

/// <summary>
/// Unit tests for <see cref="AgentweaverApiTools"/> tool behavior.
/// Uses a fake <see cref="HttpMessageHandler"/> injected via the <c>httpClientOverride</c>
/// parameter so no real server is needed. Validates that every tool returns a clear,
/// non-throwing string for 409 and 5xx — never surfaces an opaque "Tool execution failed".
/// </summary>
public sealed class AgentweaverApiToolsTests
{
    private const string ProjectId = "test-project-id";
    private const string AgentName = "test-agent";

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>Builds a single named AIFunction from AgentweaverApiTools using a stub handler.</summary>
    private static AIFunction GetTool(string toolName, FakeHttpHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var tools = AgentweaverApiTools.Build(ProjectId, AgentName, "http://localhost", null, http);
        return tools.Single(t => t.Name == toolName);
    }

    private static async Task<string> InvokeAsync(AIFunction tool, Dictionary<string, object?> args)
    {
        var result = await tool.InvokeAsync(new AIFunctionArguments(args));
        return result?.ToString() ?? string.Empty;
    }

    // =========================================================================
    // submit_decision — 2xx success
    // =========================================================================
    [Fact]
    public async Task SubmitDecision_OnSuccess_ReturnsSuccessString()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.Created,
            """{"id":1,"slug":"test-slug","status":"pending"}""");
        var tool = GetTool("submit_decision", handler);

        var result = await InvokeAsync(tool, new()
        {
            ["slug"]    = "test-slug",
            ["type"]    = "architectural",
            ["title"]   = "My Decision",
            ["content"] = "The decision content",
        });

        result.Should().Contain("My Decision",
            because: "2xx must return the success message with the decision title");
        result.Should().NotContain("failed:", because: "2xx must not look like an error");
    }

    // =========================================================================
    // submit_decision — 409 Conflict (already merged/rejected slug)
    // =========================================================================
    [Fact]
    public async Task SubmitDecision_On409_ReturnsFriendlyMessageWithoutThrowing()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.Conflict,
            """{"error":"Entry has already been merged or rejected."}""");
        var tool = GetTool("submit_decision", handler);

        var act = async () => await InvokeAsync(tool, new()
        {
            ["slug"]    = "aks-agent-typescript-runtime",
            ["type"]    = "architectural",
            ["title"]   = "Use TypeScript runtime",
            ["content"] = "We will use the TypeScript AKS agent runtime.",
        });

        // Must NOT throw — the agent must receive a string result, not an exception.
        await act.Should().NotThrowAsync("409 is a recoverable idempotency signal, not a hard failure");
        var result = await InvokeAsync(tool, new()
        {
            ["slug"]    = "aks-agent-typescript-runtime",
            ["type"]    = "architectural",
            ["title"]   = "Use TypeScript runtime",
            ["content"] = "We will use the TypeScript AKS agent runtime.",
        });
        result.Should().Contain("already recorded",
            because: "409 must surface as an informational 'already recorded' message");
        result.Should().NotContain("failed:",
            because: "409 is NOT an error — the decision was already captured");
    }

    // =========================================================================
    // submit_decision — 500 Internal Server Error
    // =========================================================================
    [Fact]
    public async Task SubmitDecision_On5xx_ReturnsErrorStringWithoutThrowing()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.InternalServerError,
            """{"error":"Database locked"}""");
        var tool = GetTool("submit_decision", handler);

        var act = async () => await InvokeAsync(tool, new()
        {
            ["slug"]    = "some-decision",
            ["type"]    = "architectural",
            ["title"]   = "Some decision",
            ["content"] = "Content",
        });

        await act.Should().NotThrowAsync("5xx must not propagate as an exception");
        var result = await InvokeAsync(tool, new()
        {
            ["slug"]    = "some-decision",
            ["type"]    = "architectural",
            ["title"]   = "Some decision",
            ["content"] = "Content",
        });
        result.Should().Contain("500", because: "error string must include the status code");
        result.Should().Contain("submit_decision failed:",
            because: "error string must identify which tool failed");
    }

    // =========================================================================
    // submit_inbox_entry — 409 returns friendly message (same inbox endpoint)
    // =========================================================================
    [Fact]
    public async Task SubmitInboxEntry_On409_ReturnsFriendlyMessageWithoutThrowing()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.Conflict,
            """{"error":"Entry has already been merged or rejected."}""");
        var tool = GetTool("submit_inbox_entry", handler);

        var result = await InvokeAsync(tool, new()
        {
            ["slug"]    = "already-merged-entry",
            ["type"]    = "learning",
            ["title"]   = "Something we learned",
            ["content"] = "The content",
        });

        result.Should().Contain("already recorded",
            because: "409 on submit_inbox_entry should also surface as 'already recorded'");
        result.Should().NotContain("failed:");
    }

    // =========================================================================
    // record_memory — 4xx error returns non-throwing error string
    // =========================================================================
    [Fact]
    public async Task RecordMemory_OnBadRequest_ReturnsErrorStringWithoutThrowing()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.BadRequest,
            """{"error":"type is required"}""");
        var tool = GetTool("record_memory", handler);

        var act = async () => await InvokeAsync(tool, new()
        {
            ["type"]       = "learning",
            ["importance"] = "high",
            ["content"]    = "Test memory",
        });

        await act.Should().NotThrowAsync();
        var result = await InvokeAsync(tool, new()
        {
            ["type"]       = "learning",
            ["importance"] = "high",
            ["content"]    = "Test memory",
        });
        result.Should().Contain("record_memory failed:");
        result.Should().Contain("400");
    }

    // =========================================================================
    // export_memory — 2xx success
    // =========================================================================
    [Fact]
    public async Task ExportMemory_OnSuccess_ReturnsSuccessString()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "{}");
        var tool = GetTool("export_memory", handler);

        var result = await InvokeAsync(tool, new());

        result.Should().Contain("exported", because: "2xx must return success message");
    }

    // =========================================================================
    // list_inbox — non-2xx returns error string without throwing
    // =========================================================================
    [Fact]
    public async Task ListInbox_On404_ReturnsErrorStringWithoutThrowing()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.NotFound, """{"error":"Project not found"}""");
        var tool = GetTool("list_inbox", handler);

        var act = async () => await InvokeAsync(tool, new());
        await act.Should().NotThrowAsync();

        var result = await InvokeAsync(tool, new());
        result.Should().Contain("list_inbox failed:");
        result.Should().Contain("404");
    }
}

/// <summary>Fake HttpMessageHandler returning a fixed status code and body for every request.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _body;

    public FakeHttpHandler(HttpStatusCode statusCode, string body)
    {
        _statusCode = statusCode;
        _body       = body;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
        });
    }
}
