using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime.Workflow;

/// <summary>
/// Executor that runs the agent turn: invokes IAgentRunner, commits the worktree,
/// computes the diff, and returns AgentTurnOutput. Token deltas stream through the
/// existing side-channel (RecordingChannelWriter) and are invisible to MAF.
/// </summary>
public sealed class AgentTurnExecutor : Executor<AgentTurnInput, AgentTurnOutput>
{
    private readonly IAgentRunner _agentRunner;
    private readonly IWorktreeOperations _worktreeOps;
    private readonly ILogger<AgentTurnExecutor> _logger;
    private readonly Func<string, ChannelWriter<RunEvent>?> _getRecordingWriter;

    public AgentTurnExecutor(
        IAgentRunner agentRunner,
        IWorktreeOperations worktreeOps,
        Func<string, ChannelWriter<RunEvent>?> getRecordingWriter,
        ILogger<AgentTurnExecutor> logger)
        : base("agent-turn")
    {
        _agentRunner = agentRunner;
        _worktreeOps = worktreeOps;
        _getRecordingWriter = getRecordingWriter;
        _logger = logger;
    }

    public override async ValueTask<AgentTurnOutput> HandleAsync(
        AgentTurnInput input, IWorkflowContext context, CancellationToken ct)
    {
        var writer = _getRecordingWriter(input.RunId);
        bool safetyFlagged = false;

        try
        {
            await _agentRunner.ExecuteAsync(
                input.Task,
                input.WorktreePath,
                input.RepositoryPath,
                ModelSourceExtensions.FromApiString(input.ModelSource),
                input.RunId,
                writer,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsContentSafetyViolation(ex))
        {
            _logger.LogWarning(ex, "Content safety violation detected for run {RunId}", input.RunId);
            safetyFlagged = true;
        }

        if (safetyFlagged)
        {
            return new AgentTurnOutput(
                input.RunId,
                TreeHash: string.Empty,
                Diff: string.Empty,
                StepCount: 0,
                input.WorktreePath,
                input.WorktreeBranch,
                input.RepositoryPath,
                input.OriginatingBranch,
                ContentSafetyFlagged: true);
        }

        string treeHash = _worktreeOps.CommitChanges(input.WorktreePath, input.RunId);
        string diff = _worktreeOps.GetDiff(input.RepositoryPath, input.OriginatingBranch, input.WorktreeBranch);
        int stepCount = _worktreeOps.GetStepCount(input.RunId);

        return new AgentTurnOutput(
            input.RunId,
            treeHash,
            diff,
            stepCount,
            input.WorktreePath,
            input.WorktreeBranch,
            input.RepositoryPath,
            input.OriginatingBranch,
            ContentSafetyFlagged: false);
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
