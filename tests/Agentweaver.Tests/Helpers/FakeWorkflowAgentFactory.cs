using System.Threading.Channels;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Test <see cref="IWorkflowAgentFactory"/> that produces fake workflow agents which never
/// touch the GitHub Copilot SDK. The worker agent funnels into the supplied
/// <see cref="TestFileEditAgentRunner"/> so tests can drive its <c>Mode</c> and observe
/// <c>InvocationCount</c>/<c>LastTask</c>. Rai, Rubberduck, and Scribe agents are inert (Rai always returns
/// GREEN, Rubberduck PASS, Scribe is a no-op) so the end-to-end workflow completes deterministically
/// without their turns interfering with the worker-agent assertions.
/// </summary>
public sealed class FakeWorkflowAgentFactory : IWorkflowAgentFactory
{
    private readonly TestFileEditAgentRunner _runner;

    public FakeWorkflowAgentFactory(TestFileEditAgentRunner runner) => _runner = runner;

    public IWorkflowTurnAgent CreateWorkerAgent() => new FakeWorkflowTurnAgent(FakeAgentRole.Worker, _runner);

    public IWorkflowTurnAgent CreateRaiAgent() => new FakeWorkflowTurnAgent(FakeAgentRole.Rai, _runner);

    public IWorkflowTurnAgent CreateRubberduckAgent() => new FakeWorkflowTurnAgent(FakeAgentRole.Rubberduck, _runner);

    public IWorkflowTurnAgent CreateScribeAgent() => new FakeWorkflowTurnAgent(FakeAgentRole.Scribe, _runner);
}

internal enum FakeAgentRole
{
    Worker,
    Rai,
    Rubberduck,
    Scribe,
}

/// <summary>
/// A fake <see cref="IWorkflowTurnAgent"/> used by integration/security tests. The worker role
/// delegates to <see cref="TestFileEditAgentRunner"/> (real file/git operations in the worktree);
/// Rai returns GREEN, Rubberduck returns PASS, and Scribe returns an empty result.
/// </summary>
internal sealed class FakeWorkflowTurnAgent : IWorkflowTurnAgent
{
    private readonly FakeAgentRole _role;
    private readonly TestFileEditAgentRunner _runner;

    private string _workingDirectory = "";
    private string _repositoryPath = "";
    private string _runId = "";
    private string? _modelId;
    private string? _systemPromptContext;
    private ChannelWriter<RunEvent>? _stream;

    public FakeWorkflowTurnAgent(FakeAgentRole role, TestFileEditAgentRunner runner)
    {
        _role = role;
        _runner = runner;
    }

    public Task SetupAsync(
        string workingDirectory,
        string repositoryPath,
        string runId,
        string? modelId,
        string? systemPromptContext,
        ChannelWriter<RunEvent>? streamWriter,
        string? projectId,
        string? agentName,
        string? apiBaseUrl,
        string? apiKey,
        CancellationToken ct)
    {
        _workingDirectory = workingDirectory;
        _repositoryPath = repositoryPath;
        _runId = runId;
        _modelId = modelId;
        _systemPromptContext = systemPromptContext;
        _stream = streamWriter;
        return Task.CompletedTask;
    }

    public Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken ct) => _role switch
    {
        // Worker funnels into the shared TestFileEditAgentRunner so Mode/InvocationCount/LastTask
        // behave exactly as before the AIAgent migration. A ContentSafety mode throws here, which
        // AgentTurnExecutor catches via IsContentSafetyViolation.
        FakeAgentRole.Worker => _runner.ExecuteAsync(
            task, _workingDirectory, _repositoryPath, ModelSource.GitHubCopilot,
            _runId, _modelId, _stream, ct, _systemPromptContext),

        // Rai must NOT touch the shared runner (it would corrupt LastTask/InvocationCount). A GREEN
        // verdict lets the workflow proceed to the review gate.
        FakeAgentRole.Rai => Task.FromResult("GREEN — no issues, safe to ship."),

        // Rubberduck is also isolated from the shared runner. PASS lets injected policy gates proceed.
        FakeAgentRole.Rubberduck => Task.FromResult("PASS — critique complete, no changes requested."),

        // Scribe is a silent no-op in tests.
        _ => Task.FromResult(string.Empty),
    };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
