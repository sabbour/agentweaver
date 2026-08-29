using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;
using FluentAssertions;

namespace Agentweaver.Tests.Mcp;

public sealed class GitHubAuthToolsTests
{
    [Fact]
    public async Task RepoAppConnect_UsesBrowserHandoffAndRedactsUnexpectedFields()
    {
        HttpRequestMessage? captured = null;
        var tools = new GitHubAuthTools(CreateApiClient((request, _) =>
        {
            captured = request;
            return Task.FromResult(Json(new
            {
                transaction_id = "opaque-transaction",
                browser_url = "https://agentweaver.test/auth/github/repo-app/handoff/opaque-transaction",
                expires_at = "2026-08-28T16:00:00Z",
                state = "must-not-leak",
                callback_cookie = "must-not-leak",
                access_token = "ghu_must-not-leak",
            }));
        }));

        var json = await tools.GitHubRepoAppConnectAsync(CancellationToken.None);

        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be("/api/auth/github/repo-app/authorizations/handoff");
        using var result = JsonDocument.Parse(json);
        result.RootElement.GetProperty("transaction_id").GetString().Should().Be("opaque-transaction");
        result.RootElement.GetProperty("browser_url").GetString().Should().Contain("/handoff/");
        result.RootElement.GetProperty("expires_at").GetString().Should().NotBeNullOrWhiteSpace();
        json.Should().NotContain("state").And.NotContain("callback_cookie").And.NotContain("ghu_");
    }

    [Fact]
    public async Task RepoAppPoll_UsesOpaqueTransactionAndReturnsOnlyLifecycleStatus()
    {
        HttpRequestMessage? captured = null;
        var tools = new GitHubAuthTools(CreateApiClient((request, _) =>
        {
            captured = request;
            return Task.FromResult(Json(new { status = "pending", subject = "must-not-leak" }));
        }));

        var json = await tools.GitHubRepoAppAuthorizationStatusAsync("a/b", CancellationToken.None);

        captured!.Method.Should().Be(HttpMethod.Get);
        captured.RequestUri!.AbsolutePath.Should().Be("/api/auth/github/repo-app/authorizations/a%2Fb");
        using var result = JsonDocument.Parse(json);
        result.RootElement.EnumerateObject().Should().ContainSingle();
        result.RootElement.GetProperty("status").GetString().Should().Be("pending");
    }

    [Fact]
    public async Task ProjectCopilotConnect_PinsEscapedProjectPathAndRedactsSensitiveFields()
    {
        HttpRequestMessage? captured = null;
        var tools = new GitHubAuthTools(CreateApiClient((request, _) =>
        {
            captured = request;
            return Task.FromResult(Json(new
            {
                transaction_id = "opaque-transaction",
                browser_url = "https://agentweaver.test/auth/github/copilot-app/handoff/opaque-transaction",
                expires_at = "2026-08-28T16:00:00Z",
                installation_id = 1,
                repository_id = 2,
                permissions = new { contents = "write" },
            }));
        }));

        var json = await tools.ProjectCopilotAppConnectAsync("project/a", CancellationToken.None);

        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be("/api/projects/project%2Fa/github/copilot/authorizations/handoff");
        json.Should().NotContain("installation").And.NotContain("repository").And.NotContain("permissions");
    }

    [Fact]
    public async Task ProjectCapabilityStatus_ExposesOnlyRedactedReadiness()
    {
        var tools = new GitHubAuthTools(CreateApiClient((request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.AbsolutePath.Should().Be("/api/projects/project/github/unattended-readiness");
            return Task.FromResult(Json(new
            {
                status = "not_ready",
                reason_code = "copilot_binding_required",
                message = "Connect a project Copilot App identity.",
                repo_app_installation_connected = true,
                installation_id = 12,
                repository_id = 34,
                permission_digest = "secret",
            }));
        }));

        var json = await tools.ProjectGitHubCapabilityStatusAsync("project", CancellationToken.None);

        json.Should().Contain("copilot_binding_required").And.NotContain("installation_id")
            .And.NotContain("repository_id").And.NotContain("permission_digest");
    }

    [Fact]
    public async Task DisconnectOperations_UseExpectedDeleteEndpoints()
    {
        var requests = new List<HttpRequestMessage>();
        var tools = new GitHubAuthTools(CreateApiClient((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }));

        await tools.GitHubRepoAppDisconnectAsync(CancellationToken.None);
        await tools.ProjectCopilotAppDisconnectAsync("project/a", CancellationToken.None);

        requests.Select(request => request.Method).Should().AllBeEquivalentTo(HttpMethod.Delete);
        requests.Select(request => request.RequestUri!.AbsolutePath).Should().Equal(
            "/api/auth/github/repo-app/authorization",
            "/api/projects/project%2Fa/github/copilot/binding");
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value),
    };

    private static AgentweaverApiClient CreateApiClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(new HttpClient(new DelegatingHandlerStub(handler)), new McpConfig("http://localhost", "test-api-key"));

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
