namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Lightweight projection of a coordinator run's persisted work-plan state used to place its board
/// card in the correct workflow column. <see cref="Status"/> is the WorkPlan.Status; <see
/// cref="AssemblyStage"/> is the (sticky, forward-only) Phase 3 assembly stage, null until assembly
/// starts.
/// </summary>
public readonly record struct CoordinatorWorkPlanStage(string Status, string? AssemblyStage);
