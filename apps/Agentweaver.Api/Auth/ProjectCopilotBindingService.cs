using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

public enum CopilotBindingOutcome
{
    Success,
    HumanEntraSubjectRequired,
    ProjectOwnerRequired,
    AuthorizationTransactionInvalid,
    AuthorizationTransactionConsumed,
    GitHubBindingUnavailable,
}

public sealed record CopilotBindingBeginResult(
    CopilotBindingOutcome Outcome,
    string? AuthorizationUrl,
    string? TransactionId,
    DateTimeOffset? ExpiresAt)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CallbackCookie { get; init; }
}

public sealed record CopilotBindingPollResult(CopilotBindingOutcome Outcome, string? Status);

/// <summary>
/// Owns the project-pinned Copilot App authorization transaction and its durable binding.
/// This service deliberately has no repository, installation, PEM, or generic token-store dependency.
/// </summary>
public sealed class ProjectCopilotBindingService(
    IConfiguration configuration,
    TwoAppPersistenceStore persistence,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    IProjectRoleAssignmentStore roleAssignments)
{
    private const string CookieName = "__Host-agentweaver-copilot-app-auth";
    private static readonly TimeSpan TransactionLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> ReturnRoutes =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["projects"] = "/projects" };

    private readonly string _baseUrl = configuration["Auth:CopilotApp:BaseUrl"] ?? "https://github.com";
    private readonly string? _clientId = configuration["Auth:CopilotApp:ClientId"];
    private readonly string? _clientSecret = configuration["Auth:CopilotApp:ClientSecret"];
    private readonly string? _callbackUrl = configuration["Auth:CopilotApp:CallbackUrl"];
    private readonly string _scopes = configuration["Auth:CopilotApp:Scopes"] ?? "read:user";

    public async Task<CopilotBindingBeginResult> BeginAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        ProjectId projectId,
        CancellationToken ct = default)
    {
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
            return new(CopilotBindingOutcome.HumanEntraSubjectRequired, null, null, null);
        if (!await IsExplicitOwnerAsync(projectId, caller.EntraObjectId!, ct).ConfigureAwait(false))
            return new(CopilotBindingOutcome.ProjectOwnerRequired, null, null, null);
        if (!IsConfigurationValid())
            return new(CopilotBindingOutcome.GitHubBindingUnavailable, null, null, null);

        var state = CreateRandomValue();
        var transactionId = TwoAppPersistenceStore.CreateExternalTransactionId();
        var cookie = CreateRandomValue();
        var verifier = CreateRandomValue();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(TransactionLifetime);
        var verifierReference = $"copilot-app-pkce-{transactionId}";
        await secretStore.SetSecretAsync(verifierReference, verifier, ct: ct).ConfigureAwait(false);
        try
        {
            await persistence.AddAuthorizationAsync(new GitHubAuthorizationRecord
            {
                State = state,
                ExternalTransactionId = transactionId,
                AppKind = GitHubAppKind.Copilot,
                Purpose = GitHubAuthorizationPurpose.InteractiveCopilot,
                EntraObjectId = caller.EntraObjectId!,
                ProjectId = projectId.ToString(),
                ExpiresAtUnixMilliseconds = expiresAt.ToUnixTimeMilliseconds(),
                ReturnRouteKey = "projects",
                PkceVerifierProtected = verifierReference,
                CallbackCookieHash = HashCookie(cookie),
                Status = GitHubAuthorizationStatus.Pending,
                CreatedAt = now,
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            await WriteTombstoneAsync(verifierReference, CancellationToken.None).ConfigureAwait(false);
            return new(CopilotBindingOutcome.GitHubBindingUnavailable, null, null, null);
        }

        var url = $"{_baseUrl.TrimEnd('/')}/login/oauth/authorize" +
            $"?client_id={Uri.EscapeDataString(_clientId!)}" +
            $"&redirect_uri={Uri.EscapeDataString(_callbackUrl!)}" +
            $"&scope={Uri.EscapeDataString(_scopes)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&code_challenge={Uri.EscapeDataString(CreateS256Challenge(verifier))}" +
            "&code_challenge_method=S256";
        return new(CopilotBindingOutcome.Success, url, transactionId, expiresAt) { CallbackCookie = cookie };
    }

    public async Task<CopilotBindingOutcome> CompleteAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        ProjectId requestedProjectId,
        string? state,
        string? code,
        string? callbackCookie,
        CancellationToken ct = default)
    {
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
            return CopilotBindingOutcome.HumanEntraSubjectRequired;
        if (string.IsNullOrWhiteSpace(state))
            return CopilotBindingOutcome.AuthorizationTransactionInvalid;
        var transaction = await persistence.GetCopilotAuthorizationTransactionAsync(state, ct).ConfigureAwait(false);
        if (!IsValidTransaction(transaction, caller.EntraObjectId!, requestedProjectId, callbackCookie))
            return CopilotBindingOutcome.AuthorizationTransactionInvalid;
        return await ClaimAndCompleteAsync(transaction!, requestedProjectId, code, ct).ConfigureAwait(false);
    }

    public async Task<CopilotBindingOutcome> CompleteBrowserCallbackAsync(
        string? state,
        string? code,
        string? callbackCookie,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(state))
            return CopilotBindingOutcome.AuthorizationTransactionInvalid;
        var transaction = await persistence.GetCopilotAuthorizationTransactionAsync(state, ct).ConfigureAwait(false);
        if (transaction is null || !ProjectId.TryParse(transaction.ProjectId, out var projectId) ||
            string.IsNullOrWhiteSpace(callbackCookie) ||
            !FixedTimeCookieHashEquals(transaction.CallbackCookieHash, callbackCookie))
            return CopilotBindingOutcome.AuthorizationTransactionInvalid;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > transaction.ExpiresAtUnixMilliseconds)
        {
            await WriteTombstoneAsync(transaction.PkceVerifierProtected, CancellationToken.None).ConfigureAwait(false);
            return CopilotBindingOutcome.AuthorizationTransactionInvalid;
        }
        return await ClaimAndCompleteAsync(transaction, projectId, code, ct).ConfigureAwait(false);
    }

    public async Task<CopilotBindingPollResult> PollAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        ProjectId projectId,
        string transactionId,
        CancellationToken ct = default)
    {
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
            return new(CopilotBindingOutcome.HumanEntraSubjectRequired, null);
        var transaction = await persistence.GetAuthorizationTransactionAsync(
            transactionId, GitHubAppKind.Copilot, caller.EntraObjectId!, ct).ConfigureAwait(false);
        if (transaction is null)
            return new(CopilotBindingOutcome.AuthorizationTransactionInvalid, null);
        var pinned = await persistence.GetCopilotAuthorizationTransactionByIdAsync(
            transactionId, caller.EntraObjectId!, ct).ConfigureAwait(false);
        return pinned is null || !string.Equals(pinned.ProjectId, projectId.ToString(), StringComparison.Ordinal)
            ? new(CopilotBindingOutcome.AuthorizationTransactionInvalid, null)
            : new(CopilotBindingOutcome.Success, ToPublicStatus(transaction.Status));
    }

    public async Task<CopilotBindingOutcome> DisconnectAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        ProjectId projectId,
        CancellationToken ct = default)
    {
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
            return CopilotBindingOutcome.HumanEntraSubjectRequired;
        var explicitOwner = await IsExplicitOwnerAsync(projectId, caller.EntraObjectId!, ct).ConfigureAwait(false);
        var platformAdmin = caller.PlatformRoles.Contains(PlatformRoles.PlatformAdmin, StringComparer.Ordinal);
        if (!explicitOwner && !platformAdmin)
            return CopilotBindingOutcome.ProjectOwnerRequired;

        RepoAppCredentialReference? reference;
        try
        {
            reference = await persistence.RevokeCopilotBindingAsync(
                projectId.ToString(),
                CreateAudit(caller.EntraObjectId!, projectId, GitHubAuditOutcome.Succeeded, GitHubAuditReasonCode.None, null),
                ct).ConfigureAwait(false);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return CopilotBindingOutcome.GitHubBindingUnavailable;
        }
        if (reference is null)
            return CopilotBindingOutcome.GitHubBindingUnavailable;
        try
        {
            var secret = await secretStore.GetSecretAsync(reference.CredentialReference, ct).ConfigureAwait(false);
            if (secret.Found)
                await RevokeWithProviderAsync(DeserializeCredential(secret.Value)?.AccessToken, ct).ConfigureAwait(false);
            await WriteTombstoneAsync(reference.CredentialReference, ct).ConfigureAwait(false);
        }
        catch
        {
            // The durable revocation is already complete; best-effort provider notification is not an authorization retry.
        }
        return CopilotBindingOutcome.Success;
    }

    public string GetCallbackRedirect(CopilotBindingOutcome outcome)
    {
        var frontend = (configuration["Auth:CopilotApp:FrontendUrl"] ?? "http://localhost:5173").TrimEnd('/');
        return $"{frontend}{ReturnRoutes["projects"]}?copilot_app_auth={ToStateCode(outcome)}";
    }

    public static void SetCallbackCookie(HttpContext context, string value) =>
        context.Response.Cookies.Append(CookieName, value, CookieOptions());
    public static string? ReadCallbackCookie(HttpContext context) =>
        context.Request.Cookies.TryGetValue(CookieName, out var value) ? value : null;
    public static void ClearCallbackCookie(HttpContext context) =>
        context.Response.Cookies.Append(CookieName, string.Empty, CookieOptions(DateTimeOffset.UnixEpoch));
    public static string CreateS256Challenge(string verifier) =>
        ToBase64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string ToStateCode(CopilotBindingOutcome outcome) => outcome switch
    {
        CopilotBindingOutcome.HumanEntraSubjectRequired => "human_entra_subject_required",
        CopilotBindingOutcome.ProjectOwnerRequired => "project_owner_required",
        CopilotBindingOutcome.AuthorizationTransactionInvalid => "authorization_transaction_invalid",
        CopilotBindingOutcome.AuthorizationTransactionConsumed => "authorization_transaction_consumed",
        CopilotBindingOutcome.GitHubBindingUnavailable => "github_binding_unavailable",
        _ => "success",
    };

    private async Task<CopilotBindingOutcome> ClaimAndCompleteAsync(
        CopilotAuthorizationTransaction transaction,
        ProjectId projectId,
        string? code,
        CancellationToken ct)
    {
        var claimed = await persistence.ClaimAuthorizationAsync(
            transaction.State, transaction.EntraObjectId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        if (claimed != AuthorizationClaimResult.Claimed)
            return claimed == AuthorizationClaimResult.Consumed
                ? CopilotBindingOutcome.AuthorizationTransactionConsumed
                : CopilotBindingOutcome.AuthorizationTransactionInvalid;
        if (!await IsExplicitOwnerAsync(projectId, transaction.EntraObjectId, ct).ConfigureAwait(false))
        {
            await CompleteFailureAsync(transaction, projectId, GitHubAuditReasonCode.TransactionInvalid, ct).ConfigureAwait(false);
            return CopilotBindingOutcome.ProjectOwnerRequired;
        }

        string? credentialReference = null;
        try
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException();
            var verifier = await secretStore.GetSecretAsync(transaction.PkceVerifierProtected, ct).ConfigureAwait(false);
            if (!verifier.Found || string.IsNullOrWhiteSpace(verifier.Value))
                throw new InvalidOperationException();
            var credential = await ExchangeCodeAsync(code, verifier.Value, ct).ConfigureAwait(false);
            await WriteTombstoneAsync(transaction.PkceVerifierProtected, ct).ConfigureAwait(false);
            if (credential is null)
                throw new InvalidOperationException();

            var version = CreateRandomValue();
            credentialReference = $"copilot-app-project-{projectId}-{version}";
            await secretStore.SetSecretAsync(
                credentialReference,
                JsonSerializer.Serialize(credential with { Status = "signed-in" }),
                ct: ct).ConfigureAwait(false);
            var completed = await persistence.CompleteCopilotAuthorizationAsync(
                transaction.State,
                new ProjectCopilotBindingRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ProjectId = projectId.ToString(),
                    EntraObjectId = transaction.EntraObjectId,
                    CredentialReference = credentialReference,
                    CredentialVersion = version,
                    GrantDigest = CreateGrantDigest(projectId, version),
                    Status = GitHubBindingStatus.Active,
                    BoundAt = DateTimeOffset.UtcNow,
                },
                CreateAudit(transaction.EntraObjectId, projectId, GitHubAuditOutcome.Succeeded, GitHubAuditReasonCode.None, version),
                ct).ConfigureAwait(false);
            if (!completed)
                throw new InvalidOperationException();
            return CopilotBindingOutcome.Success;
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(credentialReference))
                await WriteTombstoneAsync(credentialReference, CancellationToken.None).ConfigureAwait(false);
            await WriteTombstoneAsync(transaction.PkceVerifierProtected, CancellationToken.None).ConfigureAwait(false);
            await CompleteFailureAsync(transaction, projectId, GitHubAuditReasonCode.BindingUnavailable, CancellationToken.None).ConfigureAwait(false);
            return CopilotBindingOutcome.GitHubBindingUnavailable;
        }
    }

    private async Task CompleteFailureAsync(
        CopilotAuthorizationTransaction transaction,
        ProjectId projectId,
        GitHubAuditReasonCode reason,
        CancellationToken ct) =>
        await persistence.CompleteCopilotAuthorizationFailureAsync(
            transaction.State,
            CreateAudit(transaction.EntraObjectId, projectId, GitHubAuditOutcome.Failed, reason, null),
            ct).ConfigureAwait(false);

    private async Task<bool> IsExplicitOwnerAsync(ProjectId projectId, string entraObjectId, CancellationToken ct) =>
        (await roleAssignments.GetAsync(projectId, entraObjectId, ct).ConfigureAwait(false))?.Role == ProjectRole.Owner;

    private bool IsValidTransaction(
        CopilotAuthorizationTransaction? transaction,
        string entraObjectId,
        ProjectId requestedProjectId,
        string? callbackCookie) =>
        transaction is not null &&
        string.Equals(transaction.EntraObjectId, entraObjectId, StringComparison.Ordinal) &&
        string.Equals(transaction.ProjectId, requestedProjectId.ToString(), StringComparison.Ordinal) &&
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() <= transaction.ExpiresAtUnixMilliseconds &&
        !string.IsNullOrWhiteSpace(callbackCookie) &&
        FixedTimeCookieHashEquals(transaction.CallbackCookieHash, callbackCookie);

    private bool IsConfigurationValid() =>
        !string.IsNullOrWhiteSpace(_clientId) &&
        !string.IsNullOrWhiteSpace(_clientSecret) &&
        !string.IsNullOrWhiteSpace(_callbackUrl) &&
        _callbackUrl.EndsWith("/auth/github/copilot-app/callback", StringComparison.Ordinal) &&
        string.IsNullOrWhiteSpace(configuration["Auth:CopilotApp:PrivateKey"]) &&
        string.IsNullOrWhiteSpace(configuration["Auth:CopilotApp:RepositoryPermissions"]) &&
        !SameConfiguredValue(_clientId, configuration["Auth:RepoApp:ClientId"]) &&
        !SameConfiguredValue(_clientSecret, configuration["Auth:RepoApp:ClientSecret"]) &&
        !SameConfiguredValue(configuration["Auth:CopilotApp:SecretPath"], configuration["Auth:RepoApp:SecretPath"]) &&
        !string.Equals(configuration["Auth:RepoApp:RequestUserAuthorizationDuringInstallation"], "true", StringComparison.OrdinalIgnoreCase);

    private static bool SameConfiguredValue(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) &&
        !string.IsNullOrWhiteSpace(second) &&
        string.Equals(first, second, StringComparison.Ordinal);

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
                ["redirect_uri"] = _callbackUrl!,
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
            return provider is { Error: null, AccessToken: not null } && !string.IsNullOrWhiteSpace(provider.AccessToken)
                ? new("signed-in", provider.AccessToken, provider.RefreshToken)
                : null;
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
            $"{_baseUrl.TrimEnd('/')}/applications/{Uri.EscapeDataString(_clientId!)}/grant")
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
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", Expires = expires,
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
    private static string CreateGrantDigest(ProjectId projectId, string version) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"copilot:{projectId}:{version}"))).ToLowerInvariant();
    private static string ToPublicStatus(GitHubAuthorizationStatus status) => status switch
    {
        GitHubAuthorizationStatus.Pending or GitHubAuthorizationStatus.Redeeming => "pending",
        GitHubAuthorizationStatus.Completed => "completed",
        GitHubAuthorizationStatus.Expired => "expired",
        _ => "failed",
    };
    private async Task WriteTombstoneAsync(string reference, CancellationToken ct) =>
        await secretStore.SetSecretAsync(reference, """{"status":"revoked"}""", ct: ct).ConfigureAwait(false);
    private static CopilotCredential? DeserializeCredential(string? value)
    {
        try { return string.IsNullOrWhiteSpace(value) ? null : JsonSerializer.Deserialize<CopilotCredential>(value); }
        catch (JsonException) { return null; }
    }
    private static GitHubAuditRecord CreateAudit(
        string entraObjectId, ProjectId projectId, GitHubAuditOutcome outcome, GitHubAuditReasonCode reason, string? version) =>
        new()
        {
            EntraObjectId = entraObjectId, ActorKind = GitHubAuditActorKind.HumanEntraSubject,
            Action = GitHubAuditAction.BindingChanged, ResourceId = projectId.ToString(),
            AppKind = GitHubAppKind.Copilot, Purpose = GitHubAuthorizationPurpose.InteractiveCopilot,
            Outcome = outcome, ReasonCode = reason, CorrelationId = Guid.NewGuid().ToString("N"),
            OccurredAt = DateTimeOffset.UtcNow, CredentialVersionOrDigest = version,
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
    private sealed record CopilotCredential(string? Status, string? AccessToken, string? RefreshToken);
    private sealed class ProviderTokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("error")] public string? Error { get; init; }
    }
}
