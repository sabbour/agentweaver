using System.Net.Http;
using Agentweaver.Api.Sandbox;
using FluentAssertions;

namespace Agentweaver.Tests.Sandbox;

public sealed class RunRepositoryCredentialRegistryTests
{
    [Fact]
    public async Task Revoke_RetainsCredentialAfterThrownFailure_ThenRetriesAndCleansUp()
    {
        var minter = new StubCredentialMinter
        {
            Credential = new RepositoryCredential(
                "registry-sentinel-token",
                DateTimeOffset.UtcNow.AddMinutes(5)),
        };
        minter.RevokeFailures.Enqueue(new HttpRequestException("GitHub revoke failed."));
        var registry = new RunRepositoryCredentialRegistry(minter);

        (await registry.MintAsync("run-credential-retry")).Should().Be("registry-sentinel-token");
        var first = () => registry.RevokeAsync("run-credential-retry");
        await first.Should().ThrowAsync<HttpRequestException>();

        await registry.RevokeAsync("run-credential-retry");
        await registry.RevokeAsync("run-credential-retry");

        minter.RevokedTokens.Should().HaveCount(2,
            "a failed revoke must retain its retry state, while a later success removes it");
        minter.RevokedTokens.Should().OnlyContain(token => token == "registry-sentinel-token");
    }

    [Fact]
    public async Task Revoke_DropsCredentialOnlyAfterActualExpiry()
    {
        var minter = new StubCredentialMinter
        {
            Credential = new RepositoryCredential(
                "expired-registry-token",
                DateTimeOffset.UtcNow.AddSeconds(-1)),
        };
        var registry = new RunRepositoryCredentialRegistry(minter);

        (await registry.MintAsync("run-expired-credential")).Should().BeNull();
        await registry.RevokeAsync("run-expired-credential");

        minter.RevokedTokens.Should().BeEmpty();
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
}
