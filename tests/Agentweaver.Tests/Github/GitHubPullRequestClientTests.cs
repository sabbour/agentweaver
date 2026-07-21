using System.Net;
using System.Text;
using Agentweaver.Api.Github;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Github;

public sealed class GitHubPullRequestClientTests
{
    [Fact]
    public async Task CreatePullRequestAsync_ReusesExistingOpenPullRequest_WhenGitHubSaysItAlreadyExists()
    {
        var handler = new RoutingHandler(
            [
                Json(HttpStatusCode.UnprocessableEntity, """{"message":"A pull request already exists for octo:feature."}"""),
                Json(HttpStatusCode.OK, """[{"number":42,"html_url":"https://github.com/octo/widgets/pull/42"}]"""),
            ]);
        var client = new GitHubPullRequestClient(
            new SingleClientFactory(handler),
            NullLogger<GitHubPullRequestClient>.Instance);

        var result = await client.CreatePullRequestAsync(
            "octo", "widgets", "Title", "Body", "main", "feature", draft: false, "token");

        result.Should().Be(GitHubPullRequestResult.Ok(42, "https://github.com/octo/widgets/pull/42"));
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/repos/octo/widgets/pulls");
        handler.Requests[1].Method.Should().Be(HttpMethod.Get);
        handler.Requests[1].RequestUri!.Query.Should().Be("?head=octo%3Afeature&base=main&state=open");
    }

    [Fact]
    public async Task CreatePullRequestAsync_FallsBackToAlreadyExistsFailure_WhenLookupFindsNothing()
    {
        var handler = new RoutingHandler(
            [
                Json(HttpStatusCode.UnprocessableEntity, """{"message":"A pull request already exists for octo:feature."}"""),
                Json(HttpStatusCode.OK, "[]"),
            ]);
        var client = new GitHubPullRequestClient(
            new SingleClientFactory(handler),
            NullLogger<GitHubPullRequestClient>.Instance);

        var result = await client.CreatePullRequestAsync(
            "octo", "widgets", "Title", "Body", "main", "feature", draft: false, "token");

        result.Success.Should().BeFalse();
        result.ErrorReason.Should().Be("pull-request-already-exists");
        result.ErrorMessage.Should().Contain("GitHub rejected the pull request (422)");
        handler.Requests.Should().HaveCount(2);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RoutingHandler(IReadOnlyList<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private int _index;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responses[_index++]);
        }
    }
}
