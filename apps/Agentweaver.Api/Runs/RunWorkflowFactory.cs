using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs.Graph;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Builds the MAF Workflow instance, checkpoint manager, and provides the methods
/// to launch and resume streaming workflow runs.
/// </summary>
public sealed class RunWorkflowFactory
{
    private readonly GitHubCopilotClientFactory _copilotClientFactory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly ISandboxExecutor _sandboxExecutor;
    private readonly ISandboxPolicyStore _sandboxPolicyStore;
    private readonly IShellApprovalStore _approvalStore;
    private readonly IToolApprovalGate _toolApprovalGate;
    private readonly IWorktreeOperations _worktreeOps;
    private readonly IMergeCoordinator _mergeCoordinator;
    private readonly RunStreamStore _streamStore;
    private readonly SqliteRunStore _runStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkflowAgentFactory _agentFactory;
    private readonly CheckpointManager _checkpointManager;
    private readonly string _checkpointDir;
    private readonly string? _apiBaseUrl;
    private readonly string? _apiKey;

    // Per-run snapshot of executorId -> render metadata, captured when the run's workflow is built
    // (StartAsync/ResumeAsync). The watch loop uses it to translate MAF executor lifecycle events
    // into workflow.step UI events. Cleared on genuine terminal cleanup so it cannot leak.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<string, ExecutorNodeMeta>> _runExecutorMeta = new();

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
        SqliteRunStore runStore,
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory,
        IWorkflowAgentFactory agentFactory,
        IConfiguration configuration)
    {
        _ = agentRunner; // retained for DI/test compatibility; agents now come from IWorkflowAgentFactory
        _copilotClientFactory = copilotClientFactory;
        _scopeProvider = scopeProvider;
        _sandboxExecutor = sandboxExecutor;
        _sandboxPolicyStore = sandboxPolicyStore;
        _approvalStore = approvalStore;
        _toolApprovalGate = toolApprovalGate;
        _worktreeOps = worktreeOps;
        _mergeCoordinator = mergeCoordinator;
        _streamStore = streamStore;
        _runStore = runStore;
        _loggerFactory = loggerFactory;
        _scopeFactory = scopeFactory;
        _agentFactory = agentFactory;

        // Checkpoint directory: configurable via Checkpoints:Path; defaults to
        // AppPaths.DataDirectory/checkpoints so production needs no explicit config.
        // KNOWN LIMITATION: Checkpoint JSON is stored unencrypted at rest. For multi-tenant
        // deployments, add encryption-at-rest (Seraph #4). Not needed for single-user slice.
        _checkpointDir = configuration["Checkpoints:Path"]
            ?? Path.Combine(AppPaths.DataDirectory, "checkpoints");
        Directory.CreateDirectory(_checkpointDir);

        _apiBaseUrl = configuration["Agentweaver:ApiBaseUrl"] ?? "http://localhost:5000";
        // Prefer the single-key shorthand; fall back to the first entry in the multi-key list
        // so Scribe can always authenticate its self-calls regardless of which format the user uses.
        _apiKey = configuration["Auth:ApiKey"]
            ?? configuration.GetSection("Auth:Keys").GetChildren().FirstOrDefault()?["Token"];

        var store = ResilientCheckpointStore.Create(
            _checkpointDir, _loggerFactory.CreateLogger<RunWorkflowFactory>());
        _checkpointManager = CheckpointManager.CreateJson(store);
    }

    public ChannelWriter<RunEvent>? GetRecordingWriter(string runId)
    {
        var entry = _streamStore.Get(runId);
        return entry is not null ? new RecordingChannelWriter(entry) : null;
    }

    /// <summary>
    /// Creates a sub-stream entry for a built-in agent (Rai/Scribe) keyed as
    /// <c>{runId}-{suffix}</c> and returns its <see cref="ChannelWriter{T}"/>.
    /// The parent run's owner is inherited so authorization works for sub-stream SSE requests.
    /// </summary>
    public ChannelWriter<RunEvent> CreateSubStreamWriter(string subRunId, string suffix)
    {
        // Derive owner from the parent run entry (suffix is e.g. "rai"/"scribe").
        var parentRunId = subRunId.EndsWith($"-{suffix}", StringComparison.Ordinal)
            ? subRunId[..^(suffix.Length + 1)]
            : subRunId;
        var parentEntry = _streamStore.Get(parentRunId);
        var owner = parentEntry?.Owner ?? "system";
        var entry = _streamStore.Create(subRunId, owner);
        return new RecordingChannelWriter(entry);
    }

    public void CompleteSubStream(string subRunId)
    {
        _streamStore.Complete(subRunId);
        _ = PersistRunEventsAsync(subRunId);
    }

    /// <summary>
    /// Persists the in-memory event history for <paramref name="runId"/> to the
    /// <see cref="RunEventRecord"/> table so the Watch page can replay them after the
    /// stream entry is evicted from <see cref="RunStreamStore"/>. Idempotent: already-
    /// persisted sequences are skipped via a pre-check. Fire-and-forget safe.
    /// </summary>
    public async Task PersistRunEventsAsync(string runId)
    {
        try
        {
            var entry = _streamStore.Get(runId);
            if (entry is null) return;

            var events = entry.GetSnapshotSince(0).Events;
            if (events.Count == 0) return;

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

            var existingSeqs = db.RunEvents
                .Where(e => e.RunId == runId)
                .Select(e => e.Sequence)
                .ToHashSet();

            var toInsert = events
                .Where(e => !existingSeqs.Contains(e.Sequence))
                .Select(e => new RunEventRecord
                {
                    RunId = runId,
                    Sequence = e.Sequence,
                    EventType = e.Type,
                    PayloadJson = JsonSerializer.Serialize(e.Payload),
                    CreatedAt = DateTime.UtcNow,
                })
                .ToList();

            if (toInsert.Count == 0) return;

            db.RunEvents.AddRange(toInsert);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<RunWorkflowFactory>()
                .LogWarning(ex, "Failed to persist run events for {RunId}", runId);
        }
    }

    /// <summary>
    /// Shared workflow state scope used to carry AgentTurnOutput data across the
    /// review-gate pause so the merge adapter can construct MergeInput.
    /// </summary>
    internal const string MergeDataScope = "merge-data";
    internal const string MergeDataKey = "agent-output";

    /// <summary>Maximum revision iterations before capping (Rai or Review).</summary>
    private const int MaxIterations = 3;

    private (Workflow Workflow, GraphDescriptor Descriptor, IReadOnlyDictionary<string, ExecutorNodeMeta> ExecutorMeta) BuildWorkflow(bool isChild = false)
    {
        // A fresh worker agent per workflow build (per run), resolved through the injectable
        // IWorkflowAgentFactory seam. In production this builds a CopilotAIAgent — an AIAgent the
        // MAF checkpoint manager can serialize, so the Copilot SDK session is persisted into the
        // FileSystem checkpoint alongside the workflow state. Tests substitute a fake agent.
        var copilotAgent = _agentFactory.CreateWorkerAgent();

        var agentTurnExecutor = new AgentTurnExecutor(
            copilotAgent,
            _worktreeOps,
            GetRecordingWriter,
            _loggerFactory.CreateLogger<AgentTurnExecutor>(),
            apiBaseUrl: _apiBaseUrl,
            apiKey: _apiKey);

        var mergeExecutor = new MergeExecutor(
            _mergeCoordinator,
            _loggerFactory.CreateLogger<MergeExecutor>(),
            GetRecordingWriter);

        var reviewPort = RequestPort.Create<WorkflowReviewRequest, WorkflowReviewDecision>("review-gate");

        // Adapter: maps AgentTurnOutput -> WorkflowReviewRequest and stores merge
        // data in workflow state so it survives the checkpoint/resume cycle.
        // RaiSafetyFlagged is passed through so the reviewer sees Rai's verdict as context.
        ExecutorBinding reviewAdapter = new VisualFunctionExecutor<AgentTurnOutput, WorkflowReviewRequest>(
            "review-adapter", "review-adapter", "Review adapter", "plumbing", "action", true,
            async (input, ctx, ct) =>
            {
                await ctx.QueueStateUpdateAsync(MergeDataKey, input, MergeDataScope, ct)
                    .ConfigureAwait(false);
                return new WorkflowReviewRequest(
                    input.RunId, input.TreeHash, input.Diff, input.StepCount,
                    RaiSafetyFlagged: input.ContentSafetyFlagged);
            });

        // Adapter: maps WorkflowReviewDecision -> MergeInput by reading the stored
        // AgentTurnOutput from workflow state.
        ExecutorBinding mergeAdapter = new VisualFunctionExecutor<WorkflowReviewDecision, MergeInput>(
            "merge-adapter", "merge-adapter", "Merge adapter", "plumbing", "action", true,
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

        ExecutorBinding terminalNoOp = new VisualFunctionExecutor<AgentTurnOutput, NoChangesOutput>(
            "terminal-no-op", "terminal-no-op", "No changes", "plumbing", "terminal", true,
            (input, ctx, ct) => new ValueTask<NoChangesOutput>(new NoChangesOutput(input.RunId)));

        // Child assemble-ready terminal (coordinator child runs only). Short-circuits the per-child
        // human gate / merge / scribe: marks the run assemble-ready and STOPS. Records the child's
        // worktree branch + produced tree hash as the hand-off contract the coordinator collects.
        // Empty-diff (no-op) children terminalize here too — a valid assemble-ready outcome with
        // HasChanges == false.
        ExecutorBinding childAssembleReady = new VisualFunctionExecutor<AgentTurnOutput, AssembleReadyOutput>(
            "child-assemble-ready", "assemble-ready", "Assemble-ready", "assembly", "terminal", false,
            (input, ctx, ct) => new ValueTask<AssembleReadyOutput>(new AssembleReadyOutput(
                RunId: input.RunId,
                WorktreeBranch: input.WorktreeBranch,
                TreeHash: input.TreeHash,
                Diff: input.Diff,
                HasChanges: !string.IsNullOrEmpty(input.Diff),
                StepCount: input.StepCount,
                RaiSafetyFlagged: input.ContentSafetyFlagged)));

        ExecutorBinding terminalDeclined = new VisualFunctionExecutor<WorkflowReviewDecision, DeclinedOutput>(
            "terminal-declined", "terminal-declined", "Declined", "plumbing", "terminal", true,
            async (input, ctx, ct) =>
            {
                var agentInput = await ctx.ReadStateAsync<AgentTurnInput>("agent-input", "run-context", ct)
                    .ConfigureAwait(false);
                return new DeclinedOutput(agentInput?.RunId ?? string.Empty);
            });

        // Iteration cap: review requested changes but max iterations reached.
        ExecutorBinding terminalIterationCapped = new VisualFunctionExecutor<AgentTurnInput, DeclinedOutput>(
            "terminal-iteration-capped", "terminal-iteration-capped", "Iteration capped", "plumbing", "terminal", true,
            (input, ctx, ct) => new ValueTask<DeclinedOutput>(new DeclinedOutput(input.RunId)));

        ExecutorBinding terminalSafetyFailed = new VisualFunctionExecutor<AgentTurnOutput, ContentSafetyFailedOutput>(
            "terminal-safety-failed", "terminal-safety-failed", "Safety failed", "plumbing", "terminal", true,
            (input, ctx, ct) => new ValueTask<ContentSafetyFailedOutput>(new ContentSafetyFailedOutput(input.RunId)));

        ExecutorBinding terminalMerge = new VisualFunctionExecutor<MergeOutput, MergeOutput>(
            "terminal-merge", "terminal-merge", "Merge result", "plumbing", "terminal", true,
            (input, ctx, ct) => new ValueTask<MergeOutput>(input));

        // Blocked adapter: on a retriable block, re-enter the review gate via HITL
        // so the workflow stays alive and the user can re-approve once the blocker clears.
        ExecutorBinding blockedAdapter = new VisualFunctionExecutor<MergeOutput, WorkflowReviewRequest>(
            "blocked-adapter", "blocked-adapter", "Blocked adapter", "plumbing", "action", true,
            async (output, ctx, ct) =>
            {
                var agentOutput = await ctx.ReadStateAsync<AgentTurnOutput>(MergeDataKey, MergeDataScope, ct)
                    .ConfigureAwait(false);
                return new WorkflowReviewRequest(
                    agentOutput!.RunId, agentOutput.TreeHash, agentOutput.Diff, agentOutput.StepCount,
                    RaiSafetyFlagged: agentOutput.ContentSafetyFlagged);
            });

        // Store AgentTurnInput in workflow state at workflow start so Scribe adapters
        // can read project/agent context after the review-gate checkpoint/resume cycle.
        ExecutorBinding agentInputStorer = new VisualFunctionExecutor<AgentTurnInput, AgentTurnInput>(
            "agent-input-storer", "agent-input-storer", "Agent input", "plumbing", "action", true,
            async (input, ctx, ct) =>
            {
                await ctx.QueueStateUpdateAsync("agent-input", input, "run-context", ct).ConfigureAwait(false);
                return input;
            });

        // Two separate ScribeTurnExecutor instances to avoid single-node-multiple-inputs
        // ambiguity in MAF's graph builder. Each creates its own ephemeral ScribeAIAgent.
        var scribeMergeExec = new ScribeTurnExecutor(
            _copilotClientFactory, _scopeProvider, _sandboxExecutor, _sandboxPolicyStore,
            _approvalStore, _toolApprovalGate, _loggerFactory, GetRecordingWriter, "scribe-turn-merge",
            createSubStream: CreateSubStreamWriter, completeSubStream: CompleteSubStream,
            apiBaseUrl: _apiBaseUrl, apiKey: _apiKey, agentFactory: _agentFactory);
        var scribeNoChangesExec = new ScribeTurnExecutor(
            _copilotClientFactory, _scopeProvider, _sandboxExecutor, _sandboxPolicyStore,
            _approvalStore, _toolApprovalGate, _loggerFactory, GetRecordingWriter, "scribe-turn-no-changes",
            createSubStream: CreateSubStreamWriter, completeSubStream: CompleteSubStream,
            apiBaseUrl: _apiBaseUrl, apiKey: _apiKey, agentFactory: _agentFactory);
        ExecutorBinding scribeBindingMerge = scribeMergeExec;
        ExecutorBinding scribeBindingNoChanges = scribeNoChangesExec;

        // Rai RAI gate: runs after the agent turn, before the content-safety/no-op/review
        // fork. A RED verdict flips ContentSafetyFlagged so the workflow routes to the
        // safety terminal. Ephemeral RaiAIAgent per execution.
        var raiTurnExec = new RaiTurnExecutor(
            _copilotClientFactory, _scopeProvider, _sandboxExecutor, _sandboxPolicyStore,
            _approvalStore, _toolApprovalGate, _loggerFactory, GetRecordingWriter, "rai-turn",
            createSubStream: CreateSubStreamWriter, completeSubStream: CompleteSubStream,
            agentFactory: _agentFactory);
        ExecutorBinding raiBinding = raiTurnExec;

        // Scribe input adapters: read run context from DB (reliable) to build ScribeTurnInput.
        // Previously used ctx.ReadStateAsync("agent-input", "run-context") but QueueStateUpdateAsync
        // is a deferred/queued write that may not be visible across checkpoint boundaries.
        ExecutorBinding scribeInputMerge = new VisualFunctionExecutor<MergeOutput, ScribeTurnInput>(
            "scribe-input-merge", "scribe", "Scribe", "scribe", "agent", false,
            async (output, ctx, ct) =>
            {
                var log = _loggerFactory.CreateLogger<RunWorkflowFactory>();
                Agentweaver.Domain.Run? run = null;
                if (!RunId.TryParse(output.RunId, out var rid))
                {
                    log.LogWarning("scribe-input-merge: RunId.TryParse failed for value '{RunId}' — will fall back to workflow context", output.RunId);
                }
                else
                {
                    run = await _runStore.GetAsync(rid, ct).ConfigureAwait(false);
                    if (run is null)
                        log.LogWarning("scribe-input-merge: _runStore.GetAsync returned null for RunId '{RunId}' — will fall back to workflow context", output.RunId);
                    else if (string.IsNullOrEmpty(run.AgentName))
                        log.LogWarning("scribe-input-merge: run {RunId} has no AgentName — will fall back to workflow context", output.RunId);
                    else if (run.ProjectId is null)
                        log.LogWarning("scribe-input-merge: run {RunId} has no ProjectId — will fall back to workflow context", output.RunId);
                }

                // Fall back to workflow context when DB run is missing fields.
                // AgentTurnInput is stored at ("agent-input","run-context") by the workflow entry storer.
                string? projectId = run?.ProjectId?.ToString();
                string? agentName = run?.AgentName;
                if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(agentName))
                {
                    var agentInput = await ctx.ReadStateAsync<AgentTurnInput>("agent-input", "run-context", ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(projectId) && !string.IsNullOrEmpty(agentInput?.ProjectId))
                    {
                        projectId = agentInput!.ProjectId;
                        log.LogInformation("scribe-input-merge: resolved ProjectId from workflow context for run {RunId}", output.RunId);
                    }
                    if (string.IsNullOrEmpty(agentName) && !string.IsNullOrEmpty(agentInput?.AgentName))
                    {
                        agentName = agentInput!.AgentName;
                        log.LogInformation("scribe-input-merge: resolved AgentName from workflow context for run {RunId}", output.RunId);
                    }
                }

                return new ScribeTurnInput(
                    output.RunId,
                    projectId ?? "",
                    agentName ?? "",
                    run?.StartedAt ?? DateTimeOffset.UtcNow,
                    run?.RepositoryPath ?? "",
                    run?.ModelSource.ToApiString() ?? "github-copilot",
                    run?.ModelId,
                    TerminalStatus: output.Status,
                    MergeResult: output.MergeResult,
                    MergeMode: output.MergeMode);
            });

        ExecutorBinding scribeInputNoChanges = new VisualFunctionExecutor<NoChangesOutput, ScribeTurnInput>(
            "scribe-input-no-changes", "scribe", "Scribe", "scribe", "agent", false,
            async (output, ctx, ct) =>
            {
                var log = _loggerFactory.CreateLogger<RunWorkflowFactory>();
                Agentweaver.Domain.Run? run = null;
                if (!RunId.TryParse(output.RunId, out var rid))
                {
                    log.LogWarning("scribe-input-no-changes: RunId.TryParse failed for value '{RunId}' — will fall back to workflow context", output.RunId);
                }
                else
                {
                    run = await _runStore.GetAsync(rid, ct).ConfigureAwait(false);
                    if (run is null)
                        log.LogWarning("scribe-input-no-changes: _runStore.GetAsync returned null for RunId '{RunId}' — will fall back to workflow context", output.RunId);
                    else if (string.IsNullOrEmpty(run.AgentName))
                        log.LogWarning("scribe-input-no-changes: run {RunId} has no AgentName — will fall back to workflow context", output.RunId);
                    else if (run.ProjectId is null)
                        log.LogWarning("scribe-input-no-changes: run {RunId} has no ProjectId — will fall back to workflow context", output.RunId);
                }

                // Fall back to workflow context when DB run is missing fields.
                // AgentTurnInput is stored at ("agent-input","run-context") by the workflow entry storer.
                string? projectId = run?.ProjectId?.ToString();
                string? agentName = run?.AgentName;
                if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(agentName))
                {
                    var agentInput = await ctx.ReadStateAsync<AgentTurnInput>("agent-input", "run-context", ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(projectId) && !string.IsNullOrEmpty(agentInput?.ProjectId))
                    {
                        projectId = agentInput!.ProjectId;
                        log.LogInformation("scribe-input-no-changes: resolved ProjectId from workflow context for run {RunId}", output.RunId);
                    }
                    if (string.IsNullOrEmpty(agentName) && !string.IsNullOrEmpty(agentInput?.AgentName))
                    {
                        agentName = agentInput!.AgentName;
                        log.LogInformation("scribe-input-no-changes: resolved AgentName from workflow context for run {RunId}", output.RunId);
                    }
                }

                return new ScribeTurnInput(
                    output.RunId,
                    projectId ?? "",
                    agentName ?? "",
                    run?.StartedAt ?? DateTimeOffset.UtcNow,
                    run?.RepositoryPath ?? "",
                    run?.ModelSource.ToApiString() ?? "github-copilot",
                    run?.ModelId,
                    TerminalStatus: "no_changes");
            });

        // Scribe output adapters: reconstruct terminal output types from pass-through.
        ExecutorBinding scribeOutputMerge = new VisualFunctionExecutor<ScribeTurnInput, MergeOutput>(
            "scribe-output-merge", "scribe", "Scribe", "scribe", "agent", false,
            (input, ctx, ct) => new ValueTask<MergeOutput>(
                new MergeOutput(input.RunId, input.TerminalStatus ?? "merged", input.MergeResult, input.MergeMode)));

        ExecutorBinding scribeOutputNoChanges = new VisualFunctionExecutor<ScribeTurnInput, NoChangesOutput>(
            "scribe-output-no-changes", "scribe", "Scribe", "scribe", "agent", false,
            (input, ctx, ct) => new ValueTask<NoChangesOutput>(new NoChangesOutput(input.RunId)));

        ExecutorBinding agentBinding = agentTurnExecutor;
        ExecutorBinding mergeBinding = mergeExecutor;
        ExecutorBinding reviewBinding = reviewPort;

        // Rai REVISE adapter: reads stored agent-input, appends Rai feedback to Task,
        // increments Iteration so the agent knows it's a revision pass.
        ExecutorBinding raiRevisionAdapter = new VisualFunctionExecutor<AgentTurnOutput, AgentTurnInput>(
            "rai-revision-adapter", "rai-revision-adapter", "RAI revision", "plumbing", "action", true,
            async (raiOutput, ctx, ct) =>
            {
                var agentInput = await ctx.ReadStateAsync<AgentTurnInput>("agent-input", "run-context", ct)
                    .ConfigureAwait(false);
                var nextIteration = (agentInput?.Iteration ?? 0) + 1;
                var revisedTask = string.IsNullOrEmpty(raiOutput.RaiFeedback)
                    ? agentInput?.Task ?? string.Empty
                    : $"{agentInput?.Task ?? string.Empty}\n\n[Rai feedback — iteration {nextIteration}]: {raiOutput.RaiFeedback}";
                var revised = (agentInput ?? new AgentTurnInput(
                    RunId: raiOutput.RunId, Task: string.Empty, WorktreePath: string.Empty,
                    WorktreeBranch: string.Empty, RepositoryPath: string.Empty,
                    OriginatingBranch: string.Empty, ModelSource: string.Empty, ModelId: null,
                    SubmittingUser: string.Empty)) with
                {
                    Task = revisedTask,
                    Iteration = nextIteration,
                    MaxIterationsReached = nextIteration >= MaxIterations,
                };
                await ctx.QueueStateUpdateAsync("agent-input", revised, "run-context", ct).ConfigureAwait(false);
                return revised;
            });

        // Review RequestChanges adapter: reads stored agent-input, appends review feedback,
        // increments Iteration. No cap — reviewers can request as many changes as needed.
        ExecutorBinding reviewChangesAdapter = new VisualFunctionExecutor<WorkflowReviewDecision, AgentTurnInput>(
            "review-changes-adapter", "review-changes-adapter", "Review changes", "plumbing", "action", true,
            async (decision, ctx, ct) =>
            {
                var agentInput = await ctx.ReadStateAsync<AgentTurnInput>("agent-input", "run-context", ct)
                    .ConfigureAwait(false);
                var nextIteration = (agentInput?.Iteration ?? 0) + 1;
                var revisedTask = string.IsNullOrEmpty(decision.Feedback)
                    ? agentInput?.Task ?? string.Empty
                    : $"{agentInput?.Task ?? string.Empty}\n\n[Review feedback — iteration {nextIteration}]: {decision.Feedback}";
                var revised = (agentInput ?? new AgentTurnInput(
                    RunId: string.Empty, Task: string.Empty, WorktreePath: string.Empty,
                    WorktreeBranch: string.Empty, RepositoryPath: string.Empty,
                    OriginatingBranch: string.Empty, ModelSource: string.Empty, ModelId: null,
                    SubmittingUser: string.Empty)) with
                {
                    Task = revisedTask,
                    Iteration = nextIteration,
                    MaxIterationsReached = false,
                };
                await ctx.QueueStateUpdateAsync("agent-input", revised, "run-context", ct).ConfigureAwait(false);
                return revised;
            });

        // ----- Coordinator CHILD pipeline (B1) ---------------------------------------------
        // Trimmed graph: agentInputStorer -> agent -> RAI (+ the existing RAI revise loop),
        // then EVERY non-revision RAI outcome routes to childAssembleReady instead of the
        // review gate. No review-gate RequestPort, no MergeExecutor, no ScribeTurnExecutor.
        // The two edges are mutually exclusive and exhaustive over all RAI outputs, so a child
        // can never hang: it either loops (revision under cap) or terminalizes assemble-ready
        // (OK / RED / empty-diff no-op / revise-at-cap).
        if (isChild)
        {
            var childBuilder = new GraphDescriptorBuilder(agentInputStorer)
                .AddEdge(agentInputStorer, agentBinding)
                .AddEdge(agentBinding, raiBinding)
                // RAI REVISE (iteration < cap) -> revision adapter -> loop back to agent
                .AddEdge<AgentTurnOutput>(raiBinding, raiRevisionAdapter,
                    output => output is not null && output.RaiRevisionRequired && output.Iteration < MaxIterations)
                .AddEdge(raiRevisionAdapter, agentBinding, idempotent: true)
                // Everything else (OK, RED, empty-diff no-op, revise-at-cap) -> assemble-ready terminal
                .AddEdge<AgentTurnOutput>(raiBinding, childAssembleReady,
                    output => output is not null && !(output.RaiRevisionRequired && output.Iteration < MaxIterations))
                .WithOutputFrom(childAssembleReady);
            var childWf = childBuilder.Build();
            var childDescriptor = childBuilder.BuildDescriptor("agentweaver-workflow-child", "child");
            return (childWf, childDescriptor, childBuilder.BuildExecutorMetaMap());
        }

        var fullBuilder = new GraphDescriptorBuilder(agentInputStorer)
            // storer -> agent turn (unconditional)
            .AddEdge(agentInputStorer, agentBinding)
            // agent turn -> Rai RAI gate (unconditional)
            .AddEdge(agentBinding, raiBinding)
            // Rai REVISE (iteration < cap) -> revision adapter -> loop back to agent
            .AddEdge<AgentTurnOutput>(raiBinding, raiRevisionAdapter,
                output => output is not null && output.RaiRevisionRequired && output.Iteration < MaxIterations)
            .AddEdge(raiRevisionAdapter, agentBinding, idempotent: true)
            // Content safety: the agent turn itself was flagged (empty diff, ContentSafetyFlagged)
            // -> fail immediately, never reaching review. Note Rai RED keeps a non-empty diff and
            // therefore routes to the review gate below (human has final say), unchanged.
            .AddEdge<AgentTurnOutput>(raiBinding, terminalSafetyFailed,
                output => output is not null && !output.RaiRevisionRequired
                    && string.IsNullOrEmpty(output.Diff) && output.ContentSafetyFlagged)
            // No changes -> no-op -> scribe path
            .AddEdge<AgentTurnOutput>(raiBinding, terminalNoOp,
                output => output is not null && !output.RaiRevisionRequired
                    && string.IsNullOrEmpty(output.Diff) && !output.ContentSafetyFlagged)
            .AddEdge(terminalNoOp, scribeInputNoChanges)
            .AddEdge(scribeInputNoChanges, scribeBindingNoChanges)
            .AddEdge(scribeBindingNoChanges, scribeOutputNoChanges)
            // Everything else (OK, RED, REVISE cap) -> review adapter -> human review gate.
            // RaiSafetyFlagged is set so the reviewer can see Rai's verdict as advisory context.
            .AddEdge<AgentTurnOutput>(raiBinding, reviewAdapter,
                output => output is not null && !output.RaiRevisionRequired && !string.IsNullOrEmpty(output.Diff))
            // Review adapter -> review gate
            .AddEdge(reviewAdapter, reviewBinding)
            // Approved -> merge adapter -> merge executor
            .AddEdge<WorkflowReviewDecision>(reviewBinding, mergeAdapter,
                decision => decision is not null && decision.Approved)
            .AddEdge(mergeAdapter, mergeBinding)
            // Merge succeeded or failed terminally -> scribe path
            .AddEdge<MergeOutput>(mergeBinding, terminalMerge,
                output => output is not null && output.Status != "blocked")
            .AddEdge(terminalMerge, scribeInputMerge)
            .AddEdge(scribeInputMerge, scribeBindingMerge)
            .AddEdge(scribeBindingMerge, scribeOutputMerge)
            // Merge blocked -> re-enter review gate via HITL
            .AddEdge<MergeOutput>(mergeBinding, blockedAdapter,
                output => output is not null && output.Status == "blocked")
            .AddEdge(blockedAdapter, reviewBinding, idempotent: true)
            // Review RequestChanges -> revision adapter -> loop back to agent (no cap)
            .AddEdge<WorkflowReviewDecision>(reviewBinding, reviewChangesAdapter,
                decision => decision is not null && !decision.Approved && decision.RequestChanges)
            .AddEdge(reviewChangesAdapter, agentBinding, idempotent: true)
            // Hard-declined -> terminal
            .AddEdge<WorkflowReviewDecision>(reviewBinding, terminalDeclined,
                decision => decision is null || (!decision.Approved && !decision.RequestChanges))
            // Outputs
            .WithOutputFrom(scribeOutputMerge)
            .WithOutputFrom(scribeOutputNoChanges)
            .WithOutputFrom(terminalSafetyFailed)
            .WithOutputFrom(terminalDeclined);

        var wf = fullBuilder.Build();
        var descriptor = fullBuilder.BuildDescriptor("agentweaver-workflow-full", "full");

        return (wf, descriptor, fullBuilder.BuildExecutorMetaMap());
    }

    /// <summary>
    /// Launches a new streaming workflow run. When <paramref name="isChild"/> is true
    /// (the run carries <c>ParentRunId</c>), the trimmed coordinator CHILD pipeline is used:
    /// agent + RAI terminating assemble-ready, with no per-child review gate / merge / scribe.
    /// </summary>
    public async Task<StreamingRun> StartAsync(AgentTurnInput input, string runId, CancellationToken ct, bool isChild = false)
    {
        var (workflow, descriptor, executorMeta) = BuildWorkflow(isChild);
        // Capture the executorId -> render-metadata map so the watch loop can translate MAF executor
        // lifecycle events into workflow.step UI events for nodes without a dedicated self-emitter.
        _runExecutorMeta[runId] = executorMeta;
        // Emit the per-run workflow graph snapshot at run start so the SSE stream carries the
        // descriptor; it is persisted alongside other RunEvents at terminal states so the REST
        // seed path (/api/runs/{id}/events) and /api/runs/{id}/graph work for finished runs.
        _streamStore.Get(runId)?.RecordNext(EventTypes.WorkflowGraph, descriptor);
        return await InProcessExecution.RunStreamingAsync(
            workflow, input, _checkpointManager, runId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Build the per-run workflow graph descriptor (the dynamic visualization). Deterministic and
    /// side-effect free: it constructs the executors and wires the graph but does not run it. The
    /// <c>GET /api/runs/{id}/graph</c> endpoint uses this; pass <paramref name="isChild"/>
    /// = true for coordinator child runs (run.ParentRunId != null), false for the full pipeline.
    /// </summary>
    public GraphDescriptor GetGraphDescriptor(bool isChild) => BuildWorkflow(isChild).Descriptor;

    /// <summary>
    /// Test seam (drift-guard): builds the workflow AND its descriptor for a variant so the test can
    /// reflect the built MAF graph (ReflectExecutors/ReflectEdges) and assert the descriptor stays in
    /// sync with the wired executors. Reflection is used ONLY by that build-time test, never at runtime.
    /// </summary>
    internal (Workflow Workflow, GraphDescriptor Descriptor) BuildWorkflowForTest(bool isChild)
    {
        var (workflow, descriptor, _) = BuildWorkflow(isChild);
        return (workflow, descriptor);
    }

    /// <summary>
    /// Test seam: the executorId -> render-metadata map the watch loop uses to translate MAF executor
    /// lifecycle events into <c>workflow.step</c> UI events. Lets tests assert the gap-node mapping
    /// (e.g. <c>child-assemble-ready</c> -&gt; <c>assemble-ready</c>) without running a workflow.
    /// </summary>
    internal IReadOnlyDictionary<string, ExecutorNodeMeta> BuildExecutorMetaForTest(bool isChild) =>
        BuildWorkflow(isChild).ExecutorMeta;

    /// <summary>
    /// Resumes a workflow run from checkpoint. The pipeline shape (full vs trimmed child)
    /// is reselected from the persisted run's <c>ParentRunId</c> so a resumed child keeps
    /// its trimmed graph.
    /// </summary>
    public async Task<StreamingRun> ResumeAsync(CheckpointInfo checkpointInfo, CancellationToken ct)
    {
        var isChild = false;
        if (RunId.TryParse(checkpointInfo.SessionId, out var rid))
        {
            var run = await _runStore.GetAsync(rid, ct).ConfigureAwait(false);
            isChild = run?.ParentRunId is not null;
        }
        var (workflow, _, executorMeta) = BuildWorkflow(isChild);
        _runExecutorMeta[checkpointInfo.SessionId] = executorMeta;
        return await InProcessExecution.ResumeStreamingAsync(
            workflow, checkpointInfo, _checkpointManager, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the render metadata for a live MAF executor id within a run, if the run's workflow
    /// was built through this factory (StartAsync/ResumeAsync). Used by the watch loop to translate
    /// MAF executor lifecycle events into <c>workflow.step</c> UI events.
    /// </summary>
    public bool TryGetExecutorMeta(string runId, string executorId, out ExecutorNodeMeta meta)
    {
        meta = null!;
        return _runExecutorMeta.TryGetValue(runId, out var map)
            && map.TryGetValue(executorId, out meta!);
    }

    /// <summary>
    /// Drops the cached executor metadata for a run. Called on genuine terminal cleanup so the
    /// per-run map cannot leak. Not called on the non-terminal blocked/abandoned path — a revision
    /// restart repopulates the map via <see cref="StartAsync"/>.
    /// </summary>
    public void ClearRunExecutorMeta(string runId) => _runExecutorMeta.TryRemove(runId, out _);

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
