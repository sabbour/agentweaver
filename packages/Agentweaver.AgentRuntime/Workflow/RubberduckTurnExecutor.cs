using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;

namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// Policy-injected rubber-duck critique gate. It reviews the produced diff and returns a
/// <see cref="WorkflowReviewDecision"/>: PASS maps to approved, REVISE maps to request-changes back to
/// the producer. Unparseable output is logged and treated as PASS so an advisory critique cannot hang a
/// run, while parseable REVISE remains a real executable loop.
/// </summary>
public sealed class RubberduckTurnExecutor : Executor<AgentTurnOutput, WorkflowReviewDecision>, IWorkflowNodeMeta
{
    public string LogicalNodeId { get; }
    public string DisplayLabel { get; }
    public string Role => "review";
    public string NodeType => "gate";
    public bool Hidden => false;
    public string NodeKind => "live";

    private const string FallbackCharter =
        "You are the Rubber-duck reviewer. Critique the proposed change for correctness, clarity, " +
        "missed edge cases, and obvious implementation mistakes. You may PASS or request a REVISE.";

    private readonly GitHubCopilotClientFactory _copilotClientFactory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly ISandboxExecutor _sandboxExecutor;
    private readonly ISandboxPolicyStore _sandboxPolicyStore;
    private readonly IShellApprovalStore _approvalStore;
    private readonly IToolApprovalGate _toolApprovalGate;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RubberduckTurnExecutor> _logger;
    private readonly Func<string, ChannelWriter<RunEvent>?> _getRecordingWriter;
    private readonly Func<string, string, ChannelWriter<RunEvent>>? _createSubStream;
    private readonly Action<string>? _completeSubStream;
    private readonly IWorkflowAgentFactory? _agentFactory;
    private readonly string _reviewAgentId;
    private readonly string? _reviewAgentCharter;

    public RubberduckTurnExecutor(
        GitHubCopilotClientFactory copilotClientFactory,
        IGitHubTokenScopeProvider scopeProvider,
        ISandboxExecutor sandboxExecutor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        ILoggerFactory loggerFactory,
        Func<string, ChannelWriter<RunEvent>?>? getRecordingWriter = null,
        string name = "rubberduck-turn",
        string logicalNodeId = "policy-rubberduck",
        string displayLabel = "Rubber-duck review",
        Func<string, string, ChannelWriter<RunEvent>>? createSubStream = null,
        Action<string>? completeSubStream = null,
        IWorkflowAgentFactory? agentFactory = null,
        string? reviewAgentId = null,
        string? reviewAgentCharter = null)
        : base(name)
    {
        LogicalNodeId = logicalNodeId;
        DisplayLabel = displayLabel;
        _copilotClientFactory = copilotClientFactory;
        _scopeProvider = scopeProvider;
        _sandboxExecutor = sandboxExecutor;
        _sandboxPolicyStore = sandboxPolicyStore;
        _approvalStore = approvalStore;
        _toolApprovalGate = toolApprovalGate;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RubberduckTurnExecutor>();
        _getRecordingWriter = getRecordingWriter ?? (_ => null);
        _createSubStream = createSubStream;
        _completeSubStream = completeSubStream;
        _agentFactory = agentFactory;
        _reviewAgentId = string.IsNullOrWhiteSpace(reviewAgentId) ? "rubberduck" : reviewAgentId.Trim();
        _reviewAgentCharter = string.IsNullOrWhiteSpace(reviewAgentCharter) ? null : reviewAgentCharter;
    }

    public override async ValueTask<WorkflowReviewDecision> HandleAsync(
        AgentTurnOutput input, IWorkflowContext context, CancellationToken ct)
    {
        if (input is null || string.IsNullOrEmpty(input.Diff))
        {
            if (input is not null)
                WorkflowStepEvents.Emit(_getRecordingWriter(input.RunId), _logger, input.RunId, LogicalNodeId, "skipped", DisplayLabel);
            return new WorkflowReviewDecision(Approved: true);
        }

        var writer = _getRecordingWriter(input.RunId);
        WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "started", DisplayLabel);

        var subRunId = input.RunId + "-rubberduck";
        var subWriter = _createSubStream?.Invoke(subRunId, "rubberduck");
        IWorkflowTurnAgent? agent = null;

        try
        {
            var reviewPath = !string.IsNullOrEmpty(input.WorktreePath)
                ? input.WorktreePath
                : input.RepositoryPath;
            var charter = _reviewAgentCharter
                ?? BuiltInCharterResolver.Resolve(reviewPath, _reviewAgentId)
                ?? BuiltInCharterResolver.Resolve(reviewPath, "rubberduck")
                ?? FallbackCharter;

            var task = $$"""
                You are the Rubber-duck reviewer. Critique the diff below before it can merge.

                Run: {{input.RunId}}

                --- BEGIN DIFF ---
                {{input.Diff}}
                --- END DIFF ---

                Issue exactly one verdict on its own line:
                - PASS   — the change is coherent enough to continue
                - REVISE — the producer should revise before continuing

                If your verdict is REVISE, provide concise, actionable feedback.
                """;

            agent = _agentFactory?.CreateRubberduckAgent()
                ?? new CopilotAIAgent(
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
                agentName: _reviewAgentId,
                apiBaseUrl: null,
                apiKey: null,
                ct).ConfigureAwait(false);

            var response = await agent.RunTurnAsync(task, isRevision: false, ct).ConfigureAwait(false);
            if (TryParseVerdict(response, out var revise))
            {
                if (revise)
                {
                    WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "revise", DisplayLabel);
                    return new WorkflowReviewDecision(
                        Approved: false,
                        RequestChanges: true,
                        Feedback: ExtractFeedback(response));
                }
            }
            else
            {
                _logger.LogWarning(
                    "Rubberduck verdict could not be parsed for run {RunId} — defaulting to PASS. Raw response (truncated): {Raw}",
                    input.RunId, Truncate(response));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rubberduck review failed for run {RunId} — defaulting to PASS", input.RunId);
        }
        finally
        {
            if (agent is not null)
                await agent.DisposeAsync().ConfigureAwait(false);
            _completeSubStream?.Invoke(subRunId);
        }

        WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "completed", DisplayLabel);
        return new WorkflowReviewDecision(Approved: true);
    }

    internal static bool TryParseVerdict(string? response, out bool revise)
    {
        revise = false;
        if (string.IsNullOrWhiteSpace(response)) return false;

        foreach (var rawLine in response.Split('\n'))
        {
            var line = StripLeadingMarkers(rawLine);
            if (StartsWithVerdictToken(line, "REVISE"))
            {
                revise = true;
                return true;
            }
            if (StartsWithVerdictToken(line, "PASS"))
            {
                revise = false;
                return true;
            }
        }

        return false;
    }

    private static string StripLeadingMarkers(string line)
    {
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (c is ' ' or '\t' or '\r' or '-' or '*' or '#' or '>' or '`')
                i++;
            else
                break;
        }
        return i > 0 ? line[i..] : line;
    }

    private static bool StartsWithVerdictToken(string line, string token)
    {
        if (!line.StartsWith(token, StringComparison.Ordinal))
            return false;
        if (line.Length == token.Length)
            return true;
        var next = line[token.Length];
        return !(char.IsLetterOrDigit(next) || next is '-' or '\'' or '_');
    }

    private static string ExtractFeedback(string? response)
    {
        if (string.IsNullOrEmpty(response)) return string.Empty;
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var feedbackLines = lines
            .SkipWhile(l => StripLeadingMarkers(l).StartsWith("REVISE", StringComparison.Ordinal))
            .ToArray();
        return feedbackLines.Length > 0 ? string.Join('\n', feedbackLines).Trim() : response.Trim();
    }

    private static string Truncate(string? response)
    {
        if (string.IsNullOrEmpty(response)) return string.Empty;
        const int max = 500;
        return response.Length <= max ? response : response[..max] + "…";
    }
}
