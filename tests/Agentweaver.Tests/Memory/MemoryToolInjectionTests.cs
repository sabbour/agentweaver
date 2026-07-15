using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentTools;
using Agentweaver.SandboxExec;
using Agentweaver.SandboxFs;

namespace Agentweaver.Tests.Memory;

/// <summary>
/// Regression guard for issue #335: the agent-facing memory and decision tools (record_memory,
/// get_memory, submit_decision, list_decisions, list_inbox, update_session, submit_inbox_entry) must
/// be injected into the agent's callable function schema during an orchestration run, alongside the
/// reporting tools — and the system prompt must reference the SAME registered tool names.
///
/// <para>
/// These are the native loopback tools built by <see cref="AgentweaverApiTools"/> (not the
/// standalone MCP-server tools in <c>MemoryTools.cs</c>, which use different names —
/// memory_record/memory_list/... — and are never wired into in-run agents). The injection is gated
/// on both projectId and agentName being non-empty; the live-run failure was that warm-pool pods
/// received neither, so the memory tools were silently omitted from <c>agent.tools</c>.
/// </para>
/// </summary>
public sealed class MemoryToolInjectionTests : IDisposable
{
    private static readonly string[] MemoryAndDecisionTools =
    [
        "record_memory",
        "get_memory",
        "submit_decision",
        "list_decisions",
        "list_inbox",
        "update_session",
        "submit_inbox_entry",
    ];

    private readonly string _workspace;

    public MemoryToolInjectionTests()
    {
        _workspace = Path.Combine(
            Directory.GetCurrentDirectory(), ".agentweaver-test-workspaces", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }

    [Fact]
    public void SessionTools_IncludeMemoryTools_WhenProjectAndAgentPresent()
    {
        var tools = CopilotAIAgent.BuildSessionConfigTools(
            BuildContext(),
            projectId: "project-335",
            agentName: "Stark",
            apiBaseUrl: "http://127.0.0.1:5000",
            apiKey: "test-key");

        var names = tools.Select(t => t.Name).ToList();

        names.Should().Contain(MemoryAndDecisionTools,
            "agents executing a subtask must be able to record and read memory during an orchestration run (#335)");
        // The reporting tools that DID show up in the failing live run must still be present too, so
        // memory tools are proven to be injected on the SAME path — not a separate one.
        names.Should().Contain(new[] { "report_intent", "report_outcome" });
    }

    [Fact]
    public void SessionTools_OmitMemoryTools_WhenProjectOrAgentMissing()
    {
        // Documents the exact gate that failed in the field: with projectId/agentName empty (the
        // warm-pool default before #335), the memory tools are not built. The real fix plumbs the
        // per-run projectId/agentName to the pod so this branch is not taken for orchestration runs.
        var tools = CopilotAIAgent.BuildSessionConfigTools(
            BuildContext(),
            projectId: null,
            agentName: null);

        tools.Select(t => t.Name).Should().NotContain(MemoryAndDecisionTools);
    }

    [Fact]
    public void BasePrompt_MemoryAndDecisionToolNames_MatchRegisteredNativeTools()
    {
        var registered = AgentweaverApiTools.ToolNames;
        var prompt = AgentBasePrompt.Base + AgentBasePrompt.TeamCoordination;

        // #335 root cause #2: every memory/decision tool the prompt instructs agents to call must be
        // an actually-registered native loopback tool name. The authority is the injected tool set,
        // so this locks the prompt and the tool registration together against future drift.
        string[] referenced = ["record_memory", "submit_decision", "list_decisions", "get_memory", "list_inbox"];
        foreach (var name in referenced)
        {
            prompt.Should().Contain(name, $"the system prompt is expected to instruct agents to use '{name}'");
            registered.Should().Contain(name,
                $"prompt references '{name}', so it must be a registered native tool (see AgentweaverApiTools)");
        }

        // Guard against reintroducing the standalone MCP-server names (MemoryTools.cs), which are
        // never injected into an in-run agent session and would silently break tool calls.
        string[] mcpOnlyNames = ["memory_record", "memory_list", "memory_get", "memory_search"];
        foreach (var mcpName in mcpOnlyNames)
            prompt.Should().NotContain(mcpName,
                $"'{mcpName}' is a standalone MCP-server tool not injected into agent sessions; " +
                "the prompt must reference the native loopback tool name instead");
    }

    private SandboxToolContext BuildContext() => new(
        AgentId: "test-agent",
        WorkingDirectory: _workspace,
        SandboxRoot: _workspace,
        Executor: SandboxExecutorFactory.CreatePassthrough(),
        FileTools: new SandboxedFileTools(_workspace),
        SearchTools: new SandboxedSearchTools(_workspace),
        Redactor: SandboxOutputRedactor.Default,
        Options: new SandboxToolOptions(ShellEnabled: false),
        Logger: NullLogger.Instance);
}
