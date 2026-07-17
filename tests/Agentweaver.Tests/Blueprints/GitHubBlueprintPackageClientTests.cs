using System.Net;
using System.Text;
using Agentweaver.Api.Blueprints;
using Agentweaver.Domain;
using Agentweaver.Domain.BlueprintPackages;
using FluentAssertions;

namespace Agentweaver.Tests.Blueprints;

public sealed class GitHubBlueprintPackageClientTests
{
    [Fact]
    public async Task ReadTree_RequiresExactTopLevelTreeIdentity()
    {
        var client = CreateClient(new DelegateHandler((_, _) => Task.FromResult(Json(
            HttpStatusCode.OK,
            $$"""{"sha":"{{ImportTestSupport.CatalogTreeSha}}","tree":[],"truncated":false}"""))));

        var action = () => client.ReadTreeAsync(
            Locator, ImportTestSupport.CommitSha, ImportTestSupport.TreeSha, recursive: false);

        (await action.Should().ThrowAsync<GitHubBlueprintPackageAcquisitionException>())
            .Which.Failure.Should().Be(GitHubBlueprintPackageAcquisitionFailure.ObjectChanged);
    }

    [Fact]
    public async Task Request_PreservesCallerCancellation()
    {
        var client = CreateClient(new DelegateHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json(HttpStatusCode.OK, "{}");
        }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var action = () => client.ResolveCommitAsync(Locator, cancellation.Token);
        var error = await Record.ExceptionAsync(action);

        error.Should().BeAssignableTo<OperationCanceledException>();
        error.Should().NotBeOfType<GitHubBlueprintPackageAcquisitionException>();
    }

    [Fact]
    public async Task Request_MapsHttpClientTimeoutToStableTransportFailure()
    {
        var client = CreateClient(
            new DelegateHandler(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Json(HttpStatusCode.OK, "{}");
            }),
            TimeSpan.FromMilliseconds(20));

        var action = () => client.ResolveCommitAsync(Locator);

        var error = (await action.Should().ThrowAsync<GitHubBlueprintPackageAcquisitionException>()).Which;
        error.Failure.Should().Be(GitHubBlueprintPackageAcquisitionFailure.Transport);
        error.Message.Should().Be("GitHub request timed out.");
    }

    [Theory]
    [InlineData("retry-after")]
    [InlineData("remaining")]
    [InlineData("secondary-message")]
    [InlineData("abuse-message")]
    public async Task Forbidden_ClassifiesGitHubRateLimitSignalsWithoutEchoingMetadata(string signal)
    {
        const string marker = "body-marker-must-not-escape";
        var response = Json(HttpStatusCode.Forbidden, signal switch
        {
            "secondary-message" => $$"""{"message":"You have exceeded a secondary rate limit. {{marker}}"}""",
            "abuse-message" => $$"""{"message":"You have triggered an abuse detection mechanism. {{marker}}"}""",
            _ => $$"""{"message":"forbidden {{marker}}"}""",
        });
        if (signal == "retry-after") response.Headers.TryAddWithoutValidation("Retry-After", "60");
        if (signal == "remaining") response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        var client = CreateClient(new DelegateHandler((_, _) => Task.FromResult(response)));

        var action = () => client.ResolveCommitAsync(Locator);

        var error = (await action.Should().ThrowAsync<GitHubBlueprintPackageAcquisitionException>()).Which;
        error.Failure.Should().Be(GitHubBlueprintPackageAcquisitionFailure.RateLimited);
        error.ToString().Should().NotContain(marker);
    }

    [Fact]
    public async Task Forbidden_WithoutBoundedRateLimitSignalRemainsForbidden()
    {
        const string marker = "ordinary-forbidden-marker";
        var oversized = $$"""{"message":"secondary rate limit {{marker}}","padding":"{{new string('x', 9_000)}}"}""";
        var responses = new[]
        {
            Json(HttpStatusCode.Forbidden, $$"""{"message":"access denied {{marker}}"}"""),
            Json(HttpStatusCode.Forbidden, oversized),
        };

        foreach (var response in responses)
        {
            var client = CreateClient(new DelegateHandler((_, _) => Task.FromResult(response)));
            var action = () => client.ResolveCommitAsync(Locator);

            var error = (await action.Should().ThrowAsync<GitHubBlueprintPackageAcquisitionException>()).Which;
            error.Failure.Should().Be(GitHubBlueprintPackageAcquisitionFailure.Forbidden);
            error.ToString().Should().NotContain(marker);
        }
    }

    private static readonly GitHubBlueprintPackageLocator Locator = new("octo", "blueprints");

    private static GitHubBlueprintPackageClient CreateClient(HttpMessageHandler handler, TimeSpan? timeout = null) =>
        new(
            new SingleClientFactory(handler, timeout),
            new ScopeProvider(),
            new AccessProvider(),
            new Owner());

    private static HttpResponseMessage Json(HttpStatusCode status, string content) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private sealed class SingleClientFactory(HttpMessageHandler handler, TimeSpan? timeout) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class ScopeProvider : IGitHubTokenScopeProvider
    {
        public GitHubTokenScope Resolve(string? userId) => GitHubTokenScope.Installation;
    }

    private sealed class AccessProvider : IGitHubAccessTokenProvider
    {
        public Task<string?> GetValidAccessTokenAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
            Task.FromResult<string?>("placeholder");
    }

    private sealed class Owner : IAuthenticatedOwnerContext
    {
        public string OwnerId => "owner";
    }
}
