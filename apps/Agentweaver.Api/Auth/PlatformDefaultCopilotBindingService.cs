using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;

namespace Agentweaver.Api.Auth;

public enum PlatformDefaultCopilotBindingOutcome
{
    Success,
    HumanEntraSubjectRequired,
    PlatformAdminRequired,
    AuthorizationTransactionInvalid,
    AuthorizationTransactionConsumed,
    GitHubBindingUnavailable,
}

public sealed record PlatformDefaultCopilotBindingBeginResult(
    PlatformDefaultCopilotBindingOutcome Outcome,
    string? AuthorizationUrl,
    string? TransactionId,
    DateTimeOffset? ExpiresAt)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CallbackCookie { get; init; }
}

public sealed record PlatformDefaultCopilotBindingConnectionResult(
    PlatformDefaultCopilotBindingOutcome Outcome,
    bool Connected,
    string? GitHubLogin);

internal sealed class PlatformDefaultCopilotBindingService(
    IConfiguration configuration,
    GitHubConnectionsPersistenceStore persistence,
    ISecretStore secretStore,
    IGitHubConnectionsCredentialVault credentialVault,
    IHttpClientFactory httpClientFactory,
    CopilotAppRegistrationService registration,
    ILogger<PlatformDefaultCopilotBindingService> logger)
{
    private const string CookieName = "__Host-agentweaver-platform-copilot-app-auth";
    private const string CallbackSuffix = "/auth/github/copilot-app/callback";
    private static readonly TimeSpan TransactionLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(10);

    private readonly string _baseUrl = configuration["Auth:CopilotApp:BaseUrl"] ?? "https://github.com";
    private readonly string _apiUrl = configuration["Auth:CopilotApp:ApiUrl"] ?? "https://api.github.com";
    private readonly string? _clientId = configuration["Auth:CopilotApp:ClientId"];
    private readonly string? _clientSecret = configuration["Auth:CopilotApp:ClientSecret"];
    private readonly string? _configuredCallbackUrl = configuration["Auth:CopilotApp:CallbackUrl"];
    private readonly string _scopes = configuration["Auth:CopilotApp:Scopes"] ?? "read:user";

    public async Task<PlatformDefaultCopilotBindingBeginResult> BeginAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
            return new(PlatformDefaultCopilotBindingOutcome.HumanEntraSubjectRequired, null, null, null);
        if (!IsPlatformAdmin(caller))
            return new(PlatformDefaultCopilotBindingOutcome.PlatformAdminRequired, null, null, null);
        if (!IsConfigurationValid() ||
            await registration.ValidateAsync(ct).ConfigureAwait(false) != CopilotAppRegistrationState.Ready)
            return new(PlatformDefaultCopilotBindingOutcome.GitHubBindingUnavailable, null, null, null);

        var state = CreateRandomValue();
        var transactionId = GitHubConnectionsPersistenceStore.CreateExternalTransactionId();
        var cookie = CreateRandomValue();
        var verifier = CreateRandomValue();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(TransactionLifetime);
        var verifierReference = $"copilot-app-platform-pkce-{transactionId}";
        await secretStore.SetSecretAsync(verifierReference, verifier, ct: ct).ConfigureAwait(false);
        try
        {
            await persistence.AddAuthorizationAsync(new GitHubAuthorizationRecord
            {
                State = state,
                ExternalTransactionId = transactionId,
                AppKind = GitHubAppKind.Copilot,
                Purpose = GitHubAuthorizationPurpose.PlatformDefaultCopilot,
                EntraObjectId = caller.EntraObjectId!,
                ProjectId = null,
                ExpiresAtUnixMilliseconds = expiresAt.ToUnixTimeMilliseconds(),
                ReturnRouteKey = "platform-settings",
                PkceVerifierProtected = verifierReference,
                CallbackCookieHash = HashCookie(cookie),
                Status = GitHubAuthorizationStatus.Pending,
                CreatedAt = now,
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            await WriteTombstoneAsync(verifierReference, CancellationToken.None).ConfigureAwait(false);
            return new(PlatformDefaultCopilotBindingOutcome.GitHubBindingUnavailable, null, null, null);
        }

        return new(PlatformDefaultCopilotBindingOutcome.Success, BuildAuthorizationUrl(state, verifier), transactionId, expiresAt)
        {
            CallbackCookie = cookie,
        };
    }

    public async Task<PlatformDefaultCopilotBindingOutcome> CompleteBrowserCallbackAsync(
        string? browserSessionId,
        string? browserEntraObjectId,
        string? state,
        string? code,
        string? callbackCookie,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(state))
            return PlatformDefaultCopilotBindingOutcome.AuthorizationTransactionInvalid;
        var transaction = await persistence.GetPlatformDefaultCopilotAuthorizationTransactionAsync(state, ct).ConfigureAwait(false);
        if (transaction is null ||
            (transaction.BrowserSessionId is not null &&
             (!string.Equals(transaction.BrowserSessionId, browserSessionId, StringComparison.Ordinal) ||
              !string.Equals(transaction.EntraObjectId, browserEntraObjectId, StringComparison.Ordinal))) ||
            string.IsNullOrWhiteSpace(callbackCookie) ||
            !FixedTimeCookieHashEquals(transaction.CallbackCookieHash, callbackCookie))
            return PlatformDefaultCopilotBindingOutcome.AuthorizationTransactionInvalid;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > transaction.ExpiresAtUnixMilliseconds)
        {
            await WriteTombstoneAsync(transaction.PkceVerifierProtected, CancellationToken.None).ConfigureAwait(false);
            return PlatformDefaultCopilotBindingOutcome.AuthorizationTransactionInvalid;
        }

        return await ClaimAndCompleteAsync(transaction, code, ct).ConfigureAwait(false);
    }

    public async Task<PlatformDefaultCopilotBindingConnectionResult> GetConnectionAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
            return new(PlatformDefaultCopilotBindingOutcome.HumanEntraSubjectRequired, false, null);
        if (!IsPlatformAdmin(caller))
            return new(PlatformDefaultCopilotBindingOutcome.PlatformAdminRequired, false, null);
        return await GetConnectionCoreAsync(ct).ConfigureAwait(false);
    }

    public async Task<PlatformDefaultCopilotBindingOutcome> DisconnectAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
            return PlatformDefaultCopilotBindingOutcome.HumanEntraSubjectRequired;
        if (!IsPlatformAdmin(caller))
            return PlatformDefaultCopilotBindingOutcome.PlatformAdminRequired;

        RepoAppCredentialReference? reference;
        try
        {
            reference = await persistence.RevokePlatformDefaultCopilotBindingAsync(
                CreateAudit(caller.EntraObjectId!, GitHubAuditOutcome.Succeeded, GitHubAuditReasonCode.None, null),
                ct).ConfigureAwait(false);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return PlatformDefaultCopilotBindingOutcome.GitHubBindingUnavailable;
        }

        if (reference is null)
            return PlatformDefaultCopilotBindingOutcome.GitHubBindingUnavailable;
        try
        {
            var secret = await secretStore.GetSecretAsync(reference.CredentialReference, ct).ConfigureAwait(false);
            var credential = secret.Found ? DeserializeCredential(secret.Value) : null;
            if (!await IsTokenStillInUseAsync(reference.Id, credential?.AccessToken, ct).ConfigureAwait(false))
                await RevokeWithProviderAsync(credential?.AccessToken, ct).ConfigureAwait(false);
            await DeleteCredentialAsync(reference.CredentialReference, ct).ConfigureAwait(false);
        }
        catch
        {
            // Durable revocation already succeeded; provider revoke remains best-effort only.
        }

        return PlatformDefaultCopilotBindingOutcome.Success;
    }

    public Task<string> GetCallbackRedirectAsync(
        PlatformDefaultCopilotBindingOutcome outcome,
        CancellationToken ct = default)
    {
        _ = ct;
        var frontend = (configuration["Auth:CopilotApp:FrontendUrl"] ?? "http://localhost:5173").TrimEnd('/');
        return Task.FromResult($"{frontend}/platform-settings?copilot_app_auth={ToStateCode(outcome)}");
    }

    public static void SetCallbackCookie(HttpContext context, string value) =>
        context.Response.Cookies.Append(CookieName, value, CookieOptions());
    public static string? ReadCallbackCookie(HttpContext context) =>
        context.Request.Cookies.TryGetValue(CookieName, out var value) ? value : null;
    public static void ClearCallbackCookie(HttpContext context) =>
        context.Response.Cookies.Append(CookieName, string.Empty, CookieOptions(DateTimeOffset.UnixEpoch));
    public static string ToStateCode(PlatformDefaultCopilotBindingOutcome outcome) => outcome switch
    {
        PlatformDefaultCopilotBindingOutcome.HumanEntraSubjectRequired => "human_entra_subject_required",
        PlatformDefaultCopilotBindingOutcome.PlatformAdminRequired => "platform_admin_required",
        PlatformDefaultCopilotBindingOutcome.AuthorizationTransactionInvalid => "authorization_transaction_invalid",
        PlatformDefaultCopilotBindingOutcome.AuthorizationTransactionConsumed => "authorization_transaction_consumed",
        PlatformDefaultCopilotBindingOutcome.GitHubBindingUnavailable => "github_binding_unavailable",
        _ => "success",
    };

    private async Task<PlatformDefaultCopilotBindingConnectionResult> GetConnectionCoreAsync(CancellationToken ct)
    {
        var binding = await persistence.GetActivePlatformDefaultCopilotBindingAsync(ct).ConfigureAwait(false);
        if (binding is null)
            return new(PlatformDefaultCopilotBindingOutcome.Success, false, null);

        var secret = await secretStore.GetSecretAsync(binding.CredentialReference, ct).ConfigureAwait(false);
        var credential = secret.Found ? DeserializeCredential(secret.Value) : null;
        if (credential is null ||
            !string.Equals(credential.Status, "signed-in", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(credential.AccessToken))
        {
            logger.LogWarning(
                "Platform-default Copilot connection has an active binding record but its credential secret is {SecretState}.",
                !secret.Found ? "missing" : credential is null ? "unparseable" : $"status={credential.Status}");
            return new(PlatformDefaultCopilotBindingOutcome.GitHubBindingUnavailable, false, null);
        }

        var login = IsGitHubLogin(credential.GitHubLogin)
            ? credential.GitHubLogin
            : !string.IsNullOrWhiteSpace(credential.AccessToken)
                ? await GetGitHubLoginAsync(credential.AccessToken, ct).ConfigureAwait(false)
                : null;
        return new(PlatformDefaultCopilotBindingOutcome.Success, true, login);
    }

    private async Task<PlatformDefaultCopilotBindingOutcome> ClaimAndCompleteAsync(
        PlatformDefaultCopilotAuthorizationTransaction transaction,
        string? code,
        CancellationToken ct)
    {
        var claimed = await persistence.ClaimAuthorizationAsync(
            transaction.State, transaction.EntraObjectId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        if (claimed != AuthorizationClaimResult.Claimed)
            return claimed == AuthorizationClaimResult.Consumed
                ? PlatformDefaultCopilotBindingOutcome.AuthorizationTransactionConsumed
                : PlatformDefaultCopilotBindingOutcome.AuthorizationTransactionInvalid;
        var registrationState = await registration.ValidateAsync(ct).ConfigureAwait(false);
        if (registrationState != CopilotAppRegistrationState.Ready)
        {
            logger.LogWarning(
                "Platform-default Copilot binding failed: registration validation returned {RegistrationState} instead of Ready.",
                registrationState);
            await WriteTombstoneAsync(transaction.PkceVerifierProtected, CancellationToken.None).ConfigureAwait(false);
            await CompleteFailureAsync(transaction, GitHubAuditReasonCode.BindingUnavailable, CancellationToken.None)
                .ConfigureAwait(false);
            return PlatformDefaultCopilotBindingOutcome.GitHubBindingUnavailable;
        }

        string? credentialReference = null;
        try
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException("Callback did not include an authorization code.");
            var verifier = await secretStore.GetSecretAsync(transaction.PkceVerifierProtected, ct).ConfigureAwait(false);
            if (!verifier.Found || string.IsNullOrWhiteSpace(verifier.Value))
                throw new InvalidOperationException("PKCE verifier secret was not found or had expired.");
            var credential = await ExchangeCodeAsync(code, verifier.Value, ct).ConfigureAwait(false);
            await WriteTombstoneAsync(transaction.PkceVerifierProtected, ct).ConfigureAwait(false);
            if (credential is null || string.IsNullOrWhiteSpace(credential.GitHubLogin))
                throw new InvalidOperationException("Token exchange with GitHub did not return a usable credential or login.");

            var version = CreateRandomValue();
            credentialReference = $"copilot-app-platform-default-{version}";
            await credentialVault.WriteAsync(
                GitHubConnectionsCredentialLocator.ForCopilotBinding(credentialReference),
                JsonSerializer.Serialize(credential with { Status = "signed-in" }),
                ct).ConfigureAwait(false);
            var completed = await persistence.CompletePlatformDefaultCopilotAuthorizationAsync(
                transaction.State,
                new PlatformDefaultCopilotBindingRecord
                {
                    Id = PlatformDefaultCopilotBindingRecord.SingletonId,
                    EntraObjectId = transaction.EntraObjectId,
                    CredentialReference = credentialReference,
                    CredentialVersion = version,
                    GrantDigest = CreateGrantDigest(version),
                    Status = GitHubBindingStatus.Active,
                    BoundAt = DateTimeOffset.UtcNow,
                    DeactivatedAt = null,
                },
                CreateAudit(transaction.EntraObjectId, GitHubAuditOutcome.Succeeded, GitHubAuditReasonCode.None, version),
                ct).ConfigureAwait(false);
            if (!completed.Completed)
                throw new InvalidOperationException("Persisting the platform-default Copilot binding record failed.");
            if (completed.ReplacedCredential is not null)
                await RevokeReplacedCredentialAsync(
                    completed.ReplacedCredential,
                    credential.AccessToken,
                    CancellationToken.None).ConfigureAwait(false);
            return PlatformDefaultCopilotBindingOutcome.Success;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Platform-default Copilot binding failed to complete.");
            if (!string.IsNullOrWhiteSpace(credentialReference))
                await DeleteCredentialAsync(credentialReference, CancellationToken.None).ConfigureAwait(false);
            await WriteTombstoneAsync(transaction.PkceVerifierProtected, CancellationToken.None).ConfigureAwait(false);
            await CompleteFailureAsync(transaction, GitHubAuditReasonCode.BindingUnavailable, CancellationToken.None).ConfigureAwait(false);
            return PlatformDefaultCopilotBindingOutcome.GitHubBindingUnavailable;
        }
    }

    private async Task CompleteFailureAsync(
        PlatformDefaultCopilotAuthorizationTransaction transaction,
        GitHubAuditReasonCode reason,
        CancellationToken ct) =>
        await persistence.CompleteCopilotAuthorizationFailureAsync(
            transaction.State,
            CreateAudit(transaction.EntraObjectId, GitHubAuditOutcome.Failed, reason, null),
            ct).ConfigureAwait(false);

    private bool IsConfigurationValid() =>
        !string.IsNullOrWhiteSpace(_clientId) &&
        !string.IsNullOrWhiteSpace(_clientSecret) &&
        !string.IsNullOrWhiteSpace(_configuredCallbackUrl) &&
        _configuredCallbackUrl.EndsWith(CallbackSuffix, StringComparison.Ordinal) &&
        string.IsNullOrWhiteSpace(configuration["Auth:CopilotApp:PrivateKey"]) &&
        !SameConfiguredValue(_clientId, configuration["Auth:RepoApp:ClientId"]) &&
        !SameConfiguredValue(_clientSecret, configuration["Auth:RepoApp:ClientSecret"]) &&
        !SameConfiguredValue(configuration["Auth:CopilotApp:SecretPath"], configuration["Auth:RepoApp:SecretPath"]) &&
        !string.Equals(configuration["Auth:RepoApp:RequestUserAuthorizationDuringInstallation"], "true", StringComparison.OrdinalIgnoreCase);

    private static bool SameConfiguredValue(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) &&
        !string.IsNullOrWhiteSpace(second) &&
        string.Equals(first, second, StringComparison.Ordinal);

    private string BuildAuthorizationUrl(string state, string verifier) =>
        $"{_baseUrl.TrimEnd('/')}/login/oauth/authorize" +
        $"?client_id={Uri.EscapeDataString(_clientId!)}" +
        $"&redirect_uri={Uri.EscapeDataString(_configuredCallbackUrl!)}" +
        $"&scope={Uri.EscapeDataString(_scopes)}" +
        $"&state={Uri.EscapeDataString(state)}" +
        $"&code_challenge={Uri.EscapeDataString(ProjectCopilotBindingService.CreateS256Challenge(verifier))}" +
        "&code_challenge_method=S256";

    private async Task<CopilotCredential?> ExchangeCodeAsync(string code, string verifier, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl.TrimEnd('/')}/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId!,
                ["client_secret"] = _clientSecret!,
                ["code"] = code,
                ["redirect_uri"] = _configuredCallbackUrl!,
                ["code_verifier"] = verifier,
            }),
        };
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var response = await httpClientFactory.CreateClient("github-authz")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is > 64 * 1024)
                return null;
            var body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var provider = JsonSerializer.Deserialize<ProviderTokenResponse>(body);
            if (provider is not { Error: null, AccessToken: not null } ||
                string.IsNullOrWhiteSpace(provider.AccessToken))
                return null;

            var login = await GetGitHubLoginAsync(provider.AccessToken, timeout.Token).ConfigureAwait(false);
            return login is null ? null : new("signed-in", provider.AccessToken, provider.RefreshToken, login);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    private async Task<string?> GetGitHubLoginAsync(string accessToken, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("Agentweaver");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        try
        {
            using var response = await httpClientFactory.CreateClient("github-authz")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is > 64 * 1024)
                return null;
            var body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var provider = JsonSerializer.Deserialize<ProviderUserResponse>(body);
            return provider is not null && IsGitHubLogin(provider.Login) ? provider.Login : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    private async Task RevokeWithProviderAsync(string? accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            $"{_apiUrl.TrimEnd('/')}/applications/{Uri.EscapeDataString(_clientId!)}/token")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { access_token = accessToken }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}")));
        try { using var _ = await httpClientFactory.CreateClient("github-authz").SendAsync(request, timeout.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
    }

    private static CookieOptions CookieOptions(DateTimeOffset? expires = null) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = expires,
        MaxAge = expires is null ? TransactionLifetime : null,
    };

    private static string CreateRandomValue() => ToBase64Url(RandomNumberGenerator.GetBytes(32));
    private static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string HashCookie(string value) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool FixedTimeCookieHashEquals(string expected, string value)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(expected), SHA256.HashData(Encoding.UTF8.GetBytes(value))); }
        catch (FormatException) { return false; }
    }

    private static string CreateGrantDigest(string version) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"copilot:platform-default:{version}"))).ToLowerInvariant();

    private async Task WriteTombstoneAsync(string reference, CancellationToken ct) =>
        await secretStore.SetSecretAsync(reference, """{"status":"revoked"}""", ct: ct).ConfigureAwait(false);

    private async Task DeleteCredentialAsync(string reference, CancellationToken ct) =>
        await credentialVault.TombstoneAndDeleteAsync(
            GitHubConnectionsCredentialLocator.ForCopilotBinding(reference),
            ct).ConfigureAwait(false);

    private static CopilotCredential? DeserializeCredential(string? value)
    {
        try { return string.IsNullOrWhiteSpace(value) ? null : JsonSerializer.Deserialize<CopilotCredential>(value); }
        catch (JsonException) { return null; }
    }

    private async Task<bool> IsTokenStillInUseAsync(
        string bindingId,
        string? accessToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return false;
        var otherBindings = await persistence.ListActiveCopilotBindingsAsync(bindingId, ct).ConfigureAwait(false);
        foreach (var otherBinding in otherBindings)
        {
            var otherSecret = await secretStore.GetSecretAsync(otherBinding.CredentialReference, ct).ConfigureAwait(false);
            var otherCredential = otherSecret.Found ? DeserializeCredential(otherSecret.Value) : null;
            if (string.Equals(otherCredential?.AccessToken, accessToken, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static GitHubAuditRecord CreateAudit(
        string entraObjectId,
        GitHubAuditOutcome outcome,
        GitHubAuditReasonCode reason,
        string? version) =>
        new()
        {
            EntraObjectId = entraObjectId,
            ActorKind = GitHubAuditActorKind.HumanEntraSubject,
            Action = GitHubAuditAction.BindingChanged,
            ResourceId = PlatformDefaultCopilotBindingRecord.SingletonId,
            AppKind = GitHubAppKind.Copilot,
            CapabilityPurpose = GitHubCapabilityPurpose.UnattendedCopilot,
            Outcome = outcome,
            ReasonCode = reason,
            CorrelationId = Guid.NewGuid().ToString("N"),
            OccurredAt = DateTimeOffset.UtcNow,
            GrantDigest = version is null ? null : CreateGrantDigest(version),
        };

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

    private async Task RevokeReplacedCredentialAsync(
        RepoAppCredentialReference reference,
        string? replacementAccessToken,
        CancellationToken ct)
    {
        try
        {
            var secret = await secretStore.GetSecretAsync(reference.CredentialReference, ct).ConfigureAwait(false);
            var replacedCredential = secret.Found && !string.IsNullOrWhiteSpace(secret.Value)
                ? DeserializeCredential(secret.Value)
                : null;
            if (!string.Equals(replacedCredential?.AccessToken, replacementAccessToken, StringComparison.Ordinal) &&
                !await IsTokenStillInUseAsync(reference.Id, replacedCredential?.AccessToken, ct).ConfigureAwait(false))
                await RevokeWithProviderAsync(replacedCredential?.AccessToken, ct).ConfigureAwait(false);
            await DeleteCredentialAsync(reference.CredentialReference, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Platform-default Copilot binding replaced credential {CredentialReference} but cleanup failed after commit.",
                reference.CredentialReference);
        }
    }


    private static bool IsGitHubLogin(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 39 &&
        Regex.IsMatch(value, "^[A-Za-z\\d](?:[A-Za-z\\d-]{0,37}[A-Za-z\\d])?$");

    private static bool IsPlatformAdmin(CallerContext caller) =>
        caller.PlatformRoles.Contains(PlatformRoles.PlatformAdmin, StringComparer.Ordinal);

    private sealed record CopilotCredential(
        string? Status,
        string? AccessToken,
        string? RefreshToken,
        string? GitHubLogin = null);

    private sealed class ProviderTokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("error")] public string? Error { get; init; }
    }

    private sealed class ProviderUserResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("login")] public string? Login { get; init; }
    }
}
