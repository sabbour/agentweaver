using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Api.Webhooks;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

public enum RepoAppInstallationAuthorizationOutcome
{
    Success,
    HumanEntraSubjectRequired,
    ProjectOwnerRequired,
    RepositoryNotConnected,
    InstallationRequestPending,
    AuthorizationTransactionInvalid,
    AuthorizationTransactionConsumed,
    GitHubBindingUnavailable,
    InstallationConflict,
    PermissionChanged,
    RepositoryNotFoundInInstallation,
}

public sealed record RepoAppInstallationAuthorizationBeginResult(
    RepoAppInstallationAuthorizationOutcome Outcome,
    string? InstallationUrl,
    string? TransactionId,
    DateTimeOffset? ExpiresAt)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CallbackCookie { get; init; }
}

internal sealed record RepoAppInstallationCallbackResult(
    RepoAppInstallationAuthorizationOutcome Outcome,
    string? ProjectId);

/// <summary>
/// Binds a GitHub App installation to a project. Unlike the Copilot App and Repo App user-authorization
/// flows, GitHub's installation flow never exchanges an authorization code — the browser round-trips
/// through <c>github.com/apps/&lt;slug&gt;/installations/new?state=…</c> and back to this API's configured
/// Setup URL with only <c>installation_id</c> and <c>setup_action</c>. This service resolves the caller's
/// project from the signed, single-use `state` transaction (never from a client-submitted project id),
/// resolves the exact numeric repository via the live installation, and calls
/// <see cref="RepoAppInstallationLifecycleService.BindAsync"/> to create the durable installation and
/// repository-grant records production runs need for unattended repository access.
/// </summary>
public sealed class RepoAppInstallationAuthorizationService(
    IConfiguration configuration,
    GitHubConnectionsPersistenceStore persistence,
    IProjectStore projectStore,
    IProjectRoleAssignmentStore roleAssignments,
    RepoAppInstallationTokenService tokenService,
    MemoryDbContext db,
    ILogger<RepoAppInstallationAuthorizationService> logger)
{
    private const string CookieName = "__Host-agentweaver-repo-app-install-auth";
    private static readonly TimeSpan TransactionLifetime = TimeSpan.FromMinutes(10);

    private readonly string _baseUrl = configuration["Auth:RepoApp:BaseUrl"] ?? "https://github.com";
    private readonly string? _slug = configuration["Auth:RepoApp:Slug"];

    public async Task<RepoAppInstallationAuthorizationBeginResult> BeginAsync(
        CallerContext caller,
        ClaimsPrincipal principal,
        ProjectId projectId,
        CancellationToken ct = default)
    {
        if (HumanEntraSubjectAuthorization.Evaluate(caller, principal) != HumanEntraSubjectState.Allowed)
            return new(RepoAppInstallationAuthorizationOutcome.HumanEntraSubjectRequired, null, null, null);
        if (!await IsExplicitOwnerAsync(projectId, caller.EntraObjectId!, ct).ConfigureAwait(false))
            return new(RepoAppInstallationAuthorizationOutcome.ProjectOwnerRequired, null, null, null);
        if (string.IsNullOrWhiteSpace(_slug))
            return new(RepoAppInstallationAuthorizationOutcome.GitHubBindingUnavailable, null, null, null);

        var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(project?.Origin.SourceRepository))
            return new(RepoAppInstallationAuthorizationOutcome.RepositoryNotConnected, null, null, null);

        var state = CreateRandomValue();
        var transactionId = GitHubConnectionsPersistenceStore.CreateExternalTransactionId();
        var cookie = CreateRandomValue();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(TransactionLifetime);
        try
        {
            await persistence.AddAuthorizationAsync(new GitHubAuthorizationRecord
            {
                State = state,
                ExternalTransactionId = transactionId,
                AppKind = GitHubAppKind.Repo,
                Purpose = GitHubAuthorizationPurpose.UnattendedRepositoryInstallation,
                EntraObjectId = caller.EntraObjectId!,
                ProjectId = projectId.ToString(),
                ExpiresAtUnixMilliseconds = expiresAt.ToUnixTimeMilliseconds(),
                ReturnRouteKey = "projects",
                PkceVerifierProtected = "",
                CallbackCookieHash = HashCookie(cookie),
                Status = GitHubAuthorizationStatus.Pending,
                CreatedAt = now,
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            return new(RepoAppInstallationAuthorizationOutcome.GitHubBindingUnavailable, null, null, null);
        }

        return new(RepoAppInstallationAuthorizationOutcome.Success, BuildInstallationUrl(state), transactionId, expiresAt)
        {
            CallbackCookie = cookie,
        };
    }

    /// <summary>
    /// GitHub's Setup URL redirect has no bearer header, so this validates the one-time callback
    /// cookie issued at the authenticated begin request and dispatches purely by the persisted,
    /// single-use `state` transaction. It never accepts a project id or Entra subject from GitHub.
    /// </summary>
    internal async Task<RepoAppInstallationCallbackResult> CompleteBrowserCallbackAsync(
        string? browserSessionId,
        string? browserEntraObjectId,
        long? installationId,
        string? setupAction,
        string? state,
        string? callbackCookie,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(state))
            return new(RepoAppInstallationAuthorizationOutcome.AuthorizationTransactionInvalid, null);

        var transaction = await persistence.GetRepoAppInstallationAuthorizationTransactionAsync(state, ct).ConfigureAwait(false);
        if (transaction is null || !ProjectId.TryParse(transaction.ProjectId, out var projectId) ||
            (transaction.BrowserSessionId is not null &&
             (!string.Equals(transaction.BrowserSessionId, browserSessionId, StringComparison.Ordinal) ||
              !string.Equals(transaction.EntraObjectId, browserEntraObjectId, StringComparison.Ordinal))) ||
            string.IsNullOrWhiteSpace(callbackCookie) ||
            !FixedTimeCookieHashEquals(transaction.CallbackCookieHash, callbackCookie))
            return new(RepoAppInstallationAuthorizationOutcome.AuthorizationTransactionInvalid, transaction?.ProjectId);
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > transaction.ExpiresAtUnixMilliseconds)
            return new(RepoAppInstallationAuthorizationOutcome.AuthorizationTransactionInvalid, transaction.ProjectId);

        // An org member without install rights gets a "request" leg with no installation id yet;
        // an owner's later approval is a second, weakly-correlated GitHub redirect (a documented
        // GitHub limitation), so this leaves the transaction pending rather than consuming it.
        if (string.Equals(setupAction, "request", StringComparison.OrdinalIgnoreCase))
            return new(RepoAppInstallationAuthorizationOutcome.InstallationRequestPending, transaction.ProjectId);

        var claimed = await persistence.ClaimAuthorizationAsync(
            transaction.State, transaction.EntraObjectId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        if (claimed != AuthorizationClaimResult.Claimed)
            return new(
                claimed == AuthorizationClaimResult.Consumed
                    ? RepoAppInstallationAuthorizationOutcome.AuthorizationTransactionConsumed
                    : RepoAppInstallationAuthorizationOutcome.AuthorizationTransactionInvalid,
                transaction.ProjectId);

        if (!await IsExplicitOwnerAsync(projectId, transaction.EntraObjectId, ct).ConfigureAwait(false))
        {
            await persistence.CompleteAuthorizationAsync(transaction.State, succeeded: false, ct).ConfigureAwait(false);
            return new(RepoAppInstallationAuthorizationOutcome.ProjectOwnerRequired, transaction.ProjectId);
        }

        var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
        var fullName = project?.Origin.SourceRepository;
        if (installationId is not > 0 || string.IsNullOrWhiteSpace(fullName))
        {
            await persistence.CompleteAuthorizationAsync(transaction.State, succeeded: false, ct).ConfigureAwait(false);
            return new(
                string.IsNullOrWhiteSpace(fullName)
                    ? RepoAppInstallationAuthorizationOutcome.RepositoryNotConnected
                    : RepoAppInstallationAuthorizationOutcome.GitHubBindingUnavailable,
                transaction.ProjectId);
        }

        var repositoryId = await tokenService.ResolveRepositoryIdAsync(installationId.Value, fullName, ct).ConfigureAwait(false);
        if (repositoryId is not > 0)
        {
            await persistence.CompleteAuthorizationAsync(transaction.State, succeeded: false, ct).ConfigureAwait(false);
            logger.LogWarning(
                "Repo App installation {InstallationId} did not grant access to project {ProjectId}'s connected repository.",
                installationId, projectId);
            return new(RepoAppInstallationAuthorizationOutcome.RepositoryNotFoundInInstallation, transaction.ProjectId);
        }

        var authority = await tokenService.GetRepositoryAuthorityAsync(installationId.Value, repositoryId.Value, ct).ConfigureAwait(false);
        if (authority is null)
        {
            await persistence.CompleteAuthorizationAsync(transaction.State, succeeded: false, ct).ConfigureAwait(false);
            return new(RepoAppInstallationAuthorizationOutcome.GitHubBindingUnavailable, transaction.ProjectId);
        }

        var bound = await new RepoAppInstallationLifecycleService(db)
            .BindAsync(projectId.ToString(), authority, ct).ConfigureAwait(false);
        await persistence.CompleteAuthorizationAsync(
            transaction.State, bound == RepoAppInstallationBindingOutcome.Bound, ct).ConfigureAwait(false);
        return new(
            bound switch
            {
                RepoAppInstallationBindingOutcome.Bound => RepoAppInstallationAuthorizationOutcome.Success,
                RepoAppInstallationBindingOutcome.PermissionChanged => RepoAppInstallationAuthorizationOutcome.PermissionChanged,
                _ => RepoAppInstallationAuthorizationOutcome.InstallationConflict,
            },
            transaction.ProjectId);
    }

    public string GetCallbackRedirect(RepoAppInstallationAuthorizationOutcome outcome, string? projectId)
    {
        var frontend = (configuration["Auth:RepoApp:FrontendUrl"] ?? "http://localhost:5173").TrimEnd('/');
        return string.IsNullOrWhiteSpace(projectId)
            ? $"{frontend}/projects?repo_app_install={ToStateCode(outcome)}"
            : $"{frontend}/projects/{Uri.EscapeDataString(projectId)}/settings?section=unattended&repo_app_install={ToStateCode(outcome)}";
    }

    private string BuildInstallationUrl(string state) =>
        $"{_baseUrl.TrimEnd('/')}/apps/{Uri.EscapeDataString(_slug!)}/installations/new?state={Uri.EscapeDataString(state)}";

    public static void SetCallbackCookie(HttpContext context, string value) =>
        context.Response.Cookies.Append(CookieName, value, CookieOptions());
    public static string? ReadCallbackCookie(HttpContext context) =>
        context.Request.Cookies.TryGetValue(CookieName, out var value) ? value : null;
    public static void ClearCallbackCookie(HttpContext context) =>
        context.Response.Cookies.Append(CookieName, string.Empty, CookieOptions(DateTimeOffset.UnixEpoch));

    public static string ToStateCode(RepoAppInstallationAuthorizationOutcome outcome) => outcome switch
    {
        RepoAppInstallationAuthorizationOutcome.HumanEntraSubjectRequired => "human_entra_subject_required",
        RepoAppInstallationAuthorizationOutcome.ProjectOwnerRequired => "project_owner_required",
        RepoAppInstallationAuthorizationOutcome.RepositoryNotConnected => "repository_not_connected",
        RepoAppInstallationAuthorizationOutcome.InstallationRequestPending => "installation_request_pending",
        RepoAppInstallationAuthorizationOutcome.AuthorizationTransactionInvalid => "authorization_transaction_invalid",
        RepoAppInstallationAuthorizationOutcome.AuthorizationTransactionConsumed => "authorization_transaction_consumed",
        RepoAppInstallationAuthorizationOutcome.GitHubBindingUnavailable => "github_binding_unavailable",
        RepoAppInstallationAuthorizationOutcome.InstallationConflict => "installation_conflict",
        RepoAppInstallationAuthorizationOutcome.PermissionChanged => "permission_changed",
        RepoAppInstallationAuthorizationOutcome.RepositoryNotFoundInInstallation => "repository_not_found_in_installation",
        _ => "success",
    };

    private async Task<bool> IsExplicitOwnerAsync(ProjectId projectId, string entraObjectId, CancellationToken ct) =>
        (await roleAssignments.GetAsync(projectId, entraObjectId, ct).ConfigureAwait(false))?.Role == ProjectRole.Owner;

    private static CookieOptions CookieOptions(DateTimeOffset? expires = null) => new()
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", Expires = expires,
        MaxAge = expires is null ? TransactionLifetime : null,
    };
    private static string CreateRandomValue() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string HashCookie(string value) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool FixedTimeCookieHashEquals(string expected, string value)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(expected), SHA256.HashData(Encoding.UTF8.GetBytes(value))); }
        catch (FormatException) { return false; }
    }
}
