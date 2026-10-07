using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

public interface IEnvironmentLifecycleProducer
{
    Task<EnvironmentLifecycleTransitionResult> RegisterAsync(
        EnvironmentOwnerIdentity owner,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<EnvironmentLifecycleTransitionResult> TransitionAsync(
        EnvironmentGenerationFence expectedFence,
        EnvironmentLifecycleState targetState,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

public sealed class EnvironmentLifecycleProducer(IEnvironmentLifecycleStore store)
    : IEnvironmentLifecycleProducer
{
    public Task<EnvironmentLifecycleTransitionResult> RegisterAsync(
        EnvironmentOwnerIdentity owner,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return store.TransitionAsync(
            new EnvironmentLifecycleTransitionRequest(
                owner,
                expectedLifecycleGeneration: 0,
                EnvironmentLifecycleState.Active,
                idempotencyKey),
            cancellationToken);
    }

    public Task<EnvironmentLifecycleTransitionResult> TransitionAsync(
        EnvironmentGenerationFence expectedFence,
        EnvironmentLifecycleState targetState,
        string idempotencyKey,
        CancellationToken cancellationToken)
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
            cancellationToken);
    }
}
