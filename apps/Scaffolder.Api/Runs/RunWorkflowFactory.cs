using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Scaffolder.AgentRuntime;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.AgentRuntime.Workflow;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;
using Scaffolder.SandboxExec;

namespace Scaffolder.Api.Runs;

/// <summary>
/// Builds the MAF Workflow instance, checkpoint manager, and provides the methods
/// to launch and resume streaming workflow runs.
/// </summary>
public sealed class RunWorkflowFactory
{
    private readonly IAgentRunner _agentRunner;
    private readonly GitHubCopilotClientFactory _copilotClientFactory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly ISandboxExecutor _sandboxExecutor;
    private readonly ISandboxPolicyStore _sandboxPolicyStore;
    private readonly IShellApprovalStore _approvalStore;
    private readonly IToolApprovalGate _toolApprovalGate;
    private readonly IWorktreeOperations _worktreeOps;
    private readonly IMergeCoordinator _mergeCoordinator;
    private readonly RunStreamStore _streamStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CheckpointManager _checkpointManager;
    private readonly string _checkpointDir;

    public CheckpointManager CheckpointManager => _checkpointManager;
    public string CheckpointDirectory => _checkpointDir;

    public RunWorkflowFactory(
        IAgentRunner agentRunner,
        GitHubCopilotClientFactory copilotClientFactory,
        IGitHubTokenScopeProvider scopeProvider,
        ISandboxExecutor sandboxExecutor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        IWorktreeOperations worktreeOps,
        IMergeCoordinator mergeCoordinator,
        RunStreamStore streamStore,
        ILoggerFactory loggerFactory,
        IConfiguration configuration)
    {
        _agentRunner = agentRunner;
        _copilotClientFactory = copilotClientFactory;
        _scopeProvider = scopeProvider;
        _sandboxExecutor = sandboxExecutor;
        _sandboxPolicyStore = sandboxPolicyStore;
        _approvalStore = approvalStore;
        _toolApprovalGate = toolApprovalGate;
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
        // A fresh CopilotAIAgent per workflow build (per run). It is an AIAgent the MAF
        // checkpoint manager can serialize, so the Copilot SDK session is persisted into the
        // FileSystem checkpoint alongside the workflow state.
        var copilotAgent = new CopilotAIAgent(
            _copilotClientFactory,
            _scopeProvider,
            _sandboxExecutor,
            _sandboxPolicyStore,
            _approvalStore,
            _toolApprovalGate,
            _loggerFactory.CreateLogger<CopilotAIAgent>());

        var agentTurnExecutor = new AgentTurnExecutor(
            copilotAgent,
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

        ExecutorBinding terminalNoOp = new FunctionExecutor<AgentTurnOutput, NoChangesOutput>(
            "terminal-no-op",
            (input, ctx, ct) => new ValueTask<NoChangesOutput>(new NoChangesOutput(input.RunId)));

        ExecutorBinding terminalDeclined = new FunctionExecutor<WorkflowReviewDecision, DeclinedOutput>(
            "terminal-declined",
            (input, ctx, ct) => new ValueTask<DeclinedOutput>(new DeclinedOutput(input.Approved ? string.Empty : string.Empty)));

        ExecutorBinding terminalSafetyFailed = new FunctionExecutor<AgentTurnOutput, ContentSafetyFailedOutput>(
            "terminal-safety-failed",
            (input, ctx, ct) => new ValueTask<ContentSafetyFailedOutput>(new ContentSafetyFailedOutput(input.RunId)));

        ExecutorBinding terminalMerge = new FunctionExecutor<MergeOutput, MergeOutput>(
            "terminal-merge",
            (input, ctx, ct) => new ValueTask<MergeOutput>(input));

        // Blocked adapter: on a retriable block, re-enter the review gate via HITL
        // so the workflow stays alive and the user can re-approve once the blocker clears.
        ExecutorBinding blockedAdapter = new FunctionExecutor<MergeOutput, WorkflowReviewRequest>(
            "blocked-adapter",
            async (output, ctx, ct) =>
            {
                var agentOutput = await ctx.ReadStateAsync<AgentTurnOutput>(MergeDataKey, MergeDataScope, ct)
                    .ConfigureAwait(false);
                return new WorkflowReviewRequest(
                    agentOutput!.RunId, agentOutput.TreeHash, agentOutput.Diff, agentOutput.StepCount);
            });

        // Store AgentTurnInput in workflow state at workflow start so Scribe adapters
        // can read project/agent context after the review-gate checkpoint/resume cycle.
        ExecutorBinding agentInputStorer = new FunctionExecutor<AgentTurnInput, AgentTurnInput>(
            "agent-input-storer",
            async (input, ctx, ct) =>
            {
                await ctx.QueueStateUpdateAsync("agent-input", input, "run-context", ct).ConfigureAwait(false);
                return input;
            });

        // Two separate ScribeTurnExecutor instances to avoid single-node-multiple-inputs
        // ambiguity in MAF's graph builder.
        var scribeMergeExec = new ScribeTurnExecutor(
            _agentRunner, _loggerFactory.CreateLogger<ScribeTurnExecutor>(), "scribe-turn-merge");
        var scribeNoChangesExec = new ScribeTurnExecutor(
            _agentRunner, _loggerFactory.CreateLogger<ScribeTurnExecutor>(), "scribe-turn-no-changes");
        ExecutorBinding scribeBindingMerge = scribeMergeExec;
        ExecutorBinding scribeBindingNoChanges = scribeNoChangesExec;

        // Scribe input adapters: read stored AgentTurnInput, build ScribeTurnInput.
        ExecutorBinding scribeInputMerge = new FunctionExecutor<MergeOutput, ScribeTurnInput>(
            "scribe-input-merge",
            async (output, ctx, ct) =>
            {
                var agentInput = await ctx.ReadStateAsync<AgentTurnInput>("agent-input", "run-context", ct)
                    .ConfigureAwait(false);
                return new ScribeTurnInput(
                    output.RunId,
                    agentInput?.ProjectId ?? "",
                    agentInput?.AgentName ?? "",
                    agentInput?.RunStartedAt ?? DateTimeOffset.UtcNow,
                    agentInput?.RepositoryPath ?? "",
                    agentInput?.ModelSource ?? "github-copilot",
                    agentInput?.ModelId,
                    TerminalStatus: output.Status,
                    MergeResult: output.MergeResult,
                    MergeMode: output.MergeMode);
            });

        ExecutorBinding scribeInputNoChanges = new FunctionExecutor<NoChangesOutput, ScribeTurnInput>(
            "scribe-input-no-changes",
            async (output, ctx, ct) =>
            {
                var agentInput = await ctx.ReadStateAsync<AgentTurnInput>("agent-input", "run-context", ct)
                    .ConfigureAwait(false);
                return new ScribeTurnInput(
                    output.RunId,
                    agentInput?.ProjectId ?? "",
                    agentInput?.AgentName ?? "",
                    agentInput?.RunStartedAt ?? DateTimeOffset.UtcNow,
                    agentInput?.RepositoryPath ?? "",
                    agentInput?.ModelSource ?? "github-copilot",
                    agentInput?.ModelId,
                    TerminalStatus: "no_changes");
            });

        // Scribe output adapters: reconstruct terminal output types from pass-through.
        ExecutorBinding scribeOutputMerge = new FunctionExecutor<ScribeTurnInput, MergeOutput>(
            "scribe-output-merge",
            (input, ctx, ct) => new ValueTask<MergeOutput>(
                new MergeOutput(input.RunId, input.TerminalStatus ?? "merged", input.MergeResult, input.MergeMode)));

        ExecutorBinding scribeOutputNoChanges = new FunctionExecutor<ScribeTurnInput, NoChangesOutput>(
            "scribe-output-no-changes",
            (input, ctx, ct) => new ValueTask<NoChangesOutput>(new NoChangesOutput(input.RunId)));

        ExecutorBinding agentBinding = agentTurnExecutor;
        ExecutorBinding mergeBinding = mergeExecutor;
        ExecutorBinding reviewBinding = reviewPort;

        var wf = new WorkflowBuilder(agentInputStorer)
            // storer -> agent turn (unconditional)
            .AddEdge(agentInputStorer, agentBinding)
            // Content safety flagged -> immediate failure (highest priority, Guardrail 6)
            .AddEdge<AgentTurnOutput>(agentBinding, terminalSafetyFailed,
                output => output is not null && output.ContentSafetyFlagged)
            // No changes -> no-op -> scribe path
            .AddEdge<AgentTurnOutput>(agentBinding, terminalNoOp,
                output => output is not null && !output.ContentSafetyFlagged && string.IsNullOrEmpty(output.Diff))
            .AddEdge(terminalNoOp, scribeInputNoChanges)
            .AddEdge(scribeInputNoChanges, scribeBindingNoChanges)
            .AddEdge(scribeBindingNoChanges, scribeOutputNoChanges)
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
            // Merge succeeded or failed terminally -> terminal merge -> scribe path
            .AddEdge<MergeOutput>(mergeBinding, terminalMerge,
                output => output is not null && output.Status != "blocked")
            .AddEdge(terminalMerge, scribeInputMerge)
            .AddEdge(scribeInputMerge, scribeBindingMerge)
            .AddEdge(scribeBindingMerge, scribeOutputMerge)
            // Merge blocked (retriable) -> re-enter review gate via HITL.
            // idempotent: true permits the cycle back through the review port.
            .AddEdge<MergeOutput>(mergeBinding, blockedAdapter,
                output => output is not null && output.Status == "blocked")
            .AddEdge(blockedAdapter, reviewBinding, idempotent: true)
            // Declined -> terminal
            .AddEdge<WorkflowReviewDecision>(reviewBinding, terminalDeclined,
                decision => decision is null || !decision.Approved)
            // Outputs
            .WithOutputFrom(scribeOutputMerge)
            .WithOutputFrom(scribeOutputNoChanges)
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
        var workflow = BuildWorkflow();
        return await InProcessExecution.RunStreamingAsync(
            workflow, input, _checkpointManager, runId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes a workflow run from checkpoint.
    /// </summary>
    public async Task<StreamingRun> ResumeAsync(CheckpointInfo checkpointInfo, CancellationToken ct)
    {
        var workflow = BuildWorkflow();
        return await InProcessExecution.ResumeStreamingAsync(
            workflow, checkpointInfo, _checkpointManager, ct).ConfigureAwait(false);
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
        entry.RecordNext(item.Type, item.Payload);
        return true;
    }

    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(true);

    public override bool TryComplete(Exception? error = null) => true;
}
