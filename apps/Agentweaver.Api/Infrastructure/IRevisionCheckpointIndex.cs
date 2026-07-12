namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// UNIFIED AUTONOMOUS STEERING (rev8 §4, dev-store crash-window corroboration). Exposes a read-only
/// count of the durable checkpoints for a run session (SessionId == RunId) from the SHARED "runs"
/// checkpoint store, so the steering decider can corroborate a crashed-before-confirm in-place
/// revision on the non-transactional file/dev checkpoint store (where the effect marker cannot commit
/// atomically with the checkpoint).
/// <para>
/// The count is strictly monotonic (checkpoints are only ever appended), so comparing the CURRENT
/// count against the snapshot captured on the <c>initiated</c> marker
/// (<c>SteeringRevisionExecution.CheckpointWatermark</c>) is a clock-skew-free proof that THIS attempt
/// wrote ≥1 checkpoint — pre-existing same-session checkpoints from the original child run are already
/// counted in the snapshot and can never false-confirm.
/// </para>
/// Implemented over the SAME shared store instance (never a second file store, which would take a
/// conflicting exclusive directory lock) via a deferred accessor, mirroring
/// <see cref="IRevisionEffectConfirmer"/>.
/// </summary>
public interface IRevisionCheckpointIndex
{
    /// <summary>Returns the number of checkpoints currently recorded for <paramref name="sessionId"/> (== RunId).</summary>
    Task<int> CountCheckpointsAsync(string sessionId, CancellationToken ct = default);
}
