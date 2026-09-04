using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;

namespace Agentweaver.Api.Auth;

public enum UserCopilotBindingOutcome
{
    Success,
    HumanEntraSubjectRequired,
    AuthorizationTransactionInvalid,
    AuthorizationTransactionConsumed,
    GitHubBindingUnavailable,
}

public sealed record UserCopilotBindingBeginResult(
    UserCopilotBindingOutcome Outcome,
    string? AuthorizationUrl,
    string? TransactionId,
    DateTimeOffset? ExpiresAt)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CallbackCookie { get; init; }
}

public sealed record UserCopilotBindingConnectionResult(
    UserCopilotBindingOutcome Outcome,
    bool Connected,
    string? GitHubLogin);

internal sealed class UserCopilotBindingService(
    IConfiguration configuration,
    GitHubConnectionsPersistenceStore persistence,
    ISecretStore secretStore,
    IGitHubConnectionsCredentialVault credentialVault,
    IHttpClientFactory httpClientFactory,
    CopilotAppRegistrationService registration,
    ILogger<UserCopilotBindingService> logger)
{
    private const string CookieName = "__Host-agentweaver-user-copilot-app-auth";
    private const string CallbackSuffix = "/auth/github/copilot-app/callback";
    private const string TombstoneSecretValue = """{"status":"revoked"}""";
    private static readonly TimeSpan TransactionLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(10);

    private readonly string _baseUrl = configuration["Auth:CopilotApp:BaseUrl"] ?? "https://github.com";
    private readonly string _apiUrl = configuration["Auth:CopilotApp:ApiUrl"] ?? "https://api.github.com";
    private readonly string? _clientId = configuration["Auth:CopilotApp:ClientId"];
    private readonly string? _clientSecret = configuration["Auth:CopilotApp:ClientSecret"];
    private readonly string? _configuredCallbackUrl = configuration["Auth:CopilotApp:CallbackUrl"];
    private readonly string _scopes = configuration["Auth:CopilotApp:Scopes"] ?? "read:user";
    private readonly CopilotCredentialRefreshService _credentialRefresh =
        new(configuration, secretStore, httpClientFactory, logger);

    public async Task<UserCopilotBindingBeginResult> BeginAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        string? browserSessionId = null,
        CancellationToken ct = default)
    {
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
            return new(UserCopilotBindingOutcome.HumanEntraSubjectRequired, null, null, null);
        if (string.IsNullOrWhiteSpace(browserSessionId))
            return new(UserCopilotBindingOutcome.HumanEntraSubjectRequired, null, null, null);
        if (!IsConfigurationValid() ||
            await registration.ValidateAsync(ct).ConfigureAwait(false) != CopilotAppRegistrationState.Ready)
            return new(UserCopilotBindingOutcome.GitHubBindingUnavailable, null, null, null);

        var state = CreateRandomValue();
        var transactionId = GitHubConnectionsPersistenceStore.CreateExternalTransactionId();
        var cookie = CreateRandomValue();
        var verifier = CreateRandomValue();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(TransactionLifetime);
        var verifierReference = $"copilot-app-user-pkce-{transactionId}";
        await secretStore.SetSecretAsync(verifierReference, verifier, ct: ct).ConfigureAwait(false);
        try
        {
            await persistence.AddAuthorizationAsync(new GitHubAuthorizationRecord
            {
                State = state,
                ExternalTransactionId = transactionId,
                AppKind = GitHubAppKind.Copilot,
                Purpose = GitHubAuthorizationPurpose.UserCopilot,
                EntraObjectId = caller.EntraObjectId!,
                ProjectId = null,
                ExpiresAtUnixMilliseconds = expiresAt.ToUnixTimeMilliseconds(),
                ReturnRouteKey = "account-settings",
                PkceVerifierProtected = verifierReference,
                CallbackCookieHash = HashCookie(cookie),
                BrowserSessionId = browserSessionId,
                Status = GitHubAuthorizationStatus.Pending,
                CreatedAt = now,
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            await WriteTombstoneAsync(verifierReference, CancellationToken.None).ConfigureAwait(false);
            return new(UserCopilotBindingOutcome.GitHubBindingUnavailable, null, null, null);
        }

        return new(UserCopilotBindingOutcome.Success, BuildAuthorizationUrl(state, verifier), transactionId, expiresAt)
        {
            CallbackCookie = cookie,
        };
    }

    public async Task<UserCopilotBindingOutcome> CompleteBrowserCallbackAsync(
        BrowserEntraSession? browserSession,
        string? state,
        string? code,
        string? callbackCookie,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(state))
            return UserCopilotBindingOutcome.AuthorizationTransactionInvalid;
        var transaction = await persistence.GetUserCopilotAuthorizationTransactionAsync(state, ct).ConfigureAwait(false);
        if (browserSession is null)
            return UserCopilotBindingOutcome.HumanEntraSubjectRequired;
        if (transaction is null ||
            !string.Equals(transaction.EntraObjectId, browserSession.EntraObjectId, StringComparison.Ordinal) ||
            !string.Equals(transaction.BrowserSessionId, browserSession.Id, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(callbackCookie) ||
            !FixedTimeCookieHashEquals(transaction.CallbackCookieHash, callbackCookie))
            return UserCopilotBindingOutcome.AuthorizationTransactionInvalid;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > transaction.ExpiresAtUnixMilliseconds)
        {
            await WriteTombstoneAsync(transaction.PkceVerifierProtected, CancellationToken.None).ConfigureAwait(false);
            return UserCopilotBindingOutcome.AuthorizationTransactionInvalid;
        }

        return await ClaimAndCompleteAsync(transaction, code, ct).ConfigureAwait(false);
    }

    public async Task<UserCopilotBindingConnectionResult> GetConnectionAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
            return new(UserCopilotBindingOutcome.HumanEntraSubjectRequired, false, null);
        return await GetConnectionCoreAsync(caller.EntraObjectId!, ct).ConfigureAwait(false);
    }

    public async Task<UserCopilotBindingOutcome> DisconnectAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
            return UserCopilotBindingOutcome.HumanEntraSubjectRequired;
        RepoAppCredentialReference? reference;
        try
        {
            reference = await persistence.RevokeUserCopilotBindingAsync(
                caller.EntraObjectId!,
                CreateAudit(caller.EntraObjectId!, GitHubAuditOutcome.Succeeded, GitHubAuditReasonCode.None, null),
                ct).ConfigureAwait(false);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return UserCopilotBindingOutcome.GitHubBindingUnavailable;
        }

        if (reference is null)
            return UserCopilotBindingOutcome.GitHubBindingUnavailable;
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

        return UserCopilotBindingOutcome.Success;
    }

    /// <summary>
    /// Redeems the stored refresh token for the active user binding when its access token
    /// has expired or is inside the redeem-ahead window. Only a rejected refresh token marks the
    /// binding as needing re-authentication.
    /// </summary>
    internal async Task<CopilotCredentialRefreshOutcome> RefreshCredentialAsync(
        string entraObjectId,
        CancellationToken ct = default)
    {
        var binding = await persistence.GetActiveUserCopilotBindingAsync(entraObjectId, ct).ConfigureAwait(false);
        return binding is null
            ? CopilotCredentialRefreshOutcome.NotNeeded
            : await _credentialRefresh.EnsureFreshAsync(binding.CredentialReference, DateTimeOffset.UtcNow, ct)
                .ConfigureAwait(false);
    }

    public Task<string> GetCallbackRedirectAsync(
        UserCopilotBindingOutcome outcome,
        CancellationToken ct = default)
    {
        _ = ct;
        var frontend = (configuration["Auth:CopilotApp:FrontendUrl"] ?? "http://localhost:5173").TrimEnd('/');
        return Task.FromResult($"{frontend}/settings?user_copilot_auth={ToStateCode(outcome)}");
    }

    public static void SetCallbackCookie(HttpContext context, string value) =>
        context.Response.Cookies.Append(CookieName, value, CookieOptions());
    public static string? ReadCallbackCookie(HttpContext context) =>
        context.Request.Cookies.TryGetValue(CookieName, out var value) ? value : null;
    public static void ClearCallbackCookie(HttpContext context) =>
        context.Response.Cookies.Append(CookieName, string.Empty, CookieOptions(DateTimeOffset.UnixEpoch));
    public static string ToStateCode(UserCopilotBindingOutcome outcome) => outcome switch
    {
        UserCopilotBindingOutcome.HumanEntraSubjectRequired => "human_entra_subject_required",
        UserCopilotBindingOutcome.AuthorizationTransactionInvalid => "authorization_transaction_invalid",
        UserCopilotBindingOutcome.AuthorizationTransactionConsumed => "authorization_transaction_consumed",
        UserCopilotBindingOutcome.GitHubBindingUnavailable => "github_binding_unavailable",
        _ => "success",
    };

    private async Task<UserCopilotBindingConnectionResult> GetConnectionCoreAsync(
        string entraObjectId,
        CancellationToken ct)
    {
        var binding = await persistence.GetActiveUserCopilotBindingAsync(entraObjectId, ct).ConfigureAwait(false);
        if (binding is null)
            return new(UserCopilotBindingOutcome.Success, false, null);

        await _credentialRefresh.EnsureFreshAsync(binding.CredentialReference, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);
        var secret = await secretStore.GetSecretAsync(binding.CredentialReference, ct).ConfigureAwait(false);
        var credential = secret.Found ? DeserializeCredential(secret.Value) : null;
        if (credential is null ||
            !string.Equals(credential.Status, "signed-in", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(credential.AccessToken))
        {
            logger.LogWarning(
                "User Copilot connection has an active binding record but its credential secret is {SecretState}.",
                !secret.Found ? "missing" : credential is null ? "unparseable" : $"status={credential.Status}");
            return new(UserCopilotBindingOutcome.GitHubBindingUnavailable, false, null);
        }

        var login = IsGitHubLogin(credential.GitHubLogin)
            ? credential.GitHubLogin
            : !string.IsNullOrWhiteSpace(credential.AccessToken)
                ? await GetGitHubLoginAsync(credential.AccessToken, ct).ConfigureAwait(false)
                : null;
        return new(UserCopilotBindingOutcome.Success, true, login);
    }

    private async Task<UserCopilotBindingOutcome> ClaimAndCompleteAsync(
        UserCopilotAuthorizationTransaction transaction,
        string? code,
        CancellationToken ct)
    {
        var claimed = await persistence.ClaimAuthorizationAsync(
            transaction.State, transaction.EntraObjectId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        if (claimed != AuthorizationClaimResult.Claimed)
            return claimed == AuthorizationClaimResult.Consumed
                ? UserCopilotBindingOutcome.AuthorizationTransactionConsumed
                : UserCopilotBindingOutcome.AuthorizationTransactionInvalid;
        var registrationState = await registration.ValidateAsync(ct).ConfigureAwait(false);
        if (registrationState != CopilotAppRegistrationState.Ready)
        {
            logger.LogWarning(
                "User Copilot binding failed: registration validation returned {RegistrationState} instead of Ready.",
                registrationState);
            await TryWriteTombstoneAsync(
                transaction.PkceVerifierProtected,
                "user registration failure cleanup",
                CancellationToken.None).ConfigureAwait(false);
            await CompleteFailureAsync(transaction, GitHubAuditReasonCode.BindingUnavailable, CancellationToken.None)
                .ConfigureAwait(false);
            return UserCopilotBindingOutcome.GitHubBindingUnavailable;
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
            credentialReference = $"copilot-app-user-{SubjectDigest(transaction.EntraObjectId)}-{version}";
            var credentialValue = JsonSerializer.Serialize(credential with { Status = "signed-in" });
            await WriteCredentialAndVerifyAsync(credentialReference, credentialValue, ct).ConfigureAwait(false);
            var completed = await persistence.CompleteUserCopilotAuthorizationAsync(
                transaction.State,
                new UserCopilotBindingRecord
                {
                    Id = CreateBindingId(transaction.EntraObjectId),
                    EntraObjectId = transaction.EntraObjectId,
                    CredentialReference = credentialReference,
                    CredentialVersion = version,
                    GrantDigest = CreateGrantDigest(transaction.EntraObjectId, version),
                    Status = GitHubBindingStatus.Active,
                    BoundAt = DateTimeOffset.UtcNow,
                    DeactivatedAt = null,
                },
                CreateAudit(transaction.EntraObjectId, GitHubAuditOutcome.Succeeded, GitHubAuditReasonCode.None, version),
                ct).ConfigureAwait(false);
            if (!completed.Completed)
                throw new InvalidOperationException("Persisting the user Copilot binding record failed.");
            if (completed.ReplacedCredential is not null)
                await RevokeReplacedCredentialAsync(
                    completed.ReplacedCredential,
                    credential.AccessToken,
                    CancellationToken.None).ConfigureAwait(false);
            return UserCopilotBindingOutcome.Success;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "User Copilot binding failed to complete.");
            if (!string.IsNullOrWhiteSpace(credentialReference))
                await TryDeleteCredentialAsync(credentialReference, CancellationToken.None).ConfigureAwait(false);
            await TryWriteTombstoneAsync(
                transaction.PkceVerifierProtected,
                "user PKCE verifier cleanup",
                CancellationToken.None).ConfigureAwait(false);
            await CompleteFailureAsync(transaction, GitHubAuditReasonCode.BindingUnavailable, CancellationToken.None).ConfigureAwait(false);
            return UserCopilotBindingOutcome.GitHubBindingUnavailable;
        }
    }

    private async Task CompleteFailureAsync(
        UserCopilotAuthorizationTransaction transaction,
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
            return login is null
                ? null
                : new(
                    "signed-in",
                    provider.AccessToken,
                    provider.RefreshToken,
                    login,
                    provider.ExpiresIn is > 0 ? DateTimeOffset.UtcNow.AddSeconds(provider.ExpiresIn.Value) : null);
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

    private static string SubjectDigest(string entraObjectId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entraObjectId))).ToLowerInvariant()[..20];

    private static string CreateBindingId(string entraObjectId) => $"user-{SubjectDigest(entraObjectId)}";

    private static string CreateGrantDigest(string entraObjectId, string version) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"copilot:user:{entraObjectId}:{version}"))).ToLowerInvariant();

    private async Task WriteCredentialAndVerifyAsync(string reference, string value, CancellationToken ct)
    {
        var locator = GitHubConnectionsCredentialLocator.ForCopilotBinding(reference);
        await credentialVault.WriteAsync(locator, value, ct).ConfigureAwait(false);
        var persisted = await credentialVault.ReadCurrentAsync(locator, ct).ConfigureAwait(false);
        if (!persisted.Found || !string.Equals(persisted.Value, value, StringComparison.Ordinal))
            throw new InvalidOperationException($"Credential secret '{reference}' could not be verified after writing.");
    }

    private async Task TryDeleteCredentialAsync(string reference, CancellationToken ct)
    {
        var locator = GitHubConnectionsCredentialLocator.ForCopilotBinding(reference);
        try
        {
            await DeleteCredentialAsync(reference, ct).ConfigureAwait(false);
            var persisted = await credentialVault.ReadCurrentAsync(locator, ct).ConfigureAwait(false);
            if (persisted.Found)
            {
                logger.LogError(
                    "User Copilot cleanup could not verify credential removal for {Reference}.",
                    reference);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "User Copilot cleanup failed to remove credential secret {Reference}.",
                reference);
        }
    }

    private async Task TryWriteTombstoneAsync(string reference, string purpose, CancellationToken ct)
    {
        try
        {
            await WriteTombstoneAsync(reference, ct).ConfigureAwait(false);
            var persisted = await secretStore.GetSecretAsync(reference, ct).ConfigureAwait(false);
            if (!persisted.Found || !string.Equals(persisted.Value, TombstoneSecretValue, StringComparison.Ordinal))
            {
                logger.LogError(
                    "User Copilot cleanup could not verify tombstone secret write for {Reference} during {Purpose}.",
                    reference,
                    purpose);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "User Copilot cleanup failed to tombstone secret {Reference} during {Purpose}.",
                reference,
                purpose);
        }
    }

    private async Task WriteTombstoneAsync(string reference, CancellationToken ct) =>
        await secretStore.SetSecretAsync(reference, TombstoneSecretValue, ct: ct).ConfigureAwait(false);

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
            ResourceId = CreateBindingId(entraObjectId),
            AppKind = GitHubAppKind.Copilot,
            CapabilityPurpose = GitHubCapabilityPurpose.UnattendedCopilot,
            Outcome = outcome,
            ReasonCode = reason,
            CorrelationId = Guid.NewGuid().ToString("N"),
            OccurredAt = DateTimeOffset.UtcNow,
            GrantDigest = version is null ? null : CreateGrantDigest(entraObjectId, version),
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
                "User Copilot binding replaced credential {CredentialReference} but cleanup failed after commit.",
                reference.CredentialReference);
        }
    }


    private static bool IsGitHubLogin(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 39 &&
        Regex.IsMatch(value, "^[A-Za-z\\d](?:[A-Za-z\\d-]{0,37}[A-Za-z\\d])?$");

    private sealed record CopilotCredential(
        string? Status,
        string? AccessToken,
        string? RefreshToken,
        string? GitHubLogin = null,
        DateTimeOffset? ExpiresAt = null);

    private sealed class ProviderTokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("expires_in")] public long? ExpiresIn { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("error")] public string? Error { get; init; }
    }

    private sealed class ProviderUserResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("login")] public string? Login { get; init; }
    }
}
