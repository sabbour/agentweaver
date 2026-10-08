extern alias GatewayHost;

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using GatewayOwner = GatewayHost::Agentweaver.Gateway.GatewayOwner;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayGitHubRepoAppBffTests
{
    [Fact]
    public async Task GitHubRepoAppBffKeepsHumanAndCallbackBoundaries()
    {
        using var rsa = RSA.Create(2048);
        var issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;
        const string audience = "https://api.test";
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer,
            audience,
            [new Claim("sub", "github-user-1")],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddHours(1),
            new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)));
        var requests = new ConcurrentQueue<OwnerRequest>();
        const string userCookie =
            "__Host-agentweaver-repo-app-auth=oauth-nonce; Path=/; Max-Age=300; Secure; HttpOnly; SameSite=Lax";
        const string installationCookie =
            "__Host-agentweaver-repo-app-install-auth=install-nonce; Path=/; Max-Age=300; Secure; HttpOnly; SameSite=Lax";
        const string clearedUserCookie =
            "__Host-agentweaver-repo-app-auth=; Path=/; Max-Age=0; Secure; HttpOnly; SameSite=Lax";
        const string clearedInstallationCookie =
            "__Host-agentweaver-repo-app-install-auth=; Path=/; Max-Age=0; Secure; HttpOnly; SameSite=Lax";
        const string userRedirect = "https://web.test/settings?repo_app_auth=success";
        const string installationRedirect = "https://web.test/projects/project-a/settings?repo_app_install=success";

        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => new HttpClientHandler(),
            async (owner, request, cancellationToken) =>
            {
                var body = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                var path = request.RequestUri?.AbsolutePath
                    ?? throw new InvalidOperationException("Gateway omitted the owner URI.");
                requests.Enqueue(new OwnerRequest(
                    owner,
                    request.Method,
                    request.RequestUri!,
                    request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenant)
                        ? tenant.Single()
                        : null,
                    request.Headers.TryGetValues("Cookie", out var cookie)
                        ? cookie.Single()
                        : null,
                    body));

                if (path == "/auth/github/repo-app/callback" ||
                    path == "/auth/github/repo-app/installation/callback")
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Redirect)
                    {
                        Content = new StringContent(string.Empty),
                    };
                    response.Headers.Location = new Uri(
                        path.EndsWith("/installation/callback", StringComparison.Ordinal)
                            ? installationRedirect
                            : userRedirect);
                    response.Headers.TryAddWithoutValidation(
                        "Set-Cookie",
                        path.EndsWith("/installation/callback", StringComparison.Ordinal)
                            ? clearedInstallationCookie
                            : clearedUserCookie);
                    return response;
                }

                var result = path switch
                {
                    "/api/auth/github/repo-app/authorizations" =>
                        """{"authorizationUrl":"https://github.com/login/oauth/authorize","transactionId":"tx-1","expiresAt":"2026-10-08T01:00:00+00:00"}""",
                    "/api/auth/github/repo-app/authorization/status" =>
                        """{"connected":true,"githubLogin":"octo","connectionId":"identity-app-1"}""",
                    "/api/auth/github/repo-app/authorizations/tx-1" =>
                        """{"status":"pending"}""",
                    "/api/github/repository-selections" when request.Method == HttpMethod.Get =>
                        """{"repositories":[{"fullName":"octo/agentweaver","ownerLogin":"octo","isPrivate":true,"defaultBranch":"main","pushedAt":"2026-10-08T00:00:00+00:00"}],"installations":[{"accountLogin":"octo","accountType":"Organization","repositorySelection":"selected","managementUrl":"https://github.com/settings/installations"}]}""",
                    "/api/github/repository-selections" =>
                        """{"selectionCode":"opaque-selection-code","expiresAt":"2026-10-08T01:00:00+00:00"}""",
                    "/api/projects/project-a/runs/run-a/source-control/github-app-installations/authorizations" =>
                        """{"installationUrl":"https://github.com/apps/agentweaver/installations/new","transactionId":"install-tx-1","expiresAt":"2026-10-08T01:00:00+00:00"}""",
                    _ => """{"accepted":true}""",
                };
                var ownerResponse = new HttpResponseMessage(
                    path.EndsWith("/authorization/refresh", StringComparison.Ordinal) ||
                    path.EndsWith("/authorization", StringComparison.Ordinal)
                        ? HttpStatusCode.NoContent
                        : HttpStatusCode.OK)
                {
                    Content = new StringContent(result, System.Text.Encoding.UTF8, "application/json"),
                };
                if (path == "/api/auth/github/repo-app/authorizations")
                    ownerResponse.Headers.TryAddWithoutValidation("Set-Cookie", userCookie);
                if (path.EndsWith("/github-app-installations/authorizations", StringComparison.Ordinal))
                    ownerResponse.Headers.TryAddWithoutValidation("Set-Cookie", installationCookie);
                return ownerResponse;
            });

        using (var openApiResponse = await gateway.Client.GetAsync("/openapi/v1.json"))
        using (var openApi = JsonDocument.Parse(await openApiResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, openApiResponse.StatusCode);
            var paths = openApi.RootElement.GetProperty("paths");
            Assert.False(paths.TryGetProperty("/api/auth/github/repo-app/authorizations", out _));
            Assert.False(paths.TryGetProperty("/api/auth/github/repo-app/authorization/status", out _));
            Assert.False(paths.TryGetProperty("/api/auth/github/repo-app/authorizations/{transactionId}", out _));
            Assert.False(paths.TryGetProperty("/api/auth/github/repo-app/authorization/refresh", out _));
            Assert.False(paths.TryGetProperty("/api/auth/github/repo-app/authorization", out _));
            Assert.False(paths.TryGetProperty("/api/github/repository-selections", out _));
            Assert.False(paths.TryGetProperty("/auth/github/repo-app/callback", out _));
            Assert.False(paths.TryGetProperty("/auth/github/repo-app/installation/callback", out _));
            Assert.False(paths.TryGetProperty(
                "/api/v1/projects/{projectId}/runs/{runId}/source-control/github-app-installations/authorizations",
                out _));
            var pin = paths.GetProperty(
                "/api/v1/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/pin")
                .GetProperty("post");
            Assert.Equal(
                "pinSourceControlRepository",
                pin.GetProperty("operationId").GetString());
            var pinBody = pin.GetProperty("requestBody");
            Assert.False(pinBody.GetProperty("required").GetBoolean());
            Assert.Equal(
                "#/components/schemas/SourceControlRepositoryPinRequest",
                pinBody.GetProperty("content")
                    .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
            var tenantParameter = pin.GetProperty("parameters").EnumerateArray()
                .Single(parameter => parameter.GetProperty("name").GetString() == "X-Agentweaver-Tenant");
            Assert.True(tenantParameter.GetProperty("required").GetBoolean());
            var reviews = paths.GetProperty(
                "/api/v1/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}" +
                "/pull-requests/{pullRequestNumber}/reviews").GetProperty("get");
            var numberParameter = reviews.GetProperty("parameters").EnumerateArray()
                .Single(parameter => parameter.GetProperty("name").GetString() == "pullRequestNumber");
            Assert.Equal("integer", numberParameter.GetProperty("schema").GetProperty("type").GetString());
            Assert.Equal("int64", numberParameter.GetProperty("schema").GetProperty("format").GetString());
            var sourceControlSchema = openApi.RootElement.GetProperty("components")
                .GetProperty("schemas").GetProperty("ProjectConfiguration")
                .GetProperty("properties").GetProperty("sourceControl")
                .GetProperty("anyOf")[0];
            var sourceControlConfiguration = sourceControlSchema.GetProperty("properties");
            Assert.Equal(
                new[] { "secret", "githubApp" },
                sourceControlConfiguration.GetProperty("authMode").GetProperty("enum")
                    .EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(
                "string",
                sourceControlConfiguration.GetProperty("appConnectionId").GetProperty("type").GetString());
            Assert.DoesNotContain(
                sourceControlSchema.GetProperty("required").EnumerateArray(),
                required => required.GetString() == "apiSecretReference");
        }

        using (var begin = new HttpRequestMessage(
                   HttpMethod.Post,
                   "/api/auth/github/repo-app/authorizations")
               {
                   Content = JsonContent.Create(new { returnRouteKey = "settings" }),
               })
        {
            AddCallerHeaders(begin, token, includeTenant: true);
            using var response = await gateway.Client.SendAsync(begin);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(userCookie, Assert.Single(response.Headers.GetValues("Set-Cookie")));
        }

        foreach (var route in new[]
        {
            (HttpMethod.Get, "/api/auth/github/repo-app/authorization/status"),
            (HttpMethod.Get, "/api/auth/github/repo-app/authorizations/tx-1"),
            (HttpMethod.Post, "/api/auth/github/repo-app/authorization/refresh"),
            (HttpMethod.Delete, "/api/auth/github/repo-app/authorization"),
            (HttpMethod.Get, "/api/github/repository-selections"),
            (HttpMethod.Post, "/api/github/repository-selections"),
        })
        {
            using var request = new HttpRequestMessage(route.Item1, route.Item2);
            if (route.Item2 == "/api/github/repository-selections" && route.Item1 == HttpMethod.Post)
                request.Content = JsonContent.Create(new { fullName = "octo/agentweaver" });
            AddCallerHeaders(request, token, includeTenant: true);
            using var response = await gateway.Client.SendAsync(request);
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent);
        }

        using (var callback = new HttpRequestMessage(
                   HttpMethod.Get,
                   "/auth/github/repo-app/callback?code=oauth-code&state=state-token"))
        {
            callback.Headers.TryAddWithoutValidation(
                "Cookie",
                "theme=dark; __Host-agentweaver-repo-app-auth=oauth-nonce; session=unrelated");
            callback.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-forwarded");
            using var response = await gateway.Client.SendAsync(callback);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(userRedirect, response.Headers.Location?.OriginalString);
            Assert.Equal(clearedUserCookie, Assert.Single(response.Headers.GetValues("Set-Cookie")));
        }

        using (var invalidCallback = await gateway.Client.GetAsync(
                   "/auth/github/repo-app/callback?state=state-token&redirect=https%3A%2F%2Fevil.test"))
            Assert.Equal(HttpStatusCode.BadRequest, invalidCallback.StatusCode);

        using (var callback = new HttpRequestMessage(
                   HttpMethod.Get,
                   "/auth/github/repo-app/installation/callback?installation_id=123&setup_action=install&state=state-token"))
        {
            callback.Headers.TryAddWithoutValidation(
                "Cookie",
                "other=value; __Host-agentweaver-repo-app-install-auth=install-nonce");
            using var response = await gateway.Client.SendAsync(callback);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(installationRedirect, response.Headers.Location?.OriginalString);
            Assert.Equal(
                clearedInstallationCookie,
                Assert.Single(response.Headers.GetValues("Set-Cookie")));
        }

        using (var install = new HttpRequestMessage(
                   HttpMethod.Post,
                   "/api/v1/projects/project-a/runs/run-a/source-control/github-app-installations/authorizations"))
        {
            AddCallerHeaders(install, token, includeTenant: true);
            using var response = await gateway.Client.SendAsync(install);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(installationCookie, Assert.Single(response.Headers.GetValues("Set-Cookie")));
        }

        var forwarded = requests.ToArray();
        Assert.Equal(10, forwarded.Length);
        Assert.Equal(
            new[]
            {
                "/api/auth/github/repo-app/authorizations",
                "/api/auth/github/repo-app/authorization/status",
                "/api/auth/github/repo-app/authorizations/tx-1",
                "/api/auth/github/repo-app/authorization/refresh",
                "/api/auth/github/repo-app/authorization",
                "/api/github/repository-selections",
                "/api/github/repository-selections",
                "/auth/github/repo-app/callback",
                "/auth/github/repo-app/installation/callback",
                "/api/projects/project-a/runs/run-a/source-control/github-app-installations/authorizations",
            },
            forwarded.Select(request => request.Uri.AbsolutePath));
        Assert.All(forwarded.Take(7), request =>
        {
            Assert.Equal(GatewayOwner.IdentityBroker, request.Owner);
            Assert.Equal(token, AuthenticationHeaderValue.Parse(request.Authorization!).Parameter);
            Assert.Null(request.Tenant);
            Assert.Null(request.Cookie);
        });
        Assert.Null(forwarded[7].Authorization);
        Assert.Null(forwarded[7].Tenant);
        Assert.Equal("__Host-agentweaver-repo-app-auth=oauth-nonce", forwarded[7].Cookie);
        Assert.Equal("?code=oauth-code&state=state-token", forwarded[7].Uri.Query);
        Assert.Null(forwarded[8].Authorization);
        Assert.Null(forwarded[8].Tenant);
        Assert.Equal(
            "__Host-agentweaver-repo-app-install-auth=install-nonce",
            forwarded[8].Cookie);
        Assert.Equal(token, AuthenticationHeaderValue.Parse(forwarded[9].Authorization!).Parameter);
        Assert.Equal("tenant-1", forwarded[9].Tenant);
        Assert.Null(forwarded[9].Cookie);
        Assert.Equal("""{"returnRouteKey":"settings"}""", forwarded[0].Body);
        Assert.Equal("""{"fullName":"octo/agentweaver"}""", forwarded[6].Body);
    }

    private static void AddCallerHeaders(
        HttpRequestMessage request,
        string token,
        bool includeTenant)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (includeTenant)
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", "tenant-1");
    }

    private sealed record OwnerRequest(
        GatewayOwner Owner,
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string? Tenant,
        string? Cookie,
        string? Body);
}
