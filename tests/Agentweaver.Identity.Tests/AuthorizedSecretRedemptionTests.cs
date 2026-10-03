using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class AuthorizedSecretRedemptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string ActorId = "actor-1";
    private const string ProjectId = "project-1";
    private const string RunId = "run-1";
    private const string Purpose = "purpose-1";
    private const string SecretId = "secret-1";
    private const string SecretVersion = "v1";
    private const string SensitiveValue = "test-secret-value-do-not-log";

    private static TrustedActorContext Actor(
        string actorId = ActorId, string projectId = ProjectId, string runId = RunId) =>
        new(actorId, projectId, runId);

    private static SecretRedemptionRequest Request(
        string runId = RunId, string purpose = Purpose, string secretId = SecretId, string version = SecretVersion) =>
        new(new SecretRef(secretId, version), purpose, runId);

    private static SecretRedemptionGrant Grant(
        TimeProvider clock,
        string grantId = "grant-1",
        string actorId = ActorId,
        string projectId = ProjectId,
        string runId = RunId,
        string purpose = Purpose,
        string secretId = SecretId,
        string version = SecretVersion,
        GrantState state = GrantState.Active,
        DateTimeOffset? expiresAt = null) =>
        new(grantId, actorId, projectId, runId, purpose, new SecretRef(secretId, version),
            state, expiresAt ?? Now.AddMinutes(10), clock);

    [Fact]
    public async Task ValidAccessAuthorizesAndReturnsTheBackendCredential()
    {
        var clock = new TestClock(Now);
        var grant = Grant(clock);
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return(grant), FakeGrantAuthority.Return(grant));
        var issued = new SecretCredential(SensitiveValue, Now.AddMinutes(5), clock);
        var backend = new FakeSecretRedemption((_, _) => Task.FromResult(issued));
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        var credential = await redemption.RedeemAsync(Request(), CancellationToken.None);

        Assert.Same(issued, credential);
        Assert.Equal(1, backend.Calls);
        Assert.Equal(2, authority.Calls);
        Assert.Equal(SensitiveValue, credential.GetValue());
    }

    [Theory]
    [MemberData(nameof(BindingMismatchCases))]
    public async Task BindingMismatchDeniesWithoutCallingBackend(
        Func<TestClock, SecretRedemptionGrant> grantFactory, SecretAuthorizationDenialReason expectedReason)
    {
        var clock = new TestClock(Now);
        var grant = grantFactory(clock);
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return(grant));
        var backend = new FakeSecretRedemption((_, _) =>
            throw new InvalidOperationException("Backend must not be called on denial."));
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        var exception = await Assert.ThrowsAsync<SecretAuthorizationDeniedException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));

        Assert.Equal(expectedReason, exception.Reason);
        Assert.Equal(0, backend.Calls);
    }

    public static IEnumerable<object[]> BindingMismatchCases()
    {
        yield return new object[]
        {
            (Func<TestClock, SecretRedemptionGrant>)(clock => Grant(clock, actorId: "other-actor")),
            SecretAuthorizationDenialReason.ActorMismatch,
        };
        yield return new object[]
        {
            (Func<TestClock, SecretRedemptionGrant>)(clock => Grant(clock, projectId: "other-project")),
            SecretAuthorizationDenialReason.ProjectMismatch,
        };
        yield return new object[]
        {
            (Func<TestClock, SecretRedemptionGrant>)(clock => Grant(clock, runId: "other-run")),
            SecretAuthorizationDenialReason.RunMismatch,
        };
        yield return new object[]
        {
            (Func<TestClock, SecretRedemptionGrant>)(clock => Grant(clock, purpose: "other-purpose")),
            SecretAuthorizationDenialReason.PurposeMismatch,
        };
        yield return new object[]
        {
            (Func<TestClock, SecretRedemptionGrant>)(clock => Grant(clock, secretId: "other-secret")),
            SecretAuthorizationDenialReason.SecretMismatch,
        };
        yield return new object[]
        {
            // Exact version is required: a non-matching version denies rather
            // than falling back to the grant's or any "latest" version.
            (Func<TestClock, SecretRedemptionGrant>)(clock => Grant(clock, version: "v2")),
            SecretAuthorizationDenialReason.VersionMismatch,
        };
        yield return new object[]
        {
            (Func<TestClock, SecretRedemptionGrant>)(clock => Grant(clock, state: GrantState.Revoked)),
            SecretAuthorizationDenialReason.Revoked,
        };
    }

    [Fact]
    public async Task RequestRunMismatchDeniesBeforeReadingTheAuthority()
    {
        var clock = new TestClock(Now);
        var authority = new FakeGrantAuthority();
        var backend = new FakeSecretRedemption((_, _) =>
            throw new InvalidOperationException("Backend must not be called on denial."));
        var redemption = new AuthorizedSecretRedemption(Actor(runId: "actor-run"), authority, backend, clock);

        var exception = await Assert.ThrowsAsync<SecretAuthorizationDeniedException>(
            () => redemption.RedeemAsync(Request(runId: "request-run"), CancellationToken.None));

        Assert.Equal(SecretAuthorizationDenialReason.RunMismatch, exception.Reason);
        Assert.Equal(0, authority.Calls);
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task NoMatchingGrantDeniesWithoutCallingBackend()
    {
        var clock = new TestClock(Now);
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return());
        var backend = new FakeSecretRedemption((_, _) =>
            throw new InvalidOperationException("Backend must not be called on denial."));
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        var exception = await Assert.ThrowsAsync<SecretAuthorizationDeniedException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));

        Assert.Equal(SecretAuthorizationDenialReason.NoGrant, exception.Reason);
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task NullAuthorityResultDeniesWithoutCallingBackend()
    {
        var clock = new TestClock(Now);
        var authority = new FakeGrantAuthority(() => null!);
        var backend = new FakeSecretRedemption((_, _) =>
            throw new InvalidOperationException("Backend must not be called on denial."));
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        var exception = await Assert.ThrowsAsync<SecretAuthorizationDeniedException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));

        Assert.Equal(SecretAuthorizationDenialReason.NoGrant, exception.Reason);
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task AmbiguousGrantsDenyWithoutCallingBackend()
    {
        var clock = new TestClock(Now);
        var first = Grant(clock, grantId: "grant-1");
        var second = Grant(clock, grantId: "grant-2");
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return(first, second));
        var backend = new FakeSecretRedemption((_, _) =>
            throw new InvalidOperationException("Backend must not be called on denial."));
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        var exception = await Assert.ThrowsAsync<SecretAuthorizationDeniedException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));

        Assert.Equal(SecretAuthorizationDenialReason.Ambiguous, exception.Reason);
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task ExpiredGrantDeniesWithoutCallingBackend()
    {
        var clock = new TestClock(Now);
        var grant = Grant(clock, expiresAt: Now.AddSeconds(30));
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return(grant));
        var backend = new FakeSecretRedemption((_, _) =>
            throw new InvalidOperationException("Backend must not be called on denial."));
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);
        clock.UtcNow = Now.AddMinutes(1);

        var exception = await Assert.ThrowsAsync<SecretAuthorizationDeniedException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));

        Assert.Equal(SecretAuthorizationDenialReason.Expired, exception.Reason);
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task AuthorityFailureBeforeBackendPropagatesWithoutCallingBackend()
    {
        var clock = new TestClock(Now);
        var failure = new InvalidOperationException("authority store unavailable");
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Throw(failure));
        var backend = new FakeSecretRedemption((_, _) =>
            throw new InvalidOperationException("Backend must not be called on denial."));
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task AuthorityFailureAfterAcquisitionInvalidatesCredentialAndPropagates()
    {
        var clock = new TestClock(Now);
        var grant = Grant(clock);
        var failure = new InvalidOperationException("authority store unavailable on recheck");
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return(grant), FakeGrantAuthority.Throw(failure));
        SecretCredential? issued = null;
        var backend = new FakeSecretRedemption((_, _) =>
        {
            issued = new SecretCredential(SensitiveValue, Now.AddMinutes(5), clock);
            return Task.FromResult(issued);
        });
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Equal(1, backend.Calls);
        Assert.NotNull(issued);
        Assert.Throws<InvalidOperationException>(() => issued!.GetValue());
    }

    [Fact]
    public async Task RevocationDuringAcquisitionInvalidatesCredentialInsteadOfReturningIt()
    {
        var clock = new TestClock(Now);
        var active = Grant(clock);
        var revoked = Grant(clock, state: GrantState.Revoked);
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return(active), FakeGrantAuthority.Return(revoked));
        SecretCredential? issued = null;
        var backend = new FakeSecretRedemption((_, _) =>
        {
            issued = new SecretCredential(SensitiveValue, Now.AddMinutes(5), clock);
            return Task.FromResult(issued);
        });
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        var exception = await Assert.ThrowsAsync<SecretAuthorizationDeniedException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));

        Assert.Equal(SecretAuthorizationDenialReason.Revoked, exception.Reason);
        Assert.Equal(1, backend.Calls);
        Assert.NotNull(issued);
        Assert.Throws<InvalidOperationException>(() => issued!.GetValue());
    }

    [Fact]
    public async Task RevocationBeforeRefreshDeniesWithoutASecondBackendCall()
    {
        var clock = new TestClock(Now);
        var active = Grant(clock);
        var revoked = Grant(clock, state: GrantState.Revoked);
        var authority = new FakeGrantAuthority(
            FakeGrantAuthority.Return(active), FakeGrantAuthority.Return(active),
            FakeGrantAuthority.Return(revoked));
        var backend = new FakeSecretRedemption((_, _) =>
            Task.FromResult(new SecretCredential(SensitiveValue, Now.AddMinutes(5), clock)));
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        await redemption.RedeemAsync(Request(), CancellationToken.None);
        var exception = await Assert.ThrowsAsync<SecretAuthorizationDeniedException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));

        Assert.Equal(SecretAuthorizationDenialReason.Revoked, exception.Reason);
        Assert.Equal(3, authority.Calls);
        Assert.Equal(1, backend.Calls);
    }

    [Fact]
    public async Task ExpiryDuringAcquisitionInvalidatesCredentialInsteadOfReturningIt()
    {
        var clock = new TestClock(Now);
        var grant = Grant(clock, expiresAt: Now.AddSeconds(30));
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return(grant), FakeGrantAuthority.Return(grant));
        SecretCredential? issued = null;
        var backend = new FakeSecretRedemption((_, _) =>
        {
            clock.UtcNow = Now.AddMinutes(1);
            issued = new SecretCredential(SensitiveValue, clock.UtcNow.AddMinutes(5), clock);
            return Task.FromResult(issued);
        });
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        var exception = await Assert.ThrowsAsync<SecretAuthorizationDeniedException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));

        Assert.Equal(SecretAuthorizationDenialReason.Expired, exception.Reason);
        Assert.NotNull(issued);
        Assert.Throws<InvalidOperationException>(() => issued!.GetValue());
    }

    [Fact]
    public async Task RevisedGrantSharingTheSameGrantIdButADifferentBindingIsRejectedOnRecheck()
    {
        var clock = new TestClock(Now);
        var original = Grant(clock, grantId: "grant-1");
        // Simulates an attempted "revision" of the same grant identity to a
        // different secret version; immutability means this can only be
        // modeled by the authority returning a replacement, and the recheck
        // must still re-validate it against the original exact request.
        var revised = Grant(clock, grantId: "grant-1", version: "v2");
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return(original), FakeGrantAuthority.Return(revised));
        SecretCredential? issued = null;
        var backend = new FakeSecretRedemption((_, _) =>
        {
            issued = new SecretCredential(SensitiveValue, Now.AddMinutes(5), clock);
            return Task.FromResult(issued);
        });
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        var exception = await Assert.ThrowsAsync<SecretAuthorizationDeniedException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));

        Assert.Equal(SecretAuthorizationDenialReason.VersionMismatch, exception.Reason);
        Assert.NotNull(issued);
        Assert.Throws<InvalidOperationException>(() => issued!.GetValue());
    }

    [Fact]
    public async Task CancellationBeforeCallThrowsWithoutCallingAuthorityOrBackend()
    {
        var clock = new TestClock(Now);
        var authority = new FakeGrantAuthority();
        var backend = new FakeSecretRedemption((_, _) =>
            throw new InvalidOperationException("Backend must not be called."));
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => redemption.RedeemAsync(Request(), cts.Token));

        Assert.Equal(0, authority.Calls);
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task CancellationSignaledDuringAuthorityLookupPropagatesWithoutCallingBackend()
    {
        var clock = new TestClock(Now);
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Throw(new OperationCanceledException()));
        var backend = new FakeSecretRedemption((_, _) =>
            throw new InvalidOperationException("Backend must not be called."));
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));

        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task CancellationDuringBackendCallPropagatesWithoutReauthorizing()
    {
        var clock = new TestClock(Now);
        var grant = Grant(clock);
        // Only one response is configured: a second (recheck) read would
        // throw from the fake, proving cancellation skips the recheck.
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return(grant));
        var backend = new FakeSecretRedemption((_, ct) => throw new OperationCanceledException(ct));
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => redemption.RedeemAsync(Request(), cts.Token));

        Assert.Equal(1, authority.Calls);
    }

    [Fact]
    public async Task CancellationAfterBackendReturnsInvalidatesAcquiredCredential()
    {
        var clock = new TestClock(Now);
        using var cts = new CancellationTokenSource();
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return(Grant(clock)));
        var issued = new SecretCredential(SensitiveValue, Now.AddMinutes(5), clock);
        var backend = new FakeSecretRedemption((_, _) =>
        {
            cts.Cancel();
            return Task.FromResult(issued);
        });
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => redemption.RedeemAsync(Request(), cts.Token));

        Assert.Equal(1, authority.Calls);
        Assert.Throws<InvalidOperationException>(() => issued.GetValue());
    }

    [Fact]
    public async Task CredentialExpiryIsClampedToGrantExpiryWhenBackendExpiryIsLater()
    {
        var clock = new TestClock(Now);
        var grant = Grant(clock, expiresAt: Now.AddMinutes(2));
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return(grant), FakeGrantAuthority.Return(grant));
        var issued = new SecretCredential(SensitiveValue, Now.AddMinutes(10), clock);
        var backend = new FakeSecretRedemption((_, _) => Task.FromResult(issued));
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        var credential = await redemption.RedeemAsync(Request(), CancellationToken.None);

        Assert.Same(issued, credential);
        Assert.Equal(grant.ExpiresAt, credential.ExpiresAt);
        Assert.Equal(SensitiveValue, credential.GetValue());
        Assert.Equal(nameof(SecretCredential) + " [REDACTED]", credential.ToString());
        credential.Invalidate();
        Assert.Throws<InvalidOperationException>(() => issued.GetValue());
    }

    [Fact]
    public async Task CredentialIsNotClampedWhenBackendExpiryIsAlreadyNoLaterThanGrant()
    {
        var clock = new TestClock(Now);
        var grant = Grant(clock, expiresAt: Now.AddMinutes(10));
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return(grant), FakeGrantAuthority.Return(grant));
        var issued = new SecretCredential(SensitiveValue, Now.AddMinutes(2), clock);
        var backend = new FakeSecretRedemption((_, _) => Task.FromResult(issued));
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        var credential = await redemption.RedeemAsync(Request(), CancellationToken.None);

        Assert.Same(issued, credential);
        Assert.Equal(issued.ExpiresAt, credential.ExpiresAt);
    }

    [Fact]
    public async Task ClampFailureAfterAuthorizationInvalidatesAcquiredCredential()
    {
        var clock = new TestClock(Now);
        var credentialClock = new TestClock(Now);
        var grant = Grant(clock);
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return(grant), FakeGrantAuthority.Return(grant));
        var issued = new SecretCredential(SensitiveValue, Now.AddSeconds(1), credentialClock);
        var backend = new FakeSecretRedemption((_, _) =>
        {
            credentialClock.UtcNow = Now.AddSeconds(1);
            return Task.FromResult(issued);
        });
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));
        credentialClock.UtcNow = Now;
        Assert.Throws<InvalidOperationException>(() => issued.GetValue());
    }

    [Fact]
    public async Task BackendOperationalFailurePropagatesWithoutASecondAuthorityRead()
    {
        var clock = new TestClock(Now);
        var grant = Grant(clock);
        // Only one response is configured: a recheck would throw from the
        // fake, proving the backend failure path never re-reads authority.
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return(grant));
        var failure = new InvalidOperationException("backend unavailable");
        var backend = new FakeSecretRedemption((_, _) => throw failure);
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Equal(1, authority.Calls);
    }

    [Fact]
    public void ConstructorRequiresAllCollaborators()
    {
        var clock = new TestClock(Now);
        var authority = new FakeGrantAuthority();
        var backend = new FakeSecretRedemption((_, _) => throw new InvalidOperationException());

        Assert.Throws<ArgumentNullException>(() => new AuthorizedSecretRedemption(null!, authority, backend, clock));
        Assert.Throws<ArgumentNullException>(() => new AuthorizedSecretRedemption(Actor(), null!, backend, clock));
        Assert.Throws<ArgumentNullException>(() => new AuthorizedSecretRedemption(Actor(), authority, null!, clock));
    }

    [Fact]
    public async Task RedeemAsyncRequiresANonNullRequest()
    {
        var clock = new TestClock(Now);
        var authority = new FakeGrantAuthority();
        var backend = new FakeSecretRedemption((_, _) => throw new InvalidOperationException());
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        await Assert.ThrowsAsync<ArgumentNullException>(() => redemption.RedeemAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task DenialDiagnosticsNeverContainSecretValueOrRequestIdentifiers()
    {
        var clock = new TestClock(Now);
        var authority = new FakeGrantAuthority(FakeGrantAuthority.Return());
        var backend = new FakeSecretRedemption((_, _) =>
            throw new InvalidOperationException("Backend must not be called on denial."));
        var redemption = new AuthorizedSecretRedemption(Actor(), authority, backend, clock);

        var exception = await Assert.ThrowsAsync<SecretAuthorizationDeniedException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));

        Assert.Equal("Secret redemption was denied (NoGrant).", exception.Message);
        Assert.DoesNotContain(SensitiveValue, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretId, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(RunId, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ActorId, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveValue, exception.ToString(), StringComparison.Ordinal);
    }
}
