using System.Net;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Endpoints;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Webhooks;

public sealed class GitHubWebhookEndpointsTests : IClassFixture<RepoAppWebhookFactory>
{
    private readonly RepoAppWebhookFactory _factory;

    public GitHubWebhookEndpointsTests(RepoAppWebhookFactory factory) => _factory = factory;

    [Fact]
    public async Task AppLevelRoute_AcceptsCurrentAndUnexpiredPreviousSecret_AndRejectsInvalidSignatureWithoutDetail()
    {
        await SetSecretsAsync();
        var client = _factory.CreateClient();
        var body = Encoding.UTF8.GetBytes("""{"installation":{"id":1},"repository":{"id":2,"full_name":"untrusted/display"}}""");
        var suffix = Guid.NewGuid().ToString("N");

        var current = await client.SendAsync(Request(body, Sign("current-webhook-secret", body), $"current-{suffix}"));
        var previous = await client.SendAsync(Request(body, Sign("previous-webhook-secret", body), $"previous-{suffix}"));
        var rejected = await client.SendAsync(Request(body, "sha256=" + new string('0', 64), $"rejected-{suffix}"));

        current.StatusCode.Should().Be(HttpStatusCode.OK, await current.Content.ReadAsStringAsync());
        previous.StatusCode.Should().Be(HttpStatusCode.OK);
        rejected.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await rejected.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AppLevelRoute_RejectsOversizeBodyBeforeSignatureVerification()
    {
        await SetSecretsAsync();
        var client = _factory.CreateClient();
        var body = new byte[1025];
        var response = await client.SendAsync(Request(body, null, "too-large"));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task LegacyProjectScopedRoute_IsNotAnAnonymousWebhookExemption()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/projects/00000000-0000-0000-0000-000000000001/webhooks/github", new StringContent("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "unmatched routes are not inferred to have webhook or bearer authorization from their path");
    }

    private async Task SetSecretsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        await secrets.SetSecretAsync("repo-app-webhook-current", "current-webhook-secret");
        await secrets.SetSecretAsync("repo-app-webhook-previous", "previous-webhook-secret");
    }

    private static HttpRequestMessage Request(byte[] body, string? signature, string deliveryId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GitHubWebhookEndpoints.RepoAppWebhookPath)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-GitHub-Event", "push");
        request.Headers.Add("X-GitHub-Delivery", deliveryId);
        if (signature is not null) request.Headers.Add("X-Hub-Signature-256", signature);
        return request;
    }

    private static string Sign(string secret, byte[] body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();
}

public sealed class RepoAppWebhookFactory : ProjectsWebApplicationFactory
{
    protected override IDictionary<string, string?> GetAdditionalConfiguration() => new Dictionary<string, string?>
    {
        ["Testing:BypassGitHubTokenAuth"] = "false",
        ["Testing:BypassGitHubOrgAuthorization"] = "false",
        ["Auth:RepoApp:WebhookSecretName"] = "repo-app-webhook-current",
        ["Auth:RepoApp:PreviousWebhookSecretName"] = "repo-app-webhook-previous",
        ["Auth:RepoApp:PreviousWebhookSecretExpiresAt"] = "2099-01-01T00:00:00Z",
        ["Auth:RepoApp:WebhookMaxBodyBytes"] = "1024",
    };
}
