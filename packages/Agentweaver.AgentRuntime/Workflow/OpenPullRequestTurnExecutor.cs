using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// Platform-owned, non-agent workflow action that opens a GitHub pull request on the project's
/// connected repository (workflows-automation open-pull-request-action, issue #49). Deterministic —
/// makes ONE GitHub REST API call via <see cref="IGitHubPullRequestClient"/>, never an LLM turn (unlike
/// <see cref="BuildTestTurnExecutor"/>, which drives a canned-prompt AGENT turn).
/// </summary>
/// <remarks>
/// Pass-through by design: <see cref="HandleAsync"/> always returns the input
/// <see cref="AgentTurnOutput"/> unchanged so downstream steps (e.g. Scribe) keep consuming the same
/// produced diff/branch regardless of whether the PR itself was opened. Genuine failure modes (no head
/// branch / commits, unpushed branch, insufficient token scope, transport error) are caught and reported
/// via a <c>failed</c> <see cref="WorkflowStepEvents"/> entry instead of throwing, so a failed PR-open
/// never crashes the run. A project with no connected GitHub repository is treated as an expected,
/// non-error state (not every project is GitHub-connected) and reported via a <c>skipped</c> entry
/// instead — see <see cref="Skip"/>.
/// </remarks>
public sealed class OpenPullRequestTurnExecutor : Executor<AgentTurnOutput, AgentTurnOutput>, IWorkflowNodeMeta
{
    /// <summary>Base branch used when neither the node nor the project declares one.</summary>
    public const string DefaultBaseBranch = "main";

    /// <summary>
    /// Default PR title template. Supports the placeholders <c>{run_id}</c>, <c>{worktree_branch}</c>,
    /// <c>{originating_branch}</c>, and <c>{outcome_summary}</c>.
    /// </summary>
    public const string DefaultTitleTemplate = "Agentweaver: {outcome_summary}";

    /// <summary>Default PR body template. Supports the same placeholders as <see cref="DefaultTitleTemplate"/>.</summary>
    public const string DefaultBodyTemplate =
        "Automated changes produced by Agentweaver run `{run_id}` on branch `{worktree_branch}`.\n\n{outcome_summary}";

    public string LogicalNodeId { get; }
    public string DisplayLabel { get; }
    public string Role => "action";
    public string NodeType => "action";
    public bool Hidden => false;
    public string NodeKind => "live";

    private readonly IGitHubPullRequestClient _prClient;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly IGitHubAccessTokenProvider _accessTokenProvider;
    private readonly IProjectStore? _projectStore;
    private readonly ILogger<OpenPullRequestTurnExecutor> _logger;
    private readonly Func<string, ChannelWriter<RunEvent>?> _getRecordingWriter;
    private readonly string? _titleTemplate;
    private readonly string? _bodyTemplate;
    private readonly string? _baseBranchOverride;
    private readonly string? _headBranchOverride;
    private readonly bool _draft;

    public OpenPullRequestTurnExecutor(
        IGitHubPullRequestClient prClient,
        IGitHubTokenScopeProvider scopeProvider,
        IGitHubAccessTokenProvider accessTokenProvider,
        ILoggerFactory loggerFactory,
        Func<string, ChannelWriter<RunEvent>?>? getRecordingWriter = null,
        IProjectStore? projectStore = null,
        string name = "open-pull-request-turn",
        string logicalNodeId = "open-pull-request",
        string displayLabel = "Open Pull Request",
        string? title = null,
        string? body = null,
        string? baseBranch = null,
        string? headBranch = null,
        bool draft = false)
        : base(name)
    {
        LogicalNodeId = logicalNodeId;
        DisplayLabel = displayLabel;
        _prClient = prClient;
        _scopeProvider = scopeProvider;
        _accessTokenProvider = accessTokenProvider;
        _projectStore = projectStore;
        _logger = loggerFactory.CreateLogger<OpenPullRequestTurnExecutor>();
        _getRecordingWriter = getRecordingWriter ?? (_ => null);
        _titleTemplate = string.IsNullOrWhiteSpace(title) ? null : title;
        _bodyTemplate = string.IsNullOrWhiteSpace(body) ? null : body;
        _baseBranchOverride = string.IsNullOrWhiteSpace(baseBranch) ? null : baseBranch.Trim();
        _headBranchOverride = string.IsNullOrWhiteSpace(headBranch) ? null : headBranch.Trim();
        _draft = draft;
    }

    public override async ValueTask<AgentTurnOutput> HandleAsync(
        AgentTurnOutput input, IWorkflowContext context, CancellationToken ct)
    {
        var writer = _getRecordingWriter(input.RunId);
        WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "started", DisplayLabel);

        try
        {
            var headBranch = _headBranchOverride ?? input.WorktreeBranch;
            if (string.IsNullOrWhiteSpace(headBranch))
            {
                Fail(writer, input.RunId, "no-head-branch",
                    "No head branch is available to open a pull request from (the run produced no worktree branch).");
                return input;
            }

            Project? project = null;
            if (_projectStore is not null
                && !string.IsNullOrWhiteSpace(input.ProjectId)
                && ProjectId.TryParse(input.ProjectId, out var projectId))
            {
                project = await _projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
            }

            var repository = project?.Origin.SourceRepository;
            if (string.IsNullOrWhiteSpace(repository) || !TryParseOwnerRepo(repository!, out var owner, out var repo))
            {
                // Not a failure: a project with no connected GitHub repository is a valid, common state
                // (e.g. a Blank-origin project), not a broken run. Skip PR publication instead of failing
                // the run, and point the user at where they can connect or create one.
                Skip(writer, input.RunId, "no-connected-repository",
                    "Skipped: the project has no connected GitHub repository. Connect an existing repository " +
                    "or create a new one from Project Settings to enable pull request publication for future runs.");
                return input;
            }

            var baseBranch = _baseBranchOverride ?? project?.DefaultBranch;
            if (string.IsNullOrWhiteSpace(baseBranch))
                baseBranch = DefaultBaseBranch;

            var scope = await _scopeProvider
                .ResolveAsync(input.SubmittingUser, input.ProjectId, ct)
                .ConfigureAwait(false);
            var accessToken = await _accessTokenProvider.GetValidAccessTokenAsync(scope, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                Fail(writer, input.RunId, "no-access-token", "No valid GitHub access token is available to open the pull request.");
                return input;
            }

            var title = RenderTemplate(_titleTemplate ?? DefaultTitleTemplate, input);
            var body = RenderTemplate(_bodyTemplate ?? DefaultBodyTemplate, input);

            var result = await _prClient.CreatePullRequestAsync(
                owner, repo, title, body, baseBranch!, headBranch, _draft, accessToken!, ct).ConfigureAwait(false);

            if (result.Success)
            {
                WorkflowStepEvents.Emit(
                    writer, _logger, input.RunId, LogicalNodeId, "completed", DisplayLabel,
                    message: $"Opened pull request #{result.Number}: {result.Url}");
            }
            else
            {
                Fail(writer, input.RunId, result.ErrorReason ?? "unknown-error", result.ErrorMessage ?? "Pull request creation failed.");
            }
        }
        catch (OperationCanceledException)
        {
            WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "failed", DisplayLabel);
            throw;
        }
        catch (Exception ex)
        {
            Fail(writer, input.RunId, "unexpected-error", ex.Message);
            _logger.LogWarning(ex, "Open Pull Request action failed unexpectedly for run {RunId}", input.RunId);
        }

        // Pass-through: downstream steps (e.g. Scribe) consume the SAME produced output regardless of
        // whether the PR itself was opened successfully — see class remarks.
        return input;
    }

    private void Fail(ChannelWriter<RunEvent>? writer, string runId, string reason, string message)
    {
        WorkflowStepEvents.Emit(writer, _logger, runId, LogicalNodeId, "failed", DisplayLabel, message: message);
        _logger.LogWarning(
            "Open Pull Request action could not open a PR for run {RunId}: {Reason} — {Message}", runId, reason, message);
    }

    /// <summary>
    /// Emits a non-failing <c>skipped</c> step event for expected/benign non-execution paths (e.g. no
    /// GitHub repository is connected yet), as opposed to <see cref="Fail"/> for genuine errors.
    /// </summary>
    private void Skip(ChannelWriter<RunEvent>? writer, string runId, string reason, string message)
    {
        WorkflowStepEvents.Emit(writer, _logger, runId, LogicalNodeId, "skipped", DisplayLabel, message: message);
        _logger.LogInformation(
            "Open Pull Request action skipped for run {RunId}: {Reason} — {Message}", runId, reason, message);
    }

    /// <summary>Substitutes the supported placeholders with values drawn from the produced turn output.</summary>
    internal static string RenderTemplate(string template, AgentTurnOutput input) =>
        template
            .Replace("{run_id}", input.RunId)
            .Replace("{worktree_branch}", input.WorktreeBranch)
            .Replace("{originating_branch}", input.OriginatingBranch)
            .Replace("{outcome_summary}", BuildOutcomeSummary(input));

    private static string BuildOutcomeSummary(AgentTurnOutput input) => input.StepCount switch
    {
        <= 0 => "No changes were recorded for this run.",
        1 => $"1 step produced changes on `{input.WorktreeBranch}`.",
        _ => $"{input.StepCount} steps produced changes on `{input.WorktreeBranch}`.",
    };

    /// <summary>Accepts either an "owner/repo" string or a full GitHub URL (optionally ".git"-suffixed).</summary>
    internal static bool TryParseOwnerRepo(string repository, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        var trimmed = repository.Trim();
        var marker = "github.com/";
        var markerIndex = trimmed.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var withoutScheme = markerIndex >= 0 ? trimmed[(markerIndex + marker.Length)..] : trimmed;
        var segments = withoutScheme.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
            return false;

        owner = segments[0];
        repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        return owner.Length > 0 && repo.Length > 0;
    }
}
