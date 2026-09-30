using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Resolves the collective-assembly (Phase 3) gate list for a work plan from its selected workflow
/// definition, so the SAME gate set is used everywhere the coordinator graph is built — the initial
/// plan view served by <c>GET /api/runs/{id}/graph</c> (#386: the Build &amp; Test gate must render as
/// <c>planned</c> up front, not appear only once assembly execution reaches it), the topology-shape
/// <c>coordinator.graph</c> emissions, and the assembly executor's own gate loop.
///
/// It also determines gate APPLICABILITY from the actual task (#387): a non-code-producing work plan
/// (all subtasks are planning-phase deliverables — research, PRDs, design docs) has nothing to build
/// or test, so the platform-owned <c>build_test</c> gate is dropped rather than scheduled — otherwise
/// the gate finds no code, requests changes, and loops indefinitely. This is the coordinator deciding
/// whether Build &amp; Test applies based on the outcome, instead of unconditionally inserting it.
/// </summary>
public static class CoordinatorAssemblyGateResolver
{
    /// <summary>Resolves gates using services from a request/operation scope.</summary>
    public static Task<IReadOnlyList<CoordinatorGraphDescriptor.AssemblyGateNode>> ResolveAsync(
        IServiceProvider scopedServices, int workPlanId, CancellationToken ct)
    {
        var db = scopedServices.GetRequiredService<MemoryDbContext>();
        var projectStore = scopedServices.GetService<IProjectStore>();
        var workflowRegistry = scopedServices.GetService<WorkflowRegistry>();
        var codeClassifier = scopedServices.GetService<IAssemblyGateCodeClassifier>();
        return ResolveAsync(db, projectStore, workflowRegistry, codeClassifier, workPlanId, ct,
            scopedServices.GetService<IRunStore>(),
            scopedServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(CoordinatorAssemblyGateResolver)));
    }

    /// <summary>
    /// Resolves the authored assembly gates for <paramref name="workPlanId"/>, dropping the
    /// platform <c>build_test</c> gate when the plan is non-code-producing. Falls back to
    /// <see cref="CoordinatorGraphDescriptor.DefaultAssemblyGates"/> (RAI + Human Review) when the
    /// workflow can't be resolved, matching the historical default-gate behavior.
    /// </summary>
    public static Task<IReadOnlyList<CoordinatorGraphDescriptor.AssemblyGateNode>> ResolveAsync(
        MemoryDbContext db,
        IProjectStore? projectStore,
        WorkflowRegistry? workflowRegistry,
        int workPlanId,
        CancellationToken ct) =>
        ResolveAsync(db, projectStore, workflowRegistry, null, workPlanId, ct);

    /// <summary>
    /// Resolves authored assembly gates with an optional semantic code-production classifier.
    /// </summary>
    public static async Task<IReadOnlyList<CoordinatorGraphDescriptor.AssemblyGateNode>> ResolveAsync(
        MemoryDbContext db,
        IProjectStore? projectStore,
        WorkflowRegistry? workflowRegistry,
        IAssemblyGateCodeClassifier? codeClassifier,
        int workPlanId,
        CancellationToken ct,
        IRunStore? runStore = null,
        ILogger? logger = null)
    {
        var plan = await db.WorkPlans.AsNoTracking()
            .Where(w => w.Id == workPlanId)
            .Select(w => new { w.ProjectId, w.WorkflowId, w.CoordinatorRunId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (plan is null)
            return CoordinatorGraphDescriptor.DefaultAssemblyGates;

        WorkflowDefinition? workflow = null;
        Agentweaver.Domain.Run? coordinatorRun = null;
        if (!string.IsNullOrWhiteSpace(plan.WorkflowId)
            && runStore is not null && RunId.TryParse(plan.CoordinatorRunId, out var runId))
        {
            var run = coordinatorRun = await runStore.GetAsync(runId, ct).ConfigureAwait(false);
            if (run?.GetExecutableWorkflowPin() is { } pin)
            {
                workflow = ExecutableWorkflowSnapshots.Load(plan.CoordinatorRunId, pin);
                if (!string.Equals(workflow.Id, plan.WorkflowId, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(plan.WorkflowId))
                    throw new WorkflowBindException(
                        $"Coordinator run '{plan.CoordinatorRunId}' saved workflow does not match work plan '{workPlanId}'.",
                        plan.CoordinatorRunId);
            }
            else if (run?.ExecutableWorkflowPinRequired == true
                && !string.IsNullOrWhiteSpace(plan.WorkflowId))
            {
                throw new WorkflowBindException(
                    $"Coordinator run '{plan.CoordinatorRunId}' requires a saved workflow for work plan '{workPlanId}', but its manifest is missing.",
                    plan.CoordinatorRunId);
            }
            else
            {
                logger?.LogWarning(
                    "Legacy coordinator run {RunId} has no saved workflow for work plan {WorkPlanId}; resolving the current project workflow for compatibility.",
                    plan.CoordinatorRunId, workPlanId);
            }
        }
        if (workflow is null)
        {
            if (projectStore is null || workflowRegistry is null
                || !ProjectId.TryParse(plan.ProjectId, out var projectId))
                return CoordinatorGraphDescriptor.DefaultAssemblyGates;
            var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
            if (project is null)
                return CoordinatorGraphDescriptor.DefaultAssemblyGates;
            workflow = !string.IsNullOrWhiteSpace(plan.WorkflowId)
                ? workflowRegistry.Get(project, plan.WorkflowId!)?.Definition
                : workflowRegistry.ResolveDefault(project).Definition;
            workflow ??= workflowRegistry.ResolveDefault(project).Definition;
        }
        if (workflow is null)
            return CoordinatorGraphDescriptor.DefaultAssemblyGates;

        var persistedSubtasks = await db.Subtasks.AsNoTracking()
            .Where(s => s.WorkPlanId == workPlanId)
            .Select(s => new { s.Title, s.Scope, s.Phase, s.DeclaredOutputPathsJson })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var subtasks = persistedSubtasks
            .Select(s => new SubtaskGateMetadata(
                s.Title, s.Scope, s.Phase, s.DeclaredOutputPathsJson))
            .ToList();

        var submittingUser = coordinatorRun?.SubmittingUser
            ?? (db.Database.IsSqlite() ? "" : await db.Runs.AsNoTracking()
                .Where(r => r.RunId == plan.CoordinatorRunId)
                .Select(r => r.SubmittingUser)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false) ?? "");
        var classificationContext = new AssemblyGateCodeClassificationContext(
            plan.CoordinatorRunId,
            plan.ProjectId,
            submittingUser,
            "",
            "",
            []);
        var producesCode = await ProducesCodeAsync(
            subtasks, codeClassifier, classificationContext, ct).ConfigureAwait(false);

        return CoordinatorAssemblyService.ResolveAssemblyGates(workflow, producesCode);
    }

    internal sealed record SubtaskGateMetadata(
        string Title,
        string Scope,
        string? Phase,
        string? DeclaredOutputPathsJson);

    /// <summary>
    /// Whether a work plan may produce buildable/testable code. A plan is treated as non-code-producing
    /// ONLY when it has subtasks and EVERY subtask is a <c>planning</c>-phase deliverable (research,
    /// PRD, design/requirements docs). Any <c>execution</c>/<c>validation</c>/<c>none</c> subtask — or
    /// an unknown/empty phase set (pre-decomposition) — is treated as code-producing so the Build &amp;
    /// Test gate is only dropped when we are confident no code is expected.
    /// </summary>
    public static bool ProducesCode(IReadOnlyCollection<string?> subtaskPhases)
    {
        if (subtaskPhases.Count == 0)
            return true;

        return subtaskPhases.Any(p => !string.Equals(p, "planning", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Classifies gate applicability from persisted subtask content as well as phase metadata. Planning
    /// subtasks use the structural non-code fast path; every other subtask is classified by the model.
    /// Unknown work remains code-producing so the gate is only removed with positive evidence.
    /// </summary>
    internal static async Task<bool> ProducesCodeAsync(
        IReadOnlyCollection<SubtaskGateMetadata> subtasks,
        IAssemblyGateCodeClassifier? classifier,
        AssemblyGateCodeClassificationContext context,
        CancellationToken ct)
    {
        if (subtasks.Count == 0)
            return true;

        foreach (var subtask in subtasks)
        {
            if (string.Equals(subtask.Phase, "planning", StringComparison.OrdinalIgnoreCase))
                continue;

            if (classifier is null)
                return true;

            var outputPaths =
                CoordinatorOrchestratorExecutor.DeserializeDeclaredOutputPaths(subtask.DeclaredOutputPathsJson);
            bool? classification;
            try
            {
                classification = await classifier.ClassifyAsync(
                    context with
                    {
                        Title = subtask.Title,
                        Scope = subtask.Scope,
                        DeclaredOutputPaths = outputPaths,
                    },
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return true;
            }
            catch (Exception)
            {
                return true;
            }

            if (classification is not false)
                return true;
        }

        return false;
    }
}
