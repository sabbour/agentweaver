using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agentweaver.Api.Webhooks;

public enum RepoAppInstallationOutcome { Success, InstallationUnavailable, ConfigurationUnavailable, ProviderUnavailable }
internal enum RepoAppInstallationBindingOutcome { Bound, PermissionChanged, Conflict }

internal sealed record RepoAppInstallationAuthority(
    long InstallationId,
    long RepositoryId,
    string FullNameDisplay,
    IReadOnlyDictionary<string, string> Permissions);
internal sealed record RepoAppInstallationToken(string Value, DateTimeOffset? ExpiresAt);

/// <summary>
/// API-only boundary for a short-lived Repo App JWT and the single-repository installation
/// token it mints. Neither credential is written to persistence, logs, or HTTP responses.
/// </summary>
public sealed class RepoAppInstallationTokenService(
    IConfiguration configuration,
    MemoryDbContext db,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory)
{
    private static readonly TimeSpan JwtLifetime = TimeSpan.FromMinutes(9);
    private static readonly IReadOnlyDictionary<string, string> UnattendedRepositoryPermissionCeilings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contents"] = "write",
            ["pull_requests"] = "write",
        };
    private static readonly IReadOnlyDictionary<string, string> RepositoryMetadataPermissionScope =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["metadata"] = "read",
        };

    public async Task<RepoAppInstallationOutcome> MintForRepositoryAsync(
        long installationId,
        long repositoryId,
        Func<string, DateTimeOffset, Task> useToken,
        CancellationToken ct = default)
    {
        if (installationId <= 0 || repositoryId <= 0)
            return RepoAppInstallationOutcome.InstallationUnavailable;

        var installationActive = await db.GitHubInstallations.AsNoTracking()
            .AnyAsync(x => x.InstallationId == installationId &&
                           x.AppKind == GitHubAppKind.Repo &&
                           x.RevokedAt == null, ct).ConfigureAwait(false);
        var grant = await db.GitHubRepositoryGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.InstallationId == installationId &&
                                       x.RepositoryId == repositoryId &&
                                       x.RevokedAt == null, ct).ConfigureAwait(false);
        if (!installationActive || grant is null)
            return RepoAppInstallationOutcome.InstallationUnavailable;

        var authority = await GetRepositoryAuthorityAsync(installationId, repositoryId, ct).ConfigureAwait(false);
        if (authority is null)
            return RepoAppInstallationOutcome.ProviderUnavailable;
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(grant.PermissionDigest),
                Encoding.UTF8.GetBytes(CreatePermissionDigest(authority.Permissions))))
        {
            await new RepoAppInstallationLifecycleService(db)
                .InvalidateForPermissionChangeAsync(installationId, repositoryId, ct).ConfigureAwait(false);
            return RepoAppInstallationOutcome.InstallationUnavailable;
        }
        if (!TryCreateUnattendedPermissionScope(authority.Permissions, out var requestedPermissions))
            return RepoAppInstallationOutcome.InstallationUnavailable;

        try
        {
            var appJwt = await CreateAppJwtAsync(ct).ConfigureAwait(false);
            if (appJwt is null)
                return RepoAppInstallationOutcome.ConfigurationUnavailable;
            var installationToken = await GetInstallationTokenAsync(
                appJwt, installationId, repositoryId, requestedPermissions, ct).ConfigureAwait(false);
            if (installationToken is null)
                return RepoAppInstallationOutcome.ProviderUnavailable;

            if (installationToken.ExpiresAt is null || installationToken.ExpiresAt <= DateTimeOffset.UtcNow)
                return RepoAppInstallationOutcome.ProviderUnavailable;
            await useToken(installationToken.Value, installationToken.ExpiresAt.Value).ConfigureAwait(false);
            return RepoAppInstallationOutcome.Success;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return RepoAppInstallationOutcome.ProviderUnavailable;
        }
        catch (HttpRequestException)
        {
            return RepoAppInstallationOutcome.ProviderUnavailable;
        }
    }

    public async Task<bool> VerifyRepositoryInstallationAsync(
        long installationId,
        long repositoryId,
        CancellationToken ct = default)
        => await GetRepositoryAuthorityAsync(installationId, repositoryId, ct).ConfigureAwait(false) is not null;

    /// <summary>
    /// Resolves the installation's exact repository authority from GitHub. The request supplies
    /// only numeric identifiers; permissions and the display name are provider-owned values.
    /// </summary>
    internal async Task<RepoAppInstallationAuthority?> GetRepositoryAuthorityAsync(
        long installationId,
        long repositoryId,
        CancellationToken ct = default)
    {
        if (installationId <= 0 || repositoryId <= 0)
            return null;

        var appJwt = await CreateAppJwtAsync(ct).ConfigureAwait(false);
        if (appJwt is null)
            return null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var client = httpClientFactory.CreateClient("github");
            using var installationRequest = CreateGitHubRequest(
                HttpMethod.Get, $"/repositories/{repositoryId}/installation", appJwt);
            using var installationResponse = await client.SendAsync(installationRequest, timeout.Token).ConfigureAwait(false);
            if (!installationResponse.IsSuccessStatusCode)
                return null;
            using var installationDocument = JsonDocument.Parse(
                await installationResponse.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false));
            var installation = installationDocument.RootElement;
            if (!installation.TryGetProperty("id", out var actualInstallation) ||
                !actualInstallation.TryGetInt64(out var actualInstallationId) ||
                actualInstallationId != installationId ||
                !installation.TryGetProperty("repository_selection", out var repositorySelection) ||
                repositorySelection.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(repositorySelection.GetString()) ||
                !installation.TryGetProperty("account", out var account) ||
                account.ValueKind != JsonValueKind.Object ||
                !TryGetNormalizedPermissions(installation, out var permissions))
                return null;

            var metadataToken = await GetInstallationTokenAsync(
                appJwt, installationId, repositoryId, RepositoryMetadataPermissionScope, timeout.Token)
                .ConfigureAwait(false);
            if (metadataToken is null)
                return null;
            using var repositoryRequest = CreateGitHubRequest(
                HttpMethod.Get, $"/repositories/{repositoryId}", metadataToken.Value);
            using var repositoryResponse = await client.SendAsync(repositoryRequest, timeout.Token).ConfigureAwait(false);
            if (!repositoryResponse.IsSuccessStatusCode)
                return null;
            using var repositoryDocument = JsonDocument.Parse(
                await repositoryResponse.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false));
            var repository = repositoryDocument.RootElement;
            if (!repository.TryGetProperty("id", out var actualRepository) ||
                !actualRepository.TryGetInt64(out var actualRepositoryId) ||
                actualRepositoryId != repositoryId ||
                !repository.TryGetProperty("full_name", out var fullName) ||
                fullName.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(fullName.GetString()))
                return null;

            return new RepoAppInstallationAuthority(
                installationId, repositoryId, fullName.GetString()!, permissions);
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

    private async Task<string?> CreateAppJwtAsync(CancellationToken ct)
    {
        if (!long.TryParse(configuration["Auth:RepoApp:AppId"], out var appId) || appId <= 0 ||
            string.IsNullOrWhiteSpace(configuration["Auth:RepoApp:PrivateKeySecretName"]))
            return null;
        var pem = await secretStore.GetSecretAsync(configuration["Auth:RepoApp:PrivateKeySecretName"]!, ct)
            .ConfigureAwait(false);
        if (!pem.Found || string.IsNullOrWhiteSpace(pem.Value))
            return null;
        try
        {
            return CreateAppJwt(appId, pem.Value);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private HttpRequestMessage CreateGitHubRequest(HttpMethod method, string path, string appJwt)
    {
        var request = new HttpRequestMessage(
            method, $"{(configuration["Auth:RepoApp:ApiUrl"] ?? "https://api.github.com").TrimEnd('/')}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
        request.Headers.UserAgent.ParseAdd("Agentweaver/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }

    private async Task<RepoAppInstallationToken?> GetInstallationTokenAsync(
        string appJwt,
        long installationId,
        long repositoryId,
        IReadOnlyDictionary<string, string> permissions,
        CancellationToken ct)
    {
        using var request = CreateGitHubRequest(
            HttpMethod.Post, $"/app/installations/{installationId}/access_tokens", appJwt);
        request.Content = JsonContent.Create(new { repository_ids = new[] { repositoryId }, permissions });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await httpClientFactory.CreateClient("github").SendAsync(request, timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("token", out var token) ||
            string.IsNullOrWhiteSpace(token.GetString()))
            return null;
        DateTimeOffset? expiresAt = document.RootElement.TryGetProperty("expires_at", out var expiresAtElement) &&
                                    DateTimeOffset.TryParse(expiresAtElement.GetString(), out var parsedExpiry)
            ? parsedExpiry
            : null;
        return new(token.GetString()!, expiresAt);
    }

    private static bool TryGetNormalizedPermissions(
        JsonElement installation,
        out IReadOnlyDictionary<string, string> permissions)
    {
        permissions = new Dictionary<string, string>();
        if (!installation.TryGetProperty("permissions", out var source) ||
            source.ValueKind != JsonValueKind.Object)
            return false;

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var permission in source.EnumerateObject())
        {
            if (permission.Value.ValueKind != JsonValueKind.String)
                return false;
            var name = permission.Name.Trim().ToLowerInvariant();
            var value = permission.Value.GetString()?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value) ||
                !normalized.TryAdd(name, value))
                return false;
        }
        permissions = normalized;
        return normalized.Count > 0;
    }

    private static bool TryCreateUnattendedPermissionScope(
        IReadOnlyDictionary<string, string> providerPermissions,
        out IReadOnlyDictionary<string, string> requestedPermissions)
    {
        var requested = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ceiling in UnattendedRepositoryPermissionCeilings)
        {
            if (!providerPermissions.TryGetValue(ceiling.Key, out var actual))
                continue;
            if (!string.Equals(actual, "read", StringComparison.Ordinal) &&
                !string.Equals(actual, "write", StringComparison.Ordinal))
            {
                requestedPermissions = new Dictionary<string, string>();
                return false;
            }
            if (string.Equals(ceiling.Value, "read", StringComparison.Ordinal) &&
                string.Equals(actual, "write", StringComparison.Ordinal))
            {
                requestedPermissions = new Dictionary<string, string>();
                return false;
            }
            requested[ceiling.Key] = actual;
        }
        requestedPermissions = requested;
        return requested.Count > 0;
    }

    internal static string CreateAppJwt(long appId, string pem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var now = DateTime.UtcNow;
        var signingKey = new RsaSecurityKey(rsa)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = appId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = now.Add(JwtLifetime),
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
        });
    }

    internal static string CreatePermissionDigest(IReadOnlyDictionary<string, string> permissions)
    {
        var canonical = string.Join("&", permissions.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{x.Key.Trim().ToLowerInvariant()}={x.Value.Trim().ToLowerInvariant()}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

/// <summary>Durable installation/grant state machine for authenticated Repo App deliveries.</summary>
public sealed class RepoAppInstallationLifecycleService(MemoryDbContext db)
{
    private const string CompletedEventPrefix = "completed/";
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(10);

    public async Task<(bool Claimed, IReadOnlyList<string> ProjectIds)> ProcessAsync(
        string deliveryId,
        string eventName,
        GitHubWebhookPayload payload,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deliveryId))
            return (false, []);

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.GitHubLifecycleDeliveries.Add(new GitHubLifecycleDeliveryRecord
        {
            DeliveryId = deliveryId,
            EventName = eventName,
            InstallationId = payload.Installation?.Id,
            RepositoryId = payload.Repository?.Id,
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            var leaseExpiresBefore = DateTimeOffset.UtcNow.Subtract(ProcessingLease);
            var abandoned = await db.GitHubLifecycleDeliveries.FindAsync([deliveryId], ct).ConfigureAwait(false);
            if (abandoned is null || abandoned.EventName != eventName || abandoned.ReceivedAt >= leaseExpiresBefore)
                return (false, []);
            var reclaimed = await db.GitHubLifecycleDeliveries
                .Where(x => x.DeliveryId == deliveryId &&
                            x.EventName == abandoned.EventName &&
                            x.ReceivedAt == abandoned.ReceivedAt)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            if (reclaimed != 1)
                return (false, []);
            return await ProcessAsync(deliveryId, eventName, payload, ct).ConfigureAwait(false);
        }

        var installationId = (payload.Installation?.Id).GetValueOrDefault();
        if (installationId > 0 && eventName is "installation" or "installation_repositories")
        {
            await ApplyLifecycleAsync(installationId, payload, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        db.ChangeTracker.Clear();
        var installationActive = installationId > 0 && await db.GitHubInstallations.AsNoTracking()
            .AnyAsync(x => x.InstallationId == installationId &&
                           x.AppKind == GitHubAppKind.Repo &&
                           x.RevokedAt == null, ct).ConfigureAwait(false);
        var projectIds = installationActive && payload.Repository?.Id is > 0
            ? await db.GitHubRepositoryGrants.AsNoTracking()
                .Where(x => x.InstallationId == installationId &&
                            x.RepositoryId == payload.Repository.Id &&
                            x.RevokedAt == null)
                .Select(x => x.ProjectId).ToListAsync(ct).ConfigureAwait(false)
            : [];
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return (true, projectIds);
    }

    /// <summary>
    /// Releases a claim only when downstream dispatch did not complete, allowing GitHub to retry.
    /// The dispatch path has its own delivery-id idempotency guard.
    /// </summary>
    public async Task ReleaseAsync(string deliveryId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await db.GitHubLifecycleDeliveries.Where(x => x.DeliveryId == deliveryId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public Task<bool> IsCompletedAsync(string deliveryId, CancellationToken ct = default) =>
        db.GitHubLifecycleDeliveries.AsNoTracking()
            .AnyAsync(x => x.DeliveryId == deliveryId &&
                           x.EventName.StartsWith(CompletedEventPrefix), ct);

    public async Task<bool> CompleteAsync(string deliveryId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        var current = await db.GitHubLifecycleDeliveries.AsNoTracking()
            .Where(x => x.DeliveryId == deliveryId)
            .Select(x => x.EventName).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (current is null)
            return false;
        if (current.StartsWith(CompletedEventPrefix, StringComparison.Ordinal))
            return true;
        return await db.GitHubLifecycleDeliveries.Where(x => x.DeliveryId == deliveryId && x.EventName == current)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.EventName, $"{CompletedEventPrefix}{current}"), ct)
            .ConfigureAwait(false) == 1;
    }

    internal async Task<RepoAppInstallationBindingOutcome> BindAsync(
        string projectId,
        RepoAppInstallationAuthority authority,
        CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var installation = await db.GitHubInstallations.FindAsync([authority.InstallationId], ct).ConfigureAwait(false);
        if (installation is not null && installation.ProjectId is not null &&
            !string.Equals(installation.ProjectId, projectId, StringComparison.Ordinal))
            return RepoAppInstallationBindingOutcome.Conflict;
        if (installation is null)
            db.GitHubInstallations.Add(new GitHubInstallationRecord
            {
                InstallationId = authority.InstallationId, AppKind = GitHubAppKind.Repo, ProjectId = projectId, CreatedAt = now,
            });
        else
        {
            installation.ProjectId = projectId;
            installation.RevokedAt = null;
        }

        var grant = await db.GitHubRepositoryGrants.FindAsync(
            [authority.InstallationId, authority.RepositoryId], ct).ConfigureAwait(false);
        if (grant is not null && !string.Equals(grant.ProjectId, projectId, StringComparison.Ordinal))
            return RepoAppInstallationBindingOutcome.Conflict;
        if (grant is null)
            db.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
            {
                InstallationId = authority.InstallationId, RepositoryId = authority.RepositoryId, ProjectId = projectId,
                FullNameDisplay = authority.FullNameDisplay,
                PermissionDigest = RepoAppInstallationTokenService.CreatePermissionDigest(authority.Permissions),
                GrantedAt = now,
            });
        else
        {
            var permissionDigest = RepoAppInstallationTokenService.CreatePermissionDigest(authority.Permissions);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(grant.PermissionDigest), Encoding.UTF8.GetBytes(permissionDigest)))
            {
                grant.FullNameDisplay = authority.FullNameDisplay;
                grant.RevokedAt = now;
                await InvalidateForPermissionChangeAsync(authority.InstallationId, authority.RepositoryId, ct)
                    .ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return RepoAppInstallationBindingOutcome.PermissionChanged;
            }
            grant.FullNameDisplay = authority.FullNameDisplay;
            grant.RevokedAt = null;
        }
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return RepoAppInstallationBindingOutcome.Bound;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return RepoAppInstallationBindingOutcome.Conflict;
        }
    }

    public async Task InvalidateForPermissionChangeAsync(
        long installationId,
        long repositoryId,
        CancellationToken ct = default)
    {
        var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        var now = DateTimeOffset.UtcNow;
        try
        {
            await db.GitHubRepositoryGrants
                .Where(x => x.InstallationId == installationId &&
                            x.RepositoryId == repositoryId &&
                            x.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct).ConfigureAwait(false);
            await db.AutomationActivations
                .Where(x => x.InstallationId == installationId &&
                            x.RepositoryId == repositoryId &&
                            x.Status != AutomationActivationStatus.Invalidated)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, AutomationActivationStatus.Invalidated)
                    .SetProperty(x => x.InvalidatedAt, now), ct).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ApplyLifecycleAsync(long installationId, GitHubWebhookPayload payload, CancellationToken ct)
    {
        var installation = await db.GitHubInstallations.FindAsync([installationId], ct).ConfigureAwait(false);
        if (installation is null)
            return; // A delivery can never create a project binding from untrusted display data.

        var now = DateTimeOffset.UtcNow;
        if (payload.Action is "deleted" or "suspend")
        {
            installation.RevokedAt = now;
            await db.GitHubRepositoryGrants.Where(x => x.InstallationId == installationId && x.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct).ConfigureAwait(false);
            return;
        }
        if (payload.Action is "created" or "unsuspend")
            installation.RevokedAt = null;

        foreach (var repository in payload.RepositoriesRemoved ?? [])
        {
            if (repository.Id > 0)
                await db.GitHubRepositoryGrants.Where(x => x.InstallationId == installationId && x.RepositoryId == repository.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct).ConfigureAwait(false);
        }
    }
}
