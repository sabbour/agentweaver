using System.Text.Json;
using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class AuthorizationRaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly TestClock _clock = new(Now);
    private static TrustedActorContext Actor => new("actor", "project", "run");
    private static SecretRedemptionRequest Request => new(new SecretRef("secret", "v1"), "purpose", "run");
    private SecretRedemptionGrant Grant(
        string id = "grant", string revision = "1", DateTimeOffset? expiry = null,
        GrantState state = GrantState.Active) =>
        new(id, "actor", "project", "run", "purpose", new SecretRef("secret", "v1"),
            state, expiry ?? Now.AddMinutes(2), _clock, revision);

    [Theory]
    [InlineData("identity")]
    [InlineData("revision")]
    [InlineData("longer-expiry")]
    [InlineData("shorter-expiry")]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("missing")]
    [InlineData("null-list")]
    [InlineData("null-grant")]
    [InlineData("ambiguous")]
    [InlineData("authority-failure")]
    public async Task AuthorityChangesDuringSuspendedBackendInvalidateTheAcquiredCredential(string change)
    {
        var original = Grant();
        var replacement = change switch
        {
            "identity" => Grant(id: "replacement"),
            "revision" => Grant(revision: "2"),
            "longer-expiry" => Grant(expiry: Now.AddMinutes(3)),
            "shorter-expiry" => Grant(expiry: Now.AddMinutes(1)),
            "revoked" => Grant(state: GrantState.Revoked),
            _ => original,
        };
        IReadOnlyList<SecretRedemptionGrant>? current = [original];
        var failure = new IOException("Authority unavailable.");
        var lookupFailure = false;
        var authority = new AsyncAuthority((_, _, _) => lookupFailure
            ? Task.FromException<IReadOnlyList<SecretRedemptionGrant>>(failure)
            : Task.FromResult(current!));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquisition = new TaskCompletionSource<SecretCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeSecretRedemption((_, _) =>
        {
            entered.SetResult();
            return acquisition.Task;
        });
        var redemption = new AuthorizedSecretRedemption(Actor, authority, backend, _clock);
        var operation = redemption.RedeemAsync(Request, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        current = change switch
        {
            "missing" => [],
            "null-list" => null,
            "null-grant" => [null!],
            "ambiguous" => [original, replacement],
            _ => [replacement],
        };
        if (change == "expired") _clock.UtcNow = original.ExpiresAt;
        lookupFailure = change == "authority-failure";
        var issued = new SecretCredential("nonsensitive-placeholder", _clock.UtcNow.AddMinutes(5), _clock);
        acquisition.SetResult(issued);

        if (lookupFailure)
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => operation));
        else
        {
            var denied = await Assert.ThrowsAsync<SecretAuthorizationDeniedException>(() => operation);
            var expected = change switch
            {
                "revoked" => SecretAuthorizationDenialReason.Revoked,
                "expired" => SecretAuthorizationDenialReason.Expired,
                "ambiguous" => SecretAuthorizationDenialReason.Ambiguous,
                "missing" or "null-list" or "null-grant" => SecretAuthorizationDenialReason.NoGrant,
                _ => SecretAuthorizationDenialReason.GrantChanged,
            };
            Assert.Equal(expected, denied.Reason);
        }
        _clock.UtcNow = Now;
        Assert.Throws<InvalidOperationException>(() => issued.GetValue());
        Assert.Equal(2, authority.Calls);
        Assert.Equal(1, backend.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringSuspendedAuthorityReadIsObservedEvenWhenItIgnoresToken(bool recheck)
    {
        using var cancellation = new CancellationTokenSource();
        var grant = Grant();
        var pending = new TaskCompletionSource<IReadOnlyList<SecretRedemptionGrant>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authority = new AsyncAuthority((call, _, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            if (call == (recheck ? 2 : 1))
            {
                entered.SetResult();
                return pending.Task;
            }
            return Task.FromResult<IReadOnlyList<SecretRedemptionGrant>>([grant]);
        });
        var issued = new SecretCredential("placeholder", Now.AddMinutes(5), _clock);
        var backend = new FakeSecretRedemption((_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            return Task.FromResult(issued);
        });
        var redemption = new AuthorizedSecretRedemption(Actor, authority, backend, _clock);
        var operation = redemption.RedeemAsync(Request, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        pending.SetResult([grant]);
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(recheck ? 1 : 0, backend.Calls);
        if (recheck) Assert.Throws<InvalidOperationException>(() => issued.GetValue());
    }

    [Fact]
    public async Task CancellationDuringSuspendedBackendInvalidatesReturnedCredential()
    {
        using var cancellation = new CancellationTokenSource();
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return(Grant()));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<SecretCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeSecretRedemption((_, _) =>
        {
            entered.SetResult();
            return pending.Task;
        });
        var redemption = new AuthorizedSecretRedemption(Actor, authority, backend, _clock);
        var operation = redemption.RedeemAsync(Request, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var issued = new SecretCredential("placeholder", Now.AddMinutes(5), _clock);
        pending.SetResult(issued);
        await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
        Assert.Throws<InvalidOperationException>(() => issued.GetValue());
        Assert.Equal(1, authority.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("invalidated")]
    [InlineData("expired")]
    public async Task InvalidBackendResultNeverReturnsACredential(string condition)
    {
        var grant = Grant();
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return(grant), FakeGrantAuthority.Return(grant));
        var issued = new SecretCredential("placeholder", Now.AddSeconds(1), _clock);
        var backend = new FakeSecretRedemption((_, _) =>
        {
            if (condition == "invalidated") issued.Invalidate();
            if (condition == "expired") _clock.UtcNow = Now.AddSeconds(1);
            return Task.FromResult(condition == "null" ? null! : issued);
        });
        var redemption = new AuthorizedSecretRedemption(Actor, authority, backend, _clock);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => redemption.RedeemAsync(Request, CancellationToken.None));
        if (condition != "null")
        {
            _clock.UtcNow = Now;
            Assert.Throws<InvalidOperationException>(() => issued.GetValue());
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task MetadataClampPreservesCredentialAndBackendInvalidation(int backendMinutes)
    {
        var original = Grant();
        var rehydrated = Grant();
        var authority = new FakeGrantAuthority(
            FakeGrantAuthority.Return(original), FakeGrantAuthority.Return(rehydrated));
        var issued = new SecretCredential("placeholder", Now.AddMinutes(backendMinutes), _clock);
        var backend = new FakeSecretRedemption((request, _) =>
        {
            Assert.Equal("secret", request.Secret.Id);
            Assert.Equal("v1", request.Secret.Version);
            return Task.FromResult(issued);
        });
        var redemption = new AuthorizedSecretRedemption(Actor, authority, backend, _clock);
        var result = await redemption.RedeemAsync(Request, CancellationToken.None);
        Assert.Same(issued, result);
        Assert.Equal(Now.AddMinutes(Math.Min(backendMinutes, 2)), result.ExpiresAt);
        Assert.DoesNotContain("placeholder", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        issued.Invalidate();
        Assert.Throws<InvalidOperationException>(() => result.GetValue());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterMetadataLimitStillInvalidates(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var grant = Grant();
        var armed = false;
        var checks = 0;
        var finalClock = new CallbackClock(() =>
        {
            if (armed && ++checks == 2)
            {
                if (cancel) cancellation.Cancel();
                else return grant.ExpiresAt;
            }
            return Now;
        });
        var authority = new FakeGrantAuthority(
            FakeGrantAuthority.Return(grant),
            () => { armed = true; return [grant]; });
        var issued = new SecretCredential("placeholder", Now.AddMinutes(5), _clock);
        var backend = new FakeSecretRedemption((_, _) => Task.FromResult(issued));
        var redemption = new AuthorizedSecretRedemption(Actor, authority, backend, finalClock);
        if (cancel)
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => redemption.RedeemAsync(Request, cancellation.Token));
        else
        {
            var denied = await Assert.ThrowsAsync<SecretAuthorizationDeniedException>(
                () => redemption.RedeemAsync(Request, cancellation.Token));
            Assert.Equal(SecretAuthorizationDenialReason.Expired, denied.Reason);
        }
        Assert.Equal(grant.ExpiresAt, issued.ExpiresAt);
        Assert.Throws<InvalidOperationException>(() => issued.GetValue());
    }

    private sealed class CallbackClock(Func<DateTimeOffset> now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now();
    }

    private sealed class AsyncAuthority(
        Func<int, SecretRedemptionRequest, CancellationToken, Task<IReadOnlyList<SecretRedemptionGrant>>> lookup)
        : IGrantAuthority
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<SecretRedemptionGrant>> FindGrantsAsync(
            TrustedActorContext actor, SecretRedemptionRequest request, CancellationToken cancellationToken)
        {
            Assert.Equal("actor", actor.ActorId);
            Assert.Equal("project", actor.ProjectId);
            return lookup(++Calls, request, cancellationToken);
        }
    }
}
