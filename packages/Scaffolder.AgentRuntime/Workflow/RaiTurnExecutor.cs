using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.Domain;
using Scaffolder.SandboxExec;

namespace Scaffolder.AgentRuntime.Workflow;

/// <summary>
/// Post-work, pre-ship Responsible AI gate. Runs the Rai built-in agent against the
/// produced diff and maps a RED verdict to <see cref="AgentTurnOutput.ContentSafetyFlagged"/>
/// so the workflow routes to the content-safety terminal. Its charter is read dynamically
/// from <c>.squad/agents/rai/charter.md</c>.
/// Best-effort: exceptions log a warning and pass the original output through unchanged.
/// </summary>
public sealed class RaiTurnExecutor : Executor<AgentTurnOutput, AgentTurnOutput>
{
    private const string FallbackCharter =
        "You are Rai — the Responsible AI reviewer. Review the provided diff for security " +
        "vulnerabilities, harmful content, PII exposure, and ethical concerns. Issue a verdict: " +
        "GREEN (no issues), YELLOW (minor concerns), or RED (critical violation that must block shipping).";

    private readonly GitHubCopilotClientFactory _copilotClientFactory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly ISandboxExecutor _sandboxExecutor;
    private readonly ISandboxPolicyStore _sandboxPolicyStore;
    private readonly IShellApprovalStore _approvalStore;
    private readonly IToolApprovalGate _toolApprovalGate;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RaiTurnExecutor> _logger;
    private readonly Func<string, ChannelWriter<RunEvent>?> _getRecordingWriter;

    public RaiTurnExecutor(
        GitHubCopilotClientFactory copilotClientFactory,
        IGitHubTokenScopeProvider scopeProvider,
        ISandboxExecutor sandboxExecutor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        ILoggerFactory loggerFactory,
        Func<string, ChannelWriter<RunEvent>?>? getRecordingWriter = null,
        string name = "rai-turn")
        : base(name)
    {
        _copilotClientFactory = copilotClientFactory;
        _scopeProvider = scopeProvider;
        _sandboxExecutor = sandboxExecutor;
        _sandboxPolicyStore = sandboxPolicyStore;
        _approvalStore = approvalStore;
        _toolApprovalGate = toolApprovalGate;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RaiTurnExecutor>();
        _getRecordingWriter = getRecordingWriter ?? (_ => null);
    }

    public override async ValueTask<AgentTurnOutput> HandleAsync(
        AgentTurnOutput input, IWorkflowContext context, CancellationToken ct)
    {
        // Already flagged upstream, or nothing to review — pass through untouched.
        if (input is null || input.ContentSafetyFlagged || string.IsNullOrEmpty(input.Diff))
        {
            if (input is not null)
                WorkflowStepEvents.Emit(_getRecordingWriter(input.RunId), _logger, "rai", "skipped", "RAI review");
            return input!;
        }

        var writer = _getRecordingWriter(input.RunId);
        WorkflowStepEvents.Emit(writer, _logger, "rai", "started", "RAI review");

        RaiAIAgent? agent = null;
        try
        {
            var reviewPath = !string.IsNullOrEmpty(input.WorktreePath)
                ? input.WorktreePath
                : input.RepositoryPath;

            var charter = BuiltInCharterResolver.Resolve(reviewPath, "rai") ?? FallbackCharter;

            var task = $$"""
                You are Rai, the Responsible AI reviewer. A project run has just produced changes.
                Review the diff below for security vulnerabilities, harmful content, PII exposure,
                and ethical concerns.

                Run: {{input.RunId}}

                --- BEGIN DIFF ---
                {{input.Diff}}
                --- END DIFF ---

                Issue exactly one verdict on its own line:
                - GREEN  — no issues, safe to ship
                - YELLOW — minor concerns, ship with caution
                - RED    — critical violation that must block shipping

                Respond with your verdict and a one-sentence justification.
                """;

            agent = new RaiAIAgent(
                _copilotClientFactory,
                _scopeProvider,
                _sandboxExecutor,
                _sandboxPolicyStore,
                _approvalStore,
                _toolApprovalGate,
                _loggerFactory.CreateLogger<CopilotAIAgent>());

            await agent.SetupAsync(
                workingDirectory: reviewPath,
                repositoryPath: input.RepositoryPath,
                runId: input.RunId + "-rai",
                modelId: null,
                systemPromptContext: charter,
                streamWriter: null,
                ct).ConfigureAwait(false);

            var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            var response = await agent.ExecuteStreamingLoopAsync(task, session, ct).ConfigureAwait(false);

            if (IsRedVerdict(response))
            {
                _logger.LogWarning("Rai issued a RED verdict for run {RunId} — flagging content safety", input.RunId);
                WorkflowStepEvents.Emit(writer, _logger, "rai", "failed", "RAI review");
                return input with { ContentSafetyFlagged = true };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Rai RAI review failed for run {RunId} — workflow proceeds normally", input.RunId);
            WorkflowStepEvents.Emit(writer, _logger, "rai", "failed", "RAI review");
        }
        finally
        {
            if (agent is not null)
                await agent.DisposeAsync().ConfigureAwait(false);
        }

        WorkflowStepEvents.Emit(writer, _logger, "rai", "completed", "RAI review");
        return input;
    }

    private static bool IsRedVerdict(string? response)
    {
        if (string.IsNullOrEmpty(response)) return false;
        return response.Contains("🔴", StringComparison.Ordinal)
            || response.Contains("red verdict", StringComparison.OrdinalIgnoreCase)
            || response.Contains("RED", StringComparison.OrdinalIgnoreCase);
    }
}
