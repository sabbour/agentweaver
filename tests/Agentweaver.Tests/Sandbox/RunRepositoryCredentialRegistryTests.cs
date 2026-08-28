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
        registry.ActiveRunLockCount.Should().Be(1,
            "retained revocation state must keep its per-run lock available for a retry");

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
        registry.ActiveRunLockCount.Should().Be(0,
            "a successful terminal retry must remove and dispose the run's unused lock");
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

    [Fact]
    public async Task Revoke_TerminalCleanup_RemovesRunLock()
    {
        var now = new DateTimeOffset(2026, 8, 27, 20, 0, 0, TimeSpan.Zero);
        var minter = new StubCredentialMinter
        {
            Credential = new RepositoryCredential("terminal-cleanup-token", now.AddMinutes(5)),
        };
        var registry = new RunRepositoryCredentialRegistry(minter, new MutableTimeProvider(now));

        (await registry.MintAsync("run-terminal-cleanup")).Should().Be("terminal-cleanup-token");
        registry.ActiveRunLockCount.Should().Be(1,
            "an active credential must retain the lock that serializes its terminal revocation");

        await registry.RevokeAsync("run-terminal-cleanup");

        minter.RevokedTokens.Should().ContainSingle().Which.Should().Be("terminal-cleanup-token");
        registry.ActiveRunLockCount.Should().Be(0,
            "no credential, retained revocation, or operation remains after terminal cleanup");
    }

    [Fact]
    public async Task ConcurrentMintAndRevoke_KeepTheRunLockUntilBothOperationsComplete()
    {
        var now = new DateTimeOffset(2026, 8, 27, 20, 0, 0, TimeSpan.Zero);
        var minter = new BlockingCredentialMinter(
            new RepositoryCredential("concurrent-cleanup-token", now.AddMinutes(5)));
        var registry = new RunRepositoryCredentialRegistry(minter, new MutableTimeProvider(now));

        var mint = registry.MintAsync("run-concurrent-cleanup");
        await minter.MintStarted;
        var revoke = registry.RevokeAsync("run-concurrent-cleanup");
        registry.ActiveRunLockCount.Should().Be(1,
            "the queued revocation holds a lease before the mint operation can release its lock");

        minter.AllowMint();
        (await mint).Should().Be("concurrent-cleanup-token");
        await revoke;

        minter.RevokedTokens.Should().ContainSingle().Which.Should().Be("concurrent-cleanup-token");
        registry.ActiveRunLockCount.Should().Be(0,
            "the lock is disposed only after the queued terminal revocation completes");
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

    private sealed class BlockingCredentialMinter(RepositoryCredential credential)
        : IRunRepositoryCredentialMinter
    {
        private readonly TaskCompletionSource _mintStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowMint =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> RevokedTokens { get; } = [];
        public Task MintStarted => _mintStarted.Task;

        public async Task<RepositoryCredential?> MintAsync(string runId, CancellationToken ct)
        {
            _mintStarted.TrySetResult();
            await _allowMint.Task.WaitAsync(ct);
            return credential;
        }

        public Task RevokeAsync(string accessToken, CancellationToken ct)
        {
            RevokedTokens.Add(accessToken);
            return Task.CompletedTask;
        }

        public void AllowMint() => _allowMint.TrySetResult();
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
