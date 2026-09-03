using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Agentweaver.Api.Auth;

public enum RepoAppAuthorizationOutcome
{
    Success,
    HumanEntraSubjectRequired,
    AuthorizationTransactionInvalid,
    AuthorizationTransactionConsumed,
    GitHubBindingUnavailable,
    RateLimited,
}

public sealed record RepoAppAuthorizationBeginResult(
    RepoAppAuthorizationOutcome Outcome,
    string? AuthorizationUrl,
    string? TransactionId,
    DateTimeOffset? ExpiresAt)
{
    [JsonIgnore]
    public string? CallbackCookie { get; init; }
}

public sealed record RepoAppAuthorizationCallbackResult(
    RepoAppAuthorizationOutcome Outcome,
    string ReturnRouteKey);

public sealed record RepoAppAuthorizationPollResult(
    RepoAppAuthorizationOutcome Outcome,
    string? Status);

public sealed record RepoAppConnectionResult(
    RepoAppAuthorizationOutcome Outcome,
    bool Connected,
    string? GitHubLogin);

public sealed record McpBrowserHandoffResult(
    RepoAppAuthorizationOutcome Outcome,
    string? TransactionId,
    string? BrowserUrl,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Repo App's explicit Entra-user authorization lane. It has no dependency on legacy
/// GitHub token stores, so its Key Vault credential tombstones cannot resurrect disk state.
/// </summary>
public sealed class RepoAppUserAuthorizationService(
    IConfiguration configuration,
    GitHubConnectionsPersistenceStore persistence,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    ILogger<RepoAppUserAuthorizationService> logger)
{
    internal static readonly EventId AuthorizationBeginEvent = new(4100, "RepoAppAuthorizationBegin");
    internal static readonly EventId AuthorizationCallbackEvent = new(4101, "RepoAppAuthorizationCallback");
    internal static readonly EventId CredentialPersistenceEvent = new(4102, "RepoAppCredentialPersistence");
    internal static readonly EventId AuthorizationStatusEvent = new(4103, "RepoAppAuthorizationStatus");

    private const string CookieName = "__Host-agentweaver-repo-app-auth";
    private const string CredentialStatusSignedIn = "signed-in";
    private const string CredentialStatusRevoked = "revoked";
    private static readonly TimeSpan TransactionLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, RateWindow> RateWindows = new(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, string> ReturnRoutes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["settings"] = "/settings",
            ["projects"] = "/projects",
        };

    private readonly string _baseUrl = configuration["Auth:RepoApp:BaseUrl"] ?? "https://github.com";
    private readonly string? _clientId = configuration["Auth:RepoApp:ClientId"];
    private readonly string? _clientSecret = configuration["Auth:RepoApp:ClientSecret"];
    private readonly string? _callbackUrl = configuration["Auth:RepoApp:CallbackUrl"];
    private readonly string _scopes = configuration["Auth:RepoApp:Scopes"] ?? "repo read:user";

    public async Task<McpBrowserHandoffResult> BeginMcpHandoffAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        string? requestedReturnRouteKey,
        CancellationToken ct = default)
    {
        var begin = await BeginAsync(caller, principal, requestedReturnRouteKey, ct).ConfigureAwait(false);
        if (begin.Outcome != RepoAppAuthorizationOutcome.Success)
            return new(begin.Outcome, null, null, null);

        try
        {
            await secretStore.SetSecretAsync(
                McpHandoffCookieKey(begin.TransactionId!),
                begin.CallbackCookie!,
                ct: ct).ConfigureAwait(false);
        }
        catch
        {
            // A transaction without a browser-held callback cookie is unusable. Do not return
            // an authorization URL that could later be redeemed without that binding.
            return new(RepoAppAuthorizationOutcome.GitHubBindingUnavailable, null, null, null);
        }

        return new(
            RepoAppAuthorizationOutcome.Success,
            begin.TransactionId,
            BuildMcpHandoffUrl(begin.TransactionId!),
            begin.ExpiresAt);
    }

    internal async Task<(string AuthorizationUrl, string CallbackCookie)?> TakeMcpBrowserHandoffAsync(
        string transactionId,
        string browserSessionId,
        string browserEntraObjectId,
        CancellationToken ct = default)
    {
        if (!await persistence.BindMcpBrowserSessionAsync(
                transactionId,
                GitHubAppKind.Repo,
                GitHubAuthorizationPurpose.InteractiveRepository,
                browserEntraObjectId,
                browserSessionId,
                ct).ConfigureAwait(false))
            return null;

        var transaction = await persistence.GetMcpBrowserHandoffTransactionAsync(
            transactionId,
            GitHubAppKind.Repo,
            GitHubAuthorizationPurpose.InteractiveRepository,
            browserEntraObjectId,
            browserSessionId,
            ct).ConfigureAwait(false);
        if (transaction is null)
            return null;

        if (secretStore is not IAtomicSecretLeaseStore leaseStore)
            return null;

        var cookieKey = McpHandoffCookieKey(transactionId);
        await using var lease = await leaseStore.TryAcquireLeaseAsync(
            cookieKey,
            Guid.NewGuid().ToString("N"),
            TimeSpan.FromMinutes(1),
            ct).ConfigureAwait(false);
        if (lease is null)
            return null;

        var cookie = await secretStore.GetSecretAsync(cookieKey, ct).ConfigureAwait(false);
        var verifier = await secretStore.GetSecretAsync(transaction.PkceVerifierReference, ct).ConfigureAwait(false);
        if (!cookie.Found || string.IsNullOrWhiteSpace(cookie.Value) ||
            !verifier.Found || string.IsNullOrWhiteSpace(verifier.Value))
            return null;

        await secretStore.DeleteSecretAsync(cookieKey, ct).ConfigureAwait(false);
        return (BuildAuthorizationUrl(transaction.State, verifier.Value), cookie.Value);
    }

    public static string CallbackCookieName => CookieName;

    public async Task<RepoAppAuthorizationBeginResult> BeginAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        string? requestedReturnRouteKey,
        CancellationToken ct = default)
    {
        var correlationId = CreateCorrelationId();
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
            return BeginResult(RepoAppAuthorizationOutcome.HumanEntraSubjectRequired, correlationId);
        if (!TryConsumeRateLimit(caller.EntraObjectId!))
            return BeginResult(RepoAppAuthorizationOutcome.RateLimited, correlationId);
        if (string.IsNullOrWhiteSpace(_clientId) ||
            string.IsNullOrWhiteSpace(_clientSecret) ||
            string.IsNullOrWhiteSpace(_callbackUrl))
            return BeginResult(RepoAppAuthorizationOutcome.GitHubBindingUnavailable, correlationId);
        var returnPath = NormalizeReturnPath(requestedReturnRouteKey, ReturnRoutes["settings"]);

        var state = CreateRandomValue();
        var transactionId = GitHubConnectionsPersistenceStore.CreateExternalTransactionId();
        var callbackCookie = CreateRandomValue();
        var verifier = CreateRandomValue();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(TransactionLifetime);
        var verifierReference = $"repo-app-pkce-{transactionId}";
        await secretStore.SetSecretAsync(verifierReference, verifier, ct: ct).ConfigureAwait(false);

        try
        {
            await persistence.AddAuthorizationAsync(new GitHubAuthorizationRecord
            {
                State = state,
                ExternalTransactionId = transactionId,
                AppKind = GitHubAppKind.Repo,
                Purpose = GitHubAuthorizationPurpose.InteractiveRepository,
                EntraObjectId = caller.EntraObjectId!,
                ExpiresAtUnixMilliseconds = expiresAt.ToUnixTimeMilliseconds(),
                ReturnRouteKey = returnPath,
                PkceVerifierProtected = verifierReference,
                CallbackCookieHash = HashCookie(callbackCookie),
                Status = GitHubAuthorizationStatus.Pending,
                CreatedAt = now,
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            await WriteTombstoneAsync(verifierReference, ct).ConfigureAwait(false);
            return BeginResult(RepoAppAuthorizationOutcome.GitHubBindingUnavailable, transactionId);
        }

        var authorizeUrl = BuildAuthorizationUrl(state, verifier);
        return BeginResult(RepoAppAuthorizationOutcome.Success, transactionId, authorizeUrl, transactionId, expiresAt) with
        {
            CallbackCookie = callbackCookie,
        };
    }

    public async Task<RepoAppAuthorizationCallbackResult> CompleteAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        string? state,
        string? code,
        string? callbackCookie,
        CancellationToken ct = default)
    {
        const string defaultRoute = "settings";
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
            return CallbackResult(
                RepoAppAuthorizationOutcome.HumanEntraSubjectRequired,
                defaultRoute,
                "api",
                CreateCorrelationId());
        if (string.IsNullOrWhiteSpace(state))
            return CallbackResult(
                RepoAppAuthorizationOutcome.AuthorizationTransactionInvalid,
                defaultRoute,
                "api",
                CreateCorrelationId());

        var transaction = await persistence.GetRepoAppAuthorizationTransactionAsync(state, ct).ConfigureAwait(false);
        if (transaction is null ||
            transaction.AppKind != GitHubAppKind.Repo ||
            transaction.Purpose != GitHubAuthorizationPurpose.InteractiveRepository ||
            !string.Equals(transaction.EntraObjectId, caller.EntraObjectId, StringComparison.Ordinal) ||
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > transaction.ExpiresAtUnixMilliseconds ||
            string.IsNullOrWhiteSpace(callbackCookie) ||
            !FixedTimeCookieHashEquals(transaction.CallbackCookieHash, callbackCookie))
            return CallbackResult(
                RepoAppAuthorizationOutcome.AuthorizationTransactionInvalid,
                transaction?.ReturnRouteKey ?? defaultRoute,
                "api",
                transaction?.ExternalTransactionId ?? CreateCorrelationId());

        var claimed = await persistence.ClaimAuthorizationAsync(state, caller.EntraObjectId!, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);
        if (claimed != AuthorizationClaimResult.Claimed)
            return CallbackResult(
                claimed == AuthorizationClaimResult.Consumed
                    ? RepoAppAuthorizationOutcome.AuthorizationTransactionConsumed
                    : RepoAppAuthorizationOutcome.AuthorizationTransactionInvalid,
                transaction.ReturnRouteKey,
                "api",
                transaction.ExternalTransactionId);

        return await CompleteClaimedAsync(transaction, caller.EntraObjectId!, code, "api", ct).ConfigureAwait(false);
    }

    private async Task<RepoAppAuthorizationCallbackResult> CompleteClaimedAsync(
        RepoAppAuthorizationTransaction transaction,
        string entraObjectId,
        string? code,
        string callbackKind,
        CancellationToken ct)
    {
        string? credentialReference = null;
        var completionCommitted = false;
        try
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                await CompleteFailureAsync(transaction, entraObjectId, ct).ConfigureAwait(false);
                return CallbackResult(
                    RepoAppAuthorizationOutcome.AuthorizationTransactionInvalid,
                    transaction.ReturnRouteKey,
                    callbackKind,
                    transaction.ExternalTransactionId);
            }

            var verifierResult = await secretStore.GetSecretAsync(transaction.PkceVerifierProtected, ct).ConfigureAwait(false);
            if (!verifierResult.Found || string.IsNullOrWhiteSpace(verifierResult.Value))
            {
                await CompleteFailureAsync(transaction, entraObjectId, ct).ConfigureAwait(false);
                return CallbackResult(
                    RepoAppAuthorizationOutcome.GitHubBindingUnavailable,
                    transaction.ReturnRouteKey,
                    callbackKind,
                    transaction.ExternalTransactionId);
            }

            var credential = await ExchangeCodeAsync(code, verifierResult.Value, ct).ConfigureAwait(false);
            await WriteTombstoneAsync(transaction.PkceVerifierProtected, ct).ConfigureAwait(false);
            if (credential is null)
            {
                await CompleteFailureAsync(transaction, entraObjectId, ct).ConfigureAwait(false);
                return CallbackResult(
                    RepoAppAuthorizationOutcome.GitHubBindingUnavailable,
                    transaction.ReturnRouteKey,
                    callbackKind,
                    transaction.ExternalTransactionId);
            }

            var credentialVersion = CreateRandomValue();
            credentialReference = $"repo-app-user-credential-{credentialVersion}";
            await secretStore.SetSecretAsync(
                credentialReference,
                JsonSerializer.Serialize(credential with { Status = CredentialStatusSignedIn }),
                ct: ct).ConfigureAwait(false);
            var completion = await persistence.CompleteRepoAppAuthorizationAsync(
                transaction.State,
                new GitHubAppAuthorizationRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    EntraObjectId = entraObjectId,
                    AppKind = GitHubAppKind.Repo,
                    Purpose = GitHubAuthorizationPurpose.InteractiveRepository,
                    CredentialReference = credentialReference,
                    CredentialVersion = credentialVersion,
                    GrantDigest = CreateGrantDigest(credentialVersion),
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                CreateAudit(entraObjectId, GitHubAuditOutcome.Succeeded, GitHubAuditReasonCode.None, credentialVersion),
                ct).ConfigureAwait(false);
            if (!completion.Completed)
                throw new InvalidOperationException();
            completionCommitted = true;
            logger.LogInformation(
                CredentialPersistenceEvent,
                "GitHub App authorization lifecycle: app {AppKind}, purpose {Purpose}, phase {Phase}, outcome {Outcome}, correlation {CorrelationId}, credential present {CredentialPresent}, replaced credential present {ReplacedCredentialPresent}",
                "repo",
                "interactive_repository",
                "credential_persistence",
                "success",
                transaction.ExternalTransactionId,
                true,
                completion.RevokedCredentials.Count > 0);

            foreach (var previous in completion.RevokedCredentials)
                await WriteTombstoneAsync(previous.CredentialReference, ct).ConfigureAwait(false);
            return CallbackResult(
                RepoAppAuthorizationOutcome.Success,
                transaction.ReturnRouteKey,
                callbackKind,
                transaction.ExternalTransactionId);
        }
        catch
        {
            if (completionCommitted)
                return CallbackResult(
                    RepoAppAuthorizationOutcome.Success,
                    transaction.ReturnRouteKey,
                    callbackKind,
                    transaction.ExternalTransactionId);
            logger.LogWarning(
                CredentialPersistenceEvent,
                "GitHub App authorization lifecycle: app {AppKind}, purpose {Purpose}, phase {Phase}, outcome {Outcome}, correlation {CorrelationId}, credential present {CredentialPresent}, replaced credential present {ReplacedCredentialPresent}",
                "repo",
                "interactive_repository",
                "credential_persistence",
                ToStateCode(RepoAppAuthorizationOutcome.GitHubBindingUnavailable),
                transaction.ExternalTransactionId,
                false,
                false);
            await FinalizeClaimFailureAsync(transaction, entraObjectId, credentialReference).ConfigureAwait(false);
            return CallbackResult(
                RepoAppAuthorizationOutcome.GitHubBindingUnavailable,
                transaction.ReturnRouteKey,
                callbackKind,
                transaction.ExternalTransactionId);
        }
    }

    /// <summary>
    /// GitHub's top-level callback cannot carry the browser's bearer header. The callback
    /// cookie is the server-authenticated callback session binding issued only after an
    /// Entra-authenticated begin; the callback never accepts an identity from the browser.
    /// </summary>
    public async Task<RepoAppAuthorizationCallbackResult> CompleteBrowserCallbackAsync(
        string? browserSessionId,
        string? browserEntraObjectId,
        string? state,
        string? code,
        string? callbackCookie,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(state))
            return CallbackResult(
                RepoAppAuthorizationOutcome.AuthorizationTransactionInvalid,
                "settings",
                "browser",
                CreateCorrelationId());

        var transaction = await persistence.GetRepoAppAuthorizationTransactionAsync(state, ct).ConfigureAwait(false);
        if (transaction is null ||
            transaction.AppKind != GitHubAppKind.Repo ||
            transaction.Purpose != GitHubAuthorizationPurpose.InteractiveRepository ||
            (transaction.BrowserSessionId is not null &&
             (!string.Equals(transaction.BrowserSessionId, browserSessionId, StringComparison.Ordinal) ||
              !string.Equals(transaction.EntraObjectId, browserEntraObjectId, StringComparison.Ordinal))) ||
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > transaction.ExpiresAtUnixMilliseconds ||
            string.IsNullOrWhiteSpace(callbackCookie) ||
            !FixedTimeCookieHashEquals(transaction.CallbackCookieHash, callbackCookie))
            return CallbackResult(
                RepoAppAuthorizationOutcome.AuthorizationTransactionInvalid,
                "settings",
                "browser",
                transaction?.ExternalTransactionId ?? CreateCorrelationId());

        var claimed = await persistence.ClaimAuthorizationAsync(
            transaction.State, transaction.EntraObjectId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        if (claimed != AuthorizationClaimResult.Claimed)
            return CallbackResult(
                claimed == AuthorizationClaimResult.Consumed
                    ? RepoAppAuthorizationOutcome.AuthorizationTransactionConsumed
                    : RepoAppAuthorizationOutcome.AuthorizationTransactionInvalid,
                transaction.ReturnRouteKey,
                "browser",
                transaction.ExternalTransactionId);

        return await CompleteClaimedAsync(
            transaction,
            transaction.EntraObjectId,
            code,
            "browser",
            ct).ConfigureAwait(false);
    }

    public async Task<RepoAppAuthorizationPollResult> PollAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        string transactionId,
        CancellationToken ct = default)
    {
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
        {
            var deniedResult = new RepoAppAuthorizationPollResult(
                RepoAppAuthorizationOutcome.HumanEntraSubjectRequired,
                null);
            LogStatus(deniedResult.Outcome, deniedResult.Status, connected: null, correlationId: CreateCorrelationId());
            return deniedResult;
        }
        if (!TryConsumeRateLimit(caller.EntraObjectId!))
        {
            var rateLimitedResult = new RepoAppAuthorizationPollResult(RepoAppAuthorizationOutcome.RateLimited, null);
            LogStatus(
                rateLimitedResult.Outcome,
                rateLimitedResult.Status,
                connected: null,
                correlationId: CreateCorrelationId());
            return rateLimitedResult;
        }

        var transaction = await persistence.GetAuthorizationTransactionAsync(
            transactionId, GitHubAppKind.Repo, caller.EntraObjectId!, ct).ConfigureAwait(false);
        RepoAppAuthorizationPollResult result = transaction is null
            ? new(RepoAppAuthorizationOutcome.AuthorizationTransactionInvalid, null)
            : new(RepoAppAuthorizationOutcome.Success, ToPublicStatus(transaction.Status));
        LogStatus(
            result.Outcome,
            result.Status,
            connected: null,
            transaction?.TransactionId ?? CreateCorrelationId());
        return result;
    }

    public async Task<RepoAppAuthorizationOutcome> RefreshAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
            return RepoAppAuthorizationOutcome.HumanEntraSubjectRequired;
        var reference = await persistence.GetActiveRepoAppCredentialAsync(caller.EntraObjectId!, ct).ConfigureAwait(false);
        if (reference is null)
            return RepoAppAuthorizationOutcome.GitHubBindingUnavailable;

        await using var lease = await persistence.TryAcquireRepoAppCredentialLeaseAsync(reference, ct).ConfigureAwait(false);
        if (lease is null)
            return RepoAppAuthorizationOutcome.GitHubBindingUnavailable;

        var secret = await secretStore.GetSecretAsync(reference.CredentialReference, ct).ConfigureAwait(false);
        var credential = secret.Found ? DeserializeCredential(secret.Value) : null;
        if (credential is null || credential.Status != CredentialStatusSignedIn || string.IsNullOrWhiteSpace(credential.RefreshToken))
            return RepoAppAuthorizationOutcome.GitHubBindingUnavailable;

        var refreshed = await RefreshCredentialAsync(credential, ct).ConfigureAwait(false);
        if (refreshed is null)
        {
            await WriteTombstoneAsync(reference.CredentialReference, ct).ConfigureAwait(false);
            var revoked = await persistence.RevokeRepoAppCredentialUnderLeaseAsync(
                reference,
                CreateAudit(caller.EntraObjectId!, GitHubAuditOutcome.Failed, GitHubAuditReasonCode.BindingUnavailable, reference.CredentialVersion),
                ct).ConfigureAwait(false);
            await lease.CommitAsync(ct).ConfigureAwait(false);
            return revoked
                ? RepoAppAuthorizationOutcome.GitHubBindingUnavailable
                : RepoAppAuthorizationOutcome.Success;
        }

        try
        {
            await secretStore.SetSecretAsync(
                reference.CredentialReference,
                JsonSerializer.Serialize(refreshed with { Status = CredentialStatusSignedIn }),
                secret.ETag,
                ct).ConfigureAwait(false);
            await lease.CommitAsync(ct).ConfigureAwait(false);
            return RepoAppAuthorizationOutcome.Success;
        }
        catch (SecretPreconditionFailedException)
        {
            await MarkRefreshPersistenceFailureAsync(reference, caller.EntraObjectId!, lease).ConfigureAwait(false);
            return RepoAppAuthorizationOutcome.GitHubBindingUnavailable;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await MarkRefreshPersistenceFailureAsync(reference, caller.EntraObjectId!, lease).ConfigureAwait(false);
            return RepoAppAuthorizationOutcome.GitHubBindingUnavailable;
        }
    }

    public async Task<RepoAppConnectionResult> GetConnectionAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
            return ConnectionResult(RepoAppAuthorizationOutcome.HumanEntraSubjectRequired, false);

        var reference = await persistence.GetActiveRepoAppCredentialAsync(caller.EntraObjectId!, ct).ConfigureAwait(false);
        if (reference is null)
            return ConnectionResult(RepoAppAuthorizationOutcome.Success, false);

        var secret = await secretStore.GetSecretAsync(reference.CredentialReference, ct).ConfigureAwait(false);
        var credential = secret.Found ? DeserializeCredential(secret.Value) : null;
        if (credential is null || credential.Status != CredentialStatusSignedIn || string.IsNullOrWhiteSpace(credential.AccessToken))
            return ConnectionResult(RepoAppAuthorizationOutcome.Success, false);

        var login = IsGitHubLogin(credential.GitHubLogin)
            ? credential.GitHubLogin
            : await GetGitHubLoginAsync(credential.AccessToken, ct).ConfigureAwait(false);
        if (!IsGitHubLogin(login))
            return ConnectionResult(RepoAppAuthorizationOutcome.Success, false);

        if (!string.Equals(credential.GitHubLogin, login, StringComparison.Ordinal))
            await TryPersistGitHubLoginAsync(reference.CredentialReference, secret.ETag, credential, login!, ct)
                .ConfigureAwait(false);

        return ConnectionResult(RepoAppAuthorizationOutcome.Success, true, login);
    }

    public async Task<RepoAppAuthorizationOutcome> RevokeAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
            return RepoAppAuthorizationOutcome.HumanEntraSubjectRequired;
        IReadOnlyList<RepoAppCredentialReference> references;
        try
        {
            references = await RevokeAllWithRetryAsync(caller.EntraObjectId!, ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return RepoAppAuthorizationOutcome.GitHubBindingUnavailable;
        }
        if (references.Count == 0)
            return RepoAppAuthorizationOutcome.GitHubBindingUnavailable;

        foreach (var reference in references)
        {
            var secret = await secretStore.GetSecretAsync(reference.CredentialReference, ct).ConfigureAwait(false);
            var credential = secret.Found ? DeserializeCredential(secret.Value) : null;
            if (credential is not null)
                await RevokeWithProviderAsync(credential.AccessToken, ct).ConfigureAwait(false);
            await WriteTombstoneAsync(reference.CredentialReference, ct).ConfigureAwait(false);
        }
        return RepoAppAuthorizationOutcome.Success;
    }

    public string GetCallbackRedirect(string returnRouteKey, RepoAppAuthorizationOutcome outcome)
    {
        var frontend = (configuration["Auth:RepoApp:FrontendUrl"] ?? "http://localhost:5173").TrimEnd('/');
        var route = NormalizeReturnPath(returnRouteKey, ReturnRoutes["settings"]);
        return $"{frontend}{AppendQuery(route, "repo_app_auth", ToStateCode(outcome))}";
    }

    private static string NormalizeReturnPath(string? requestedReturnPath, string defaultPath)
    {
        var candidate = string.IsNullOrWhiteSpace(requestedReturnPath)
            ? defaultPath
            : requestedReturnPath.Trim();
        if (ReturnRoutes.TryGetValue(candidate, out var mapped))
            return mapped;
        return IsSafeFrontendReturnPath(candidate) ? candidate : defaultPath;
    }

    private static bool IsSafeFrontendReturnPath(string candidate) =>
        candidate.StartsWith("/", StringComparison.Ordinal) &&
        !candidate.StartsWith("//", StringComparison.Ordinal) &&
        !candidate.Contains('\\') &&
        !candidate.Contains('\r') &&
        !candidate.Contains('\n');

    private static string AppendQuery(string path, string key, string value) =>
        $"{path}{(path.Contains('?') ? '&' : '?')}{key}={Uri.EscapeDataString(value)}";

    private RepoAppAuthorizationBeginResult BeginResult(
        RepoAppAuthorizationOutcome outcome,
        string correlationId,
        string? authorizationUrl = null,
        string? transactionId = null,
        DateTimeOffset? expiresAt = null)
    {
        logger.LogInformation(
            AuthorizationBeginEvent,
            "GitHub App authorization lifecycle: app {AppKind}, purpose {Purpose}, phase {Phase}, outcome {Outcome}, correlation {CorrelationId}",
            "repo",
            "interactive_repository",
            "begin",
            ToStateCode(outcome),
            correlationId);
        return new(outcome, authorizationUrl, transactionId, expiresAt);
    }

    private RepoAppAuthorizationCallbackResult CallbackResult(
        RepoAppAuthorizationOutcome outcome,
        string returnPath,
        string callbackKind,
        string correlationId)
    {
        logger.LogInformation(
            AuthorizationCallbackEvent,
            "GitHub App authorization lifecycle: app {AppKind}, purpose {Purpose}, phase {Phase}, outcome {Outcome}, correlation {CorrelationId}",
            "repo",
            "interactive_repository",
            callbackKind == "browser" ? "browser_callback" : "api_callback",
            ToStateCode(outcome),
            correlationId);
        return new(outcome, returnPath);
    }

    private RepoAppConnectionResult ConnectionResult(
        RepoAppAuthorizationOutcome outcome,
        bool connected,
        string? login = null)
    {
        LogStatus(outcome, status: null, connected, correlationId: CreateCorrelationId());
        return new(outcome, connected, login);
    }

    private void LogStatus(
        RepoAppAuthorizationOutcome outcome,
        string? status,
        bool? connected,
        string correlationId) =>
        logger.LogInformation(
            AuthorizationStatusEvent,
            "GitHub App authorization lifecycle: app {AppKind}, purpose {Purpose}, phase {Phase}, outcome {Outcome}, correlation {CorrelationId}, status present {StatusPresent}, connected {Connected}",
            "repo",
            "interactive_repository",
            "status",
            ToStateCode(outcome),
            correlationId,
            status is not null,
            connected);

    private static string CreateCorrelationId() => Guid.NewGuid().ToString("N");

    private string BuildMcpHandoffUrl(string transactionId) =>
        CallbackBaseUrl() + "/auth/github/repo-app/handoff/" + Uri.EscapeDataString(transactionId);

    private string BuildAuthorizationUrl(string state, string verifier) =>
        $"{_baseUrl.TrimEnd('/')}/login/oauth/authorize" +
        $"?client_id={Uri.EscapeDataString(_clientId!)}" +
        $"&redirect_uri={Uri.EscapeDataString(_callbackUrl!)}" +
        $"&scope={Uri.EscapeDataString(_scopes)}" +
        $"&state={Uri.EscapeDataString(state)}" +
        $"&code_challenge={Uri.EscapeDataString(CreateS256Challenge(verifier))}" +
        "&code_challenge_method=S256";

    private string CallbackBaseUrl() =>
        _callbackUrl!.EndsWith("/auth/github/repo-app/callback", StringComparison.Ordinal)
            ? _callbackUrl[..^"/auth/github/repo-app/callback".Length].TrimEnd('/')
            : throw new InvalidOperationException("Repo App callback URL is invalid.");

    private static string McpHandoffCookieKey(string transactionId) =>
        $"repo-app-mcp-handoff-{transactionId}";

    public static void SetCallbackCookie(HttpContext context, string callbackCookie) =>
        context.Response.Cookies.Append(CookieName, callbackCookie, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TransactionLifetime,
        });

    public static string? ReadCallbackCookie(HttpContext context) =>
        context.Request.Cookies.TryGetValue(CookieName, out var value) ? value : null;

    public static void ClearCallbackCookie(HttpContext context) =>
        context.Response.Cookies.Append(CookieName, string.Empty, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.UnixEpoch,
        });

    public static string CreateS256Challenge(string verifier) =>
        ToBase64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private async Task<RepoAppCredential?> ExchangeCodeAsync(string code, string verifier, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_clientId) ||
            string.IsNullOrWhiteSpace(_clientSecret) ||
            string.IsNullOrWhiteSpace(_callbackUrl))
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl.TrimEnd('/')}/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["code"] = code,
                ["redirect_uri"] = _callbackUrl,
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
            var result = JsonSerializer.Deserialize<ProviderTokenResponse>(body);
            if (result is not { Error: null, AccessToken: not null } || string.IsNullOrWhiteSpace(result.AccessToken))
                return null;

            var login = await GetGitHubLoginAsync(result.AccessToken, timeout.Token).ConfigureAwait(false);
            return login is null
                ? null
                : new(null, result.AccessToken, result.RefreshToken, result.ExpiresIn is > 0
                    ? DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn.Value)
                    : null, login);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> GetGitHubLoginAsync(string accessToken, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl.TrimEnd('/')}/user");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("Agentweaver");
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

    private async Task<RepoAppCredential?> RefreshCredentialAsync(RepoAppCredential credential, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_clientSecret))
            return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl.TrimEnd('/')}/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = credential.RefreshToken!,
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
            var result = JsonSerializer.Deserialize<ProviderTokenResponse>(body);
            return result is { Error: null, AccessToken: not null } && !string.IsNullOrWhiteSpace(result.AccessToken)
                ? credential with
                {
                    AccessToken = result.AccessToken,
                    RefreshToken = string.IsNullOrWhiteSpace(result.RefreshToken) ? credential.RefreshToken : result.RefreshToken,
                    ExpiresAt = result.ExpiresIn is > 0 ? DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn.Value) : null,
                }
                : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task RevokeWithProviderAsync(string? accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_clientSecret))
            return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl.TrimEnd('/')}/applications/{Uri.EscapeDataString(_clientId)}/grant")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { access_token = accessToken }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}")));
        try
        {
            using var _ = await httpClientFactory.CreateClient("github-authz")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (HttpRequestException) { }
    }

    private async Task CompleteFailureAsync(RepoAppAuthorizationTransaction transaction, string entraObjectId, CancellationToken ct) =>
        await persistence.CompleteRepoAppAuthorizationFailureAsync(
            transaction.State,
            CreateAudit(entraObjectId, GitHubAuditOutcome.Failed, GitHubAuditReasonCode.TransactionInvalid, null),
            ct).ConfigureAwait(false);

    private async Task FinalizeClaimFailureAsync(
        RepoAppAuthorizationTransaction transaction,
        string entraObjectId,
        string? credentialReference)
    {
        persistence.ClearPendingChanges();
        try { await WriteTombstoneAsync(transaction.PkceVerifierProtected, CancellationToken.None).ConfigureAwait(false); }
        catch { }
        if (!string.IsNullOrWhiteSpace(credentialReference))
        {
            try { await WriteTombstoneAsync(credentialReference, CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }
        try { await CompleteFailureAsync(transaction, entraObjectId, CancellationToken.None).ConfigureAwait(false); }
        catch { }
    }

    private async Task MarkRefreshPersistenceFailureAsync(
        RepoAppCredentialReference reference,
        string entraObjectId,
        RepoAppCredentialLease lease)
    {
        try { await WriteTombstoneAsync(reference.CredentialReference, CancellationToken.None).ConfigureAwait(false); }
        catch { }
        await persistence.RevokeRepoAppCredentialUnderLeaseAsync(
            reference,
            CreateAudit(entraObjectId, GitHubAuditOutcome.Failed, GitHubAuditReasonCode.BindingUnavailable, reference.CredentialVersion),
            CancellationToken.None).ConfigureAwait(false);
        await lease.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RepoAppCredentialReference>> RevokeAllWithRetryAsync(
        string entraObjectId,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await persistence.RevokeRepoAppCredentialsAsync(
                    entraObjectId,
                    CreateAudit(entraObjectId, GitHubAuditOutcome.Succeeded, GitHubAuditReasonCode.None, null),
                    ct).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (attempt < 2 && IsRetryableConcurrencyFailure(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsRetryableConcurrencyFailure(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected
        };

    private async Task WriteTombstoneAsync(string reference, CancellationToken ct) =>
        await secretStore.SetSecretAsync(reference, JsonSerializer.Serialize(new RepoAppCredential(CredentialStatusRevoked, null, null, null)), ct: ct)
            .ConfigureAwait(false);

    private static RepoAppCredential? DeserializeCredential(string? value)
    {
        try { return string.IsNullOrWhiteSpace(value) ? null : JsonSerializer.Deserialize<RepoAppCredential>(value); }
        catch (JsonException) { return null; }
    }

    private static GitHubAuditRecord CreateAudit(
        string entraObjectId,
        GitHubAuditOutcome outcome,
        GitHubAuditReasonCode reason,
        string? credentialVersion) =>
        new()
        {
            EntraObjectId = entraObjectId,
            ActorKind = GitHubAuditActorKind.HumanEntraSubject,
            Action = GitHubAuditAction.AuthorizationCompleted,
            ResourceId = "repo-app-authorization",
            AppKind = GitHubAppKind.Repo,
            CapabilityPurpose = GitHubCapabilityPurpose.InteractiveRepository,
            Outcome = outcome,
            ReasonCode = reason,
            CorrelationId = Guid.NewGuid().ToString("N"),
            OccurredAt = DateTimeOffset.UtcNow,
            GrantDigest = credentialVersion is null ? null : CreateGrantDigest(credentialVersion),
        };

    private static bool TryConsumeRateLimit(string entraObjectId)
    {
        var now = DateTimeOffset.UtcNow;
        var window = RateWindows.AddOrUpdate(
            entraObjectId,
            _ => new RateWindow(now, 1),
            (_, existing) => now - existing.Start >= TimeSpan.FromMinutes(1)
                ? new RateWindow(now, 1)
                : new RateWindow(existing.Start, existing.Count + 1));
        return window.Count <= 20;
    }

    private static string ToPublicStatus(GitHubAuthorizationStatus status) => status switch
    {
        GitHubAuthorizationStatus.Pending => "pending",
        GitHubAuthorizationStatus.Redeeming => "pending",
        GitHubAuthorizationStatus.Completed => "completed",
        GitHubAuthorizationStatus.Failed => "failed",
        GitHubAuthorizationStatus.Expired => "expired",
        _ => "failed",
    };

    public static string ToStateCode(RepoAppAuthorizationOutcome outcome) => outcome switch
    {
        RepoAppAuthorizationOutcome.HumanEntraSubjectRequired => "human_entra_subject_required",
        RepoAppAuthorizationOutcome.AuthorizationTransactionInvalid => "authorization_transaction_invalid",
        RepoAppAuthorizationOutcome.AuthorizationTransactionConsumed => "authorization_transaction_consumed",
        RepoAppAuthorizationOutcome.GitHubBindingUnavailable => "github_binding_unavailable",
        RepoAppAuthorizationOutcome.RateLimited => "rate_limited",
        _ => "success",
    };

    private static string CreateRandomValue() => ToBase64Url(RandomNumberGenerator.GetBytes(32));

    private static string HashCookie(string callbackCookie) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(callbackCookie)));

    private static bool FixedTimeCookieHashEquals(string expectedHash, string callbackCookie)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(expectedHash),
                SHA256.HashData(Encoding.UTF8.GetBytes(callbackCookie)));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string CreateGrantDigest(string credentialVersion) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"repo:{credentialVersion}"))).ToLowerInvariant();

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
                return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            if (buffer.Length + read > 64 * 1024)
                throw new JsonException();
            buffer.Write(chunk, 0, read);
        }
    }

    private sealed record RateWindow(DateTimeOffset Start, int Count);
    private async Task TryPersistGitHubLoginAsync(
        string credentialReference,
        string? etag,
        RepoAppCredential credential,
        string gitHubLogin,
        CancellationToken ct)
    {
        try
        {
            await secretStore.SetSecretAsync(
                credentialReference,
                JsonSerializer.Serialize(credential with { GitHubLogin = gitHubLogin }),
                etag,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SecretPreconditionFailedException or HttpRequestException)
        {
        }
    }

    private static bool IsGitHubLogin(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 39 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-');

    private sealed record RepoAppCredential(
        string? Status,
        string? AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAt,
        string? GitHubLogin = null);
    private sealed class ProviderTokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("expires_in")] public long? ExpiresIn { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("error")] public string? Error { get; init; }
    }

    private sealed class ProviderUserResponse
    {
        [JsonPropertyName("login")] public string? Login { get; init; }
    }
}
