using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Agentweaver.Api.Endpoints;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.OAuth;

/// <summary>
/// Tests for the web sign-in OAuth <c>state</c> browser binding that mitigates login-CSRF
/// (Seraph findings-auth Alert 6). The <c>state</c> issued at <c>/auth/github/authorize</c> is
/// echoed into a Secure, HttpOnly, SameSite=Lax cookie; <c>/auth/github/callback</c> requires the
/// cookie to match the <c>state</c> GitHub returns before redeeming the code (double-submit-cookie),
/// so an attacker's pre-authorized state/code cannot be grafted onto a victim's browser.
/// </summary>
public sealed class OAuthStateBindingTests
{
    private static HttpClient NoRedirectClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    // -------------------------------------------------------------------------
    // /auth/github/callback with NO state cookie → rejected as state_mismatch,
    // never reaching the GitHub code exchange.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Callback_WithoutStateCookie_IsRejected_AsStateMismatch()
    {
        await using var factory = new OAuthWebApplicationFactory();
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync("/auth/github/callback?code=attacker-code&state=attacker-state");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("auth=error")
            .And.Contain("reason=state_mismatch");
    }

    // -------------------------------------------------------------------------
    // /auth/github/callback with a MISMATCHED state cookie → rejected (the classic
    // login-CSRF: victim's browser carries a different (or no) bound state than the
    // attacker's grafted callback URL).
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Callback_WithMismatchedStateCookie_IsRejected()
    {
        await using var factory = new OAuthWebApplicationFactory();
        var client = NoRedirectClient(factory);

        var req = new HttpRequestMessage(HttpMethod.Get,
            "/auth/github/callback?code=attacker-code&state=attacker-state");
        req.Headers.Add("Cookie", $"{OAuthStateCookie.Name}=victims-own-different-state");

        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("reason=state_mismatch");
    }

    // -------------------------------------------------------------------------
    // /auth/github/authorize arms the state binding: it sets a Secure, HttpOnly,
    // SameSite=Lax cookie whose value equals the `state` sent to GitHub.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Authorize_SetsSecureHttpOnlyStateCookie_BoundToState()
    {
        await using var factory = new OAuthWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // BeginAuthorizationAsync requires a callback URL to build the GitHub redirect.
                    ["Auth:GitHub:CallbackUrl"] = "https://app.example/auth/github/callback",
                })));

        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync("/auth/github/authorize");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!.ToString();
        location.Should().Contain("github.com/login/oauth/authorize");

        var setCookie = resp.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.FirstOrDefault(c => c.StartsWith($"{OAuthStateCookie.Name}=", StringComparison.Ordinal))
            : null;
        setCookie.Should().NotBeNull("authorize must arm the browser-bound state cookie");
        setCookie.Should().Contain("httponly", "the state cookie must not be JS-readable")
            .And.Contain("samesite=lax");

        // The cookie value must equal the `state` echoed to GitHub (double-submit binding).
        var stateInUrl = OAuthStateCookie.ExtractState(location);
        stateInUrl.Should().NotBeNullOrEmpty();
        setCookie!.Should().Contain($"{OAuthStateCookie.Name}={stateInUrl}");
    }
}
