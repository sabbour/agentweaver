using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;

namespace Agentweaver.AgentRuntime.Workflow;

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
    public string LogicalNodeId { get; }
    /// <inheritdoc />
    public string DisplayLabel { get; }
    /// <inheritdoc />
    public string Role => "rai";
    /// <inheritdoc />
    public string NodeType => "agent";
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
    private readonly IWorkflowAgentFactory? _agentFactory;
    private readonly string _subStreamSuffix;

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
        Action<string>? completeSubStream = null,
        IWorkflowAgentFactory? agentFactory = null,
        string logicalNodeId = "rai",
        string displayLabel = "Rai",
        string subStreamSuffix = "rai")
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
        _logger = loggerFactory.CreateLogger<RaiTurnExecutor>();
        _getRecordingWriter = getRecordingWriter ?? (_ => null);
        _createSubStream = createSubStream;
        _completeSubStream = completeSubStream;
        _agentFactory = agentFactory;
        _subStreamSuffix = subStreamSuffix;
    }

    public override async ValueTask<AgentTurnOutput> HandleAsync(
        AgentTurnOutput input, IWorkflowContext context, CancellationToken ct)
    {
        // Already flagged upstream, or nothing to review — pass through untouched.
        if (input is null || input.ContentSafetyFlagged || string.IsNullOrEmpty(input.Diff))
        {
            if (input is not null)
                WorkflowStepEvents.Emit(_getRecordingWriter(input.RunId), _logger, input.RunId, LogicalNodeId, "skipped", DisplayLabel);
            return input!;
        }

        var writer = _getRecordingWriter(input.RunId);
        WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "started", DisplayLabel);

        var subRunId = input.RunId + "-" + _subStreamSuffix;
        var subWriter = _createSubStream?.Invoke(subRunId, _subStreamSuffix);

        IWorkflowTurnAgent? agent = null;
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

            agent = _agentFactory?.CreateRaiAgent()
                ?? new RaiAIAgent(
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

            var response = await agent.RunTurnAsync(task, isRevision: false, ct).ConfigureAwait(false);

            if (!TryParseVerdict(response, out var verdict))
            {
                // Fail-open: an unparseable verdict must never block shipping, but it must be
                // observable so a genuine miss can be diagnosed.
                _logger.LogWarning(
                    "Rai verdict could not be parsed for run {RunId} — defaulting to GREEN (fail-open). Raw response (truncated): {Raw}",
                    input.RunId, Truncate(response));
            }

            if (verdict == RaiVerdict.Red)
            {
                _logger.LogWarning("Rai issued a RED verdict for run {RunId} — flagging content safety", input.RunId);
                subWriter?.TryWrite(new RunEvent(1, EventTypes.RaiVerdict, new { verdict = "red", runId = input.RunId }));
                WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "failed", DisplayLabel);
                _completeSubStream?.Invoke(subRunId);
                return input with { ContentSafetyFlagged = true };
            }

            if (verdict == RaiVerdict.Revise)
            {
                _logger.LogInformation("Rai issued a REVISE verdict for run {RunId} — requesting agent revision", input.RunId);
                subWriter?.TryWrite(new RunEvent(1, EventTypes.RaiVerdict, new { verdict = "revise", runId = input.RunId }));
                WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "revise", DisplayLabel);
                _completeSubStream?.Invoke(subRunId);
                return input with { RaiRevisionRequired = true, RaiFeedback = ExtractFeedback(response) };
            }

            var verdictLabel = ToLabel(verdict);
            subWriter?.TryWrite(new RunEvent(2, EventTypes.RaiVerdict, new { verdict = verdictLabel, runId = input.RunId }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Rai RAI review failed for run {RunId} — workflow proceeds normally", input.RunId);
            WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "failed", DisplayLabel);
        }
        finally
        {
            if (agent is not null)
                await agent.DisposeAsync().ConfigureAwait(false);
            _completeSubStream?.Invoke(subRunId);
        }

        WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "completed", DisplayLabel);
        return input;
    }

    /// <summary>
    /// The four verdicts the Rai reviewer may issue, ordered by escalating severity. Used as the
    /// precedence ranking when a response (defensively) appears to contain more than one verdict
    /// line: RED &gt; REVISE &gt; YELLOW &gt; GREEN, so the most conservative outcome wins.
    /// </summary>
    internal enum RaiVerdict
    {
        Green = 0,
        Yellow = 1,
        Revise = 2,
        Red = 3,
    }

    private static readonly (string Token, RaiVerdict Verdict)[] VerdictTokens =
    {
        ("RED", RaiVerdict.Red),
        ("REVISE", RaiVerdict.Revise),
        ("YELLOW", RaiVerdict.Yellow),
        ("GREEN", RaiVerdict.Green),
    };

    /// <summary>
    /// Robustly parses the Rai reviewer's declared verdict from its response. The reviewer is
    /// instructed (see the task prompt) to "Issue exactly one verdict on its own line:
    /// GREEN / YELLOW / REVISE / RED". This parser therefore inspects each line and only counts a
    /// verdict when the line <em>is</em> or <em>starts with</em> the uppercase token (optionally
    /// after a leading bullet / markdown marker) followed by a word boundary, or contains an
    /// unambiguous verdict emoji (🔴 RED / 🟡 YELLOW). Mid-sentence or hyphenated prose mentions
    /// (e.g. "no RED-level issues") are deliberately NOT treated as verdicts, which fixes the
    /// false-positive that flagged benign GREEN responses as RED. When multiple verdict lines are
    /// present the highest-severity one wins (RED &gt; REVISE &gt; YELLOW &gt; GREEN).
    /// </summary>
    /// <returns>
    /// <c>true</c> when an explicit verdict line/emoji was found (<paramref name="verdict"/> holds
    /// it). <c>false</c> when no verdict could be parsed — <paramref name="verdict"/> is then set to
    /// <see cref="RaiVerdict.Green"/> (fail-open: an unparseable advisory check must not block
    /// shipping) and the caller should log the miss.
    /// </returns>
    internal static bool TryParseVerdict(string? response, out RaiVerdict verdict)
    {
        verdict = RaiVerdict.Green;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        var found = false;
        var best = RaiVerdict.Green;
        foreach (var rawLine in response.Split('\n'))
        {
            if (!TryParseVerdictLine(rawLine, out var lineVerdict))
                continue;

            if (!found || lineVerdict > best)
                best = lineVerdict;
            found = true;
        }

        verdict = best;
        return found;
    }

    /// <summary>Convenience wrapper that applies the fail-open default (GREEN) when unparseable.</summary>
    internal static RaiVerdict ParseVerdict(string? response) =>
        TryParseVerdict(response, out var verdict) ? verdict : RaiVerdict.Green;

    private static bool TryParseVerdictLine(string rawLine, out RaiVerdict verdict)
    {
        // Emoji verdicts are unambiguous markers and may appear anywhere on the line.
        if (rawLine.Contains("🔴", StringComparison.Ordinal))
        {
            verdict = RaiVerdict.Red;
            return true;
        }
        if (rawLine.Contains("🟡", StringComparison.Ordinal))
        {
            verdict = RaiVerdict.Yellow;
            return true;
        }

        var line = StripLeadingMarkers(rawLine);
        foreach (var (token, candidate) in VerdictTokens)
        {
            if (StartsWithVerdictToken(line, token))
            {
                verdict = candidate;
                return true;
            }
        }

        verdict = RaiVerdict.Green;
        return false;
    }

    /// <summary>
    /// Strips leading whitespace and common bullet / markdown markers ("- ", "* ", "**", "#", "&gt;")
    /// so a verdict emitted as "- RED — ..." or "**RED**" is still recognized at the line boundary.
    /// </summary>
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

    /// <summary>
    /// True when <paramref name="line"/> begins with the exact uppercase <paramref name="token"/> as
    /// a whole word — i.e. followed by end-of-line or a non-word, non-hyphen, non-apostrophe
    /// character. This rejects compounds like "RED-level" and possessives so prose never counts as a
    /// verdict; only the agent's declared verdict token does.
    /// </summary>
    private static bool StartsWithVerdictToken(string line, string token)
    {
        if (!line.StartsWith(token, StringComparison.Ordinal))
            return false;
        if (line.Length == token.Length)
            return true;

        var next = line[token.Length];
        return !(char.IsLetterOrDigit(next) || next is '-' or '\'' or '_');
    }

    private static string ToLabel(RaiVerdict verdict) => verdict switch
    {
        RaiVerdict.Red => "red",
        RaiVerdict.Revise => "revise",
        RaiVerdict.Yellow => "yellow",
        _ => "green",
    };

    private static string Truncate(string? response)
    {
        if (string.IsNullOrEmpty(response))
            return string.Empty;
        const int max = 500;
        return response.Length <= max ? response : response[..max] + "…";
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

    private static string DetermineVerdict(string? response) => ToLabel(ParseVerdict(response));
}
