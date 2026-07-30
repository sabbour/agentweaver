using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;

namespace Agentweaver.Tests.Mcp;

public sealed class GitHubAuthToolsTests
{
    [Fact]
    public async Task GitHubAccountsList_CallsAccountsEndpoint()
    {
        HttpRequestMessage? capturedRequest = null;
        var tools = new GitHubAuthTools(CreateApiClient((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[] { new { login = "octocat", kind = "user" } })
            });
        }));

        var json = await tools.GitHubAccountsListAsync(CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Method.Should().Be(HttpMethod.Get);
        capturedRequest.RequestUri!.AbsolutePath.Should().Be("/api/github/accounts");
        JsonDocument.Parse(json).RootElement.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GitHubReposList_WithoutAccount_CallsDefaultReposEndpoint()
    {
        HttpRequestMessage? capturedRequest = null;
        var tools = new GitHubAuthTools(CreateApiClient((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[] { new { fullName = "octocat/hello-world" } })
            });
        }));

        var json = await tools.GitHubReposListAsync(ct: CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Method.Should().Be(HttpMethod.Get);
        capturedRequest.RequestUri!.PathAndQuery.Should().Be("/api/github/repos");
        JsonDocument.Parse(json).RootElement.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GitHubReposList_WithAccount_EncodesQueryParameter()
    {
        HttpRequestMessage? capturedRequest = null;
        var tools = new GitHubAuthTools(CreateApiClient((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[] { new { fullName = "contoso/repo" } })
            });
        }));

        await tools.GitHubReposListAsync("Azure Dev", CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestUri!.PathAndQuery.Should().Be("/api/github/repos?account=Azure%20Dev");
    }

    private static AgentweaverApiClient CreateApiClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var httpClient = new HttpClient(new DelegatingHandlerStub(handler))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        return new AgentweaverApiClient(httpClient, new McpConfig("http://localhost", "test-api-key"));
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
