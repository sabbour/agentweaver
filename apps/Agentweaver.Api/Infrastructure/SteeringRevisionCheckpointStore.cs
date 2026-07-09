using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// UNIFIED AUTONOMOUS STEERING (rev8 §3d) — a PER-LAUNCH decorator over the MAF checkpoint store used
/// ONLY for a single in-place steering (direction A) revision launch. It delegates every operation to
/// the shared underlying <see cref="JsonCheckpointStore"/> (same lock, same rows, same session) and,
/// on the FIRST <see cref="CreateCheckpointAsync"/> of that launch, writes the durable
/// <c>SteeringRevisionExecution</c> effect marker to <c>effect_confirmed</c> via
/// <see cref="IRevisionEffectConfirmer"/>.
/// <para>
/// Because the workflow only writes a checkpoint once it has actually begun a superstep, the confirmed
/// marker is an attempt-specific, crash-durable proof that THIS <c>(directiveId, attempt)</c> ran ≥1
/// superstep — a pre-existing same-<c>RunId</c> checkpoint (from the original child or a prior attempt)
/// can never false-confirm it, and the in-memory run registry is never the correctness signal.
/// </para>
/// <para>
/// BLAST RADIUS: this wrapper is applied ONLY to the <c>CheckpointManager</c> created for the revision
/// launch; normal runs keep using the shared, undecorated manager, so global checkpoint behavior is
/// unchanged.
/// </para>
/// </summary>
public sealed class SteeringRevisionCheckpointStore : JsonCheckpointStore
{
    private readonly JsonCheckpointStore _inner;
    private readonly int _directiveId;
    private readonly int _attempt;
    private readonly IRevisionEffectConfirmer _confirmer;
    private readonly ILogger? _logger;
    private int _confirmed;

    public SteeringRevisionCheckpointStore(
        JsonCheckpointStore inner,
        int directiveId,
        int attempt,
        IRevisionEffectConfirmer confirmer,
        ILogger? logger = null)
    {
        _inner = inner;
        _directiveId = directiveId;
        _attempt = attempt;
        _confirmer = confirmer;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async ValueTask<CheckpointInfo> CreateCheckpointAsync(
        string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        // Delegate the real checkpoint write first: the effect marker means "the workflow got far
        // enough to persist a checkpoint (≥1 superstep)", so we confirm strictly AFTER the durable
        // checkpoint exists.
        var info = await _inner.CreateCheckpointAsync(sessionId, value, parent).ConfigureAwait(false);

        if (Interlocked.CompareExchange(ref _confirmed, 1, 0) == 0)
        {
            try
            {
                // sessionId IS the resumed child run id (SessionId == RunId for an in-place resume), so
                // this confirms the PER-CHILD (directiveId, attempt, runId) marker (RD-B).
                await _confirmer.ConfirmRevisionEffectAsync(_directiveId, _attempt, sessionId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Allow a later checkpoint to retry the confirmation; recovery re-drives if the effect
                // is still unconfirmed (idempotent under the unique (directiveId, attempt) key).
                Interlocked.Exchange(ref _confirmed, 0);
                _logger?.LogWarning(ex,
                    "Steering: failed to confirm revision effect for directive {DirectiveId} attempt {Attempt} on first checkpoint of session {SessionId}",
                    _directiveId, _attempt, sessionId);
            }
        }

        return info;
    }

    /// <inheritdoc />
    public override ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
        => _inner.RetrieveCheckpointAsync(sessionId, key);

    /// <inheritdoc />
    public override ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(
        string sessionId, CheckpointInfo? withParent = null)
        => _inner.RetrieveIndexAsync(sessionId, withParent);
}
