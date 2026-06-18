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
public sealed class RaiTurnExecutor : Executor<AgentTurnOutput, AgentTurnOutput>, IWorkflowNodeMeta
{
    /// <inheritdoc />
    public string LogicalNodeId => "rai";
    /// <inheritdoc />
    public string DisplayLabel => "Rai";
    /// <inheritdoc />
    public string Role => "rai";
    /// <inheritdoc />
    public bool Hidden => false;
    /// <inheritdoc />
    public string NodeKind => "live";

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
    private readonly Func<string, string, ChannelWriter<RunEvent>>? _createSubStream;
    private readonly Action<string>? _completeSubStream;

    public RaiTurnExecutor(
        GitHubCopilotClientFactory copilotClientFactory,
        IGitHubTokenScopeProvider scopeProvider,
        ISandboxExecutor sandboxExecutor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        ILoggerFactory loggerFactory,
        Func<string, ChannelWriter<RunEvent>?>? getRecordingWriter = null,
        string name = "rai-turn",
        Func<string, string, ChannelWriter<RunEvent>>? createSubStream = null,
        Action<string>? completeSubStream = null)
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
        _createSubStream = createSubStream;
        _completeSubStream = completeSubStream;
    }

    public override async ValueTask<AgentTurnOutput> HandleAsync(
        AgentTurnOutput input, IWorkflowContext context, CancellationToken ct)
    {
        // Already flagged upstream, or nothing to review — pass through untouched.
        if (input is null || input.ContentSafetyFlagged || string.IsNullOrEmpty(input.Diff))
        {
            if (input is not null)
                WorkflowStepEvents.Emit(_getRecordingWriter(input.RunId), _logger, input.RunId, "rai", "skipped", "RAI review");
            return input!;
        }

        var writer = _getRecordingWriter(input.RunId);
        WorkflowStepEvents.Emit(writer, _logger, input.RunId, "rai", "started", "RAI review");

        var subRunId = input.RunId + "-rai";
        var subWriter = _createSubStream?.Invoke(subRunId, "rai");

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
                - REVISE — fixable issues found; the agent should revise before shipping (provide specific feedback)
                - RED    — critical violation that must block shipping entirely (e.g. credentials, PII, harmful content)

                Respond with your verdict and a clear explanation.
                If your verdict is REVISE, provide actionable feedback the agent can act on.
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
                runId: subRunId,
                modelId: null,
                systemPromptContext: charter,
                streamWriter: subWriter,
                projectId: null,
                agentName: null,
                apiBaseUrl: null,
                apiKey: null,
                ct).ConfigureAwait(false);

            var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            var response = await agent.ExecuteStreamingLoopAsync(task, session, ct).ConfigureAwait(false);

            if (IsRedVerdict(response))
            {
                _logger.LogWarning("Rai issued a RED verdict for run {RunId} — flagging content safety", input.RunId);
                subWriter?.TryWrite(new RunEvent(1, EventTypes.RaiVerdict, new { verdict = "red", runId = input.RunId }));
                WorkflowStepEvents.Emit(writer, _logger, input.RunId, "rai", "failed", "RAI review");
                _completeSubStream?.Invoke(subRunId);
                return input with { ContentSafetyFlagged = true };
            }

            if (IsReviseVerdict(response))
            {
                _logger.LogInformation("Rai issued a REVISE verdict for run {RunId} — requesting agent revision", input.RunId);
                subWriter?.TryWrite(new RunEvent(1, EventTypes.RaiVerdict, new { verdict = "revise", runId = input.RunId }));
                WorkflowStepEvents.Emit(writer, _logger, input.RunId, "rai", "revise", "RAI review");
                _completeSubStream?.Invoke(subRunId);
                return input with { RaiRevisionRequired = true, RaiFeedback = ExtractFeedback(response) };
            }

            var verdictLabel = DetermineVerdict(response);
            subWriter?.TryWrite(new RunEvent(2, EventTypes.RaiVerdict, new { verdict = verdictLabel, runId = input.RunId }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Rai RAI review failed for run {RunId} — workflow proceeds normally", input.RunId);
            WorkflowStepEvents.Emit(writer, _logger, input.RunId, "rai", "failed", "RAI review");
        }
        finally
        {
            if (agent is not null)
                await agent.DisposeAsync().ConfigureAwait(false);
            _completeSubStream?.Invoke(subRunId);
        }

        WorkflowStepEvents.Emit(writer, _logger, input.RunId, "rai", "completed", "RAI review");
        return input;
    }

    private static bool IsRedVerdict(string? response)
    {
        if (string.IsNullOrEmpty(response)) return false;
        return response.Contains("🔴", StringComparison.Ordinal)
            || response.Contains("red verdict", StringComparison.OrdinalIgnoreCase)
            || (response.Contains("RED", StringComparison.Ordinal)
                && !response.Contains("REVISE", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsReviseVerdict(string? response)
    {
        if (string.IsNullOrEmpty(response)) return false;
        return response.Contains("REVISE", StringComparison.OrdinalIgnoreCase)
            || response.Contains("revise verdict", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts feedback text from a REVISE response. Returns the full response if no
    /// structured feedback block is found — the agent will receive the entire Rai response.
    /// </summary>
    private static string ExtractFeedback(string? response)
    {
        if (string.IsNullOrEmpty(response)) return string.Empty;
        // Strip leading verdict line if present, return the rest as feedback.
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var feedbackLines = lines
            .SkipWhile(l => l.TrimStart().StartsWith("REVISE", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return feedbackLines.Length > 0
            ? string.Join('\n', feedbackLines).Trim()
            : response.Trim();
    }

    private static string DetermineVerdict(string? response)
    {
        if (string.IsNullOrEmpty(response)) return "unknown";
        if (IsRedVerdict(response)) return "red";
        if (IsReviseVerdict(response)) return "revise";
        if (response.Contains("🟡", StringComparison.Ordinal)
            || response.Contains("yellow", StringComparison.OrdinalIgnoreCase)
            || response.Contains("YELLOW", StringComparison.OrdinalIgnoreCase))
            return "yellow";
        return "green";
    }
}
