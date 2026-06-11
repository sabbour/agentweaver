using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Scaffolder.Tests.Helpers;

namespace Scaffolder.Tests.Projects;

/// <summary>
/// Tests that no raw token value appears in API responses or log output (SC-006).
/// Uses a known sentinel token value and verifies it never surfaces in HTTP responses
/// from project and auth endpoints.
/// </summary>
public sealed class TokenRedactionTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private const string SentinelToken = "ghp_test_sentinel_token_value_REDACT_ME";

    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TokenRedactionTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateAuthenticatedClient();
    }

    // =========================================================================
    // TR-01: GET /api/projects response does not leak the auth token
    // =========================================================================
    [Fact]
    public async Task GetProjects_ResponseDoesNotContainToken()
    {
        // Set a sentinel token in the in-memory store
        await _factory.TokenStore.SetAsync(
            Scaffolder.Domain.GitHubTokenScope.Installation,
            new Scaffolder.Domain.GitHubToken(
                SentinelToken, null, null, "tokenuser", ["repo"]));

        var response = await _client.GetAsync("/api/projects");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(SentinelToken,
            "the raw access token must never appear in the project list response");
    }

    // =========================================================================
    // TR-02: GET /api/auth/github response does not contain the access token
    // =========================================================================
    [Fact]
    public async Task GetAuthStatus_ResponseDoesNotContainToken()
    {
        await _factory.TokenStore.SetAsync(
            Scaffolder.Domain.GitHubTokenScope.Installation,
            new Scaffolder.Domain.GitHubToken(
                SentinelToken, null, null, "tokenuser", ["repo"]));

        var response = await _client.GetAsync("/api/auth/github");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(SentinelToken,
            "the raw access token must never appear in the auth status response");
    }

    // =========================================================================
    // TR-03: Auth status response contains only 'status' and 'login' fields — no token
    // =========================================================================
    [Fact]
    public async Task GetAuthStatus_ReturnsOnlyStatusAndLogin()
    {
        await _factory.TokenStore.SetAsync(
            Scaffolder.Domain.GitHubTokenScope.Installation,
            new Scaffolder.Domain.GitHubToken(
                SentinelToken, null, null, "mylogin", ["repo"]));

        var response = await _client.GetAsync("/api/auth/github");
        var body     = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.TryGetProperty("status", out var status).Should().BeTrue();
        status.GetString().Should().Be("signed_in");

        body.TryGetProperty("login", out var login).Should().BeTrue();
        login.GetString().Should().Be("mylogin");

        // Verify no 'access_token' or 'token' field is present
        body.TryGetProperty("access_token", out _).Should().BeFalse(
            "access_token field must never be returned in auth status");
        body.TryGetProperty("token", out _).Should().BeFalse(
            "token field must never be returned in auth status");
    }

    // =========================================================================
    // TR-04: POST /api/auth/github/sign-out response does not contain a token
    // =========================================================================
    [Fact]
    public async Task SignOut_ResponseDoesNotContainToken()
    {
        await _factory.TokenStore.SetAsync(
            Scaffolder.Domain.GitHubTokenScope.Installation,
            new Scaffolder.Domain.GitHubToken(SentinelToken, null, null, "user", []));

        var response = await _client.PostAsync("/api/auth/github/sign-out", null);

        // 204 No Content — body should be empty
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(SentinelToken,
            "sign-out response must not echo the token");
    }
}
