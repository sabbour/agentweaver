using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Scaffolder.AgentRuntime.Workflow;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;

namespace Scaffolder.Api.Runs;

/// <summary>
/// Builds the MAF Workflow instance, checkpoint manager, and provides the methods
/// to launch and resume streaming workflow runs.
/// </summary>
public sealed class RunWorkflowFactory
{
    private readonly IAgentRunner _agentRunner;
    private readonly IWorktreeOperations _worktreeOps;
    private readonly IMergeCoordinator _mergeCoordinator;
    private readonly RunStreamStore _streamStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Workflow _workflow;
    private readonly CheckpointManager _checkpointManager;
    private readonly string _checkpointDir;

    public CheckpointManager CheckpointManager => _checkpointManager;
    public Workflow Workflow => _workflow;
    public string CheckpointDirectory => _checkpointDir;

    public RunWorkflowFactory(
        IAgentRunner agentRunner,
        IWorktreeOperations worktreeOps,
        IMergeCoordinator mergeCoordinator,
        RunStreamStore streamStore,
        ILoggerFactory loggerFactory,
        IConfiguration configuration)
    {
        _agentRunner = agentRunner;
        _worktreeOps = worktreeOps;
        _mergeCoordinator = mergeCoordinator;
        _streamStore = streamStore;
        _loggerFactory = loggerFactory;

        // Checkpoint directory: configurable via Checkpoints:Path; defaults to
        // AppPaths.DataDirectory/checkpoints so production needs no explicit config.
        // KNOWN LIMITATION: Checkpoint JSON is stored unencrypted at rest. For multi-tenant
        // deployments, add encryption-at-rest (Seraph #4). Not needed for single-user slice.
        _checkpointDir = configuration["Checkpoints:Path"]
            ?? Path.Combine(AppPaths.DataDirectory, "checkpoints");
        Directory.CreateDirectory(_checkpointDir);

        var store = new FileSystemJsonCheckpointStore(new DirectoryInfo(_checkpointDir));
        _checkpointManager = CheckpointManager.CreateJson(store);

        _workflow = BuildWorkflow();
    }

    public ChannelWriter<RunEvent>? GetRecordingWriter(string runId)
    {
        var entry = _streamStore.Get(runId);
        return entry is not null ? new RecordingChannelWriter(entry) : null;
    }

    /// <summary>
    /// Shared workflow state scope used to carry AgentTurnOutput data across the
    /// review-gate pause so the merge adapter can construct MergeInput.
    /// </summary>
    internal const string MergeDataScope = "merge-data";
    internal const string MergeDataKey = "agent-output";

    private Workflow BuildWorkflow()
    {
        var agentTurnExecutor = new AgentTurnExecutor(
            _agentRunner,
            _worktreeOps,
            GetRecordingWriter,
            _loggerFactory.CreateLogger<AgentTurnExecutor>());

        var mergeExecutor = new MergeExecutor(
            _mergeCoordinator,
            _loggerFactory.CreateLogger<MergeExecutor>());

        var reviewPort = RequestPort.Create<WorkflowReviewRequest, WorkflowReviewDecision>("review-gate");

        // Adapter: maps AgentTurnOutput -> WorkflowReviewRequest and stores merge
        // data in workflow state so it survives the checkpoint/resume cycle.
        ExecutorBinding reviewAdapter = new FunctionExecutor<AgentTurnOutput, WorkflowReviewRequest>(
            "review-adapter",
            async (input, ctx, ct) =>
            {
                await ctx.QueueStateUpdateAsync(MergeDataKey, input, MergeDataScope, ct)
                    .ConfigureAwait(false);
                return new WorkflowReviewRequest(input.RunId, input.TreeHash, input.Diff, input.StepCount);
            });

        // Adapter: maps WorkflowReviewDecision -> MergeInput by reading the stored
        // AgentTurnOutput from workflow state.
        ExecutorBinding mergeAdapter = new FunctionExecutor<WorkflowReviewDecision, MergeInput>(
            "merge-adapter",
            async (decision, ctx, ct) =>
            {
                var agentOutput = await ctx.ReadStateAsync<AgentTurnOutput>(MergeDataKey, MergeDataScope, ct)
                    .ConfigureAwait(false);
                return new MergeInput(
                    agentOutput!.RunId,
                    agentOutput.TreeHash,
                    agentOutput.WorktreePath,
                    agentOutput.WorktreeBranch,
                    agentOutput.RepositoryPath,
                    agentOutput.OriginatingBranch);
            });

        // Terminal executors using FunctionExecutor for pass-through outputs.
        ExecutorBinding terminalNoOp = new FunctionExecutor<AgentTurnOutput, NoChangesOutput>(
            "terminal-no-op",
            (input, ctx, ct) => new ValueTask<NoChangesOutput>(new NoChangesOutput(input.RunId)));

        ExecutorBinding terminalDeclined = new FunctionExecutor<WorkflowReviewDecision, DeclinedOutput>(
            "terminal-declined",
            (input, ctx, ct) => new ValueTask<DeclinedOutput>(new DeclinedOutput(input.Approved ? string.Empty : string.Empty)));

        ExecutorBinding terminalSafetyFailed = new FunctionExecutor<AgentTurnOutput, ContentSafetyFailedOutput>(
            "terminal-safety-failed",
            (input, ctx, ct) => new ValueTask<ContentSafetyFailedOutput>(new ContentSafetyFailedOutput(input.RunId)));

        ExecutorBinding agentBinding = agentTurnExecutor;
        ExecutorBinding mergeBinding = mergeExecutor;
        ExecutorBinding reviewBinding = reviewPort;

        var wf = new WorkflowBuilder(agentBinding)
            // Content safety flagged -> immediate failure (highest priority, Guardrail 6)
            .AddEdge<AgentTurnOutput>(agentBinding, terminalSafetyFailed,
                output => output is not null && output.ContentSafetyFlagged)
            // No changes -> completed/no-op
            .AddEdge<AgentTurnOutput>(agentBinding, terminalNoOp,
                output => output is not null && !output.ContentSafetyFlagged && string.IsNullOrEmpty(output.Diff))
            // Has changes -> review adapter (stores merge data, maps to WorkflowReviewRequest)
            .AddEdge<AgentTurnOutput>(agentBinding, reviewAdapter,
                output => output is not null && !output.ContentSafetyFlagged && !string.IsNullOrEmpty(output.Diff))
            // Review adapter -> review gate (unconditional: WorkflowReviewRequest flows in)
            .AddEdge(reviewAdapter, reviewBinding)
            // Approved -> merge adapter (reads stored merge data, builds MergeInput)
            .AddEdge<WorkflowReviewDecision>(reviewBinding, mergeAdapter,
                decision => decision is not null && decision.Approved)
            // Merge adapter -> merge executor (unconditional: MergeInput flows in)
            .AddEdge(mergeAdapter, mergeBinding)
            // Declined -> terminal
            .AddEdge<WorkflowReviewDecision>(reviewBinding, terminalDeclined,
                decision => decision is null || !decision.Approved)
            .WithOutputFrom(mergeBinding)
            .WithOutputFrom(terminalNoOp)
            .WithOutputFrom(terminalDeclined)
            .WithOutputFrom(terminalSafetyFailed)
            .Build()!;

        return wf;
    }

    /// <summary>
    /// Launches a new streaming workflow run.
    /// </summary>
    public async Task<StreamingRun> StartAsync(AgentTurnInput input, string runId, CancellationToken ct)
    {
        return await InProcessExecution.RunStreamingAsync(
            _workflow, input, _checkpointManager, runId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes a workflow run from checkpoint.
    /// </summary>
    public async Task<StreamingRun> ResumeAsync(CheckpointInfo checkpointInfo, CancellationToken ct)
    {
        return await InProcessExecution.ResumeStreamingAsync(
            _workflow, checkpointInfo, _checkpointManager, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes checkpoint files for a given run (Guardrail 8: cleanup on terminal state).
    /// </summary>
    public void DeleteCheckpoints(string runId)
    {
        var runCheckpointDir = Path.Combine(_checkpointDir, runId);
        if (Directory.Exists(runCheckpointDir))
        {
            try { Directory.Delete(runCheckpointDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Checks if a checkpoint exists for the given runId.
    /// </summary>
    public bool HasCheckpoint(string runId)
    {
        var runCheckpointDir = Path.Combine(_checkpointDir, runId);
        return Directory.Exists(runCheckpointDir) &&
               Directory.GetFiles(runCheckpointDir).Length > 0;
    }

    /// <summary>
    /// Gets the latest checkpoint info for resumption. Returns null if no checkpoint exists.
    /// </summary>
    public CheckpointInfo? GetLatestCheckpoint(string runId)
    {
        if (!HasCheckpoint(runId)) return null;
        // CheckpointInfo(sessionId, checkpointId). We use the runId as session; the MAF
        // runtime resolves the actual checkpoint data from the store during resume.
        // For the latest checkpoint, we scan the directory for the most recent file.
        var runCheckpointDir = Path.Combine(_checkpointDir, runId);
        var latestFile = Directory.GetFiles(runCheckpointDir)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (latestFile is null) return null;
        var checkpointId = Path.GetFileNameWithoutExtension(latestFile);
        return new CheckpointInfo(runId, checkpointId);
    }
}

/// <summary>
/// Adapts a RunStreamEntry into a ChannelWriter for the agent runner's token streaming.
/// </summary>
internal sealed class RecordingChannelWriter(RunStreamEntry entry) : ChannelWriter<RunEvent>
{
    public override bool TryWrite(RunEvent item)
    {
        entry.Record(item);
        return true;
    }

    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(true);

    public override bool TryComplete(Exception? error = null) => true;
}
