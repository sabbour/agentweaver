using FluentAssertions;
using Microsoft.Extensions.AI;
using Agentweaver.Api.Runs;
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

    [Theory]
    [InlineData("CopilotAIAgent")]
    [InlineData("GitHubCopilotAgentRunner")]
    public void FinalPrompt_WithoutMemoryTools_OmitsMemorySection(string path)
    {
        var tools = GitHubCopilotAgentRunner.BuildSessionConfigTools(BuildContext());
        var prompt = ComposeFinalPrompt(path, "child context", tools);

        prompt.Should().NotContain("## Project memory and coordination");
        prompt.Should().NotContainAny(AgentweaverApiTools.ToolNames);
        prompt.Should().Contain("WORKSPACE BOUNDARY");
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("child")]
    [InlineData("revision")]
    [InlineData("remote")]
    public void CopilotFinalPrompt_WithCompleteMemoryTools_NamesOnlyRegisteredTools_AndOneBoundary(string executionPath)
    {
        var tools = CopilotAIAgent.BuildSessionConfigTools(
            BuildContext(), "project-1239", "Morpheus", "http://127.0.0.1:5000", "test-key");
        var context = executionPath == "child"
            ? RunOrchestrator.ComposeChildSystemPrompt("Charter")
            : "Charter";
        var prompt = CopilotAIAgent.ComposeFinalPrompt(context, tools.Select(tool => tool.Name));

        prompt.Should().Contain("## Project memory and coordination");
        AssertMemoryNamesMatchCallableTools(prompt, tools);
        CountOccurrences(prompt, "WORKSPACE BOUNDARY").Should().Be(1);
        if (executionPath == "child")
            prompt.Should().Contain("## Deliverable files");
    }

    [Fact]
    public void GitHubCopilotRunnerFinalPrompt_WithCompleteMemoryTools_NamesOnlyRegisteredTools()
    {
        var tools = CopilotAIAgent.BuildSessionConfigTools(
            BuildContext(), "project-1239", "Morpheus", "http://127.0.0.1:5000", "test-key");
        var prompt = GitHubCopilotAgentRunner.ComposeFinalPrompt("Charter", tools.Select(tool => tool.Name));

        prompt.Should().Contain("## Project memory and coordination");
        AssertMemoryNamesMatchCallableTools(prompt, tools);
        CountOccurrences(prompt, "WORKSPACE BOUNDARY").Should().Be(1);
    }

    [Theory]
    [InlineData("CopilotAIAgent")]
    [InlineData("GitHubCopilotAgentRunner")]
    public void FinalPrompt_WithPartialMemoryTools_NamesOnlyTheCallableSubset(string path)
    {
        var completeTools = CopilotAIAgent.BuildSessionConfigTools(
            BuildContext(), "project-1239", "Morpheus", "http://127.0.0.1:5000", "test-key");
        var tools = completeTools.Where(tool => tool.Name is "record_memory" or "get_memory").ToList();
        var prompt = ComposeFinalPrompt(path, "Charter", tools);

        prompt.Should().Contain("## Project memory and coordination");
        AssertMemoryNamesMatchCallableTools(prompt, tools);
    }

    private static string ComposeFinalPrompt(string path, string? context, IEnumerable<AIFunction> tools) =>
        path == "CopilotAIAgent"
            ? CopilotAIAgent.ComposeFinalPrompt(context, tools.Select(tool => tool.Name))
            : GitHubCopilotAgentRunner.ComposeFinalPrompt(context, tools.Select(tool => tool.Name));

    private static void AssertMemoryNamesMatchCallableTools(string prompt, IEnumerable<AIFunction> tools)
    {
        var callable = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in AgentweaverApiTools.ToolNames)
        {
            if (callable.Contains(name))
                prompt.Should().Contain(name, $"'{name}' is registered in this final session tool set");
            else
                prompt.Should().NotContain(name, $"'{name}' is not callable in this final session tool set");
        }
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
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
