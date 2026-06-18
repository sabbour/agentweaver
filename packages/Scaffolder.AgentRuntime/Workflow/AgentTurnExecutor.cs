using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime.Workflow;

/// <summary>
/// Executor that runs the agent turn: drives a <see cref="CopilotAIAgent"/> (which the MAF
/// checkpoint manager can serialize), commits the worktree, computes the diff, and returns
/// AgentTurnOutput. Token deltas stream through the existing side-channel
/// (RecordingChannelWriter) and are invisible to MAF.
/// </summary>
public sealed class AgentTurnExecutor : Executor<AgentTurnInput, AgentTurnOutput>, IWorkflowNodeMeta
{
    /// <inheritdoc />
    public string LogicalNodeId => "agent";
    /// <inheritdoc />
    public string DisplayLabel => "Agent";
    /// <inheritdoc />
    public string Role => "agent";
    /// <inheritdoc />
    public string NodeType => "agent";
    /// <inheritdoc />
    public bool Hidden => false;
    /// <inheritdoc />
    public string NodeKind => "live";

    private readonly CopilotAIAgent _agent;
    private readonly IWorktreeOperations _worktreeOps;
    private readonly ILogger<AgentTurnExecutor> _logger;
    private readonly Func<string, ChannelWriter<RunEvent>?> _getRecordingWriter;
    private readonly string? _apiBaseUrl;
    private readonly string? _apiKey;

    public AgentTurnExecutor(
        CopilotAIAgent agent,
        IWorktreeOperations worktreeOps,
        Func<string, ChannelWriter<RunEvent>?> getRecordingWriter,
        ILogger<AgentTurnExecutor> logger,
        string? apiBaseUrl = null,
        string? apiKey = null)
        : base("agent-turn")
    {
        _agent = agent;
        _worktreeOps = worktreeOps;
        _getRecordingWriter = getRecordingWriter;
        _logger = logger;
        _apiBaseUrl = apiBaseUrl;
        _apiKey = apiKey;
    }

    public override async ValueTask<AgentTurnOutput> HandleAsync(
        AgentTurnInput input, IWorkflowContext context, CancellationToken ct)
    {
        var writer = _getRecordingWriter(input.RunId);
        bool safetyFlagged = false;

        WorkflowStepEvents.Emit(writer, _logger, input.RunId, "agent", "started", "Agent turn",
            agentName: input.AgentName);

        try
        {
            await _agent.SetupAsync(
                input.WorktreePath,
                input.RepositoryPath,
                input.RunId,
                input.ModelId,
                input.SystemPromptContext,
                writer,
                input.ProjectId,
                input.AgentName,
                _apiBaseUrl,
                _apiKey,
                ct).ConfigureAwait(false);

            var session = input.IsRevision
                ? await _agent.ResumeSessionAsync(ct).ConfigureAwait(false)
                : await _agent.CreateSessionAsync(ct).ConfigureAwait(false);

            await _agent.ExecuteStreamingLoopAsync(input.Task, session, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsContentSafetyViolation(ex))
        {
            _logger.LogWarning(ex, "Content safety violation detected for run {RunId}", input.RunId);
            safetyFlagged = true;
        }
        catch
        {
            WorkflowStepEvents.Emit(writer, _logger, input.RunId, "agent", "failed", "Agent turn");
            throw;
        }

        if (safetyFlagged)
        {
            WorkflowStepEvents.Emit(writer, _logger, input.RunId, "agent", "failed", "Agent turn");
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

        string treeHash = _worktreeOps.CommitChanges(input.WorktreePath, input.RunId);
        string diff = _worktreeOps.GetDiff(input.RepositoryPath, input.OriginatingBranch, input.WorktreeBranch);
        int stepCount = _worktreeOps.GetStepCount(input.RunId);

        WorkflowStepEvents.Emit(writer, _logger, input.RunId, "agent", "completed", "Agent turn",
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
