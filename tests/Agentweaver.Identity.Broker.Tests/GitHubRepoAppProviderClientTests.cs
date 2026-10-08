using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity.Broker;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GitHubRepoAppProviderClientTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T09:00:00Z");

    [Fact]
    public async Task ExchangesAuthorizationCodeWithPkceAndKeepsRotatingTokensRedacted()
    {
        var oauthHandler = new StubHandler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/login/oauth/access_token", request.RequestUri!.AbsolutePath);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            var fields = ParseForm(await request.Content.ReadAsStringAsync());
            Assert.Equal("repo-client", fields["client_id"]);
            Assert.Equal("repo-client-secret", fields["client_secret"]);
            Assert.Equal("authorization-code", fields["code"]);
            Assert.Equal("https://app.example/auth/github/repo-app/callback", fields["redirect_uri"]);
            Assert.Equal("pkce-verifier", fields["code_verifier"]);
            return JsonResponse(
                "{\"access_token\":\"user-access-secret\",\"expires_in\":28800," +
                "\"refresh_token\":\"user-refresh-secret\",\"refresh_token_expires_in\":15897600}");
        });
        var client = CreateClient(oauthHandler, EmptyHandler());

        var tokens = await client.ExchangeCodeAsync(
            "authorization-code", "pkce-verifier", CancellationToken.None);

        Assert.Equal(Now.AddSeconds(28800), tokens.AccessToken.ExpiresAt);
        Assert.Equal(Now.AddSeconds(15897600), tokens.RefreshToken.ExpiresAt);
        Assert.Equal("user-access-secret", tokens.AccessToken.GetValue());
        Assert.Equal("user-refresh-secret", tokens.RefreshToken.GetValue());
        Assert.DoesNotContain("user-access-secret", tokens.AccessToken.ToString());
        Assert.DoesNotContain("user-refresh-secret", tokens.RefreshToken.ToString());
    }

    [Fact]
    public async Task RefreshUsesRotatedRefreshTokenAndReportsUncertainProviderFailure()
    {
        var calls = 0;
        var oauthHandler = new StubHandler(async (request, _) =>
        {
            calls++;
            var fields = ParseForm(await request.Content!.ReadAsStringAsync());
            Assert.Equal("refresh_token", fields["grant_type"]);
            Assert.Equal("old-refresh-token", fields["refresh_token"]);
            return JsonResponse(
                "{\"access_token\":\"new-access-token\",\"expires_in\":1800," +
                "\"refresh_token\":\"new-refresh-token\",\"refresh_token_expires_in\":86400}");
        });
        var client = CreateClient(oauthHandler, EmptyHandler());
        var refreshToken = new SecretCredential("old-refresh-token", Now.AddHours(1), new FrozenTimeProvider(Now));

        var tokens = await client.RefreshAsync(refreshToken, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("new-access-token", tokens.AccessToken.GetValue());
        Assert.Equal("new-refresh-token", tokens.RefreshToken.GetValue());
        Assert.Equal(Now.AddSeconds(86400), tokens.RefreshToken.ExpiresAt);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task RejectsNonObjectProviderResponsesAsSanitizedInvalidResponse(string body)
    {
        var client = CreateClient(
            EmptyHandler(),
            new StubHandler((_, _) => Task.FromResult(JsonResponse(body))));
        var accessToken = new SecretCredential(
            "user-access-token", Now.AddHours(1), new FrozenTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<GitHubRepoAppProviderException>(
            () => client.ReadLoginAsync(accessToken, CancellationToken.None));

        Assert.Equal(GitHubRepoAppProviderFailure.InvalidResponse, exception.Failure);
        Assert.DoesNotContain(body, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryBindsRepositoryToUserAuthorizedInstallationWithoutReturningCredentials()
    {
        var requests = new List<string>();
        var apiHandler = new StubHandler((request, _) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("user-access-token", request.Headers.Authorization.Parameter);
            Assert.Equal("2022-11-28", request.Headers.GetValues("X-GitHub-Api-Version").Single());
            requests.Add(request.RequestUri!.PathAndQuery);
            var body = request.RequestUri.PathAndQuery switch
            {
                "/user/installations?per_page=100&page=1" =>
                    "{\"installations\":[{\"id\":456,\"account\":{\"login\":\"octo\",\"type\":\"Organization\"}," +
                    "\"repository_selection\":\"selected\"}]}",
                "/user/installations/456/repositories?per_page=100&page=1" =>
                    "{\"repositories\":[{\"id\":789,\"full_name\":\"octo/widget\"," +
                    "\"owner\":{\"login\":\"octo\"},\"private\":true,\"default_branch\":\"main\"}]}",
                _ => throw new Xunit.Sdk.XunitException(
                    $"Unexpected GitHub repository discovery path: {request.RequestUri.PathAndQuery}")
            };
            return Task.FromResult(JsonResponse(body));
        });
        var client = CreateClient(EmptyHandler(), apiHandler);
        var accessToken = new SecretCredential("user-access-token", Now.AddHours(1), new FrozenTimeProvider(Now));

        var result = await client.BrowseAsync(accessToken, CancellationToken.None);

        Assert.Equal(
            ["/user/installations?per_page=100&page=1",
             "/user/installations/456/repositories?per_page=100&page=1"],
            requests);
        var repository = Assert.Single(result.Repositories);
        Assert.Equal(456, repository.InstallationId);
        Assert.Equal(789, repository.RepositoryId);
        Assert.Equal("octo/widget", repository.FullName);
        Assert.Equal("octo", repository.OwnerLogin);
        Assert.True(repository.IsPrivate);
        var installation = Assert.Single(result.Installations);
        Assert.Equal(456, installation.InstallationId);
        Assert.DoesNotContain("user-access-token", result.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task DiscoveryReadsAllInstallationPagesBeforeReturning()
    {
        var requests = new List<string>();
        var apiHandler = new StubHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri.PathAndQuery);
            if (uri.AbsolutePath == "/user/installations")
            {
                var page = PageNumber(uri);
                var offset = (page - 1) * 100;
                var count = Math.Min(100, 201 - offset);
                var installations = Enumerable.Range(offset + 1, count)
                    .Select(id => new
                    {
                        id,
                        account = new { login = $"org{id}", type = "Organization" },
                        repository_selection = "selected"
                    });
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new { installations })));
            }

            if (uri.AbsolutePath.StartsWith("/user/installations/", StringComparison.Ordinal) &&
                uri.AbsolutePath.EndsWith("/repositories", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse("{\"repositories\":[]}"));

            throw new Xunit.Sdk.XunitException($"Unexpected GitHub discovery path: {uri.PathAndQuery}");
        });
        var client = CreateClient(EmptyHandler(), apiHandler);
        var accessToken = new SecretCredential(
            "user-access-token", Now.AddHours(1), new FrozenTimeProvider(Now));

        var result = await client.BrowseAsync(accessToken, CancellationToken.None);

        Assert.True(
            result.Installations.Count == 201,
            $"Expected 201 installations; requested pages: {string.Join(", ", requests)}");
        Assert.Contains(result.Installations, installation => installation.InstallationId == 201);
        Assert.Contains("/user/installations?per_page=100&page=3", requests);
        Assert.Equal(204, requests.Count);
    }

    [Fact]
    public async Task DiscoveryReadsAllRepositoryPagesBeforeReturning()
    {
        var requests = new List<string>();
        var apiHandler = new StubHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri.PathAndQuery);
            if (uri.AbsolutePath == "/user/installations")
                return Task.FromResult(JsonResponse(
                    "{\"installations\":[{\"id\":456,\"account\":{\"login\":\"octo\"," +
                    "\"type\":\"Organization\"},\"repository_selection\":\"selected\"}]}"));

            if (uri.AbsolutePath == "/user/installations/456/repositories")
            {
                var page = PageNumber(uri);
                var offset = (page - 1) * 100;
                var count = Math.Min(100, 250 - offset);
                var repositories = Enumerable.Range(offset + 1, count)
                    .Select(id => new
                    {
                        id,
                        full_name = $"octo/repo{id}",
                        owner = new { login = "octo" },
                        @private = true,
                        default_branch = "main"
                    });
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new { repositories })));
            }

            throw new Xunit.Sdk.XunitException($"Unexpected GitHub discovery path: {uri.PathAndQuery}");
        });
        var client = CreateClient(EmptyHandler(), apiHandler);
        var accessToken = new SecretCredential(
            "user-access-token", Now.AddHours(1), new FrozenTimeProvider(Now));

        var result = await client.BrowseAsync(accessToken, CancellationToken.None);

        Assert.True(
            result.Repositories.Count == 250,
            $"Expected 250 repositories; requested pages: {string.Join(", ", requests)}");
        Assert.Contains(result.Repositories, repository => repository.FullName == "octo/repo250");
        Assert.Contains("/user/installations/456/repositories?per_page=100&page=3", requests);
        Assert.Equal(4, requests.Count);
    }

    [Fact]
    public async Task RefreshHttpFailureIsNotRetriedAndDoesNotExposeResponseBody()
    {
        var calls = 0;
        var oauthHandler = new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"error\":\"private-provider-detail\"}", Encoding.UTF8, "application/json")
            });
        });
        var client = CreateClient(oauthHandler, EmptyHandler());
        var refreshToken = new SecretCredential("old-refresh-token", Now.AddHours(1), new FrozenTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<GitHubRepoAppProviderException>(
            () => client.RefreshAsync(refreshToken, CancellationToken.None));

        Assert.Equal(GitHubRepoAppProviderFailure.OutcomeUncertain, exception.Failure);
        Assert.Equal(1, calls);
        Assert.DoesNotContain("private-provider-detail", exception.ToString());
        Assert.DoesNotContain("old-refresh-token", exception.ToString());
    }

    [Fact]
    public void RejectsUntrustedProviderOriginsAndInsecureCallbackConfiguration()
    {
        var time = new FrozenTimeProvider(Now);
        Assert.Throws<ArgumentException>(() => new GitHubRepoAppProviderClient(
            new HttpClient { BaseAddress = new Uri("https://github.com/") },
            new HttpClient { BaseAddress = new Uri("https://attacker.example/") },
            Options(new Uri("https://app.example/auth/github/repo-app/callback")),
            time));
        Assert.Throws<ArgumentException>(() => new GitHubRepoAppProviderClient(
            new HttpClient { BaseAddress = new Uri("https://github.com/") },
            new HttpClient { BaseAddress = new Uri("https://api.github.com/") },
            Options(new Uri("http://app.example/auth/github/repo-app/callback")),
            time));
    }

    private static GitHubRepoAppProviderClient CreateClient(
        HttpMessageHandler oauthHandler,
        HttpMessageHandler apiHandler) =>
        new(
            new HttpClient(oauthHandler) { BaseAddress = new Uri("https://github.com/") },
            new HttpClient(apiHandler) { BaseAddress = new Uri("https://api.github.com/") },
            Options(new Uri("https://app.example/auth/github/repo-app/callback")),
            new FrozenTimeProvider(Now));

    private static GitHubRepoAppProviderOptions Options(Uri callbackUri) =>
        new("repo-client", "repo-client-secret", callbackUri);

    private static HttpMessageHandler EmptyHandler() =>
        new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
                pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')),
                StringComparer.Ordinal);

    private static int PageNumber(Uri uri)
    {
        var page = uri.Query.Split('&')
            .Single(part => part.StartsWith("page=", StringComparison.Ordinal));
        return int.Parse(page.AsSpan("page=".Length), CultureInfo.InvariantCulture);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
