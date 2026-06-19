using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Builds and runs the Phase 1 coordinator MAF <see cref="Workflow"/>. The graph is:
/// <c>draft -&gt; await-confirmation gate (RequestPort) -&gt; confirm-terminal | revise-loop</c>.
///
/// The drafting executor reads the team's Feature 006 memories and decisions for grounding,
/// drafts an <see cref="OutcomeSpec"/> from the human's goal (a real Copilot agent turn via
/// <see cref="ICoordinatorSpecDrafter"/>; if the model is unavailable or unparseable the run fails
/// rather than fabricating a spec), persists it as <c>awaiting_confirmation</c> into the existing
/// <see cref="MemoryDbContext"/>, emits <c>coordinator.outcome_spec</c>, and routes the spec into
/// a <see cref="RequestPort"/> so the run SUSPENDS until a human confirms or revises. This mirrors
/// the existing review-gate suspend/resume mechanism in <c>RunWorkflowFactory</c>.
///
/// Phase 1 scope only: no decomposition, no child dispatch, no steering. On confirm the run
/// finalizes the spec to <c>confirmed</c> and terminates; on revise it re-drafts and re-suspends.
/// </summary>
public sealed class CoordinatorWorkflowFactory
{
    private const string InputStateKey = "coordinator-input";
    private const string InputStateScope = "run-context";
    private const string CoordinatorAgentName = "Coordinator";

    private const string FallbackCharter =
        "You are the Coordinator, the built-in orchestration agent. Restate the human's goal as a " +
        "confirmable outcome spec (desired outcome, in/out of scope, assumptions, and only the " +
        "clarifying questions that would materially change scope). Do not do domain work yourself.";

    private readonly GitHubCopilotClientFactory _copilotClientFactory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly ISandboxExecutor _sandboxExecutor;
    private readonly ISandboxPolicyStore _sandboxPolicyStore;
    private readonly IShellApprovalStore _approvalStore;
    private readonly IToolApprovalGate _toolApprovalGate;
    private readonly ICoordinatorSpecDrafter _drafter;
    private readonly RunStreamStore _streamStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CoordinatorWorkflowFactory> _logger;
    private readonly CheckpointManager _checkpointManager;
    private readonly string _checkpointDir;
    private readonly CoordinatorOrchestratorExecutor _orchestrator;

    public CoordinatorWorkflowFactory(
        GitHubCopilotClientFactory copilotClientFactory,
        IGitHubTokenScopeProvider scopeProvider,
        ISandboxExecutor sandboxExecutor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        ICoordinatorSpecDrafter drafter,
        RunStreamStore streamStore,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        IConfiguration configuration)
    {
        _copilotClientFactory = copilotClientFactory;
        _scopeProvider = scopeProvider;
        _sandboxExecutor = sandboxExecutor;
        _sandboxPolicyStore = sandboxPolicyStore;
        _approvalStore = approvalStore;
        _toolApprovalGate = toolApprovalGate;
        _drafter = drafter;
        _streamStore = streamStore;
        _scopeFactory = scopeFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CoordinatorWorkflowFactory>();

        _checkpointDir = configuration["Coordinator:Checkpoints:Path"]
            ?? Path.Combine(AppPaths.DataDirectory, "coordinator-checkpoints");
        Directory.CreateDirectory(_checkpointDir);
        var store = new FileSystemJsonCheckpointStore(new DirectoryInfo(_checkpointDir));
        _checkpointManager = CheckpointManager.CreateJson(store);

        // Phase 2 orchestrator: decompose + persist runs only after the human confirms the spec.
        _orchestrator = new CoordinatorOrchestratorExecutor(
            copilotClientFactory,
            scopeProvider,
            sandboxExecutor,
            sandboxPolicyStore,
            approvalStore,
            toolApprovalGate,
            streamStore,
            scopeFactory,
            loggerFactory,
            configuration["Providers:GitHubCopilot:Model"] ?? "gpt-4o");
    }

    /// <summary>The request-port id surfaced to the resume seam when the run suspends.</summary>
    public const string ConfirmationGateId = "coordinator-confirmation-gate";

    private Workflow BuildWorkflow()
    {
        // draft: read context, draft + persist the spec, emit the event, hand the spec to the gate.
        ExecutorBinding draft = new FunctionExecutor<CoordinatorDraftInput, CoordinatorOutcomeSpecRequest>(
            "coordinator-draft",
            async (input, ctx, ct) =>
            {
                await ctx.QueueStateUpdateAsync(InputStateKey, input, InputStateScope, ct).ConfigureAwait(false);
                return await DraftAndPersistAsync(input, ct).ConfigureAwait(false);
            });

        // await-confirmation gate: suspends the run until the human confirms or revises.
        var gate = RequestPort.Create<CoordinatorOutcomeSpecRequest, CoordinatorOutcomeSpecDecision>(
            ConfirmationGateId);
        ExecutorBinding gateBinding = gate;

        // confirm/decline: finalize the spec and terminate the run (Phase 1; dispatch is Phase 2).
        ExecutorBinding finalize = new FunctionExecutor<CoordinatorOutcomeSpecDecision, CoordinatorOutcome>(
            "coordinator-finalize",
            async (decision, ctx, ct) =>
            {
                var input = await ctx.ReadStateAsync<CoordinatorDraftInput>(InputStateKey, InputStateScope, ct)
                    .ConfigureAwait(false);
                return await FinalizeAsync(input!, decision, ct).ConfigureAwait(false);
            });

        // revise: stash the human's feedback and loop back to the drafting executor.
        ExecutorBinding revise = new FunctionExecutor<CoordinatorOutcomeSpecDecision, CoordinatorDraftInput>(
            "coordinator-revise",
            async (decision, ctx, ct) =>
            {
                var input = await ctx.ReadStateAsync<CoordinatorDraftInput>(InputStateKey, InputStateScope, ct)
                    .ConfigureAwait(false);
                var revised = input! with { ReviseFeedback = decision.ReviseFeedback };
                await ctx.QueueStateUpdateAsync(InputStateKey, revised, InputStateScope, ct).ConfigureAwait(false);
                return revised;
            });

        // orchestrate: AFTER confirm, decompose the confirmed spec into a persisted work plan
        // (Phase 2 — decompose + persist only; dispatch is the next wave). On the declined path this
        // is a pass-through so the run still terminates cleanly. This is the seam the dispatch wave
        // extends; the persisted pending subtasks are its hand-off.
        ExecutorBinding orchestrate = new FunctionExecutor<CoordinatorOutcome, CoordinatorOutcome>(
            "coordinator-orchestrate",
            async (outcome, ctx, ct) =>
            {
                if (outcome.Status != "confirmed")
                    return outcome;

                var input = await ctx.ReadStateAsync<CoordinatorDraftInput>(InputStateKey, InputStateScope, ct)
                    .ConfigureAwait(false);
                await _orchestrator.OrchestrateAsync(input!, ct).ConfigureAwait(false);
                return outcome;
            });

        return new WorkflowBuilder(draft)
            .AddEdge(draft, gateBinding)
            // Revise -> re-draft (loop back). Idempotent: the draft node has multiple inbound edges.
            .AddEdge<CoordinatorOutcomeSpecDecision>(gateBinding, revise,
                decision => decision is not null && decision.Revise)
            .AddEdge(revise, draft, idempotent: true)
            // Confirm or decline -> finalize, then orchestrate (decompose + persist on confirm only).
            .AddEdge<CoordinatorOutcomeSpecDecision>(gateBinding, finalize,
                decision => decision is not null && !decision.Revise)
            .AddEdge(finalize, orchestrate)
            .WithOutputFrom(orchestrate)
            .Build()!;
    }

    /// <summary>Launches a new streaming coordinator run.</summary>
    public async Task<StreamingRun> StartAsync(CoordinatorDraftInput input, string runId, CancellationToken ct)
    {
        var workflow = BuildWorkflow();
        return await InProcessExecution.RunStreamingAsync(
            workflow, input, _checkpointManager, runId, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes checkpoint files for a coordinator run (cleanup on terminal state).</summary>
    public void DeleteCheckpoints(string runId)
    {
        var dir = Path.Combine(_checkpointDir, runId);
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // -----------------------------------------------------------------------
    // Drafting + persistence
    // -----------------------------------------------------------------------

    private async Task<CoordinatorOutcomeSpecRequest> DraftAndPersistAsync(
        CoordinatorDraftInput input, CancellationToken ct)
    {
        var memoryContext = await CompileMemoryContextAsync(input.ProjectId, ct).ConfigureAwait(false);
        var charter = BuiltInCharterResolver.Resolve(input.RepositoryPath, "coordinator") ?? FallbackCharter;

        var draft = await _drafter.DraftAsync(input, charter, memoryContext, ct).ConfigureAwait(false);

        var (specId, status) = await PersistDraftAsync(input, draft, ct).ConfigureAwait(false);

        // Emit coordinator.outcome_spec on the run stream (same envelope + sequence as every event)
        // and mark the entry awaiting-review so it is not evicted while the run is suspended.
        var entry = _streamStore.Get(input.RunId);
        entry?.MarkAwaitingReview();
        entry?.RecordNext(EventTypes.CoordinatorOutcomeSpec, new
        {
            specId,
            status,
            goal = input.Goal,
            desiredOutcome = draft.DesiredOutcome,
            scope = draft.Scope,
            assumptions = draft.Assumptions,
            clarifyingQuestions = draft.ClarifyingQuestions,
        });

        return new CoordinatorOutcomeSpecRequest(
            input.RunId, specId, input.Goal,
            draft.DesiredOutcome, draft.Scope, draft.Assumptions, draft.ClarifyingQuestions, status);
    }

    private async Task<string?> CompileMemoryContextAsync(string projectId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var compiler = scope.ServiceProvider.GetRequiredService<MemoryContextCompiler>();
            return await compiler.CompileAsync(projectId, CoordinatorAgentName, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Coordinator memory-context compilation failed for project {ProjectId} — drafting without", projectId);
            return null;
        }
    }

    private async Task<(int SpecId, string Status)> PersistDraftAsync(
        CoordinatorDraftInput input, OutcomeSpecDraft draft, CancellationToken ct)
    {
        const string status = "awaiting_confirmation";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var now = DateTimeOffset.UtcNow;
        var spec = await db.OutcomeSpecs
            .FirstOrDefaultAsync(s => s.CoordinatorRunId == input.RunId, ct)
            .ConfigureAwait(false);

        if (spec is null)
        {
            spec = new OutcomeSpec
            {
                ProjectId = input.ProjectId,
                CoordinatorRunId = input.RunId,
                Goal = input.Goal,
                DesiredOutcome = draft.DesiredOutcome,
                Scope = draft.Scope,
                Assumptions = draft.Assumptions,
                ClarifyingQuestions = draft.ClarifyingQuestions,
                Status = status,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.OutcomeSpecs.Add(spec);
        }
        else
        {
            // Revision: overwrite the existing draft in place and re-arm it for confirmation.
            spec.Goal = input.Goal;
            spec.DesiredOutcome = draft.DesiredOutcome;
            spec.Scope = draft.Scope;
            spec.Assumptions = draft.Assumptions;
            spec.ClarifyingQuestions = draft.ClarifyingQuestions;
            spec.Status = status;
            spec.ConfirmedBy = null;
            spec.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (spec.Id, status);
    }

    private async Task<CoordinatorOutcome> FinalizeAsync(
        CoordinatorDraftInput input, CoordinatorOutcomeSpecDecision decision, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var spec = await db.OutcomeSpecs
            .FirstOrDefaultAsync(s => s.CoordinatorRunId == input.RunId, ct)
            .ConfigureAwait(false);

        var status = decision.Confirmed ? "confirmed" : "declined";

        if (spec is not null)
        {
            spec.Status = status;
            spec.ConfirmedBy = decision.Confirmed ? decision.ConfirmedBy : null;
            spec.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var specId = spec?.Id ?? 0;

        if (decision.Confirmed)
        {
            var entry = _streamStore.Get(input.RunId);
            entry?.RecordNext(EventTypes.CoordinatorOutcomeSpecConfirmed, new
            {
                specId,
                confirmedBy = decision.ConfirmedBy,
            });
        }

        return new CoordinatorOutcome(input.RunId, specId, status);
    }
}
