using System.Text.Json;
using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Providers.Tests;

public sealed class SecretContractsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Placeholder = "nonsensitive-test-placeholder";
    private readonly TestClock _clock = new(Now);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("line\nbreak")]
    [InlineData("é")]
    public void ReferenceRejectsInvalidIdentifiers(string? invalid)
    {
        Assert.Throws<ArgumentException>(() => new SecretRef(invalid!, "v1"));
        Assert.Throws<ArgumentException>(() => new SecretRef("opaque", invalid!));
        Assert.Throws<ArgumentException>(() => new SecretRedemptionRequest(
            new SecretRef("opaque", "v1"), invalid!, "run-1"));
        Assert.Throws<ArgumentException>(() => new SecretRedemptionRequest(
            new SecretRef("opaque", "v1"), "source-control.checkout", invalid!));
    }

    [Fact]
    public void RequestRequiresExplicitReferenceAndBoundedIdentifiers()
    {
        Assert.Throws<ArgumentNullException>(() => new SecretRedemptionRequest(null!, "purpose", "run"));
        Assert.Throws<ArgumentException>(() => new SecretRef(new string('a', 257), "v1"));
        Assert.Throws<ArgumentException>(() => new SecretRedemptionRequest(
            new SecretRef("opaque", "v1"), new string('a', 257), "run"));

        var request = new SecretRedemptionRequest(new SecretRef("opaque-id", "v:2"), "source-control.checkout", "run-1");
        Assert.Equal("v:2", request.Secret.Version);
        Assert.Equal("source-control.checkout", request.Purpose);
        Assert.Equal("run-1", request.RunId);
    }

    [Fact]
    public void CredentialRequiresFutureExpiryAndNonemptyValue()
    {
        Assert.Throws<ArgumentException>(() => new SecretCredential("", Now.AddMinutes(1), _clock));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SecretCredential(Placeholder, Now, _clock));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SecretCredential(Placeholder, Now.AddSeconds(-1), _clock));
    }

    [Fact]
    public void CredentialExpiresAndCanBeInvalidatedWithoutDiagnosticDisclosure()
    {
        var credential = new SecretCredential(Placeholder, Now.AddMinutes(1), _clock);
        Assert.Equal(Placeholder, credential.GetValue());
        _clock.UtcNow = Now.AddMinutes(1);
        Assert.Throws<InvalidOperationException>(() => credential.GetValue());
        _clock.UtcNow = Now;
        credential.Invalidate();
        credential.Invalidate();
        var error = Assert.Throws<InvalidOperationException>(() => credential.GetValue());

        Assert.DoesNotContain(Placeholder, credential.ToString());
        Assert.DoesNotContain(Placeholder, JsonSerializer.Serialize(credential));
        Assert.DoesNotContain(Placeholder, error.ToString());
        Assert.DoesNotContain(Placeholder, Assert.Throws<ArgumentException>(() =>
            new SecretCredential("", Now.AddMinutes(1), _clock)).ToString());
    }

    [Fact]
    public async Task RedemptionContractPassesCancellationToTrustedImplementation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        ISecretRedemption redemption = new TestRedemption();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => redemption.RedeemAsync(
            new SecretRedemptionRequest(new SecretRef("opaque", "v1"), "purpose", "run"), cancellation.Token));
    }

    [Fact]
    public async Task LifetimeLimitIsMetadataOnlyMonotonicAndThreadSafe()
    {
        var credential = new SecretCredential(Placeholder, Now.AddMinutes(10), _clock);
        await Task.WhenAll(Enumerable.Range(1, 64).Select(seconds =>
            Task.Run(() => credential.LimitLifetime(Now.AddSeconds(seconds)))));
        Assert.Equal(Now.AddSeconds(1), credential.ExpiresAt);
        credential.LimitLifetime(Now.AddHours(1));
        Assert.Equal(Now.AddSeconds(1), credential.ExpiresAt);
        Assert.Equal(Placeholder, credential.GetValue());
        Assert.DoesNotContain(Placeholder, JsonSerializer.Serialize(credential), StringComparison.Ordinal);
        credential.Invalidate();
        Assert.Throws<InvalidOperationException>(() => credential.LimitLifetime(Now.AddMinutes(1)));
        Assert.Throws<InvalidOperationException>(() => credential.GetValue());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpiredLifetimeLimitInvalidatesWithoutRestoringValue(bool backendExpired)
    {
        var credential = new SecretCredential(Placeholder, Now.AddSeconds(1), _clock);
        if (backendExpired) _clock.UtcNow = Now.AddSeconds(1);
        Assert.Throws<InvalidOperationException>(() =>
            credential.LimitLifetime(backendExpired ? Now.AddMinutes(10) : Now));
        _clock.UtcNow = Now;
        Assert.Throws<InvalidOperationException>(() => credential.GetValue());
        Assert.Throws<InvalidOperationException>(() => credential.LimitLifetime(Now.AddMinutes(1)));
    }

    private sealed class TestRedemption : ISecretRedemption
    {
        public Task<SecretCredential> RedeemAsync(SecretRedemptionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SecretCredential(Placeholder, DateTimeOffset.UtcNow.AddMinutes(1)));
        }
    }

    private sealed class TestClock(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
