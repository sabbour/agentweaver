using System.Net;
using System.Text;
using Agentweaver.Api.Github;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Github;

public sealed class GitHubRepositoryClientTests
{
    [Fact]
    public async Task ListRepositoryOwnersAsync_ReturnsUserFirstThenOrgs()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["https://api.github.com/user"] = """{"login":"octo"}""",
            ["https://api.github.com/user/orgs"] = """[{"login":"octo-org"},{"login":"other-org"}]""",
        });
        var client = new GitHubRepositoryClient(new SingleClientFactory(handler), NullLogger<GitHubRepositoryClient>.Instance);

        var owners = await client.ListRepositoryOwnersAsync("token");

        owners.Should().HaveCount(3);
        owners[0].Should().Be(new GitHubRepositoryOwner("octo", IsUser: true));
        owners[1].Should().Be(new GitHubRepositoryOwner("octo-org", IsUser: false));
        owners[2].Should().Be(new GitHubRepositoryOwner("other-org", IsUser: false));
    }

    [Fact]
    public async Task CreateRepositoryAsync_UsesUserReposEndpoint_WhenOwnerIsAuthenticatedUser()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["https://api.github.com/user"] = """{"login":"octo"}""",
        });
        handler.QueueResponse(HttpMethod.Post, Json(HttpStatusCode.Created,
            """{"full_name":"octo/widgets","html_url":"https://github.com/octo/widgets","clone_url":"https://github.com/octo/widgets.git","default_branch":"main"}"""));
        var client = new GitHubRepositoryClient(new SingleClientFactory(handler), NullLogger<GitHubRepositoryClient>.Instance);

        var result = await client.CreateRepositoryAsync("octo", "widgets", isPrivate: true, "token");

        result.Success.Should().BeTrue();
        result.FullName.Should().Be("octo/widgets");
        result.CloneUrl.Should().Be("https://github.com/octo/widgets.git");
        result.DefaultBranch.Should().Be("main");
        handler.PostRequests.Should().ContainSingle();
        handler.PostRequests[0].RequestUri!.AbsoluteUri.Should().Be("https://api.github.com/user/repos");
    }

    [Fact]
    public async Task CreateRepositoryAsync_UsesOrgReposEndpoint_WhenOwnerIsAnOrg()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["https://api.github.com/user"] = """{"login":"octo"}""",
        });
        handler.QueueResponse(HttpMethod.Post, Json(HttpStatusCode.Created,
            """{"full_name":"octo-org/widgets","html_url":"https://github.com/octo-org/widgets","clone_url":"https://github.com/octo-org/widgets.git","default_branch":"main"}"""));
        var client = new GitHubRepositoryClient(new SingleClientFactory(handler), NullLogger<GitHubRepositoryClient>.Instance);

        var result = await client.CreateRepositoryAsync("octo-org", "widgets", isPrivate: false, "token");

        result.Success.Should().BeTrue();
        handler.PostRequests.Should().ContainSingle();
        handler.PostRequests[0].RequestUri!.AbsoluteUri.Should().Be("https://api.github.com/orgs/octo-org/repos");
    }

    [Fact]
    public async Task CreateRepositoryAsync_RetriesWithNumericSuffix_OnNameCollision()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["https://api.github.com/user"] = """{"login":"octo"}""",
        });
        handler.QueueResponse(HttpMethod.Post, Json(HttpStatusCode.UnprocessableEntity, """{"message":"name already exists on this account"}"""));
        handler.QueueResponse(HttpMethod.Post, Json(HttpStatusCode.Created,
            """{"full_name":"octo/widgets-2","html_url":"https://github.com/octo/widgets-2","clone_url":"https://github.com/octo/widgets-2.git","default_branch":"main"}"""));
        var client = new GitHubRepositoryClient(new SingleClientFactory(handler), NullLogger<GitHubRepositoryClient>.Instance);

        var result = await client.CreateRepositoryAsync("octo", "widgets", isPrivate: true, "token");

        result.Success.Should().BeTrue();
        result.FullName.Should().Be("octo/widgets-2");
        handler.PostRequests.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateRepositoryAsync_GivesUpAfterMaxAttempts_OnRepeatedCollision()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["https://api.github.com/user"] = """{"login":"octo"}""",
        });
        for (var i = 0; i < GitHubRepositoryClient.MaxNameAttempts; i++)
            handler.QueueResponse(HttpMethod.Post, Json(HttpStatusCode.UnprocessableEntity, """{"message":"name already exists on this account"}"""));
        var client = new GitHubRepositoryClient(new SingleClientFactory(handler), NullLogger<GitHubRepositoryClient>.Instance);

        var result = await client.CreateRepositoryAsync("octo", "widgets", isPrivate: true, "token");

        result.Success.Should().BeFalse();
        result.ErrorReason.Should().Be("name-already-exists");
        handler.PostRequests.Should().HaveCount(GitHubRepositoryClient.MaxNameAttempts);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    /// Routes GET requests by exact URL from a fixed dictionary (re-used across every call, e.g.
    /// repeated /user lookups), and dispatches queued responses to POST requests in FIFO order.
    /// </summary>
    private sealed class RoutingHandler(IReadOnlyDictionary<string, string> getResponses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _postResponses = new();

        public List<HttpRequestMessage> PostRequests { get; } = [];

        public void QueueResponse(HttpMethod method, HttpResponseMessage response)
        {
            if (method == HttpMethod.Post) _postResponses.Enqueue(response);
            else throw new NotSupportedException("Only POST queuing is supported by this test handler.");
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                PostRequests.Add(request);
                return Task.FromResult(_postResponses.Count > 0
                    ? _postResponses.Dequeue()
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var url = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(getResponses.TryGetValue(url, out var body)
                ? Json(HttpStatusCode.OK, body)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
