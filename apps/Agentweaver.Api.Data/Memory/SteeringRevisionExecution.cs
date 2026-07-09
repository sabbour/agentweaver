using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Memory;

/// <summary>
/// UNIFIED AUTONOMOUS STEERING (rev8, §3d) — the durable, ATTEMPT-SPECIFIC two-phase marker that
/// makes in-place steering (direction A) exactly-once under crash recovery.
///
/// <para>Keyed uniquely on <c>(SteeringDirectiveId, ActionAttempt)</c>. The row moves through two
/// phases:</para>
/// <list type="number">
/// <item><b>initiated</b> — inserted BEFORE the revision workflow is launched. Its purpose is to
/// DEDUPE concurrent/replayed launches: the unique key means a racing second inserter conflicts, so
/// at most one actor owns this attempt. Presence of an <c>initiated</c> row is NOT sufficient to
/// declare the effect applied.</item>
/// <item><b>effect_confirmed</b> — set ONLY once the resumed workflow truly begins executing, by the
/// per-launch checkpoint-store decorator on its FIRST checkpoint write (near/at the first superstep).
/// A row with <see cref="EffectState"/> == <c>effect_confirmed</c> exists IFF this specific
/// <c>(directiveId, attempt)</c> ran ≥1 superstep — attempt-specific and durable across pod crash,
/// immune to a pre-existing same-<c>RunId</c> checkpoint (which cannot disambiguate attempts because
/// in-place steering resumes the SAME session id = RunId).</item>
/// </list>
///
/// <para>Recovery probes THIS row (never a bare <c>WorkflowCheckpointRecord</c>, never
/// <c>RunStatus.InProgress</c>, and never the in-memory registry as the correctness signal):
/// no confirmed row → re-drive the launch once; confirmed row present → advance to <c>applied</c>,
/// no re-inject.</para>
/// </summary>
public sealed class SteeringRevisionExecution
{
    [Key] public int Id { get; set; }

    /// <summary>The child run id the revision is (re)driven against.</summary>
    public required string RunId { get; set; }

    /// <summary>The steering directive that chose direction A. Part of the unique idempotency key.</summary>
    public int SteeringDirectiveId { get; set; }

    /// <summary>The budget attempt number recorded at decision time. Part of the unique idempotency key.</summary>
    public int ActionAttempt { get; set; }

    /// <summary>initiated | effect_confirmed (see class remarks).</summary>
    public required string EffectState { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §4, dev-store crash-window corroboration) — snapshot of the
    /// checkpoint-index count for <see cref="RunId"/> taken at <c>initiated</c> insert time (BEFORE the
    /// revision is launched). On the non-transactional file/dev checkpoint store (where the effect row
    /// cannot commit atomically with the checkpoint), recovery corroborates a crashed-before-confirm
    /// attempt by observing the session's checkpoint count is now STRICTLY GREATER than this watermark:
    /// under the single-launcher lease + unique <c>(directiveId, attempt)</c> key, any checkpoint beyond
    /// the snapshot can only have been written by THIS attempt, so the effect is present. The count is
    /// strictly monotonic (no clock-skew false positive, unlike a raw timestamp comparison), and
    /// pre-existing same-session checkpoints from the original child run are already counted in the
    /// snapshot so they can never false-confirm.
    /// </summary>
    public int CheckpointWatermark { get; set; }

    /// <summary>When the effect was confirmed (first checkpoint written). Null while <c>initiated</c>.</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }
}

/// <summary>Canonical <see cref="SteeringRevisionExecution.EffectState"/> values.</summary>
public static class RevisionEffectState
{
    public const string Initiated = "initiated";
    public const string EffectConfirmed = "effect_confirmed";
}
