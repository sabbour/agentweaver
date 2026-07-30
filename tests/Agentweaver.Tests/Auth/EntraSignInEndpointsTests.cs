using System.Net;
using System.Text.Json;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Agentweaver.Api.Endpoints;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Auth;

/// <summary>
/// Tests for the Microsoft Entra ID browser sign-in redirect endpoints (<c>/auth/entra/authorize</c>
/// and <c>/auth/entra/callback</c>), the Entra counterpart to the GitHub OAuth endpoints. Mirrors the
/// GitHub coverage: authorize-redirect-when-configured, 503-when-wrong-mode, callback-state-mismatch
/// (login-CSRF), and callback-happy-path (issues an opaque one-time session code, never a token in the
/// URL). The stubbed Microsoft token endpoint (see <see cref="EntraSignInWebApplicationFactory"/>)
/// returns an access token signed by the same key the API's JWKS is configured with.
/// </summary>
public sealed class EntraSignInEndpointsTests
{
    private static WebApplicationFactoryClientOptions NoRedirectNoCookies => new()
    {
        AllowAutoRedirect = false,
        HandleCookies = false,
    };

    // -------------------------------------------------------------------------
    // /auth/entra/authorize (Entra mode) → 302 to Microsoft, arming the browser-bound
    // Secure/HttpOnly/SameSite=Lax state cookie whose value equals the `state` (double-submit),
    // with PKCE (code_challenge + S256).
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Authorize_WhenEntraConfigured_RedirectsToMicrosoft_AndArmsStateCookie()
    {
        await using var factory = new EntraSignInWebApplicationFactory();
        var client = factory.CreateClient(NoRedirectNoCookies);

        var resp = await client.GetAsync("/auth/entra/authorize");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!.ToString();
        location.Should().Contain("login.microsoftonline.com")
            .And.Contain("/oauth2/v2.0/authorize")
            .And.Contain("response_type=code")
            .And.Contain("code_challenge=")
            .And.Contain("code_challenge_method=S256");

        var setCookie = resp.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.FirstOrDefault(c => c.StartsWith($"{EntraOAuthStateCookie.Name}=", StringComparison.Ordinal))
            : null;
        setCookie.Should().NotBeNull("authorize must arm the browser-bound state cookie");
        setCookie.Should().Contain("httponly").And.Contain("samesite=lax").And.Contain("path=/auth/entra");

        var stateInUrl = OAuthStateCookie.ExtractState(location);
        stateInUrl.Should().NotBeNullOrEmpty();
        setCookie!.Should().Contain($"{EntraOAuthStateCookie.Name}={stateInUrl}");
    }

    // -------------------------------------------------------------------------
    // /auth/entra/authorize while running GitHubLegacy mode → 503 (Entra sign-in disabled),
    // never reaching Microsoft.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Authorize_WhenNotEntraMode_Returns503()
    {
        await using var factory = new OAuthWebApplicationFactory();
        var client = factory.CreateClient(NoRedirectNoCookies);

        var resp = await client.GetAsync("/auth/entra/authorize");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Callback_WhenNotEntraMode_Returns503()
    {
        await using var factory = new OAuthWebApplicationFactory();
        var client = factory.CreateClient(NoRedirectNoCookies);

        var resp = await client.GetAsync("/auth/entra/callback?code=x&state=y");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // -------------------------------------------------------------------------
    // /auth/entra/callback with a MISMATCHED (or absent) state cookie → rejected as state_mismatch
    // BEFORE any code redemption (login-CSRF: the victim's browser never held a cookie for the
    // attacker's grafted state).
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Callback_WithMismatchedStateCookie_IsRejected_AsStateMismatch()
    {
        await using var factory = new EntraSignInWebApplicationFactory();
        var client = factory.CreateClient(NoRedirectNoCookies);

        var req = new HttpRequestMessage(HttpMethod.Get,
            "/auth/entra/callback?code=attacker-code&state=attacker-state");
        req.Headers.Add("Cookie", $"{EntraOAuthStateCookie.Name}=victims-own-different-state");

        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("auth=error").And.Contain("reason=state_mismatch");
    }

    [Fact]
    public async Task Callback_WithoutStateCookie_IsRejected_AsStateMismatch()
    {
        await using var factory = new EntraSignInWebApplicationFactory();
        var client = factory.CreateClient(NoRedirectNoCookies);

        var resp = await client.GetAsync("/auth/entra/callback?code=attacker-code&state=attacker-state");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("reason=state_mismatch");
    }

    // -------------------------------------------------------------------------
    // Full happy path: authorize arms state → callback redeems the code at the (stubbed) Microsoft
    // token endpoint, validates the returned access token, and redirects with ONLY an opaque
    // one-time code (no token in the URL). Exchanging that code yields the Entra access token, which
    // authenticates a real API request carrying the signed-in object id.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Callback_HappyPath_IssuesOneTimeCode_ExchangeableForEntraSession()
    {
        await using var factory = new EntraSignInWebApplicationFactory();
        var client = factory.CreateClient(NoRedirectNoCookies);

        // 1. Arm the CSRF/PKCE state (persists the verifier server-side, sets the state cookie).
        var authorizeResp = await client.GetAsync("/auth/entra/authorize");
        authorizeResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var state = OAuthStateCookie.ExtractState(authorizeResp.Headers.Location!.ToString());
        state.Should().NotBeNullOrEmpty();

        // 2. Microsoft redirects back with the code; the browser carries the bound state cookie.
        var callbackReq = new HttpRequestMessage(HttpMethod.Get,
            $"/auth/entra/callback?code=test-authorization-code&state={Uri.EscapeDataString(state!)}");
        callbackReq.Headers.Add("Cookie", $"{EntraOAuthStateCookie.Name}={state}");
        var callbackResp = await client.SendAsync(callbackReq);

        callbackResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var callbackLocation = callbackResp.Headers.Location!.ToString();
        callbackLocation.Should().StartWith(EntraSignInWebApplicationFactory.FrontendUrlValue)
            .And.Contain("auth=success")
            .And.Contain("code=");
        // F5: the raw access token must never appear in the redirect URL.
        callbackLocation.Should().NotContain("session_token").And.NotContain("access_token");

        var oneTimeCode = ExtractQueryValue(callbackLocation, "code");
        oneTimeCode.Should().NotBeNullOrEmpty();

        // 3. Exchange the opaque one-time code for the session token (server-side POST, F5).
        using var exchangeResp = await client.PostAsync(
            "/api/auth/session/exchange",
            new StringContent($$"""{"code":"{{oneTimeCode}}"}""", System.Text.Encoding.UTF8, "application/json"));
        exchangeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var exchangeJson = JsonDocument.Parse(await exchangeResp.Content.ReadAsStringAsync());
        var sessionToken = exchangeJson.RootElement.GetProperty("session_token").GetString();
        sessionToken.Should().NotBeNullOrEmpty();

        // 4. The session token IS the validated Entra access token: it authenticates an API request
        //    and resolves to the signed-in object id.
        var apiClient = factory.CreateClient(NoRedirectNoCookies);
        apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
        var contextResp = await apiClient.GetAsync("/api/auth/context");
        contextResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var contextJson = JsonDocument.Parse(await contextResp.Content.ReadAsStringAsync());
        contextJson.RootElement.GetProperty("entra_object_id").GetString()
            .Should().Be(factory.SignedInObjectId);
    }

    private static string? ExtractQueryValue(string url, string key)
    {
        var query = new Uri(url).Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith($"{key}=", StringComparison.Ordinal))
                return Uri.UnescapeDataString(pair[(key.Length + 1)..]);
        }
        return null;
    }
}
