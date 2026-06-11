using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Scaffolder.Api.Auth;
using Scaffolder.Domain;

namespace Scaffolder.Tests.Projects;

/// <summary>
/// Tests for GitHubDeviceFlowAuthService state machine.
/// Live tests that call real GitHub are gated behind GITHUB_INTEGRATION_TESTS=1.
/// State machine tests use a fake/stub HTTP handler to exercise the flow offline.
/// </summary>
public sealed class GitHubDeviceFlowTests
{
    // =========================================================================
    // DFT-01: PollDeviceFlowAsync returns Expired when no flow was started
    // =========================================================================
    [Fact]
    public async Task PollWithNoFlowStarted_ReturnsExpired()
    {
        var tokenStore = new InMemoryGitHubTokenStore();
        var scope      = GitHubTokenScope.Installation;

        var (service, _) = BuildService(tokenStore);

        var result = await service.PollDeviceFlowAsync(scope);

        result.Result.Should().Be(GitHubDeviceFlowPollResult.Expired,
            "polling before any flow is started must return Expired");
        result.Login.Should().BeNull();
    }

    // =========================================================================
    // DFT-02: Start -> Poll with authorization_pending -> Pending result
    // =========================================================================
    [Fact]
    public async Task StartThenPollPending_ReturnsPendingResult()
    {
        var tokenStore = new InMemoryGitHubTokenStore();
        var scope      = GitHubTokenScope.Installation;

        var responses = new Queue<string>(new[]
        {
            // Start response
            """{"device_code":"dc_123","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""",
            // Poll response: pending
            """{"error":"authorization_pending"}""",
        });

        var (service, _) = BuildService(tokenStore, responses);

        await service.StartDeviceFlowAsync(scope);
        var pollResult = await service.PollDeviceFlowAsync(scope);

        pollResult.Result.Should().Be(GitHubDeviceFlowPollResult.Pending);
    }

    // =========================================================================
    // DFT-03: Start -> Poll with slow_down -> treated as Pending
    // =========================================================================
    [Fact]
    public async Task StartThenPollSlowDown_ReturnsPendingResult()
    {
        var tokenStore = new InMemoryGitHubTokenStore();
        var scope      = GitHubTokenScope.Installation;

        var responses = new Queue<string>(new[]
        {
            """{"device_code":"dc_123","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""",
            """{"error":"slow_down"}""",
        });

        var (service, _) = BuildService(tokenStore, responses);

        await service.StartDeviceFlowAsync(scope);
        var pollResult = await service.PollDeviceFlowAsync(scope);

        pollResult.Result.Should().Be(GitHubDeviceFlowPollResult.Pending,
            "slow_down should be treated the same as authorization_pending");
    }

    // =========================================================================
    // DFT-04: Start -> Poll with access_denied -> Denied result
    // =========================================================================
    [Fact]
    public async Task StartThenPollAccessDenied_ReturnsDeniedResult()
    {
        var tokenStore = new InMemoryGitHubTokenStore();
        var scope      = GitHubTokenScope.Installation;

        var responses = new Queue<string>(new[]
        {
            """{"device_code":"dc_123","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""",
            """{"error":"access_denied"}""",
        });

        var (service, _) = BuildService(tokenStore, responses);

        await service.StartDeviceFlowAsync(scope);
        var pollResult = await service.PollDeviceFlowAsync(scope);

        pollResult.Result.Should().Be(GitHubDeviceFlowPollResult.Denied);
    }

    // =========================================================================
    // DFT-05: Expired device_code clears flow state
    // =========================================================================
    [Fact]
    public async Task StartThenPollExpired_ClearsFlowState()
    {
        var tokenStore = new InMemoryGitHubTokenStore();
        var scope      = GitHubTokenScope.Installation;

        var responses = new Queue<string>(new[]
        {
            // expires_in=1 so we can force expiry by waiting
            """{"device_code":"dc_123","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":1,"interval":5}""",
            """{"error":"expired_token"}""",
        });

        var (service, _) = BuildService(tokenStore, responses);

        await service.StartDeviceFlowAsync(scope);
        await Task.Delay(1200); // exceed expires_in=1

        // After expiry the server-side flow should be cleaned up regardless of the poll error
        var pollResult = await service.PollDeviceFlowAsync(scope);

        pollResult.Result.Should().Be(GitHubDeviceFlowPollResult.Expired,
            "flow must be marked expired after expires_in elapses");
    }

    // =========================================================================
    // DFT-06: SignOutAsync delegates to token store
    // =========================================================================
    [Fact]
    public async Task SignOutAsync_WritesSignedOutTombstone()
    {
        var tokenStore = new InMemoryGitHubTokenStore();
        var scope      = GitHubTokenScope.Installation;
        await tokenStore.SetAsync(scope, new GitHubToken("tok", null, null, "user", []));

        var (service, _) = BuildService(tokenStore);
        await service.SignOutAsync(scope);

        var entry = await tokenStore.GetAsync(scope);
        entry.Status.Should().Be(GitHubTokenStatus.SignedOut);
    }

    // =========================================================================
    // DFT-LIVE: Live integration test — requires GITHUB_INTEGRATION_TESTS=1
    // =========================================================================
    [Fact(Skip = "requires GITHUB_INTEGRATION_TESTS=1 and real GitHub OAuth app credentials")]
    public async Task LiveDeviceFlow_StartAndPoll_ReturnsUserCode()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_INTEGRATION_TESTS") != "1")
            return;

        var tokenStore = new InMemoryGitHubTokenStore();
        var scope      = GitHubTokenScope.Installation;
        var (service, _) = BuildService(tokenStore);

        var start = await service.StartDeviceFlowAsync(scope);
        start.UserCode.Should().NotBeNullOrWhiteSpace();
        start.VerificationUri.Should().StartWith("https://");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static (GitHubDeviceFlowAuthService Service, FakeHttpMessageHandler Handler) BuildService(
        IGitHubTokenStore tokenStore, Queue<string>? responses = null)
    {
        var handler = new FakeHttpMessageHandler(responses ?? new Queue<string>());
        var http    = new HttpClient(handler);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:GitHub:BaseUrl"]  = "https://github.com",
                ["Auth:GitHub:ClientId"] = "test-client-id",
                ["Auth:GitHub:Scopes"]   = "repo read:user",
            })
            .Build();

        var service = new GitHubDeviceFlowAuthService(
            config, tokenStore, http,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubDeviceFlowAuthService>.Instance);

        return (service, handler);
    }

    /// <summary>
    /// Fake HTTP handler that returns pre-configured JSON responses in order.
    /// Returns 200 OK for each queued response; returns 500 if the queue is empty.
    /// </summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public FakeHttpMessageHandler(Queue<string> responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responses.TryDequeue(out var json))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        }
    }
}
