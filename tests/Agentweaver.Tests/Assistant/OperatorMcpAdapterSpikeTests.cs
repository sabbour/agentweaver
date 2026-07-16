using System.ComponentModel;
using System.Net.Sockets;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.AgentRuntime;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Agentweaver.Tests.Assistant;

/// <summary>
/// Spike (#346) end-to-end proof for the operator-assistant MCP tool adapter.
///
/// It stands up a REAL in-process MCP server (streamable-HTTP, stateless — the same transport shape
/// as apps/Agentweaver.Mcp) on a loopback port, then drives it through the production
/// <see cref="AgentweaverMcpToolProvider"/>. This exercises the genuine ModelContextProtocol C# client
/// over the wire (initialize + tools/list + tools/call), not a mock, and verifies:
///   1. tools/list returns every server tool, adapted to Microsoft.Extensions.AI AIFunctions;
///   2. a tools/call round-trips correctly through AIFunction.InvokeAsync;
///   3. the caller's GitHub bearer token is passed through on each call (the whoami tool echoes the
///      Authorization header the server actually received);
///   4. those MCP tools drop straight into a Copilot SessionConfig via
///      <see cref="OperatorAssistantAgent.BuildSessionConfig"/> (the wiring the real assistant uses).
/// </summary>
public sealed class OperatorMcpAdapterSpikeTests : IAsyncLifetime
{
    private WebApplication? _server;
    private Uri _mcpEndpoint = null!;

    public async Task InitializeAsync()
    {
        var port = GetFreeLoopbackPort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();

        // Same transport contract as the production MCP server: streamable HTTP, stateless so the
        // inbound HttpContext (and its Authorization header) is visible to tool execution.
        builder.Services
            .AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<SpikeMcpTools>();

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        _server = app;
        _mcpEndpoint = new Uri($"http://127.0.0.1:{port}/mcp");
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
    }

    [Fact]
    public async Task ToolsList_ReturnsAllServerTools_AdaptedToAIFunctions()
    {
        var provider = new AgentweaverMcpToolProvider(new AgentweaverMcpConnectionOptions { Endpoint = _mcpEndpoint });

        await using var session = await provider.ConnectAsync("caller-token-list", CancellationToken.None);

        session.Tools.Should().HaveCount(2, "the spike server exposes exactly spike_echo and spike_whoami");
        session.Tools.Select(t => t.Name).Should().Contain(new[] { "spike_echo", "spike_whoami" });
        session.Tools.Should().AllBeAssignableTo<AIFunction>(
            "McpClientTool derives from AIFunction so the Copilot SDK can invoke it directly");
    }

    [Fact]
    public async Task ToolCall_RoundTripsThroughAIFunctionInvoke()
    {
        var provider = new AgentweaverMcpToolProvider(new AgentweaverMcpConnectionOptions { Endpoint = _mcpEndpoint });
        await using var session = await provider.ConnectAsync("caller-token-echo", CancellationToken.None);

        var echo = session.Tools.Single(t => t.Name == "spike_echo");
        var result = await echo.InvokeAsync(
            new AIFunctionArguments { ["message"] = "hello-mcp" },
            CancellationToken.None);

        JsonSerializer.Serialize(result).Should().Contain("echo:hello-mcp",
            "invoking the adapted AIFunction must issue a real tools/call and return the server result");
    }

    [Fact]
    public async Task ToolCall_PassesThroughCallerBearerToken_PerCall()
    {
        const string callerToken = "caller-token-passthrough-xyz";
        var provider = new AgentweaverMcpToolProvider(new AgentweaverMcpConnectionOptions { Endpoint = _mcpEndpoint });
        await using var session = await provider.ConnectAsync(callerToken, CancellationToken.None);

        var whoami = session.Tools.Single(t => t.Name == "spike_whoami");
        var result = await whoami.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        JsonSerializer.Serialize(result).Should().Contain(callerToken,
            "the server must receive the caller's bearer on the tools/call HTTP request (per-call passthrough)");
    }

    [Fact]
    public async Task McpTools_DropIntoOperatorSessionConfig()
    {
        var provider = new AgentweaverMcpToolProvider(new AgentweaverMcpConnectionOptions { Endpoint = _mcpEndpoint });
        await using var session = await provider.ConnectAsync("caller-token-session", CancellationToken.None);

        var config = OperatorAssistantAgent.BuildSessionConfig(
            conversationId: "conv-1",
            systemPrompt: OperatorAssistantAgent.BuildSystemPromptForTests("# Agentweaver Driver", session.Tools.Count),
            tools: session.AsToolDeclarations(),
            modelId: "claude-sonnet-4.6");

        config.Tools.Should().NotBeNull();
        config.Tools!.Should().HaveCount(2, "the MCP tool set must flow into SessionConfig.Tools unchanged");
        config.EnableConfigDiscovery.Should().BeFalse();
        config.Tools!.Select(t => t.Name).Should().Contain("spike_echo");
    }

    [Fact]
    public async Task OperatorSessionConfig_RestrictsToolSurfaceToMcpToolsOnly_NoSdkBuiltins()
    {
        // SECURITY regression (#346): the operator assistant runs in-process in the API pod with no
        // OS-level sandbox, so the SDK's built-in native tools (bash/shell, view/read, write,
        // str_replace_editor, grep, web_fetch, …) must be removed from its tool surface. The config
        // must (a) allowlist ONLY the MCP tool names via AvailableTools — "only these tools will be
        // available when specified" — and (b) install a deny-by-default permission handler so any
        // native shell/file/URL request that reaches the permission layer is rejected.
        var provider = new AgentweaverMcpToolProvider(new AgentweaverMcpConnectionOptions { Endpoint = _mcpEndpoint });
        await using var session = await provider.ConnectAsync("caller-token-sandbox", CancellationToken.None);

        var mcpToolNames = session.Tools.Select(t => t.Name).ToArray();

        var config = OperatorAssistantAgent.BuildSessionConfig(
            conversationId: "conv-sandbox",
            systemPrompt: OperatorAssistantAgent.BuildSystemPromptForTests("# Agentweaver Driver", session.Tools.Count),
            tools: session.AsToolDeclarations(),
            modelId: "claude-sonnet-4.6");

        config.AvailableTools.Should().NotBeNull("the session must whitelist a tool surface, not inherit SDK built-ins");
        config.AvailableTools.Should().BeEquivalentTo(mcpToolNames,
            "AvailableTools must contain exactly the MCP tool names so every SDK built-in (bash/view/write/…) is excluded");
        config.AvailableTools.Should().NotContain(new[] { "bash", "shell", "view", "read", "write", "str_replace_editor", "grep", "web_fetch" },
            "no SDK built-in native tool name may appear in the operator assistant tool surface");
        config.OnPermissionRequest.Should().NotBeNull("a deny-by-default backstop must reject native tool requests");

        // Exercise the backstop directly: native shell/read/write requests are rejected.
        var shellDecision = await config.OnPermissionRequest!(new PermissionRequestShell
        {
            Kind = "shell",
            CanOfferSessionApproval = false,
            Commands = [],
            FullCommandText = "bash -lc 'id'",
            HasWriteFileRedirection = false,
            Intention = "run a command",
            PossiblePaths = [],
            PossibleUrls = [],
        }, null!);
        shellDecision.Should().BeOfType<PermissionDecisionReject>("native shell execution must be denied");

        var readDecision = await config.OnPermissionRequest!(new PermissionRequestRead
        {
            Kind = "read",
            Intention = "read a file",
            Path = "/etc/passwd",
        }, null!);
        readDecision.Should().BeOfType<PermissionDecisionReject>("native file read must be denied");

        var writeDecision = await config.OnPermissionRequest!(new PermissionRequestWrite
        {
            Kind = "write",
            CanOfferSessionApproval = false,
            Diff = "+ pwned",
            FileName = "/tmp/pwned",
            Intention = "write a file",
        }, null!);
        writeDecision.Should().BeOfType<PermissionDecisionReject>("native file write must be denied");
    }

    [Fact]
    public async Task Connect_RejectsMissingCallerToken()
    {
        var provider = new AgentweaverMcpToolProvider(new AgentweaverMcpConnectionOptions { Endpoint = _mcpEndpoint });

        var act = () => provider.ConnectAsync("   ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>(
            "the assistant must never connect without the signed-in user's token");
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Minimal MCP tool surface for the spike. spike_whoami reflects the received bearer.</summary>
    [McpServerToolType]
    public sealed class SpikeMcpTools(IHttpContextAccessor httpContextAccessor)
    {
        [McpServerTool(Name = "spike_echo"), Description("Echoes the message back.")]
        public string Echo([Description("Message to echo")] string message) => $"echo:{message}";

        [McpServerTool(Name = "spike_whoami"), Description("Returns the bearer token the server received for THIS call.")]
        public string WhoAmI()
        {
            var auth = httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString() ?? string.Empty;
            const string prefix = "Bearer ";
            return auth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? auth[prefix.Length..].Trim()
                : "<none>";
        }
    }
}
