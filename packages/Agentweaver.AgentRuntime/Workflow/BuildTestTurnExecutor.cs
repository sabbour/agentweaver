using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;

namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>Platform-owned Build & Test gate with a single canned prompt and preview activation guidance.</summary>
public sealed class BuildTestTurnExecutor : Executor<AgentTurnOutput, WorkflowReviewDecision>, IWorkflowNodeMeta
{
    public const string CannedPrompt =
        "Run the project's build and ALL tests. Execute all available build commands and test runners for the repository. " +
        "The step passes only if the build succeeds AND all tests pass. Report any failures with full error output. " +
        "Do not approve if there are compilation errors, test failures, or lint errors that indicate broken code. " +
        "After tests pass, if the project is a web application or service, start its development/preview server so stakeholders can access the running changes before human review. " +
        "Do NOT assume a hardcoded or pre-configured port — the port is not known ahead of time and can differ per execution. " +
        "Instead, discover how to run the app by inspecting the project itself (package.json scripts, Dockerfile, Makefile, README, framework defaults, etc.), start the server, and then observe the actual port it binds to from the process stdout/logs. " +
        "Once the server is up and verified (e.g. with curl), register it by calling the `start_preview(port=PORT)` tool with the exact port the server actually bound to, so the preview sandbox attaches to the running process.";

    public string LogicalNodeId { get; }
    public string DisplayLabel { get; }
    public string Role => "review";
    public string NodeType => "gate";
    public bool Hidden => false;
    public string NodeKind => "live";

    private readonly GitHubCopilotClientFactory _copilotClientFactory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly ISandboxExecutor _sandboxExecutor;
    private readonly ISandboxPolicyStore _sandboxPolicyStore;
    private readonly IShellApprovalStore _approvalStore;
    private readonly IToolApprovalGate _toolApprovalGate;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<BuildTestTurnExecutor> _logger;
    private readonly Func<string, ChannelWriter<RunEvent>?> _getRecordingWriter;
    private readonly Func<string, string, ChannelWriter<RunEvent>>? _createSubStream;
    private readonly Action<string>? _completeSubStream;
    private readonly IWorkflowAgentFactory? _agentFactory;
    private readonly string _agentId;

    public BuildTestTurnExecutor(
        GitHubCopilotClientFactory copilotClientFactory,
        IGitHubTokenScopeProvider scopeProvider,
        ISandboxExecutor sandboxExecutor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        ILoggerFactory loggerFactory,
        Func<string, ChannelWriter<RunEvent>?>? getRecordingWriter = null,
        string name = "build-test-turn",
        string logicalNodeId = "build-test",
        string displayLabel = "Build & Test",
        Func<string, string, ChannelWriter<RunEvent>>? createSubStream = null,
        Action<string>? completeSubStream = null,
        IWorkflowAgentFactory? agentFactory = null,
        string? agentId = null)
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
        _logger = loggerFactory.CreateLogger<BuildTestTurnExecutor>();
        _getRecordingWriter = getRecordingWriter ?? (_ => null);
        _createSubStream = createSubStream;
        _completeSubStream = completeSubStream;
        _agentFactory = agentFactory;
        _agentId = string.IsNullOrWhiteSpace(agentId) ? "qa-engineer" : agentId.Trim();
    }

    public override async ValueTask<WorkflowReviewDecision> HandleAsync(
        AgentTurnOutput input, IWorkflowContext context, CancellationToken ct)
    {
        var writer = _getRecordingWriter(input.RunId);
        WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "started", DisplayLabel, agentName: _agentId);

        var subRunId = input.RunId + "-build-test";
        var subWriter = _createSubStream?.Invoke(subRunId, "build-test");
        IWorkflowTurnAgent? agent = null;

        try
        {
            var worktree = !string.IsNullOrWhiteSpace(input.WorktreePath)
                ? input.WorktreePath
                : input.RepositoryPath;
            agent = _agentFactory?.CreateBuildTestAgent()
                ?? new CopilotAIAgent(
                    _copilotClientFactory,
                    _scopeProvider,
                    _sandboxExecutor,
                    _sandboxPolicyStore,
                    _approvalStore,
                    _toolApprovalGate,
                    _loggerFactory.CreateLogger<CopilotAIAgent>());

            await agent.SetupAsync(
                worktree,
                input.RepositoryPath,
                subRunId,
                modelId: null,
                systemPromptContext: null,
                streamWriter: subWriter,
                projectId: null,
                agentName: _agentId,
                apiBaseUrl: null,
                apiKey: null,
                ct,
                input.SubmittingUser).ConfigureAwait(false);

            var response = await agent.RunTurnAsync(BuildTask(input), isRevision: false, ct).ConfigureAwait(false);
            if (TryParseVerdict(response, out var decision))
            {
                WorkflowStepEvents.Emit(
                    writer,
                    _logger,
                    input.RunId,
                    LogicalNodeId,
                    decision.Approved ? "completed" : "revise",
                    DisplayLabel,
                    agentName: _agentId);
                return decision;
            }

            _logger.LogWarning(
                "Build & Test verdict could not be parsed for run {RunId}; treating as request-changes. Raw response (truncated): {Raw}",
                input.RunId, Truncate(response));
            return new WorkflowReviewDecision(
                Approved: false,
                RequestChanges: true,
                Feedback: string.IsNullOrWhiteSpace(response) ? "Build & Test did not return a parseable verdict." : response.Trim());
        }
        catch (Exception ex)
        {
            WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "failed", DisplayLabel, agentName: _agentId);
            _logger.LogWarning(ex, "Build & Test gate failed for run {RunId}; requesting changes", input.RunId);
            return new WorkflowReviewDecision(false, RequestChanges: true, Feedback: ex.Message);
        }
        finally
        {
            if (agent is not null)
                await agent.DisposeAsync().ConfigureAwait(false);
            _completeSubStream?.Invoke(subRunId);
        }
    }

    private static string BuildTask(AgentTurnOutput input) =>
        $$"""
        {{CannedPrompt}}

        Run: {{input.RunId}}

        Review the produced changes currently present in the workspace.
        Worktree branch: {{input.WorktreeBranch}}
        If a worktree branch is provided and the workspace is not already on it, check out that branch before running build/test commands.

        If useful, here is the diff context:
        --- BEGIN DIFF ---
        {{input.Diff}}
        --- END DIFF ---

        Issue exactly one verdict on its own line:
        - APPROVED — build succeeds, all tests pass, and preview was registered when applicable.
        - REQUEST_CHANGES — build/tests/lint fail, preview verification fails, or required checks cannot be completed.
        - DECLINED — the work is not viable or should not continue.
        """;

    internal static bool TryParseVerdict(string? response, out WorkflowReviewDecision decision)
    {
        decision = new WorkflowReviewDecision(false, RequestChanges: true);
        if (string.IsNullOrWhiteSpace(response)) return false;

        foreach (var rawLine in response.Split('\n'))
        {
            if (!TryParseVerdictLine(rawLine, out var verdict))
                continue;

            if (verdict == BuildTestVerdict.Approved)
            {
                decision = new WorkflowReviewDecision(true, Feedback: ExtractFeedback(response));
                return true;
            }

            if (verdict == BuildTestVerdict.RequestChanges)
            {
                decision = new WorkflowReviewDecision(false, RequestChanges: true, Feedback: ExtractFeedback(response));
                return true;
            }

            if (verdict == BuildTestVerdict.Declined)
            {
                decision = new WorkflowReviewDecision(false, RequestChanges: false, Feedback: ExtractFeedback(response));
                return true;
            }
        }

        return false;
    }

    private enum BuildTestVerdict
    {
        Approved,
        RequestChanges,
        Declined,
    }

    private static bool TryParseVerdictLine(string rawLine, out BuildTestVerdict verdict)
    {
        var line = StripLeadingMarkers(rawLine);
        if (StartsWithVerdictToken(line, "APPROVED") || StartsWithVerdictToken(line, "PASS"))
        {
            verdict = BuildTestVerdict.Approved;
            return true;
        }

        if (StartsWithVerdictToken(line, "REQUEST_CHANGES") ||
            StartsWithVerdictToken(line, "REQUEST-CHANGES") ||
            StartsWithVerdictToken(line, "REVISE") ||
            StartsWithVerdictToken(line, "FAIL"))
        {
            verdict = BuildTestVerdict.RequestChanges;
            return true;
        }

        if (StartsWithVerdictToken(line, "DECLINED"))
        {
            verdict = BuildTestVerdict.Declined;
            return true;
        }

        if (ContainsCompactVerdictToken(line, "REQUEST_CHANGES") ||
            ContainsCompactVerdictToken(line, "REQUEST-CHANGES") ||
            ContainsCompactVerdictToken(line, "REVISE") ||
            ContainsCompactVerdictToken(line, "FAIL"))
        {
            verdict = BuildTestVerdict.RequestChanges;
            return true;
        }

        if (ContainsCompactVerdictToken(line, "DECLINED"))
        {
            verdict = BuildTestVerdict.Declined;
            return true;
        }

        if (ContainsCompactVerdictToken(line, "APPROVED") || ContainsCompactVerdictToken(line, "PASS"))
        {
            verdict = BuildTestVerdict.Approved;
            return true;
        }

        verdict = BuildTestVerdict.RequestChanges;
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
        if (!StartsWithTokenOrCurlEcho(line, token))
            return false;

        var tokenIndex = line.StartsWith(token, StringComparison.OrdinalIgnoreCase)
            ? 0
            : line.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (tokenIndex < 0)
            return false;

        var nextIndex = tokenIndex + token.Length;
        if (line.Length == nextIndex)
            return true;

        var next = line[nextIndex];
        return !(char.IsLetterOrDigit(next) || next is '\'' or '_');
    }

    private static bool StartsWithTokenOrCurlEcho(string line, string token) =>
        line.StartsWith(token, StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("curl" + token, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsCompactVerdictToken(string line, string token)
    {
        var idx = line.IndexOf(token, StringComparison.Ordinal);
        while (idx >= 0)
        {
            if (HasTokenEnd(line, idx + token.Length)
                && HasSafeTokenStart(line, idx)
                && !IsNegated(line, idx))
                return true;

            idx = line.IndexOf(token, idx + token.Length, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool HasTokenEnd(string line, int end)
    {
        if (end >= line.Length)
            return true;

        var next = line[end];
        return !(char.IsLetterOrDigit(next) || next is '\'' or '_');
    }

    private static bool HasSafeTokenStart(string line, int idx)
    {
        if (idx == 0)
            return true;

        var previous = line[idx - 1];
        if (!(char.IsLetterOrDigit(previous) || previous is '\'' or '_'))
            return true;

        // Recovery path for malformed command-output prefixes such as "curlAPPROVED": allow only a
        // small known command prefix immediately attached to an uppercase verdict token.
        var prefix = line[..idx].Trim();
        return prefix is "curl" or "curl.exe" or "wget" or "http" or "httpie" or "iwr" or "irm";
    }

    private static bool IsNegated(string line, int tokenStart)
    {
        var before = line[..tokenStart].TrimEnd();
        if (before.Length == 0)
            return false;

        var end = before.Length - 1;
        var start = end;
        while (start >= 0 && (char.IsLetter(before[start]) || before[start] is '\'' or '’'))
            start--;

        var word = before[(start + 1)..].Trim('\'', '’').ToUpperInvariant();
        return word is "NO" or "NOT" or "NEVER" or "WITHOUT" or "CANNOT" or "CAN'T" or "WON'T" or "ISN'T" or "WASN'T";
    }

    private static string ExtractFeedback(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return string.Empty;
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .SkipWhile(l =>
            {
                return TryParseVerdictLine(l, out _);
            })
            .ToArray();
        return lines.Length > 0 ? string.Join('\n', lines).Trim() : response.Trim();
    }

    private static string Truncate(string? response)
    {
        if (string.IsNullOrEmpty(response)) return string.Empty;
        const int max = 500;
        return response.Length <= max ? response : response[..max] + "…";
    }
}
