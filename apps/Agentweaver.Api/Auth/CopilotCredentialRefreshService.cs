using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Agentweaver.Api.Auth;

internal enum CopilotCredentialRefreshOutcome
{
    /// <summary>Nothing to redeem: the credential is absent, already unusable, or still valid.</summary>
    NotNeeded,

    /// <summary>A new access token (and rotated refresh token) was redeemed and persisted.</summary>
    Refreshed,

    /// <summary>The refresh token itself was rejected, so only a new OAuth flow can restore the binding.</summary>
    ReauthRequired,

    /// <summary>The redemption could not be attempted or persisted for a transient reason.</summary>
    Unavailable,
}

/// <summary>
/// Redeems the refresh token that both Copilot App bindings persist alongside their access token.
/// GitHub App user-to-server access tokens expire after about eight hours; without this redemption
/// path an unattended launch fails with a connection-required error even though a valid refresh
/// token is sitting in storage, forcing the operator through the OAuth flow again.
/// Redemption is guarded per credential reference in-process and by an ETag precondition across
/// processes, so concurrent callers redeem at most once.
/// </summary>
internal sealed class CopilotCredentialRefreshService(
    IConfiguration configuration,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    ILogger logger)
{
    internal const string SignedInStatus = "signed-in";

    /// <summary>
    /// Status persisted when GitHub rejects the stored refresh token. It is deliberately not
    /// <c>signed-in</c>, so every existing consumer (connection status, capability broker) reports the
    /// binding as unavailable and the operator is asked to reconnect.
    /// </summary>
    internal const string ReauthRequiredStatus = "reauth-required";

    /// <summary>
    /// A credential is redeemed ahead of its expiry so a capability handed out now stays valid for the
    /// whole <see cref="GitHubCapabilityBroker.MaximumCapabilityLifetime"/> the broker may grant.
    /// </summary>
    internal static readonly TimeSpan RefreshAheadWindow = GitHubCapabilityBroker.MaximumCapabilityLifetime;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshGates = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions CredentialReadOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(10);

    private readonly string _baseUrl = configuration["Auth:CopilotApp:BaseUrl"] ?? "https://github.com";
    private readonly string? _clientId = configuration["Auth:CopilotApp:ClientId"];
    private readonly string? _clientSecret = configuration["Auth:CopilotApp:ClientSecret"];

    /// <summary>
    /// Ensures the stored credential can still be redeemed as a capability, redeeming the refresh
    /// token when the access token has expired or is inside the redeem-ahead window.
    /// </summary>
    internal async Task<CopilotCredentialRefreshOutcome> EnsureFreshAsync(
        string credentialReference,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(credentialReference) ||
            string.IsNullOrWhiteSpace(_clientId) ||
            string.IsNullOrWhiteSpace(_clientSecret))
            return CopilotCredentialRefreshOutcome.NotNeeded;

        var current = await ReadAsync(credentialReference, ct).ConfigureAwait(false);
        if (current is null || !NeedsRefresh(current.Credential, now))
            return CopilotCredentialRefreshOutcome.NotNeeded;

        var gate = RefreshGates.GetOrAdd(credentialReference, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read under the gate: a concurrent caller may already have redeemed this credential.
            var latest = await ReadAsync(credentialReference, ct).ConfigureAwait(false);
            if (latest is null)
                return CopilotCredentialRefreshOutcome.Unavailable;
            if (!NeedsRefresh(latest.Credential, now))
                return CopilotCredentialRefreshOutcome.NotNeeded;
            if (string.IsNullOrWhiteSpace(latest.Credential.RefreshToken))
                return CopilotCredentialRefreshOutcome.ReauthRequired;

            var exchange = await RedeemAsync(latest.Credential.RefreshToken!, ct).ConfigureAwait(false);
            if (exchange.Status == CopilotTokenExchangeStatus.Rejected)
            {
                logger.LogWarning(
                    "GitHub rejected the stored Copilot refresh token for credential {CredentialReference}; the binding now requires re-authentication.",
                    credentialReference);
                await MarkReauthRequiredAsync(credentialReference, latest, ct).ConfigureAwait(false);
                return CopilotCredentialRefreshOutcome.ReauthRequired;
            }

            if (exchange.Status != CopilotTokenExchangeStatus.Succeeded)
            {
                logger.LogWarning(
                    "Redeeming the stored Copilot refresh token for credential {CredentialReference} failed transiently; the binding is left untouched.",
                    credentialReference);
                return CopilotCredentialRefreshOutcome.Unavailable;
            }

            var refreshed = latest.Credential with
            {
                Status = SignedInStatus,
                AccessToken = exchange.AccessToken,
                RefreshToken = string.IsNullOrWhiteSpace(exchange.RefreshToken)
                    ? latest.Credential.RefreshToken
                    : exchange.RefreshToken,
                ExpiresAt = exchange.ExpiresAt,
            };
            try
            {
                await secretStore.SetSecretAsync(
                    credentialReference,
                    JsonSerializer.Serialize(refreshed),
                    latest.ETag,
                    ct).ConfigureAwait(false);
            }
            catch (SecretPreconditionFailedException)
            {
                // Another process persisted a redemption first; its token wins.
                var reread = await ReadAsync(credentialReference, ct).ConfigureAwait(false);
                return reread is not null && !NeedsRefresh(reread.Credential, now)
                    ? CopilotCredentialRefreshOutcome.NotNeeded
                    : CopilotCredentialRefreshOutcome.Unavailable;
            }

            logger.LogInformation(
                "Redeemed the stored Copilot refresh token for credential {CredentialReference} without operator re-authentication.",
                credentialReference);
            return CopilotCredentialRefreshOutcome.Refreshed;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool NeedsRefresh(StoredCopilotCredential credential, DateTimeOffset now) =>
        string.Equals(credential.Status, SignedInStatus, StringComparison.Ordinal) &&
        credential.ExpiresAt is { } expiresAt &&
        expiresAt <= now.Add(RefreshAheadWindow);

    private async Task<StoredCopilotCredentialSecret?> ReadAsync(string credentialReference, CancellationToken ct)
    {
        var secret = await secretStore.GetSecretAsync(credentialReference, ct).ConfigureAwait(false);
        if (!secret.Found || string.IsNullOrWhiteSpace(secret.Value))
            return null;
        try
        {
            var credential = JsonSerializer.Deserialize<StoredCopilotCredential>(secret.Value, CredentialReadOptions);
            return credential is null ? null : new(credential, secret.ETag);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task MarkReauthRequiredAsync(
        string credentialReference,
        StoredCopilotCredentialSecret latest,
        CancellationToken ct)
    {
        try
        {
            await secretStore.SetSecretAsync(
                credentialReference,
                JsonSerializer.Serialize(latest.Credential with
                {
                    Status = ReauthRequiredStatus,
                    AccessToken = null,
                    RefreshToken = null,
                    ExpiresAt = null,
                }),
                latest.ETag,
                ct).ConfigureAwait(false);
        }
        catch (SecretPreconditionFailedException)
        {
            // A concurrent writer replaced the credential; its value is the authoritative one.
        }
    }

    private async Task<CopilotTokenExchange> RedeemAsync(string refreshToken, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl.TrimEnd('/')}/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId!,
                ["client_secret"] = _clientSecret!,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
            }),
        };
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var response = await httpClientFactory.CreateClient("github-authz")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return CopilotTokenExchange.Rejected;
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is > 64 * 1024)
                return CopilotTokenExchange.Transient;
            var body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var provider = JsonSerializer.Deserialize<ProviderTokenResponse>(body);
            // GitHub answers a dead or revoked refresh token with 200 and an OAuth error payload.
            if (provider is not { Error: null, AccessToken: not null } || string.IsNullOrWhiteSpace(provider.AccessToken))
                return CopilotTokenExchange.Rejected;
            return new(
                CopilotTokenExchangeStatus.Succeeded,
                provider.AccessToken,
                provider.RefreshToken,
                provider.ExpiresIn is > 0 ? DateTimeOffset.UtcNow.AddSeconds(provider.ExpiresIn.Value) : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return CopilotTokenExchange.Transient;
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            if (buffer.Length + read > 64 * 1024) throw new JsonException();
            buffer.Write(chunk, 0, read);
        }
    }

    private enum CopilotTokenExchangeStatus { Succeeded, Rejected, Transient }

    private sealed record CopilotTokenExchange(
        CopilotTokenExchangeStatus Status,
        string? AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAt)
    {
        internal static CopilotTokenExchange Rejected { get; } = new(CopilotTokenExchangeStatus.Rejected, null, null, null);
        internal static CopilotTokenExchange Transient { get; } = new(CopilotTokenExchangeStatus.Transient, null, null, null);
    }

    private sealed record StoredCopilotCredentialSecret(StoredCopilotCredential Credential, string? ETag);

    private sealed record StoredCopilotCredential(
        string? Status,
        string? AccessToken,
        string? RefreshToken,
        string? GitHubLogin,
        DateTimeOffset? ExpiresAt);

    private sealed class ProviderTokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("expires_in")] public long? ExpiresIn { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("error")] public string? Error { get; init; }
    }
}
