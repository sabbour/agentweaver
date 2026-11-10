using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

/// <summary>Outcome of one platform egress reconcile pass.</summary>
public enum PlatformEgressReconcileOutcome { Converged, Applied, Failed }

/// <summary>Result of one platform egress reconcile pass.</summary>
/// <param name="Outcome">Converged when no apply was needed, Applied after a successful apply, else Failed.</param>
/// <param name="Code">Stable reason code when the outcome is Failed.</param>
/// <param name="Snapshot">The admitted source snapshot that the pass used, or null.</param>
/// <param name="Status">The last provider status that the pass read, or null.</param>
/// <param name="DriftCorrected">True when the provider had the same source but a different or invalid readback.</param>
public sealed record PlatformEgressReconcileResult(
    PlatformEgressReconcileOutcome Outcome,
    string? Code,
    PlatformEgressPolicySnapshot? Snapshot,
    PlatformEgressStatus? Status,
    bool DriftCorrected = false);

/// <summary>Timing for the wait after an apply.</summary>
public sealed record PlatformEgressReconcilerOptions(TimeSpan ConvergenceTimeout, TimeSpan PollInterval)
{
    public static PlatformEgressReconcilerOptions Default { get; } =
        new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(2));
}

/// <summary>
/// Applies the admitted platform egress policy from the one policy source to the selected provider.
/// </summary>
/// <remarks>
/// Fails closed: when the source is absent, unreadable, or invalid, or the provider cannot apply the mode,
/// the pass changes nothing in the provider. The applied provider object holds the generation, so the pass
/// needs no local state, and concurrent passes are fenced by the provider's compare-and-swap.
/// </remarks>
public sealed class PlatformEgressReconciler(
    IPlatformEgressPolicySource source,
    IPlatformEgressProvider provider,
    TimeProvider time,
    PlatformEgressReconcilerOptions? options = null)
{
    private const int MaxSourceReads = 3;
    private readonly PlatformEgressReconcilerOptions _options = options ?? PlatformEgressReconcilerOptions.Default;

    /// <summary>Runs one reconcile pass.</summary>
    public async Task<PlatformEgressReconcileResult> ReconcileAsync(CancellationToken cancellationToken)
    {
        var snapshot = await ReadSourceAsync(cancellationToken).ConfigureAwait(false);
        for (var read = 1; ; read++)
        {
            if (snapshot is null)
                return new(PlatformEgressReconcileOutcome.Failed, "policy_source_unavailable", null, null);
            if (snapshot.Validate() is not null)
                return new(PlatformEgressReconcileOutcome.Failed, "policy_source_invalid", snapshot, null);
            if (!PlatformEgressCapabilities.CanApply(provider.Descriptor.AdvertisedCapabilities, snapshot.Mode))
                return new(PlatformEgressReconcileOutcome.Failed, "capability_unavailable", snapshot, null);

            var intentHash = PlatformEgressPolicy.ComputeIntentHash(snapshot.Mode, snapshot.Destinations);
            PlatformEgressStatus status;
            try
            {
                status = await provider.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new(PlatformEgressReconcileOutcome.Failed, "provider_unavailable", snapshot, null);
            }
            if (status.Matches(intentHash, snapshot.SourceHash))
                return new(PlatformEgressReconcileOutcome.Converged, null, snapshot, status);

            // Recheck the source just before the apply, so a revoked or replaced policy is never applied.
            var current = await ReadSourceAsync(cancellationToken).ConfigureAwait(false);
            if (current is null || !SameAdmission(current, snapshot, intentHash))
            {
                if (read >= MaxSourceReads)
                    return new(PlatformEgressReconcileOutcome.Failed,
                        current is null ? "policy_source_unavailable" : "policy_source_changed", current, status);
                snapshot = current;
                continue;
            }

            var drift = status.Generation > 0 && status.SourceHash == snapshot.SourceHash;
            var policy = PlatformEgressPolicy.Create(
                status.Generation + 1, snapshot.Mode, snapshot.Destinations, snapshot.SourceHash);
            return await ApplyAsync(snapshot, policy, drift, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PlatformEgressReconcileResult> ApplyAsync(
        PlatformEgressPolicySnapshot snapshot,
        PlatformEgressPolicy policy,
        bool drift,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await provider.ApplyAsync(policy, cancellationToken).ConfigureAwait(false);
            var deadline = time.GetUtcNow() + _options.ConvergenceTimeout;
            while (!status.Matches(policy.IntentHash, policy.SourceHash))
            {
                if (status.State == PlatformEgressApplyState.Failed && status.Generation != policy.Generation)
                    return new(PlatformEgressReconcileOutcome.Failed, status.Message ?? "apply_failed", snapshot, status);
                if (time.GetUtcNow() >= deadline)
                    return new(PlatformEgressReconcileOutcome.Failed, "convergence_timeout", snapshot, status);
                await Task.Delay(_options.PollInterval, time, cancellationToken).ConfigureAwait(false);
                status = await provider.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            return new(PlatformEgressReconcileOutcome.Applied, null, snapshot, status, drift);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(PlatformEgressReconcileOutcome.Failed, "provider_unavailable", snapshot, null);
        }
    }

    private async Task<PlatformEgressPolicySnapshot?> ReadSourceAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await source.GetAdmittedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool SameAdmission(
        PlatformEgressPolicySnapshot current,
        PlatformEgressPolicySnapshot expected,
        string intentHash) =>
        current.Validate() is null &&
        current.SourceId == expected.SourceId &&
        current.SourceRevision == expected.SourceRevision &&
        current.SourceHash == expected.SourceHash &&
        current.Checkpoint == expected.Checkpoint &&
        current.Mode == expected.Mode &&
        PlatformEgressPolicy.ComputeIntentHash(current.Mode, current.Destinations) == intentHash;
}
