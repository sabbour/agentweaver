using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Agentweaver.Identity.Broker.Tests;

/// <summary>
/// Drives the full, real authorize → external login → consent → code → token exchange across
/// two separate in-process hosts (the broker under test and the <see cref="FakeIdentityProvider"/>)
/// exactly as a real browser + client application would, one HTTP hop at a time. No auto-redirect
/// following is used: every hop's response is inspected so tests can also assert on intermediate
/// failures (e.g. an unregistered client failing before any external login ever happens).
/// </summary>
public static class BrokerFlowDriver
{
    public static async Task<HttpResponseMessage> StartAuthorizeAsync(
        HttpClient broker, string clientId, string redirectUri, string scope,
        string? codeChallenge, string? codeChallengeMethod, string state = "state-1", string nonce = "nonce-1")
    {
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
            ["state"] = state,
            ["nonce"] = nonce,
        };
        if (codeChallenge is not null) query["code_challenge"] = codeChallenge;
        if (codeChallengeMethod is not null) query["code_challenge_method"] = codeChallengeMethod;

        var url = QueryHelpers.AddQueryString("/connect/authorize", query);
        return await broker.GetAsync(url);
    }

    /// <summary>
    /// Runs the full authorize → external login → consent → authorize-again flow for a client
    /// that does not yet have a permanent authorization, and returns the final authorization
    /// code issued to <paramref name="redirectUri"/>.
    /// </summary>
    public static async Task<string> AuthorizeWithConsentAsync(
        HttpClient broker, HttpClient fakeIdp, string clientId, string redirectUri, string scope,
        string codeChallenge, string state = "state-1", string nonce = "nonce-1")
    {
        var consentBody = await BeginConsentAsync(broker, fakeIdp, clientId, redirectUri, scope,
            codeChallenge, state, nonce);
        var consentResponse = await SubmitConsentAsync(broker, consentBody);
        AssertRedirect(consentResponse, "consent");
        var postConsentAuthorizeLocation = consentResponse.Headers.Location!;

        var codeResponse = await broker.GetAsync(MakeAbsoluteIfNeeded(broker, postConsentAuthorizeLocation));
        AssertRedirect(codeResponse, "final authorize (code issuance)");
        var finalLocation = codeResponse.Headers.Location!;

        return ExtractQueryParameter(finalLocation, "code")
            ?? throw new InvalidOperationException($"No 'code' parameter in final redirect: {finalLocation}");
    }

    public static async Task<JsonElement> BeginConsentAsync(
        HttpClient broker, HttpClient fakeIdp, string clientId, string redirectUri, string scope,
        string codeChallenge, string state = "state-1", string nonce = "nonce-1")
    {
        var challengeResponse = await StartAuthorizeAsync(
            broker, clientId, redirectUri, scope, codeChallenge, "S256", state, nonce);
        if (challengeResponse.StatusCode == System.Net.HttpStatusCode.OK)
            return await challengeResponse.Content.ReadFromJsonAsync<JsonElement>();
        AssertRedirect(challengeResponse, "authorize challenge");
        var externalAuthorizeLocation = challengeResponse.Headers.Location!;

        var fakeIdpResponse = await fakeIdp.GetAsync(externalAuthorizeLocation);
        AssertRedirect(fakeIdpResponse, "fake IdP authorize");
        var callbackLocation = fakeIdpResponse.Headers.Location!;

        var callbackResponse = await broker.GetAsync(MakeAbsoluteIfNeeded(broker, callbackLocation));
        AssertRedirect(callbackResponse, "external login callback");
        var resumeLocation = callbackResponse.Headers.Location!;

        var resumeResponse = await broker.GetAsync(MakeAbsoluteIfNeeded(broker, resumeLocation));
        AssertRedirect(resumeResponse, "resume");
        var reenterLocation = resumeResponse.Headers.Location!;

        var consentPromptResponse = await broker.GetAsync(MakeAbsoluteIfNeeded(broker, reenterLocation));
        if (consentPromptResponse.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"Expected a consent_required JSON response but got {consentPromptResponse.StatusCode}: " +
                await consentPromptResponse.Content.ReadAsStringAsync());
        }

        return await consentPromptResponse.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static Task<HttpResponseMessage> SubmitConsentAsync(HttpClient broker, JsonElement prompt,
        bool approve = true, string[]? scopes = null, bool csrf = true, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/consent")
        {
            Content = JsonContent.Create(new
            {
                consent_handle = prompt.GetProperty("consent_handle").GetString(),
                approve,
                scopes,
            }),
        };
        if (csrf) request.Headers.Add("X-CSRF-TOKEN", prompt.GetProperty("csrf_token").GetString());
        return broker.SendAsync(request, ct);
    }

    /// <summary>
    /// Drives the flow only as far as the broker's external-login callback (the hop where the
    /// broker's real OIDC middleware validates the upstream id_token) and returns that raw
    /// response without asserting success, so tests can inspect how a tampered id_token
    /// (wrong issuer/signature/expiry) is rejected by the broker's own middleware.
    /// </summary>
    public static async Task<HttpResponseMessage> AttemptExternalCallbackAsync(
        HttpClient broker, HttpClient fakeIdp, string clientId, string redirectUri, string scope,
        string codeChallenge, string state = "state-1", string nonce = "nonce-1")
    {
        var challengeResponse = await StartAuthorizeAsync(
            broker, clientId, redirectUri, scope, codeChallenge, "S256", state, nonce);
        AssertRedirect(challengeResponse, "authorize challenge");
        var externalAuthorizeLocation = challengeResponse.Headers.Location!;

        var fakeIdpResponse = await fakeIdp.GetAsync(externalAuthorizeLocation);
        AssertRedirect(fakeIdpResponse, "fake IdP authorize");
        var callbackLocation = fakeIdpResponse.Headers.Location!;

        return await broker.GetAsync(MakeAbsoluteIfNeeded(broker, callbackLocation));
    }

    public static async Task<JsonElement> ExchangeCodeForTokensAsync(
        HttpClient broker, string clientId, string redirectUri, string code, string codeVerifier)
    {
        var response = await broker.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
        }));

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Token exchange failed with {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static Task<HttpResponseMessage> RefreshAsync(HttpClient broker, string clientId, string refreshToken) =>
        broker.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
        }));

    private static void AssertRedirect(HttpResponseMessage response, string step)
    {
        if (response.StatusCode is not (System.Net.HttpStatusCode.Redirect or System.Net.HttpStatusCode.Found)
            || response.Headers.Location is null)
        {
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new InvalidOperationException(
                $"Expected a redirect at step '{step}' but got {response.StatusCode}: {body}");
        }
    }

    private static Uri MakeAbsoluteIfNeeded(HttpClient client, Uri location) =>
        location.IsAbsoluteUri ? location : new Uri(client.BaseAddress!, location);

    private static string? ExtractQueryParameter(Uri uri, string name)
    {
        var query = QueryHelpers.ParseQuery(uri.Query);
        return query.TryGetValue(name, out var values) ? values.ToString() : null;
    }
}
