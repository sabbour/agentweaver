using System.Net;
using Agentweaver.Api.Auth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Auth;

public sealed class CopilotAppRegistrationServiceTests
{
    [Fact]
    public async Task ValidateAsync_ReturnsReadyWhenRegistrationHasOnlyMandatoryMetadataReadPermission()
    {
        var registration = CreateRegistration("""{"permissions":{"metadata":"read"}}""");

        var result = await registration.ValidateAsync();

        result.Should().Be(CopilotAppRegistrationState.Ready);
    }

    [Theory]
    [InlineData("""{"permissions":{}}""")]
    [InlineData("""{"permissions":{"metadata":"write"}}""")]
    [InlineData("""{"permissions":{"metadata":"read","contents":"read"}}""")]
    public async Task ValidateAsync_FailsClosedWhenRegistrationHasUnexpectedOrAdditionalPermissions(string payload)
    {
        var registration = CreateRegistration(payload);

        var result = await registration.ValidateAsync();

        result.Should().Be(CopilotAppRegistrationState.RepositoryPermissionsDetected);
    }

    [Fact]
    public async Task Startup_AllowsOnlyMandatoryMetadataReadPermission()
    {
        var registration = CreateRegistration("""{"permissions":{"metadata":"read"}}""");
        var startup = new CopilotAppRegistrationStartupService(
            registration,
            NullLogger<CopilotAppRegistrationStartupService>.Instance);

        await startup.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Startup_RefusesToStartWhenRegistrationHasAdditionalRepositoryPermissions()
    {
        var registration = CreateRegistration("""{"permissions":{"metadata":"read","issues":"write"}}""");
        var startup = new CopilotAppRegistrationStartupService(
            registration,
            NullLogger<CopilotAppRegistrationStartupService>.Instance);

        var action = () => startup.StartAsync(CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*repository permissions*");
    }

    [Fact]
    public async Task ValidateAsync_ReturnsOnlyAClosedStateForProviderFailure()
    {
        var registration = CreateRegistration("provider detail", HttpStatusCode.BadGateway);

        var result = await registration.ValidateAsync();

        result.Should().Be(CopilotAppRegistrationState.RegistrationUnavailable);
    }

    private static CopilotAppRegistrationService CreateRegistration(string payload, HttpStatusCode status = HttpStatusCode.OK)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:CopilotApp:ClientId"] = "copilot-client",
            ["Auth:CopilotApp:ClientSecret"] = "copilot-secret",
            ["Auth:CopilotApp:CallbackUrl"] = "https://agentweaver.test/auth/github/copilot-app/callback",
            ["Auth:CopilotApp:Slug"] = "agentweaver-copilot",
        }).Build();
        return new CopilotAppRegistrationService(configuration, new TestHttpClientFactory(payload, status));
    }

    private sealed class TestHttpClientFactory(string payload, HttpStatusCode status) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(payload, status));

        private sealed class Handler(string payload, HttpStatusCode status) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(payload) });
        }
    }
}
