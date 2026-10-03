using Agentweaver.Abstractions;

namespace Agentweaver.Identity.Tests;

public sealed class TestClock(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

// A fake authority whose responses are configured one per expected call, in
// order. Running out of configured responses throws, which makes an
// unexpected extra re-read (for example after a cancelled or failed backend
// call) fail the test loudly instead of silently returning stale data.
internal sealed class FakeGrantAuthority : IGrantAuthority
{
    private readonly Queue<Func<IReadOnlyList<SecretRedemptionGrant>>> _responses;

    public FakeGrantAuthority(params Func<IReadOnlyList<SecretRedemptionGrant>>[] responses)
    {
        _responses = new Queue<Func<IReadOnlyList<SecretRedemptionGrant>>>(responses);
    }

    public int Calls { get; private set; }

    public static Func<IReadOnlyList<SecretRedemptionGrant>> Return(params SecretRedemptionGrant[] grants) =>
        () => grants;

    public static Func<IReadOnlyList<SecretRedemptionGrant>> Throw(Exception exception) =>
        () => throw exception;

    public Task<IReadOnlyList<SecretRedemptionGrant>> FindGrantsAsync(
        TrustedActorContext actor, SecretRedemptionRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        if (_responses.Count == 0)
            throw new InvalidOperationException("Fake authority has no further configured responses for this call.");
        return Task.FromResult(_responses.Dequeue()());
    }
}

// A fake backend whose handler decides the result (or failure/cancellation)
// per call, so tests can simulate an in-flight race between acquisition and a
// grant change without a real credential backend.
internal sealed class FakeSecretRedemption : ISecretRedemption
{
    private readonly Func<SecretRedemptionRequest, CancellationToken, Task<SecretCredential>> _handler;

    public FakeSecretRedemption(Func<SecretRedemptionRequest, CancellationToken, Task<SecretCredential>> handler)
    {
        _handler = handler;
    }

    public int Calls { get; private set; }

    public Task<SecretCredential> RedeemAsync(SecretRedemptionRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        return _handler(request, cancellationToken);
    }
}
