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
using Agentweaver.Api.ReviewPolicies;
using Agentweaver.Api.Runs.Graph;
using Agentweaver.Api.Workflows;
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
    private readonly IRunEventStream? _eventStream;
    private readonly IRunStore _runStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkflowAgentFactory _agentFactory;
    private readonly IProjectStore? _projectStore;
    private readonly WorkflowRegistry? _workflowRegistry;
    private readonly ReviewPolicyRegistry? _reviewPolicyRegistry;
    private readonly IBacklogTaskStore? _backlogTaskStore;
    private readonly CheckpointManager _checkpointManager;
    private readonly string _checkpointDir;
    private readonly ICheckpointStoreFactory _checkpointStoreFactory;
    private readonly string? _apiBaseUrl;
    private readonly string? _apiKey;

    // Per-run snapshot of executorId -> render metadata, captured when the run's workflow is built
    // (StartAsync/ResumeAsync). The watch loop uses it to translate MAF executor lifecycle events
    // into workflow.step UI events. Cleared on genuine terminal cleanup so it cannot leak.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<string, ExecutorNodeMeta>> _runExecutorMeta = new();

    public CheckpointManager CheckpointManager => _checkpointManager;
    public string CheckpointDirectory => _checkpointDir;

    /// <summary>
    /// The resolved API base URL used by Scribe for loopback memory-tool calls.
    /// Exposed so <see cref="Agentweaver.Api.Coordinator.CollectiveAssemblyPipeline"/> can reuse
    /// the same resolved value rather than duplicating the config-key lookup.
    /// </summary>
    internal string? ApiBaseUrl => _apiBaseUrl;

    /// <summary>
    /// The resolved API key used by Scribe for loopback memory-tool calls.
    /// Exposed so <see cref="Agentweaver.Api.Coordinator.CollectiveAssemblyPipeline"/> can reuse
    /// the same resolved value rather than duplicating the config-key lookup.
    /// </summary>
    internal string? ApiKey => _apiKey;

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
        IRunStore runStore,
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory,
        IWorkflowAgentFactory agentFactory,
        IConfiguration configuration,
        IRunEventStream? eventStream = null,
        IBacklogTaskStore? backlogTaskStore = null)
        : this(
            agentRunner,
            copilotClientFactory,
            scopeProvider,
            sandboxExecutor,
            sandboxPolicyStore,
            approvalStore,
            toolApprovalGate,
            worktreeOps,
            mergeCoordinator,
            streamStore,
            runStore,
            loggerFactory,
            scopeFactory,
            agentFactory,
            configuration,
            projectStore: null,
            workflowRegistry: null,
            reviewPolicyRegistry: null,
            eventStream: eventStream,
            backlogTaskStore: backlogTaskStore)
    {
    }

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
        IRunStore runStore,
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory,
        IWorkflowAgentFactory agentFactory,
        IConfiguration configuration,
        IProjectStore? projectStore,
        WorkflowRegistry? workflowRegistry,
        ReviewPolicyRegistry? reviewPolicyRegistry,
        IRunEventStream? eventStream = null,
        IBacklogTaskStore? backlogTaskStore = null,
        ICheckpointStoreFactory? checkpointStoreFactory = null)
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
        _eventStream = eventStream;
        _runStore = runStore;
        _loggerFactory = loggerFactory;
        _scopeFactory = scopeFactory;
        _agentFactory = agentFactory;
        _projectStore = projectStore;
        _workflowRegistry = workflowRegistry;
        _reviewPolicyRegistry = reviewPolicyRegistry;
        _backlogTaskStore = backlogTaskStore;

        // Checkpoint directory: configurable via Checkpoints:Path; defaults to
        // AppPaths.DataDirectory/checkpoints so production needs no explicit config.
        // KNOWN LIMITATION: Checkpoint JSON is stored unencrypted at rest. For multi-tenant
        // deployments, add encryption-at-rest (Seraph #4). Not needed for single-user slice.
        _checkpointDir = configuration["Checkpoints:Path"]
            ?? Path.Combine(AppPaths.DataDirectory, "checkpoints");
        Directory.CreateDirectory(_checkpointDir);

        _apiBaseUrl = ResolveApiBaseUrl(configuration);
        // Prefer the single-key shorthand; fall back to the first entry in the multi-key list
        // so Scribe can always authenticate its self-calls regardless of which format the user uses.
        _apiKey = configuration["Auth:ApiKey"]
            ?? configuration.GetSection("Auth:Keys").GetChildren().FirstOrDefault()?["Token"];

        // Production (Postgres) uses a shared, concurrency-safe checkpoint store so both replicas read
        // and write the same checkpoints; local/dev (sqlite) falls back to the per-pod file store.
        // The selector is optional so the convenience ctor / tests still get the file store.
        var store = (_checkpointStoreFactory = checkpointStoreFactory ?? new FileCheckpointStoreFactory())
            .Create("runs", _checkpointDir, _loggerFactory.CreateLogger<RunWorkflowFactory>());
        _checkpointManager = CheckpointManager.CreateJson(store);
    }

    /// <summary>
    /// Resolves the API base URL used for in-process loopback tool calls (Scribe and every
    /// agent's decision/memory/inbox tools). Resolution order:
    /// <list type="number">
    ///   <item>Explicit <c>Agentweaver:ApiBaseUrl</c> config / env override.</item>
    ///   <item>Derived from the server's actual Kestrel binding (<c>ASPNETCORE_URLS</c> / the
    ///   <c>urls</c> config key), replacing a wildcard host with <c>localhost</c>. This makes
    ///   loopback calls hit the real listening port (e.g. 8080 in the container) without any
    ///   extra configuration.</item>
    ///   <item>Legacy dev fallback <c>http://localhost:5000</c>.</item>
    /// </list>
    /// Without this, an unset override on a non-5000 binding (AKS binds 8080) sent every loopback
    /// tool call to a dead port → connection refused → "Tool execution failed".
    /// </summary>
    internal static string ResolveApiBaseUrl(IConfiguration configuration)
    {
        var explicitUrl = configuration["Agentweaver:ApiBaseUrl"];
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            return explicitUrl;
        }

        // ASP.NET Core surfaces ASPNETCORE_URLS / DOTNET_URLS via the "urls" config key.
        var serverUrls = configuration["urls"] ?? configuration["ASPNETCORE_URLS"];
        var derived = DeriveLoopbackUrl(serverUrls);
        return derived ?? "http://localhost:5000";
    }

    /// <summary>
    /// Converts a server binding string (possibly semicolon-separated, possibly using a wildcard
    /// host such as <c>+</c>, <c>*</c>, <c>0.0.0.0</c>, or <c>[::]</c>) into a concrete loopback
    /// URL. Prefers an <c>http</c> binding over <c>https</c> to avoid self-signed cert issues on
    /// the loopback path. Returns null if nothing parseable is found.
    /// </summary>
    private static string? DeriveLoopbackUrl(string? serverUrls)
    {
        if (string.IsNullOrWhiteSpace(serverUrls))
        {
            return null;
        }

        var candidates = serverUrls
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? Normalize(string url)
        {
            // Replace wildcard hosts with localhost so the URI is dialable. Uri.TryCreate rejects
            // '+' and '*' as authority, so substitute before parsing.
            var replaced = url
                .Replace("://+:", "://localhost:")
                .Replace("://*:", "://localhost:")
                .Replace("://0.0.0.0:", "://localhost:")
                .Replace("://[::]:", "://localhost:");

            if (!Uri.TryCreate(replaced, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var host = uri.Host is "0.0.0.0" or "::" or "[::]" ? "localhost" : uri.Host;
            return $"{uri.Scheme}://{host}:{uri.Port}";
        }

        // Prefer http over https for loopback.
        var http = candidates.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        var chosen = Normalize(http ?? candidates[0]);
        return chosen;
    }

    public ChannelWriter<RunEvent>? GetRecordingWriter(string runId)
    {
        var entry = _streamStore.Get(runId);
        return entry is not null ? new RecordingChannelWriter(entry, runId, _eventStream) : null;
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
        return new RecordingChannelWriter(entry, subRunId, _eventStream);
    }

    public void CompleteSubStream(string subRunId)
    {
        _streamStore.Complete(subRunId);
        _ = PersistRunEventsAsync(subRunId);
    }

    /// <summary>
    /// Persists the in-memory event history for <paramref name="runId"/> to the
    /// <see cref="RunEventRecord"/> table so the Watch page can replay them after the
    /// stream entry is evicted from <see cref="RunStreamStore"/>, and signals run completion
    /// to the durable event stream so live subscribers terminate cleanly.
    /// Idempotent: already-persisted sequences are skipped. Fire-and-forget safe.
    /// With per-append durability (016-run-event-stream) most events are already persisted;
    /// this serves as a terminal backfill safety net and the channel-close signal.
    /// </summary>
    public async Task PersistRunEventsAsync(string runId)
    {
        try
        {
            var entry = _streamStore.Get(runId);
            var events = entry?.GetSnapshotSince(0).Events ?? [];

            if (events.Count > 0)
            {
                if (_eventStream is not null)
                {
                    // Durable write-through is idempotent on the unique (RunId, Sequence) index,
                    // so re-appending the full history reconciles any gaps left by a dropped
                    // per-append mirror without duplicating rows.
                    foreach (var e in events)
                        await _eventStream.AppendAsync(runId, e).ConfigureAwait(false);
                }
                else
                {
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

                    if (toInsert.Count > 0)
                    {
                        db.RunEvents.AddRange(toInsert);
                        await db.SaveChangesAsync().ConfigureAwait(false);
                    }
                }
            }

            // Close the live channel so any IRunEventStream subscribers drain and complete.
            if (_eventStream is not null)
                await _eventStream.CompleteAsync(runId).ConfigureAwait(false);
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

    private (Workflow Workflow, GraphDescriptor Descriptor, IReadOnlyDictionary<string, ExecutorNodeMeta> ExecutorMeta) BuildWorkflow(
        bool isChild = false,
        WorkflowDefinition? effectiveDefinition = null)
    {
        // A fresh worker agent per workflow build (per run), resolved through the injectable
        // IWorkflowAgentFactory seam. In production this builds a CopilotAIAgent — an AIAgent the
        // MAF checkpoint manager can serialize, so the Copilot SDK session is persisted into the
        // FileSystem checkpoint alongside the workflow state. Tests substitute a fake agent.
        var copilotAgent = _agentFactory.CreateWorkerAgent();

        // Resolve an inline bespoke charter from the effective definition's agent node (if any), so a
        // generated workflow that mints a domain-specific role with an inline `charter` runs the agent
        // under that persona. Catalog-role nodes carry no charter and fall through to file resolution.
        var agentNodeCharter = ResolveAgentNodeCharter(
            effectiveDefinition ?? Workflows.BuiltInWorkflows.Default.Definition!);

        var agentTurnExecutor = new AgentTurnExecutor(
            copilotAgent,
            _worktreeOps,
            GetRecordingWriter,
            _loggerFactory.CreateLogger<AgentTurnExecutor>(),
            apiBaseUrl: _apiBaseUrl,
            apiKey: _apiKey,
            agentNodeCharter: agentNodeCharter);

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
                    agentOutput.OriginatingBranch,
                    ReviewedBy: decision.ReviewedBy);
            });

        ExecutorBinding policyAgentOutputAdapter = new VisualFunctionExecutor<WorkflowReviewDecision, AgentTurnOutput>(
            "policy-agent-output-adapter", "policy-agent-output-adapter", "Policy gate input", "plumbing", "action", true,
            async (decision, ctx, ct) =>
            {
                var agentOutput = await ctx.ReadStateAsync<AgentTurnOutput>(MergeDataKey, MergeDataScope, ct)
                    .ConfigureAwait(false);
                return agentOutput!;
            });

        ExecutorBinding policyAgentTurnStorer = new VisualFunctionExecutor<AgentTurnOutput, AgentTurnOutput>(
            "policy-agent-turn-storer", "policy-agent-turn-storer", "Policy agent output", "plumbing", "action", true,
            async (output, ctx, ct) =>
            {
                await ctx.QueueStateUpdateAsync(MergeDataKey, output, MergeDataScope, ct)
                    .ConfigureAwait(false);
                return output;
            });

        ExecutorBinding policyDirectMergeAdapter = new VisualFunctionExecutor<AgentTurnOutput, MergeInput>(
            "policy-direct-merge-adapter", "policy-direct-merge-adapter", "Policy direct merge", "plumbing", "action", true,
            (output, ctx, ct) => new ValueTask<MergeInput>(new MergeInput(
                output.RunId,
                output.TreeHash,
                output.WorktreePath,
                output.WorktreeBranch,
                output.RepositoryPath,
                output.OriginatingBranch)));

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
        var fullDefinition = effectiveDefinition ?? Workflows.BuiltInWorkflows.Default.Definition!;
        var policyGateBindings = BuildPolicyGateBindings(fullDefinition);

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
                if (agentInput is not null && RunId.TryParse(agentInput.RunId, out var parsedRunId))
                {
                    try
                    {
                        await _runStore.TryTransitionReviewToInProgressAsync(parsedRunId, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _loggerFactory.CreateLogger<RunWorkflowFactory>()
                            .LogWarning(ex, "Review-change policy loop could not transition run {RunId} to in_progress", agentInput.RunId);
                    }
                }
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
        //
        // INTENTIONAL DESIGN: child runs bypass the per-child human review gate.
        // Review happens at the aggregate level in CoordinatorAssemblyService (Phase 3), where
        // ONE human review covers the COMBINED output of ALL children. The rationale is that
        // reviewing each child's diff in isolation would be misleading — the meaningful unit of
        // review is the integrated whole, not individual sub-tasks. This is the "collective
        // review" contract documented in Feature 008 Phase 3 (specs/008-coordinator-agent).
        // AssembleReady is the child's terminal state; the coordinator's assembly wave reads
        // Run.WorktreeBranch + Run.TreeHash as the hand-off artefact.
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

        // Definition-driven full pipeline (Feature 010, wf-maf-binding): the graph is assembled by
        // ITERATING the default WorkflowDefinition's edges and resolving each (from, to, when) verdict
        // to the concrete executor wiring + typed predicate, instead of a hand-coded chain. The concrete
        // executors above hold the real behavior (Principle VII); the binder maps the model onto them.
        // PARITY-FIRST: the raw edges/predicates/idempotent-flags/outputs the binder emits are identical
        // to the previous hand-coded wiring, so the collapsed descriptor and executed graph are unchanged.
        // The child pipeline (above) is intentionally left hand-coded for this stage.
        var fullBuilder = new GraphDescriptorBuilder(agentInputStorer);

        // The per-node / per-edge executor mint for generic catalog topologies (Feature 015 US3). Seeded
        // with the canonical "agent" node so the default + review-policy-composed pipelines keep the exact
        // same agent executor instance the policy plumbing references (golden descriptor parity).
        var wiringSupport = new GenericWiringSupport(this, canonicalAgentNodeId: "agent", canonicalAgentBinding: agentBinding);

        RunWorkflowGraphBinder.WireFull(
            fullBuilder,
            fullDefinition,
            new RunWorkflowBindings(
                AgentInputStorer: agentInputStorer,
                AgentBinding: agentBinding,
                RaiBinding: raiBinding,
                RaiRevisionAdapter: raiRevisionAdapter,
                TerminalSafetyFailed: terminalSafetyFailed,
                TerminalNoOp: terminalNoOp,
                ScribeInputNoChanges: scribeInputNoChanges,
                ScribeBindingNoChanges: scribeBindingNoChanges,
                ScribeOutputNoChanges: scribeOutputNoChanges,
                ReviewAdapter: reviewAdapter,
                ReviewBinding: reviewBinding,
                PolicyAgentTurnStorer: policyAgentTurnStorer,
                PolicyAgentOutputAdapter: policyAgentOutputAdapter,
                PolicyDirectMergeAdapter: policyDirectMergeAdapter,
                PolicyGateBindings: policyGateBindings,
                MergeAdapter: mergeAdapter,
                MergeBinding: mergeBinding,
                TerminalMerge: terminalMerge,
                ScribeInputMerge: scribeInputMerge,
                ScribeBindingMerge: scribeBindingMerge,
                ScribeOutputMerge: scribeOutputMerge,
                BlockedAdapter: blockedAdapter,
                ReviewChangesAdapter: reviewChangesAdapter,
                TerminalDeclined: terminalDeclined,
                MaxIterations: MaxIterations,
                Wiring: wiringSupport));

        var wf = fullBuilder.Build();
        var descriptor = fullBuilder.BuildDescriptor("agentweaver-workflow-full", "full");

        return (wf, descriptor, fullBuilder.BuildExecutorMetaMap());
    }

    /// <summary>
    /// Builds a <see cref="ScribeTurnInput"/> for a generic direct-completion scribe path (Feature 015 US3:
    /// <c>Agent → Scribe</c> / <c>Review → Scribe</c> workflows that record an outcome without a merge).
    /// Resolves project/agent context from the persisted run, falling back to the workflow entry context
    /// stored at <c>("agent-input","run-context")</c> — the same precedence the canonical scribe-input
    /// adapters use.
    /// </summary>
    internal async ValueTask<ScribeTurnInput> BuildScribeTurnInputAsync(
        string runId, string terminalStatus, IWorkflowContext ctx, CancellationToken ct)
    {
        var log = _loggerFactory.CreateLogger<RunWorkflowFactory>();
        Agentweaver.Domain.Run? run = null;
        if (RunId.TryParse(runId, out var rid))
            run = await _runStore.GetAsync(rid, ct).ConfigureAwait(false);

        string? projectId = run?.ProjectId?.ToString();
        string? agentName = run?.AgentName;
        if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(agentName))
        {
            var agentInput = await ctx.ReadStateAsync<AgentTurnInput>("agent-input", "run-context", ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(projectId) && !string.IsNullOrEmpty(agentInput?.ProjectId))
                projectId = agentInput!.ProjectId;
            if (string.IsNullOrEmpty(agentName) && !string.IsNullOrEmpty(agentInput?.AgentName))
                agentName = agentInput!.AgentName;
        }

        return new ScribeTurnInput(
            runId,
            projectId ?? "",
            agentName ?? "",
            run?.StartedAt ?? DateTimeOffset.UtcNow,
            run?.RepositoryPath ?? "",
            run?.ModelSource.ToApiString() ?? "github-copilot",
            run?.ModelId,
            TerminalStatus: terminalStatus);
    }

    /// <summary>
    /// The per-node / per-edge executor mint that lets <see cref="RunWorkflowGraphBinder"/> wire the generic
    /// catalog topologies (Feature 015 US3) onto the REAL executors (Principle VII — no mocks). Per-node
    /// agent / peer-review executors are cached by node id so chained turns each get their OWN MAF node;
    /// per-edge adapters are minted fresh (keyed by the edge) so no hidden plumbing node receives two inputs.
    /// </summary>
    private sealed class GenericWiringSupport : IRunWorkflowWiringSupport
    {
        private readonly RunWorkflowFactory _factory;
        private readonly Dictionary<string, ExecutorBinding> _agentNodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExecutorBinding> _peerReviewNodes = new(StringComparer.Ordinal);

        public GenericWiringSupport(
            RunWorkflowFactory factory, string canonicalAgentNodeId, ExecutorBinding canonicalAgentBinding)
        {
            _factory = factory;
            // Seed the canonical agent node so the DEFAULT (and review-policy-composed) pipeline keeps the
            // exact same agent executor instance the policy plumbing also references (b.AgentBinding) — this
            // is what preserves golden descriptor parity and keeps loop-backs targeting one agent node.
            _agentNodes[canonicalAgentNodeId] = canonicalAgentBinding;
        }

        public ExecutorBinding ResolveAgentNode(WorkflowNode node)
        {
            if (_agentNodes.TryGetValue(node.Id, out var existing))
                return existing;

            var agent = _factory._agentFactory.CreateWorkerAgent();
            ExecutorBinding binding = new AgentTurnExecutor(
                agent,
                _factory._worktreeOps,
                _factory.GetRecordingWriter,
                _factory._loggerFactory.CreateLogger<AgentTurnExecutor>(),
                apiBaseUrl: _factory._apiBaseUrl,
                apiKey: _factory._apiKey,
                agentNodeCharter: node.Charter,
                agentNodePrompt: node.Prompt,
                name: $"agent-turn-{node.Id}",
                logicalNodeId: node.Id,
                displayLabel: node.Label);
            _agentNodes[node.Id] = binding;
            return binding;
        }

        public ExecutorBinding ResolvePeerReviewNode(WorkflowNode node)
        {
            if (_peerReviewNodes.TryGetValue(node.Id, out var existing))
                return existing;

            // A peer-review verdict gate is a REAL AI reviewer: the existing RubberduckTurnExecutor takes the
            // produced AgentTurnOutput, runs an AI critique, and emits a WorkflowReviewDecision (PASS→approved,
            // REVISE→request-changes) — exactly the peer_review contract.
            ExecutorBinding binding = new RubberduckTurnExecutor(
                _factory._copilotClientFactory,
                _factory._scopeProvider,
                _factory._sandboxExecutor,
                _factory._sandboxPolicyStore,
                _factory._approvalStore,
                _factory._toolApprovalGate,
                _factory._loggerFactory,
                _factory.GetRecordingWriter,
                name: $"peer-review-{node.Id}",
                logicalNodeId: node.Id,
                displayLabel: node.Label,
                createSubStream: _factory.CreateSubStreamWriter,
                completeSubStream: _factory.CompleteSubStream,
                agentFactory: _factory._agentFactory,
                reviewAgentId: node.Agent,
                reviewAgentCharter: node.Charter);
            _peerReviewNodes[node.Id] = binding;
            return binding;
        }

        private static string EdgeId(string role, WorkflowEdge e) =>
            $"{role}-{e.From}-{e.To}-{e.When ?? "x"}";

        public ExecutorBinding SequentialAgentAdapter(WorkflowEdge edge)
        {
            var id = EdgeId("seq-turn", edge);
            return new VisualFunctionExecutor<AgentTurnOutput, AgentTurnInput>(
                id, id, "Next turn", "plumbing", "action", true,
                async (output, ctx, ct) =>
                {
                    var prev = await ctx.ReadStateAsync<AgentTurnInput>("agent-input", "run-context", ct).ConfigureAwait(false);
                    var next = ContinueTurn(prev, output, "Previous step output", isRevision: false);
                    await ctx.QueueStateUpdateAsync("agent-input", next, "run-context", ct).ConfigureAwait(false);
                    return next;
                });
        }

        public ExecutorBinding ReviewToAgentForwardAdapter(WorkflowEdge edge)
        {
            var id = EdgeId("review-forward", edge);
            return new VisualFunctionExecutor<WorkflowReviewDecision, AgentTurnInput>(
                id, id, "Approved → next turn", "plumbing", "action", true,
                async (decision, ctx, ct) =>
                {
                    var prev = await ctx.ReadStateAsync<AgentTurnInput>("agent-input", "run-context", ct).ConfigureAwait(false);
                    var produced = await ctx.ReadStateAsync<AgentTurnOutput>(MergeDataKey, MergeDataScope, ct).ConfigureAwait(false);
                    var next = produced is not null
                        ? ContinueTurn(prev, produced, "Reviewed and approved output", isRevision: false)
                        : (prev ?? EmptyTurn(string.Empty)) with { IsRevision = false };
                    await ctx.QueueStateUpdateAsync("agent-input", next, "run-context", ct).ConfigureAwait(false);
                    return next;
                });
        }

        public ExecutorBinding ReviewToAgentReviseAdapter(WorkflowEdge edge)
        {
            var id = EdgeId("review-revise", edge);
            return new VisualFunctionExecutor<WorkflowReviewDecision, AgentTurnInput>(
                id, id, "Request changes", "plumbing", "action", true,
                async (decision, ctx, ct) =>
                {
                    var prev = await ctx.ReadStateAsync<AgentTurnInput>("agent-input", "run-context", ct).ConfigureAwait(false);
                    var next = ReviseTurn(prev, decision.Feedback);
                    await ctx.QueueStateUpdateAsync("agent-input", next, "run-context", ct).ConfigureAwait(false);
                    return next;
                });
        }

        public ExecutorBinding StoreAgentOutputAdapter(WorkflowEdge edge)
        {
            var id = EdgeId("store-output", edge);
            return new VisualFunctionExecutor<AgentTurnOutput, AgentTurnOutput>(
                id, id, "Store output", "plumbing", "action", true,
                async (output, ctx, ct) =>
                {
                    await ctx.QueueStateUpdateAsync(MergeDataKey, output, MergeDataScope, ct).ConfigureAwait(false);
                    return output;
                });
        }

        public ExecutorBinding ReviewToAgentOutputAdapter(WorkflowEdge edge)
        {
            var id = EdgeId("review-to-output", edge);
            return new VisualFunctionExecutor<WorkflowReviewDecision, AgentTurnOutput>(
                id, id, "Reconstruct output", "plumbing", "action", true,
                async (decision, ctx, ct) =>
                {
                    var produced = await ctx.ReadStateAsync<AgentTurnOutput>(MergeDataKey, MergeDataScope, ct).ConfigureAwait(false);
                    return produced!;
                });
        }

        public ExecutorBinding ReviewToMergeAdapter(WorkflowEdge edge)
        {
            var id = EdgeId("review-to-merge", edge);
            return new VisualFunctionExecutor<WorkflowReviewDecision, MergeInput>(
                id, id, "Merge adapter", "plumbing", "action", true,
                async (decision, ctx, ct) =>
                {
                    var ao = await ctx.ReadStateAsync<AgentTurnOutput>(MergeDataKey, MergeDataScope, ct).ConfigureAwait(false);
                    return new MergeInput(
                        ao!.RunId, ao.TreeHash, ao.WorktreePath, ao.WorktreeBranch,
                        ao.RepositoryPath, ao.OriginatingBranch, ReviewedBy: decision.ReviewedBy);
                });
        }

        public ExecutorBinding AgentToReviewRequestAdapter(WorkflowEdge edge)
        {
            var id = EdgeId("agent-to-review", edge);
            return new VisualFunctionExecutor<AgentTurnOutput, WorkflowReviewRequest>(
                id, id, "Review adapter", "plumbing", "action", true,
                async (output, ctx, ct) =>
                {
                    await ctx.QueueStateUpdateAsync(MergeDataKey, output, MergeDataScope, ct).ConfigureAwait(false);
                    return new WorkflowReviewRequest(
                        output.RunId, output.TreeHash, output.Diff, output.StepCount,
                        RaiSafetyFlagged: output.ContentSafetyFlagged);
                });
        }

        public ExecutorBinding AgentToMergeAdapter(WorkflowEdge edge)
        {
            var id = EdgeId("agent-to-merge", edge);
            return new VisualFunctionExecutor<AgentTurnOutput, MergeInput>(
                id, id, "Direct merge", "plumbing", "action", true,
                (output, ctx, ct) => new ValueTask<MergeInput>(new MergeInput(
                    output.RunId, output.TreeHash, output.WorktreePath, output.WorktreeBranch,
                    output.RepositoryPath, output.OriginatingBranch)));
        }

        public ExecutorBinding MergeToAgentOutputAdapter(WorkflowEdge edge)
        {
            var id = EdgeId("merge-to-output", edge);
            return new VisualFunctionExecutor<MergeOutput, AgentTurnOutput>(
                id, id, "Blocked → review", "plumbing", "action", true,
                async (mo, ctx, ct) =>
                {
                    var ao = await ctx.ReadStateAsync<AgentTurnOutput>(MergeDataKey, MergeDataScope, ct).ConfigureAwait(false);
                    return ao!;
                });
        }

        public ExecutorBinding MergeToAgentReviseAdapter(WorkflowEdge edge)
        {
            var id = EdgeId("merge-to-revise", edge);
            return new VisualFunctionExecutor<MergeOutput, AgentTurnInput>(
                id, id, "Blocked → revise", "plumbing", "action", true,
                async (mo, ctx, ct) =>
                {
                    var prev = await ctx.ReadStateAsync<AgentTurnInput>("agent-input", "run-context", ct).ConfigureAwait(false);
                    var next = ReviseTurn(prev, feedback: null);
                    await ctx.QueueStateUpdateAsync("agent-input", next, "run-context", ct).ConfigureAwait(false);
                    return next;
                });
        }

        public ScribeSubPath AgentScribePath(WorkflowEdge edge)
        {
            var inputId = $"scribe-input-{edge.From}-{edge.To}";
            ExecutorBinding input = new VisualFunctionExecutor<AgentTurnOutput, ScribeTurnInput>(
                inputId, "scribe", "Scribe", "scribe", "agent", false,
                async (output, ctx, ct) =>
                    await _factory.BuildScribeTurnInputAsync(output.RunId, "completed", ctx, ct).ConfigureAwait(false));
            return BuildScribePath(edge, input);
        }

        public ScribeSubPath ReviewScribePath(WorkflowEdge edge)
        {
            var inputId = $"scribe-input-{edge.From}-{edge.To}";
            ExecutorBinding input = new VisualFunctionExecutor<WorkflowReviewDecision, ScribeTurnInput>(
                inputId, "scribe", "Scribe", "scribe", "agent", false,
                async (decision, ctx, ct) =>
                {
                    var prev = await ctx.ReadStateAsync<AgentTurnInput>("agent-input", "run-context", ct).ConfigureAwait(false);
                    return await _factory.BuildScribeTurnInputAsync(prev?.RunId ?? string.Empty, "completed", ctx, ct).ConfigureAwait(false);
                });
            return BuildScribePath(edge, input);
        }

        private ScribeSubPath BuildScribePath(WorkflowEdge edge, ExecutorBinding input)
        {
            ExecutorBinding scribe = new ScribeTurnExecutor(
                _factory._copilotClientFactory, _factory._scopeProvider, _factory._sandboxExecutor,
                _factory._sandboxPolicyStore, _factory._approvalStore, _factory._toolApprovalGate,
                _factory._loggerFactory, _factory.GetRecordingWriter,
                name: $"scribe-turn-{edge.From}-{edge.To}",
                createSubStream: _factory.CreateSubStreamWriter, completeSubStream: _factory.CompleteSubStream,
                apiBaseUrl: _factory._apiBaseUrl, apiKey: _factory._apiKey, agentFactory: _factory._agentFactory);

            var outputId = $"scribe-output-{edge.From}-{edge.To}";
            ExecutorBinding output = new VisualFunctionExecutor<ScribeTurnInput, MergeOutput>(
                outputId, "scribe", "Scribe", "scribe", "agent", false,
                (sti, ctx, ct) => new ValueTask<MergeOutput>(
                    new MergeOutput(sti.RunId, sti.TerminalStatus ?? "completed", sti.MergeResult, sti.MergeMode)));

            return new ScribeSubPath(input, scribe, output);
        }

        // ── Shared AgentTurnInput continuation helpers ──────────────────────────────────────────────
        private static AgentTurnInput EmptyTurn(string runId) => new(
            runId, string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, null, string.Empty);

        /// <summary>Forward continuation onto a DIFFERENT agent (a fresh session): carry the produced
        /// worktree forward and append the prior output as context. Not a revision.</summary>
        private static AgentTurnInput ContinueTurn(
            AgentTurnInput? prev, AgentTurnOutput output, string contextLabel, bool isRevision)
        {
            var basis = prev ?? EmptyTurn(output.RunId);
            var task = string.IsNullOrEmpty(output.Diff)
                ? basis.Task
                : $"{basis.Task}\n\n[{contextLabel}]\n{output.Diff}";
            return basis with
            {
                Task = task,
                WorktreePath = output.WorktreePath,
                WorktreeBranch = output.WorktreeBranch,
                RepositoryPath = output.RepositoryPath,
                OriginatingBranch = output.OriginatingBranch,
                Iteration = basis.Iteration + 1,
                MaxIterationsReached = false,
                IsRevision = isRevision,
            };
        }

        /// <summary>Revision loop back onto the SAME producing agent (resume its session): append the
        /// reviewer feedback and increment the iteration counter.</summary>
        private static AgentTurnInput ReviseTurn(AgentTurnInput? prev, string? feedback)
        {
            var basis = prev ?? EmptyTurn(string.Empty);
            var nextIteration = basis.Iteration + 1;
            var task = string.IsNullOrEmpty(feedback)
                ? basis.Task
                : $"{basis.Task}\n\n[Review feedback — iteration {nextIteration}]: {feedback}";
            return basis with
            {
                Task = task,
                Iteration = nextIteration,
                MaxIterationsReached = false,
                IsRevision = true,
            };
        }
    }

    private IReadOnlyDictionary<string, ExecutorBinding> BuildPolicyGateBindings(WorkflowDefinition definition)
    {
        var bindings = new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal);

        foreach (var node in definition.Nodes.Where(n => n.Type == WorkflowNodeType.Check))
        {
            var gateKind = GateKindOf(node);
            if (gateKind is null) continue;

            if (string.Equals(node.Id, "rai", StringComparison.Ordinal) ||
                string.Equals(node.Id, "review", StringComparison.Ordinal))
                continue;

            bindings[node.Id] = gateKind switch
            {
                "rai" => new RaiTurnExecutor(
                    _copilotClientFactory, _scopeProvider, _sandboxExecutor, _sandboxPolicyStore,
                    _approvalStore, _toolApprovalGate, _loggerFactory, GetRecordingWriter,
                    name: $"{node.Id}-turn",
                    createSubStream: CreateSubStreamWriter,
                    completeSubStream: CompleteSubStream,
                    agentFactory: _agentFactory,
                    logicalNodeId: node.Id,
                    displayLabel: node.Label,
                    subStreamSuffix: "rai"),

                "rubberduck" => new RubberduckTurnExecutor(
                    _copilotClientFactory, _scopeProvider, _sandboxExecutor, _sandboxPolicyStore,
                    _approvalStore, _toolApprovalGate, _loggerFactory, GetRecordingWriter,
                    name: $"{node.Id}-turn",
                    logicalNodeId: node.Id,
                    displayLabel: node.Label,
                    createSubStream: CreateSubStreamWriter,
                    completeSubStream: CompleteSubStream,
                    agentFactory: _agentFactory),

                "human-review" => RequestPort.Create<WorkflowReviewRequest, WorkflowReviewDecision>($"{node.Id}-gate"),

                _ => throw new ReviewPolicyCompositionException(
                    "review_policy_unsupported_gate",
                    $"Workflow '{definition.Id}' contains unsupported review-policy gate kind '{gateKind}' on node '{node.Id}'."),
            };
        }

        return bindings;
    }

    private static string? GateKindOf(WorkflowNode node)
    {
        var raw = !string.IsNullOrWhiteSpace(node.GateKind) ? node.GateKind! : node.Id;
        return raw.Trim().Replace('_', '-').Replace(' ', '-').ToLowerInvariant() switch
        {
            "rai" => "rai",
            "review" or "human-review" => "human-review",
            "rubberduck" or "rubber-duck" => "rubberduck",
            _ => null,
        };
    }

    /// <summary>
    /// Returns the inline bespoke charter for the workflow's agent turn, if the definition declares
    /// one. Prefers the start node when it is a prompt node carrying a <c>charter</c>, otherwise the
    /// first prompt node with a non-empty charter. Returns null when no agent node declares a bespoke
    /// charter (the common case: catalog roles resolve their charter from <c>.squad/agents</c>).
    /// </summary>
    private static string? ResolveAgentNodeCharter(WorkflowDefinition definition)
    {
        bool IsPromptWithCharter(WorkflowNode n) =>
            n.Type == WorkflowNodeType.Prompt && !string.IsNullOrWhiteSpace(n.Charter);

        var startNode = definition.Nodes.FirstOrDefault(
            n => string.Equals(n.Id, definition.Start, StringComparison.Ordinal));
        if (startNode is not null && IsPromptWithCharter(startNode))
            return startNode.Charter!.Trim();

        return definition.Nodes.FirstOrDefault(IsPromptWithCharter)?.Charter?.Trim();
    }

    /// <summary>
    /// Launches a new streaming workflow run. When <paramref name="isChild"/> is true
    /// (the run carries <c>ParentRunId</c>), the trimmed coordinator CHILD pipeline is used:
    /// agent + RAI terminating assemble-ready, with no per-child review gate / merge / scribe.
    /// </summary>
    public async Task<StreamingRun> StartAsync(AgentTurnInput input, string runId, CancellationToken ct, bool isChild = false)
    {
        var effectiveDefinition = isChild
            ? null
            : await ResolveEffectiveDefinitionAsync(input.ProjectId, runId, ct).ConfigureAwait(false);
        var (workflow, descriptor, executorMeta) = BuildWorkflow(isChild, effectiveDefinition);
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
    /// Builds the run graph descriptor using the persisted run's selected project workflow and active
    /// review policy, matching the graph used by <see cref="StartAsync"/>.
    /// </summary>
    public async Task<GraphDescriptor> GetGraphDescriptorAsync(Agentweaver.Domain.Run run, CancellationToken ct)
    {
        var isChild = run.ParentRunId is not null;
        var effectiveDefinition = isChild
            ? null
            : await ResolveEffectiveDefinitionAsync(run.ProjectId?.ToString(), run.Id.ToString(), ct).ConfigureAwait(false);
        return BuildWorkflow(isChild, effectiveDefinition).Descriptor;
    }

    /// <summary>
    /// Test seam (drift-guard): builds the workflow AND its descriptor for a variant so the test can
    /// reflect the built MAF graph (ReflectExecutors/ReflectEdges) and assert the descriptor stays in
    /// sync with the wired executors. Reflection is used ONLY by that build-time test, never at runtime.
    /// </summary>
    internal (Workflow Workflow, GraphDescriptor Descriptor) BuildWorkflowForTest(bool isChild, WorkflowDefinition? effectiveDefinition = null)
    {
        var (workflow, descriptor, _) = BuildWorkflow(isChild, effectiveDefinition);
        return (workflow, descriptor);
    }

    /// <summary>
    /// Test seam: the executorId -> render-metadata map the watch loop uses to translate MAF executor
    /// lifecycle events into <c>workflow.step</c> UI events. Lets tests assert the gap-node mapping
    /// (e.g. <c>child-assemble-ready</c> -&gt; <c>assemble-ready</c>) without running a workflow.
    /// </summary>
    internal IReadOnlyDictionary<string, ExecutorNodeMeta> BuildExecutorMetaForTest(bool isChild) =>
        BuildWorkflow(isChild).ExecutorMeta;

    private async Task<WorkflowDefinition> ResolveEffectiveDefinitionAsync(
        string? projectId,
        string? runId,
        CancellationToken ct)
    {
        var fallback = Workflows.BuiltInWorkflows.Default.Definition!;
        if (_projectStore is null || _workflowRegistry is null || _reviewPolicyRegistry is null)
            return ReviewPolicyComposer.ComposeForRuntime(fallback, BuiltInReviewPolicies.Default.Policy!).Effective;

        if (string.IsNullOrWhiteSpace(projectId) || !ProjectId.TryParse(projectId, out var pid))
            return ReviewPolicyComposer.ComposeForRuntime(fallback, BuiltInReviewPolicies.Default.Policy!).Effective;

        var project = await _projectStore.GetAsync(pid, ct).ConfigureAwait(false);
        if (project is null)
            return ReviewPolicyComposer.ComposeForRuntime(fallback, BuiltInReviewPolicies.Default.Policy!).Effective;

        var invocationKind = await ResolveInvocationKindAsync(runId, ct).ConfigureAwait(false);
        var overrideId = await ResolveWorkflowOverrideIdAsync(runId, ct).ConfigureAwait(false);
        var workflowResult = ResolveWorkflowForRun(project, overrideId, invocationKind);
        if (!workflowResult.IsValid || workflowResult.Definition is null)
            throw new ReviewPolicyCompositionException(
                "workflow_resolution_failed",
                $"Project '{project.Id}' workflow could not be resolved: {workflowResult.Error ?? "unknown workflow error"}");

        var policyResult = _reviewPolicyRegistry.ResolveActive(project);
        if (!policyResult.IsValid || policyResult.Policy is null)
            throw new ReviewPolicyCompositionException(
                "review_policy_resolution_failed",
                $"Project '{project.Id}' active review policy could not be resolved: {policyResult.Error ?? "unknown review-policy error"}");

        try
        {
            return ReviewPolicyComposer.ComposeForRuntime(workflowResult.Definition, policyResult.Policy).Effective;
        }
        catch (ReviewPolicyCompositionException ex)
        {
            throw new ReviewPolicyCompositionException(
                ex.Code,
                $"{ex.Message} Workflow source={workflowResult.Source}; review policy source={policyResult.Source}.");
        }
    }

    private async Task<WorkflowInvocationKind> ResolveInvocationKindAsync(string? runId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(runId) || !RunId.TryParse(runId, out var rid))
            return WorkflowInvocationKind.Manual;

        var run = await _runStore.GetAsync(rid, ct).ConfigureAwait(false);
        return run?.Origin == RunOrigin.BacklogPickup
            ? WorkflowInvocationKind.Heartbeat
            : WorkflowInvocationKind.Manual;
    }

    private async Task<string?> ResolveWorkflowOverrideIdAsync(string? runId, CancellationToken ct)
    {
        if (_backlogTaskStore is null || string.IsNullOrWhiteSpace(runId) || !RunId.TryParse(runId, out var rid))
            return null;

        var task = await _backlogTaskStore.GetByRunIdAsync(rid, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(task?.WorkflowOverrideId) ? null : task.WorkflowOverrideId;
    }

    private WorkflowLoadResult ResolveWorkflowForRun(
        Project project,
        string? overrideId,
        WorkflowInvocationKind invocationKind)
    {
        var set = _workflowRegistry!.GetOrLoad(project);

        if (!string.IsNullOrWhiteSpace(overrideId))
        {
            var overrideResult = set.FindById(overrideId);
            if (overrideResult?.Definition is null)
                return WorkflowLoadResult.Invalid(
                    "workflow-override",
                    $"Workflow override '{overrideId}' could not be resolved for project '{project.Id}'.");
            if (!WorkflowTriggerEvaluator.IsEligible(overrideResult.Definition.Trigger, invocationKind))
                return WorkflowLoadResult.Invalid(
                    overrideResult.Source,
                    $"Workflow override '{overrideResult.Definition.Id}' is not eligible for a {invocationKind} invocation.");
            return overrideResult;
        }

        var configuredId = string.IsNullOrWhiteSpace(project.DefaultWorkflowId)
            ? BuiltInWorkflows.DefaultWorkflowId
            : project.DefaultWorkflowId!;
        var configured = set.FindById(configuredId);
        if (configured?.Definition is not null &&
            WorkflowTriggerEvaluator.IsEligible(configured.Definition.Trigger, invocationKind))
            return configured;

        var eligible = set.Available
            .FirstOrDefault(r => r.Definition is not null &&
                                 WorkflowTriggerEvaluator.IsEligible(r.Definition.Trigger, invocationKind));
        return eligible
            ?? WorkflowLoadResult.Invalid(
                "workflow-selection",
                $"No valid workflow is eligible for a {invocationKind} invocation in project '{project.Id}'.");
    }

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
            var effectiveDefinition = isChild
                ? null
                : await ResolveEffectiveDefinitionAsync(run?.ProjectId?.ToString(), checkpointInfo.SessionId, ct).ConfigureAwait(false);
            var (workflowForRun, _, executorMetaForRun) = BuildWorkflow(isChild, effectiveDefinition);
            _runExecutorMeta[checkpointInfo.SessionId] = executorMetaForRun;
            return await InProcessExecution.ResumeStreamingAsync(
                workflowForRun, checkpointInfo, _checkpointManager, ct).ConfigureAwait(false);
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
    public async Task<bool> HasCheckpointAsync(string runId, CancellationToken ct = default)
        => await GetLatestCheckpointAsync(runId, ct).ConfigureAwait(false) is not null;

    /// <summary>
    /// Gets the latest checkpoint info for resumption. Returns null if no checkpoint exists.
    /// Reads from the active checkpoint store (Postgres or file) so recovery resolves the same store
    /// that wrote the checkpoint — a file directory scan is wrong when checkpoints live in Postgres.
    /// </summary>
    public Task<CheckpointInfo?> GetLatestCheckpointAsync(string runId, CancellationToken ct = default)
        => _checkpointStoreFactory.GetLatestCheckpointAsync("runs", runId, ct);
}

/// <summary>
/// Adapts a RunStreamEntry into a ChannelWriter for the agent runner's token streaming.
/// When constructed with an <see cref="IRunEventStream"/>, each written event is also durably
/// written-through to the run event log (Layer 1) using the sequence the entry assigned, so the
/// persisted log stays aligned with in-memory history on a per-append basis (016-run-event-stream).
/// </summary>
internal sealed class RecordingChannelWriter : ChannelWriter<RunEvent>
{
    private readonly RunStreamEntry _entry;
    private readonly string? _runId;
    private readonly IRunEventStream? _eventStream;

    public RecordingChannelWriter(RunStreamEntry entry)
        : this(entry, null, null)
    {
    }

    public RecordingChannelWriter(RunStreamEntry entry, string? runId, IRunEventStream? eventStream)
    {
        _entry = entry;
        _runId = runId;
        _eventStream = eventStream;
    }

    public override bool TryWrite(RunEvent item)
    {
        var sequence = _entry.RecordNext(item.Type, item.Payload);

        // Durable write-through. The entry-assigned sequence keeps the persisted log aligned with
        // in-memory history. Best-effort: a durable-write failure must not break live streaming;
        // PersistRunEventsAsync provides a terminal backfill safety net for any gaps.
        if (sequence > 0 && _runId is not null && _eventStream is not null)
        {
            try
            {
                _eventStream.AppendAsync(_runId, new RunEvent(sequence, item.Type, item.Payload))
                    .AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Swallow: durability is reconciled by the terminal PersistRunEventsAsync backfill.
            }
        }

        return true;
    }

    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(true);

    public override bool TryComplete(Exception? error = null) => true;
}
