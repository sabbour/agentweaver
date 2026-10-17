using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

/// <summary>Defines the Environment lifecycle operations exposed to API-facing coordinators.</summary>
public interface IEnvironmentLifecycleProducer
{
    /// <summary>Registers an owner as active at generation one, using the store's idempotency contract.</summary>
    Task<EnvironmentLifecycleTransitionResult> RegisterAsync(
        EnvironmentOwnerIdentity owner,
        string idempotencyKey,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeCommitCheck = null);

    /// <summary>Applies a state transition only when the supplied lifecycle generation is still current.</summary>
    Task<EnvironmentLifecycleTransitionResult> TransitionAsync(
        EnvironmentGenerationFence expectedFence,
        EnvironmentLifecycleState targetState,
        string idempotencyKey,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeCommitCheck = null);
}

/// <summary>Maps high-level register and transition calls into requests for the lifecycle store.</summary>
public sealed class EnvironmentLifecycleProducer(IEnvironmentLifecycleStore store)
    : IEnvironmentLifecycleProducer
{
    /// <summary>Creates the initial active lifecycle record with generation zero as the expected prior state.</summary>
    public Task<EnvironmentLifecycleTransitionResult> RegisterAsync(
        EnvironmentOwnerIdentity owner,
        string idempotencyKey,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeCommitCheck = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return store.TransitionAsync(
            new EnvironmentLifecycleTransitionRequest(
                owner,
                expectedLifecycleGeneration: 0,
                EnvironmentLifecycleState.Active,
                idempotencyKey),
            cancellationToken,
            beforeCommitCheck);
    }

    /// <summary>Forwards a generation-fenced state change and optional pre-commit authorization check to the store.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The expected lifecycle generation is not positive.</exception>
    public Task<EnvironmentLifecycleTransitionResult> TransitionAsync(
        EnvironmentGenerationFence expectedFence,
        EnvironmentLifecycleState targetState,
        string idempotencyKey,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeCommitCheck = null)
    {
        ArgumentNullException.ThrowIfNull(expectedFence);
        if (expectedFence.LifecycleGeneration <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(expectedFence),
                "An explicit positive Environment lifecycle generation is required.");
        return store.TransitionAsync(
            new EnvironmentLifecycleTransitionRequest(
                expectedFence.Owner,
                expectedFence.LifecycleGeneration,
                targetState,
                idempotencyKey),
            cancellationToken,
            beforeCommitCheck);
    }
}
