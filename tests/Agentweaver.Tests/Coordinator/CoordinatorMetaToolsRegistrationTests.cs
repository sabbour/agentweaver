using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentTools;
using Agentweaver.SandboxExec;
using Agentweaver.SandboxFs;

namespace Agentweaver.Tests.Coordinator;

public sealed class CoordinatorMetaToolsRegistrationTests : IDisposable
{
    private readonly string _workspace;

    public CoordinatorMetaToolsRegistrationTests()
    {
        _workspace = Path.Combine(Directory.GetCurrentDirectory(), ".agentweaver-test-workspaces", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
        try
        {
            var parent = Path.GetDirectoryName(_workspace);
            if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        catch { }
    }

    [Fact]
    public void CoordinatorSessionTools_IncludeAgentweaverMcpEquivalentMetaTools()
    {
        var tools = CopilotAIAgent.BuildSessionConfigTools(
            BuildContext(),
            projectId: "project-123",
            agentName: "Coordinator",
            apiBaseUrl: "http://127.0.0.1:5000",
            apiKey: "test-key");

        tools.Select(t => t.Name).Should().Contain(new[]
        {
            "project_get",
            "project_list_runs",
            "backlog_capture_task",
            "backlog_get_board",
            "run_status",
            "run_show_artifacts",
            "coordinator_work_plan_get",
            "coordinator_children_get",
            "orchestration_topology",
        }, "coordinator runs need the Agentweaver MCP-equivalent project meta surface");
    }

    [Fact]
    public void NonCoordinatorSessionTools_DoNotExposeCoordinatorMetaSurface()
    {
        var tools = CopilotAIAgent.BuildSessionConfigTools(
            BuildContext(),
            projectId: "project-123",
            agentName: "Worker",
            apiBaseUrl: "http://127.0.0.1:5000",
            apiKey: "test-key");

        tools.Select(t => t.Name).Should().NotContain(new[]
        {
            "project_get",
            "project_list_runs",
            "backlog_capture_task",
            "backlog_get_board",
            "run_status",
            "run_show_artifacts",
            "coordinator_work_plan_get",
            "coordinator_children_get",
            "orchestration_topology",
        }, "meta tools are intentionally scoped to the coordinator agent");
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
