using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Configuration;
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

    /// <summary>
    /// One-shot re-ask issued when the first Rai response lacks a parseable <c>VERDICT:</c> sentinel.
    /// It demands ONLY the machine-readable line so the bounded retry has the best chance of parsing.
    /// </summary>
    private const string ReAskPrompt =
        "Your previous response was missing the required machine-readable verdict line. " +
        "Reply with ONLY this line and nothing else:\n" +
        "VERDICT: <GREEN|YELLOW|REVISE|RED>";

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
    private readonly bool _failClosedOnError;

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
        string subStreamSuffix = "rai",
        IConfiguration? configuration = null)
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
        _failClosedOnError = configuration?.GetValue<bool>("Rai:FailClosedOnError") ?? false;
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
        var terminalStepEmitted = false;
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

                IMPORTANT — machine-readable verdict: After your explanation, the LAST line of your
                response MUST be exactly:
                VERDICT: <ONE OF: GREEN | YELLOW | REVISE | RED>
                with nothing else on that line. Only this VERDICT: line is read as your decision — your
                prose is for humans and is NOT parsed for the verdict. Example final line:
                VERDICT: GREEN

                Run: {{input.RunId}}

                --- BEGIN DIFF ---
                {{input.Diff}}
                --- END DIFF ---

                Verdict legend (for your human-readable explanation):
                - GREEN  — no issues, safe to ship
                - YELLOW — minor concerns, ship with caution
                - REVISE — fixable issues found; the agent should revise before shipping (provide specific feedback)
                - RED    — critical violation that must block shipping entirely (e.g. credentials, PII, harmful content)

                Respond with a clear explanation, and if your verdict is REVISE provide actionable
                feedback the agent can act on. Then, as the FINAL line of your response, emit the
                machine-readable verdict exactly as:
                VERDICT: <GREEN|YELLOW|REVISE|RED>
                and nothing else on that line.
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
                // A remote AgentHost is registered for the coordinator/parent run, not its
                // event-only RAI substream. The local implementation is unaffected.
                runId: input.RunId,
                modelId: null,
                systemPromptContext: charter,
                streamWriter: subWriter,
                projectId: null,
                agentName: null,
                apiBaseUrl: null,
                apiKey: null,
                ct,
                userId: input.SubmittingUser).ConfigureAwait(false);

            var response = await agent.RunTurnAsync(task, isRevision: false, ct).ConfigureAwait(false);

            if (!TryParseVerdict(response, out var verdict))
            {
                // The reviewer omitted the machine-readable VERDICT: sentinel. Issue exactly ONE
                // bounded re-ask (not a loop) that demands only the sentinel line, then re-parse.
                _logger.LogWarning(
                    "Rai response for run {RunId} had no parseable VERDICT: sentinel — issuing one bounded re-ask. Raw response (truncated): {Raw}",
                    input.RunId, Truncate(response));

                var reAsk = await agent.RunTurnAsync(ReAskPrompt, isRevision: true, ct).ConfigureAwait(false);

                if (!TryParseVerdict(reAsk, out verdict))
                {
                    // FAIL-SAFE (safety-critical): a stricter parser raises the unparseable rate.
                    // Silently shipping a real RED (credentials/PII) is strictly worse than a rare,
                    // visible, recoverable false-block — so an unparseable verdict fails safe to RED.
                    verdict = RaiVerdict.Red;
                    _logger.LogWarning(
                        "Rai verdict still unparseable after re-ask for run {RunId} — failing safe to RED. Re-ask response (truncated): {Raw}",
                        input.RunId, Truncate(reAsk));
                    writer?.TryWrite(new RunEvent(0, "run.rai_error", new
                    {
                        runId = input.RunId,
                        reason = "unparseable_after_reask",
                        failClosed = _failClosedOnError,
                        verdict = ToLabel(verdict),
                    }));
                }
                else
                {
                    _logger.LogInformation(
                        "Rai re-ask produced a parseable verdict {Verdict} for run {RunId}.", verdict, input.RunId);
                }
            }

            if (verdict == RaiVerdict.Red)
            {
                _logger.LogWarning("Rai issued a RED verdict for run {RunId} — flagging content safety", input.RunId);
                EmitVerdict(writer, subWriter, input.RunId, verdict, response);
                WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "failed", DisplayLabel);
                _completeSubStream?.Invoke(subRunId);
                return input with { ContentSafetyFlagged = true };
            }

            if (verdict == RaiVerdict.Revise)
            {
                _logger.LogInformation("Rai issued a REVISE verdict for run {RunId} — requesting agent revision", input.RunId);
                EmitVerdict(writer, subWriter, input.RunId, verdict, response);
                WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "revise", DisplayLabel);
                _completeSubStream?.Invoke(subRunId);
                return input with { RaiRevisionRequired = true, RaiFeedback = ExtractFeedback(response) };
            }

            EmitVerdict(writer, subWriter, input.RunId, verdict, response);
        }
        catch (Exception ex)
        {
            var verdict = DefaultVerdictOnRaiFailure(_failClosedOnError);
            _logger.LogWarning(ex,
                "Rai RAI review failed for run {RunId} — defaulting to {Verdict}", input.RunId, verdict);
            writer?.TryWrite(new RunEvent(0, "run.rai_error", new
            {
                runId = input.RunId,
                reason = "exception",
                failClosed = _failClosedOnError,
                verdict = ToLabel(verdict),
                message = ex.Message,
            }));
            EmitVerdict(writer, subWriter, input.RunId, verdict, $"RAI review failed: {ex.Message}");
            if (verdict == RaiVerdict.Red)
            {
                WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "failed", DisplayLabel);
                return input with { ContentSafetyFlagged = true };
            }

            WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "completed", DisplayLabel,
                message: "RAI review failed; proceeding with advisory warning.");
            terminalStepEmitted = true;
        }
        finally
        {
            if (agent is not null)
                await agent.DisposeAsync().ConfigureAwait(false);
            _completeSubStream?.Invoke(subRunId);
        }

        if (!terminalStepEmitted)
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
    /// Parses the Rai reviewer's declared verdict from its response using the structured,
    /// sentinel-anchored contract. The reviewer is instructed (see the task prompt) to end its
    /// response with a single machine-readable line <c>VERDICT: &lt;GREEN|YELLOW|REVISE|RED&gt;</c>.
    /// <para>
    /// The sentinel is AUTHORITATIVE: when present, the human prose is NEVER scanned for verdict
    /// tokens. This is the root fix for the false-positive that hard-blocked benign runs — the old
    /// prose scan mis-read the prompt's own legend bullet ("RED — critical violation...") and
    /// section headers ("RED: none", "RED flags: none") as RED verdicts and escalated a benign
    /// REVISE/GREEN to a hard RED.
    /// </para>
    /// <para>
    /// When no sentinel is present, only an unambiguous verdict emoji (🔴 → RED, 🟡 → YELLOW) still
    /// yields a decision. Everything else is unparseable (<c>false</c>), which drives the bounded
    /// one-shot re-ask and fail-safe-to-RED handling in <see cref="HandleAsync"/>.
    /// </para>
    /// </summary>
    /// <returns>
    /// <c>true</c> when a sentinel (or unambiguous emoji) verdict was found (<paramref name="verdict"/>
    /// holds it). <c>false</c> when no verdict could be parsed — <paramref name="verdict"/> is then set
    /// to <see cref="RaiVerdict.Yellow"/> so callers never treat unparseable output as an explicit
    /// GREEN; the caller is responsible for the re-ask / fail-safe.
    /// </returns>
    internal static bool TryParseVerdict(string? response, out RaiVerdict verdict)
    {
        verdict = RaiVerdict.Yellow;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        // PRIMARY: the machine-readable VERDICT: sentinel is authoritative. When present we do NOT
        // scan prose at all — this is what kills the legend-echo / "RED: none" false-positive.
        if (TryParseSentinelVerdict(response, out var sentinel))
        {
            verdict = sentinel;
            return true;
        }

        // No sentinel: only an unambiguous verdict emoji may still yield a decision. 🔴 escalates
        // to a blocking RED; 🟡 is an advisory (non-blocking) YELLOW. Prose is deliberately NOT
        // scanned — an unparseable response drives the bounded re-ask + fail-safe in HandleAsync.
        if (TryParseEmojiVerdict(response, out var emoji))
        {
            verdict = emoji;
            return true;
        }

        verdict = RaiVerdict.Yellow;
        return false;
    }

    /// <summary>Convenience wrapper that applies the advisory default (YELLOW) when unparseable.</summary>
    internal static RaiVerdict ParseVerdict(string? response) =>
        TryParseVerdict(response, out var verdict) ? verdict : RaiVerdict.Yellow;

    /// <summary>
    /// Scans for the machine-readable <c>VERDICT: &lt;GREEN|YELLOW|REVISE|RED&gt;</c> sentinel line.
    /// A sentinel line matches (after stripping leading bullet/markdown markers and optional
    /// surrounding <c>**</c>) the pattern: the keyword <c>VERDICT:</c> (case-insensitive) followed by
    /// whitespace then one of the verdict tokens as a whole word (case-insensitive). When MULTIPLE
    /// sentinel lines are present the LAST one wins; if a single sentinel line names more than one
    /// token the most-severe wins.
    /// </summary>
    internal static bool TryParseSentinelVerdict(string? response, out RaiVerdict verdict)
    {
        verdict = RaiVerdict.Yellow;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        var found = false;
        foreach (var rawLine in response.Split('\n'))
        {
            if (TryParseSentinelLine(rawLine, out var lineVerdict))
            {
                verdict = lineVerdict; // last sentinel wins
                found = true;
            }
        }

        return found;
    }

    private const string SentinelKeyword = "VERDICT:";

    private static bool TryParseSentinelLine(string rawLine, out RaiVerdict verdict)
    {
        verdict = RaiVerdict.Yellow;

        // Strip leading bullets/markdown, then any surrounding bold (**) markers.
        var line = StripLeadingMarkers(rawLine).Trim().Trim('*').Trim();

        if (!line.StartsWith(SentinelKeyword, StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = line[SentinelKeyword.Length..];

        // A well-formed line names exactly one token; if it is ambiguous (names several), the
        // most-severe wins so the outcome stays conservative.
        var found = false;
        var best = RaiVerdict.Green;
        foreach (var (token, candidate) in VerdictTokens)
        {
            if (!ContainsWholeWord(remainder, token))
                continue;
            if (!found || candidate > best)
                best = candidate;
            found = true;
        }

        if (found)
            verdict = best;
        return found;
    }

    private static bool TryParseEmojiVerdict(string response, out RaiVerdict verdict)
    {
        // Emoji verdicts are unambiguous markers. 🔴 wins over 🟡 so ambiguity stays conservative.
        if (response.Contains("🔴", StringComparison.Ordinal))
        {
            verdict = RaiVerdict.Red;
            return true;
        }
        if (response.Contains("🟡", StringComparison.Ordinal))
        {
            verdict = RaiVerdict.Yellow;
            return true;
        }

        verdict = RaiVerdict.Yellow;
        return false;
    }

    /// <summary>
    /// True when <paramref name="text"/> contains <paramref name="token"/> (case-insensitive) as a
    /// whole word — not adjacent to a letter, digit, hyphen, apostrophe or underscore. Rejects
    /// compounds like "REDACTED" or "RED-level" so only the declared token counts.
    /// </summary>
    private static bool ContainsWholeWord(string text, string token)
    {
        var idx = 0;
        while ((idx = text.IndexOf(token, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var beforeOk = idx == 0 || !IsWordChar(text[idx - 1]);
            var after = idx + token.Length;
            var afterOk = after >= text.Length || !IsWordChar(text[after]);
            if (beforeOk && afterOk)
                return true;
            idx = after;
        }
        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '\'' or '_';

    private static RaiVerdict DefaultVerdictOnRaiFailure(bool failClosedOnError) =>
        failClosedOnError ? RaiVerdict.Red : RaiVerdict.Yellow;

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

    private static void EmitVerdict(
        ChannelWriter<RunEvent>? parentWriter,
        ChannelWriter<RunEvent>? subWriter,
        string runId,
        RaiVerdict verdict,
        string? response)
    {
        var payload = new
        {
            verdict = ToLabel(verdict),
            runId,
            rationale = ExtractRationale(response, verdict),
        };

        parentWriter?.TryWrite(new RunEvent(0, EventTypes.RaiVerdict, payload));
        subWriter?.TryWrite(new RunEvent(1, EventTypes.RaiVerdict, payload));
    }

    private static string ExtractRationale(string? response, RaiVerdict verdict)
    {
        if (string.IsNullOrWhiteSpace(response))
            return verdict switch
            {
                RaiVerdict.Red => "RAI reviewer blocked the change.",
                RaiVerdict.Revise => "RAI reviewer requested a revision.",
                RaiVerdict.Yellow => "RAI reviewer returned an advisory warning.",
                _ => "RAI reviewer found no blocking issues.",
            };

        foreach (var rawLine in response.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            // The machine-readable VERDICT: sentinel is for the parser, not humans — skip it so the
            // surfaced rationale is the explanation (not a degenerate "VERDICT: REVISE").
            if (TryParseSentinelLine(rawLine, out _))
                continue;

            foreach (var (token, _) in VerdictTokens)
            {
                var stripped = StripLeadingMarkers(line).TrimStart('*').Trim();
                if (!StartsWithVerdictToken(stripped, token))
                    continue;

                var remainder = stripped[token.Length..].Trim();
                remainder = remainder.TrimStart('*').Trim();
                remainder = remainder.TrimStart(':', '-', '—', '–').Trim();
                if (!string.IsNullOrWhiteSpace(remainder))
                    return TruncateOneLine(remainder);
            }

            return TruncateOneLine(line);
        }

        return "RAI reviewer completed without a written rationale.";
    }

    private static string TruncateOneLine(string value)
    {
        var oneLine = string.Join(' ', value.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        const int max = 240;
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }

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
        // Strip a leading REVISE verdict line if present, and drop the machine-readable VERDICT:
        // sentinel line, so the agent receives only the actionable human feedback.
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var feedbackLines = lines
            .SkipWhile(l => l.TrimStart().StartsWith("REVISE", StringComparison.OrdinalIgnoreCase))
            .Where(l => !TryParseSentinelLine(l, out _))
            .ToArray();
        return feedbackLines.Length > 0
            ? string.Join('\n', feedbackLines).Trim()
            : response.Trim();
    }

    private static string DetermineVerdict(string? response) => ToLabel(ParseVerdict(response));
}
