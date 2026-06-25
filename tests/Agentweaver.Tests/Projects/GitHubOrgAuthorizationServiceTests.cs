using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Auth;

namespace Agentweaver.Tests.Projects;

public sealed class GitHubOrgAuthorizationServiceTests
{
    [Fact]
    public async Task CheckMembershipAsync_Allows_WhenAllowedOrgIsAuthorized()
    {
        var handler = new StaticHttpMessageHandler(HttpStatusCode.OK, """[{"login":"microsoft"}]""");
        var service = BuildService(handler);

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Allowed);
        handler.RequestUris.Should().ContainSingle(uri => uri.AbsolutePath == "/user/orgs");
        handler.RequestUris.Should().NotContain(uri => uri.AbsolutePath.Contains("/members/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckMembershipAsync_Denies_WhenAllowedOrgIsNotAuthorized()
    {
        var handler = new StaticHttpMessageHandler(HttpStatusCode.OK, """[{"login":"github"}]""");
        var service = BuildService(handler);

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.Denied);
    }

    [Fact]
    public async Task CheckMembershipAsync_ReturnsOrgAccessNotGranted_WhenAuthorizedOrgsIsForbidden()
    {
        var handler = new StaticHttpMessageHandler(HttpStatusCode.Forbidden, """{"message":"Resource protected"}""");
        var service = BuildService(handler);

        var result = await service.CheckMembershipAsync("token", "octocat", CancellationToken.None);

        result.Should().Be(OrgAuthResult.OrgAccessNotGranted);
    }

    private static GitHubOrgAuthorizationService BuildService(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:GitHub:AllowedOrg"] = "microsoft",
            })
            .Build();

        return new GitHubOrgAuthorizationService(
            config,
            new SingleClientHttpClientFactory(handler),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<GitHubOrgAuthorizationService>.Instance);
    }

    private sealed class StaticHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        public List<Uri> RequestUris { get; } = [];

        public StaticHttpMessageHandler(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);

            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleClientHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
