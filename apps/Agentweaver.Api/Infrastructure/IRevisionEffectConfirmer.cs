namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// UNIFIED AUTONOMOUS STEERING (rev8 §3d, Phase-2). Confirms the durable, attempt-specific
/// revision-effect marker once the resumed in-place (direction A) revision workflow has actually begun
/// executing. Implemented by <c>CoordinatorSteeringService</c>/<c>CoordinatorSteeringDecider</c> and
/// invoked by the per-launch <see cref="SteeringRevisionCheckpointStore"/> decorator on the FIRST
/// checkpoint write of that launch — so "marker == effect_confirmed" ⟺ "this specific
/// <c>(directiveId, attempt)</c> ran ≥1 superstep". Kept as a tiny interface in the Infrastructure
/// layer so <c>RunWorkflowFactory</c> can depend on it without a reference cycle into the Coordinator.
/// </summary>
public interface IRevisionEffectConfirmer
{
    /// <summary>Marks the <c>(directiveId, attempt, runId)</c> revision-effect marker <c>effect_confirmed</c>. Idempotent.</summary>
    Task ConfirmRevisionEffectAsync(int directiveId, int attempt, string runId, CancellationToken ct = default);

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §3d, RD#1) — confirms (or inserts) the
    /// <c>(directiveId, attempt)</c> effect marker <c>effect_confirmed</c> by ENQUEUING the change on the
    /// SUPPLIED <see cref="Agentweaver.Api.Memory.MemoryDbContext"/> WITHOUT calling <c>SaveChanges</c>,
    /// so the transactional Postgres checkpoint store can commit the effect row in the SAME
    /// <c>SaveChanges</c> as its first checkpoint insert — atomic, with no crash window between the
    /// checkpoint write and the confirmation. Idempotent (no-op if already confirmed).
    /// </summary>
    Task ConfirmRevisionEffectOnContextAsync(
        Agentweaver.Api.Memory.MemoryDbContext db, int directiveId, int attempt, string runId, CancellationToken ct = default);
}
