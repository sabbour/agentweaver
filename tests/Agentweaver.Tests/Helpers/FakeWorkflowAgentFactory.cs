using System.Threading.Channels;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Test <see cref="IWorkflowAgentFactory"/> that produces fake workflow agents which never
/// touch the GitHub Copilot SDK. The worker agent funnels into the supplied
/// <see cref="TestFileEditAgentRunner"/> so tests can drive its <c>Mode</c> and observe
/// <c>InvocationCount</c>/<c>LastTask</c>. Rai, Rubberduck, BuildTest, and Scribe agents are inert (Rai always returns
/// GREEN, Rubberduck/BuildTest PASS, Scribe is a no-op) so the end-to-end workflow completes deterministically
/// without their turns interfering with the worker-agent assertions.
/// </summary>
public sealed class FakeWorkflowAgentFactory : IWorkflowAgentFactory
{
    private readonly TestFileEditAgentRunner _runner;

    public FakeWorkflowAgentFactory(TestFileEditAgentRunner runner) => _runner = runner;

    internal FakeWorkflowTurnAgent? LastWorkerAgent { get; private set; }
    internal FakeWorkflowTurnAgent? LastRaiAgent { get; private set; }
    internal FakeWorkflowTurnAgent? LastRubberduckAgent { get; private set; }
    internal FakeWorkflowTurnAgent? LastBuildTestAgent { get; private set; }
    internal FakeWorkflowTurnAgent? LastScribeAgent { get; private set; }
    internal FakeAgentRole? ProviderFailureRole { get; set; }

    public IWorkflowTurnAgent CreateWorkerAgent() =>
        LastWorkerAgent = Create(FakeAgentRole.Worker);

    public IWorkflowTurnAgent CreateRaiAgent() =>
        LastRaiAgent = Create(FakeAgentRole.Rai);

    public IWorkflowTurnAgent CreateRubberduckAgent() =>
        LastRubberduckAgent = Create(FakeAgentRole.Rubberduck);

    public IWorkflowTurnAgent CreateBuildTestAgent() =>
        LastBuildTestAgent = Create(FakeAgentRole.BuildTest);

    public IWorkflowTurnAgent CreateScribeAgent() =>
        LastScribeAgent = Create(FakeAgentRole.Scribe);

    private FakeWorkflowTurnAgent Create(FakeAgentRole role) =>
        new(role, _runner, ProviderFailureRole == role);
}

internal enum FakeAgentRole
{
    Worker,
    Rai,
    Rubberduck,
    BuildTest,
    Scribe,
}

/// <summary>
/// A fake <see cref="IWorkflowTurnAgent"/> used by integration/security tests. The worker role
/// delegates to <see cref="TestFileEditAgentRunner"/> (real file/git operations in the worktree);
/// Rai returns GREEN, Rubberduck/BuildTest return PASS, and Scribe returns an empty result.
/// </summary>
internal sealed class FakeWorkflowTurnAgent : IWorkflowTurnAgent, IProviderBoundWorkflowTurnAgent
{
    private readonly FakeAgentRole _role;
    private readonly TestFileEditAgentRunner _runner;

    private string _workingDirectory = "";
    private string _repositoryPath = "";
    private string _runId = "";
    private string? _modelId;
    private string? _systemPromptContext;
    private ChannelWriter<RunEvent>? _stream;
    private readonly bool _throwProviderFailure;

    internal ModelSource? ProviderModelSource { get; private set; }
    internal string? ByokProviderFingerprint { get; private set; }

    public FakeWorkflowTurnAgent(
        FakeAgentRole role,
        TestFileEditAgentRunner runner,
        bool throwProviderFailure = false)
    {
        _role = role;
        _runner = runner;
        _throwProviderFailure = throwProviderFailure;
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
        CancellationToken ct,
        string? userId = null)
    {
        _workingDirectory = workingDirectory;
        _repositoryPath = repositoryPath;
        _runId = runId;
        _modelId = modelId;
        _systemPromptContext = systemPromptContext;
        _stream = streamWriter;
        return Task.CompletedTask;
    }

    public Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken ct)
    {
        if (_throwProviderFailure)
        {
            throw new AgentProviderException(
                ModelSource.Byok,
                AgentProviderFailureKind.Configuration,
                "byok_provider_configuration_mismatch",
                "The accepted BYOK provider configuration changed.",
                isRetryable: false);
        }

        return _role switch
        {
        // Worker funnels into the shared TestFileEditAgentRunner so Mode/InvocationCount/LastTask
        // behave exactly as before the AIAgent migration. A ContentSafety mode throws here, which
        // AgentTurnExecutor catches via IsContentSafetyViolation.
        FakeAgentRole.Worker => _runner.ExecuteAsync(
            task, _workingDirectory, _repositoryPath, ModelSource.GitHubCopilot,
            _runId, _modelId, _stream, ct, _systemPromptContext),

        // Rai must NOT touch the shared runner (it would corrupt LastTask/InvocationCount). A GREEN
        // verdict (with the machine-readable VERDICT: sentinel) lets the workflow proceed to the
        // review gate.
        FakeAgentRole.Rai => Task.FromResult("GREEN — no issues, safe to ship.\nVERDICT: GREEN"),

        // Rubberduck is also isolated from the shared runner. PASS lets injected policy gates proceed.
        FakeAgentRole.Rubberduck => Task.FromResult("PASS — critique complete, no changes requested."),

        FakeAgentRole.BuildTest => Task.FromResult("APPROVED — build and test gate passed."),

        // Scribe is a silent no-op in tests.
            _ => Task.FromResult(string.Empty),
        };
    }

    public void ConfigureProviderBoundary(ModelSource modelSource, string? byokProviderFingerprint)
    {
        ProviderModelSource = modelSource;
        ByokProviderFingerprint = byokProviderFingerprint;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
