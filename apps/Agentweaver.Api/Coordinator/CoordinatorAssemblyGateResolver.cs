using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        return ResolveAsync(db, projectStore, workflowRegistry, workPlanId, ct);
    }

    /// <summary>
    /// Resolves the authored assembly gates for <paramref name="workPlanId"/>, dropping the
    /// platform <c>build_test</c> gate when the plan is non-code-producing. Falls back to
    /// <see cref="CoordinatorGraphDescriptor.DefaultAssemblyGates"/> (RAI + Human Review) when the
    /// workflow can't be resolved, matching the historical default-gate behavior.
    /// </summary>
    public static async Task<IReadOnlyList<CoordinatorGraphDescriptor.AssemblyGateNode>> ResolveAsync(
        MemoryDbContext db,
        IProjectStore? projectStore,
        WorkflowRegistry? workflowRegistry,
        int workPlanId,
        CancellationToken ct)
    {
        if (projectStore is null || workflowRegistry is null)
            return CoordinatorGraphDescriptor.DefaultAssemblyGates;

        var plan = await db.WorkPlans.AsNoTracking()
            .Where(w => w.Id == workPlanId)
            .Select(w => new { w.ProjectId, w.WorkflowId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (plan is null || !ProjectId.TryParse(plan.ProjectId, out var projectId))
            return CoordinatorGraphDescriptor.DefaultAssemblyGates;

        var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null)
            return CoordinatorGraphDescriptor.DefaultAssemblyGates;

        var workflow = !string.IsNullOrWhiteSpace(plan.WorkflowId)
            ? workflowRegistry.Get(project, plan.WorkflowId!)?.Definition
            : workflowRegistry.ResolveDefault(project).Definition;
        workflow ??= workflowRegistry.ResolveDefault(project).Definition;
        if (workflow is null)
            return CoordinatorGraphDescriptor.DefaultAssemblyGates;

        var phases = await db.Subtasks.AsNoTracking()
            .Where(s => s.WorkPlanId == workPlanId)
            .Select(s => s.Phase)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return CoordinatorAssemblyService.ResolveAssemblyGates(workflow, ProducesCode(phases));
    }

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
}
