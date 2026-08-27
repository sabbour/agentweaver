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

    public async Task<RepoAppInstallationOutcome> MintForRepositoryAsync(
        long installationId,
        long repositoryId,
        IReadOnlyDictionary<string, string> permissions,
        Func<string, DateTimeOffset, Task> useToken,
        CancellationToken ct = default)
    {
        if (installationId <= 0 || repositoryId <= 0 || permissions.Count == 0)
            return RepoAppInstallationOutcome.InstallationUnavailable;

        var permissionDigest = CreatePermissionDigest(permissions);
        var installationActive = await db.GitHubInstallations.AsNoTracking()
            .AnyAsync(x => x.InstallationId == installationId &&
                           x.AppKind == GitHubAppKind.Repo &&
                           x.RevokedAt == null, ct).ConfigureAwait(false);
        var grant = await db.GitHubRepositoryGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.InstallationId == installationId &&
                                       x.RepositoryId == repositoryId &&
                                       x.RevokedAt == null, ct).ConfigureAwait(false);
        if (!installationActive || grant is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(grant.PermissionDigest), Encoding.UTF8.GetBytes(permissionDigest)))
            return RepoAppInstallationOutcome.InstallationUnavailable;

        if (!long.TryParse(configuration["Auth:RepoApp:AppId"], out var appId) || appId <= 0 ||
            string.IsNullOrWhiteSpace(configuration["Auth:RepoApp:PrivateKeySecretName"]))
            return RepoAppInstallationOutcome.ConfigurationUnavailable;

        var pem = await secretStore.GetSecretAsync(configuration["Auth:RepoApp:PrivateKeySecretName"]!, ct)
            .ConfigureAwait(false);
        if (!pem.Found || string.IsNullOrWhiteSpace(pem.Value))
            return RepoAppInstallationOutcome.ConfigurationUnavailable;

        string appJwt;
        try
        {
            appJwt = CreateAppJwt(appId, pem.Value);
        }

        catch (CryptographicException)
        {
            return RepoAppInstallationOutcome.ConfigurationUnavailable;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{(configuration["Auth:RepoApp:ApiUrl"] ?? "https://api.github.com").TrimEnd('/')}/app/installations/{installationId}/access_tokens");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
            request.Headers.UserAgent.ParseAdd("Agentweaver/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Content = JsonContent.Create(new
            {
                repository_ids = new[] { repositoryId },
                permissions,
            });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await httpClientFactory.CreateClient("github").SendAsync(request, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return RepoAppInstallationOutcome.ProviderUnavailable;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("token", out var tokenElement) ||
                string.IsNullOrWhiteSpace(tokenElement.GetString()))
                return RepoAppInstallationOutcome.ProviderUnavailable;

            await useToken(tokenElement.GetString()!, DateTimeOffset.UtcNow.AddMinutes(55)).ConfigureAwait(false);
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
    {
        if (installationId <= 0 || repositoryId <= 0 ||
            !long.TryParse(configuration["Auth:RepoApp:AppId"], out var appId) || appId <= 0 ||
            string.IsNullOrWhiteSpace(configuration["Auth:RepoApp:PrivateKeySecretName"]))
            return false;

        var pem = await secretStore.GetSecretAsync(configuration["Auth:RepoApp:PrivateKeySecretName"]!, ct)
            .ConfigureAwait(false);
        if (!pem.Found || string.IsNullOrWhiteSpace(pem.Value))
            return false;

        string appJwt;
        try
        {
            appJwt = CreateAppJwt(appId, pem.Value);
        }
        catch (CryptographicException)
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{(configuration["Auth:RepoApp:ApiUrl"] ?? "https://api.github.com").TrimEnd('/')}/repositories/{repositoryId}/installation");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
            request.Headers.UserAgent.ParseAdd("Agentweaver/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await httpClientFactory.CreateClient("github").SendAsync(request, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;
            using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false));
            return body.RootElement.TryGetProperty("id", out var id) && id.TryGetInt64(out var actualInstallationId)
                && actualInstallationId == installationId;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
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

    public async Task<bool> BindAsync(
        string projectId,
        long installationId,
        long repositoryId,
        string fullNameDisplay,
        IReadOnlyDictionary<string, string> permissions,
        CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var installation = await db.GitHubInstallations.FindAsync([installationId], ct).ConfigureAwait(false);
        if (installation is not null && installation.ProjectId is not null &&
            !string.Equals(installation.ProjectId, projectId, StringComparison.Ordinal))
            return false;
        if (installation is null)
            db.GitHubInstallations.Add(new GitHubInstallationRecord
            {
                InstallationId = installationId, AppKind = GitHubAppKind.Repo, ProjectId = projectId, CreatedAt = now,
            });
        else
        {
            installation.ProjectId = projectId;
            installation.RevokedAt = null;
        }

        var grant = await db.GitHubRepositoryGrants.FindAsync([installationId, repositoryId], ct).ConfigureAwait(false);
        if (grant is not null && !string.Equals(grant.ProjectId, projectId, StringComparison.Ordinal))
            return false;
        if (grant is null)
            db.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
            {
                InstallationId = installationId, RepositoryId = repositoryId, ProjectId = projectId,
                FullNameDisplay = fullNameDisplay, PermissionDigest = RepoAppInstallationTokenService.CreatePermissionDigest(permissions),
                GrantedAt = now,
            });
        else
        {
            grant.FullNameDisplay = fullNameDisplay;
            grant.PermissionDigest = RepoAppInstallationTokenService.CreatePermissionDigest(permissions);
            grant.RevokedAt = null;
        }
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return false;
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
