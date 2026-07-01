using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// Executor that runs the agent turn: drives a <see cref="CopilotAIAgent"/> (which the MAF
/// checkpoint manager can serialize), commits the worktree, computes the diff, and returns
/// AgentTurnOutput. Token deltas stream through the existing side-channel
/// (RecordingChannelWriter) and are invisible to MAF.
/// </summary>
public sealed class AgentTurnExecutor : Executor<AgentTurnInput, AgentTurnOutput>, IWorkflowNodeMeta
{
    /// <inheritdoc />
    public string LogicalNodeId { get; }
    /// <inheritdoc />
    public string DisplayLabel { get; }
    /// <inheritdoc />
    public string Role => "agent";
    /// <inheritdoc />
    public string NodeType => "agent";
    /// <inheritdoc />
    public bool Hidden => false;
    /// <inheritdoc />
    public string NodeKind => "live";

    private readonly IWorkflowTurnAgent _agent;
    private readonly IWorktreeOperations _worktreeOps;
    private readonly ILogger<AgentTurnExecutor> _logger;
    private readonly Func<string, ChannelWriter<RunEvent>?> _getRecordingWriter;
    private readonly string? _apiBaseUrl;
    private readonly string? _apiKey;
    private readonly string? _agentNodeCharter;
    private readonly string? _agentNodePrompt;

    public AgentTurnExecutor(
        IWorkflowTurnAgent agent,
        IWorktreeOperations worktreeOps,
        Func<string, ChannelWriter<RunEvent>?> getRecordingWriter,
        ILogger<AgentTurnExecutor> logger,
        string? apiBaseUrl = null,
        string? apiKey = null,
        string? agentNodeCharter = null,
        string? agentNodePrompt = null,
        string name = "agent-turn",
        string logicalNodeId = "agent",
        string displayLabel = "Agent")
        : base(name)
    {
        LogicalNodeId = logicalNodeId;
        DisplayLabel = displayLabel;
        _agent = agent;
        _worktreeOps = worktreeOps;
        _getRecordingWriter = getRecordingWriter;
        _logger = logger;
        _apiBaseUrl = apiBaseUrl;
        _apiKey = apiKey;
        _agentNodeCharter = string.IsNullOrWhiteSpace(agentNodeCharter) ? null : agentNodeCharter;
        _agentNodePrompt = string.IsNullOrWhiteSpace(agentNodePrompt) ? null : agentNodePrompt;
    }

    public override async ValueTask<AgentTurnOutput> HandleAsync(
        AgentTurnInput input, IWorkflowContext context, CancellationToken ct)
    {
        var writer = _getRecordingWriter(input.RunId);
        bool safetyFlagged = false;

        WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "started", DisplayLabel,
            agentName: input.AgentName);

        try
        {
            // When the workflow node declared a bespoke inline charter (a role with no catalog
            // charter), prepend it to the run's system prompt so the agent adopts the authored
            // persona. Skipped when the run already carries the same charter (e.g. the node used a
            // catalog role whose charter was resolved upstream into SystemPromptContext).
            var systemPromptContext = input.SystemPromptContext;
            if (_agentNodeCharter is not null &&
                (string.IsNullOrEmpty(systemPromptContext) ||
                 !systemPromptContext.Contains(_agentNodeCharter, StringComparison.Ordinal)))
            {
                systemPromptContext = string.IsNullOrEmpty(systemPromptContext)
                    ? _agentNodeCharter
                    : _agentNodeCharter + "\n\n---\n\n" + systemPromptContext;
            }

            await _agent.SetupAsync(
                input.WorktreePath,
                input.RepositoryPath,
                input.RunId,
                input.ModelId,
                systemPromptContext,
                writer,
                input.ProjectId,
                input.AgentName,
                _apiBaseUrl,
                _apiKey,
                ct,
                input.SubmittingUser).ConfigureAwait(false);

            var task = input.IsRevision || _agentNodePrompt is null
                ? input.Task
                : _agentNodePrompt;
            await _agent.RunTurnAsync(task, input.IsRevision, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsContentSafetyViolation(ex))
        {
            _logger.LogWarning(ex, "Content safety violation detected for run {RunId}", input.RunId);
            safetyFlagged = true;
        }
        catch
        {
            WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "failed", DisplayLabel);
            throw;
        }

        if (safetyFlagged)
        {
            WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "failed", DisplayLabel);
            return new AgentTurnOutput(
                input.RunId,
                TreeHash: string.Empty,
                Diff: string.Empty,
                StepCount: 0,
                input.WorktreePath,
                input.WorktreeBranch,
                input.RepositoryPath,
                input.OriginatingBranch,
                ContentSafetyFlagged: true,
                Iteration: input.Iteration);
        }

        string treeHash;
        string diff;
        int stepCount;
        try
        {
            treeHash = _worktreeOps.CommitChanges(input.WorktreePath, input.RunId);
            diff = _worktreeOps.GetDiff(input.RepositoryPath, input.OriginatingBranch, input.WorktreeBranch);
            stepCount = _worktreeOps.GetStepCount(input.RunId);
        }
        catch
        {
            WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "failed", DisplayLabel);
            throw;
        }

        WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "completed", DisplayLabel,
            agentName: input.AgentName);

        return new AgentTurnOutput(
            input.RunId,
            treeHash,
            diff,
            stepCount,
            input.WorktreePath,
            input.WorktreeBranch,
            input.RepositoryPath,
            input.OriginatingBranch,
            ContentSafetyFlagged: false,
            Iteration: input.Iteration);
    }

    private static bool IsContentSafetyViolation(Exception ex)
    {
        // The governance kernel throws with a recognizable message pattern when
        // content safety policy is violated. Match on type name to avoid coupling
        // to the governance package's internal exception type.
        return ex.GetType().Name.Contains("ContentSafety", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("content safety", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase);
    }
}
