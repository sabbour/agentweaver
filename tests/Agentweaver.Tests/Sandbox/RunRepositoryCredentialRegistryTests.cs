using System.Net.Http;
using Agentweaver.Api.Sandbox;
using FluentAssertions;

namespace Agentweaver.Tests.Sandbox;

public sealed class RunRepositoryCredentialRegistryTests
{
    [Fact]
    public async Task Revoke_RetainsCredentialAfterThrownFailure_ThenRetriesAndCleansUp()
    {
        var now = new DateTimeOffset(2026, 8, 27, 20, 0, 0, TimeSpan.Zero);
        var minter = new StubCredentialMinter
        {
            Credential = new RepositoryCredential(
                "registry-sentinel-token",
                now.AddMinutes(5)),
        };
        minter.RevokeFailures.Enqueue(new HttpRequestException("GitHub revoke failed."));
        var clock = new MutableTimeProvider(now);
        var registry = new RunRepositoryCredentialRegistry(minter, clock);

        (await registry.MintAsync("run-credential-retry")).Should().Be("registry-sentinel-token");
        var first = () => registry.RevokeAsync("run-credential-retry");
        await first.Should().ThrowAsync<HttpRequestException>();

        await registry.RevokeAsync("run-credential-retry");
        (await registry.RetryFailedRevocationsAsync()).Should().BeEmpty(
            "the automatic retry must wait for its initial backoff");
        minter.RevokedTokens.Should().ContainSingle();

        clock.Advance(RunRepositoryCredentialRegistry.InitialRevocationRetryDelay);
        (await registry.RetryFailedRevocationsAsync()).Should().BeEmpty();
        await registry.RetryFailedRevocationsAsync();

        minter.RevokedTokens.Should().HaveCount(2,
            "a failed revoke must retain its retry state, while a later automatic success removes it");
        minter.RevokedTokens.Should().OnlyContain(token => token == "registry-sentinel-token");
    }

    [Fact]
    public async Task Retry_DropsRetainedCredentialOnlyAfterActualExpiry()
    {
        var now = new DateTimeOffset(2026, 8, 27, 20, 0, 0, TimeSpan.Zero);
        var minter = new StubCredentialMinter
        {
            Credential = new RepositoryCredential(
                "expired-registry-token",
                now.AddSeconds(1)),
        };
        minter.RevokeFailures.Enqueue(new HttpRequestException("GitHub revoke failed."));
        var clock = new MutableTimeProvider(now);
        var registry = new RunRepositoryCredentialRegistry(minter, clock);

        (await registry.MintAsync("run-expired-credential")).Should().Be("expired-registry-token");
        var first = () => registry.RevokeAsync("run-expired-credential");
        await first.Should().ThrowAsync<HttpRequestException>();

        clock.Advance(RunRepositoryCredentialRegistry.InitialRevocationRetryDelay);
        await registry.RevokeAsync("run-expired-credential");
        await registry.RetryFailedRevocationsAsync();

        minter.RevokedTokens.Should().ContainSingle(
            "an expired credential must not be sent for another provider revocation attempt");
    }

    private sealed class StubCredentialMinter : IRunRepositoryCredentialMinter
    {
        public RepositoryCredential? Credential { get; init; }
        public Queue<Exception> RevokeFailures { get; } = new();
        public List<string> RevokedTokens { get; } = [];

        public Task<RepositoryCredential?> MintAsync(string runId, CancellationToken ct) =>
            Task.FromResult(Credential);

        public Task RevokeAsync(string accessToken, CancellationToken ct)
        {
            RevokedTokens.Add(accessToken);
            if (RevokeFailures.TryDequeue(out var failure))
                throw failure;
            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
