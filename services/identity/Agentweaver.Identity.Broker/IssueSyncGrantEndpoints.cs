using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Secrets.AzureKeyVault;
using Agentweaver.SourceControl;
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

namespace Agentweaver.Identity.Broker;

internal static class IssueSyncGrantEndpoints
{
    internal static void MapIssueSyncGrants(this WebApplication app, bool enabled)
    {
        if (!enabled)
            return;
        var group = app.MapGroup("/api/projects/{projectId}/issue-sync/grants")
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme
            });
        group.MapPost("/", (string projectId, CreateIssueSyncGrant input, HttpContext context,
            IssueSyncGrantAuthority authority) => ExecuteAsync(context, async () =>
            await authority.CreateAsync(await ReadOwnerAsync<IssueSyncProjectProof>(context, projectId, "issue-sync-authority"), input.SelectionCode,
                context.RequestAborted)));
        group.MapGet("/{grantId:guid}", (string projectId, Guid grantId, HttpContext context,
            IssueSyncGrantAuthority authority) => ExecuteAsync(context, async () =>
        {
            var owner = await ReadOwnerAsync<IssueSyncOwnerProof>(context, projectId, "issue-sync-owner");
            var grant = await authority.RequireAsync(grantId, context.RequestAborted);
            if (grant.ProjectId != projectId || grant.TenantId != owner.TenantId)
                throw new RuntimeAuthorizationException("issue_sync_grant_scope_denied");
            return IssueSyncGrantAuthority.Receipt(grant);
        }));
        group.MapPost("/{grantId:guid}/revoke", (string projectId, Guid grantId, HttpContext context,
            IssueSyncGrantAuthority authority) => ExecuteAsync(context, async () =>
        {
            await authority.RevokeAsync(grantId,
                await ReadOwnerAsync<IssueSyncRevocationProof>(context, projectId, "issue-sync-grant-revocation"), context.RequestAborted);
            return new { revoked = true };
        }));
        app.MapPost("/internal/issue-sync/grants/{grantId:guid}/token",
            (Guid grantId, HttpContext context, IssueSyncGrantAuthority authority,
                GitHubRepoAppConnectionService connections, GitHubAppInstallationTokenIssuer issuer) =>
            ExecuteAsync(context, async () =>
            {
                IssueSyncServiceTransport.RequirePeer(context, IssueSyncGrantAuthority.WorkerChannel);
                var grant = await authority.RequireAsync(grantId, context.RequestAborted);
                var credential = await connections.MintIssueSyncTokenAsync(grant, authority, issuer, context.RequestAborted);
                try
                {
                    return new IssueSyncCredentialResponse(IssueSyncGrantAuthority.Receipt(grant),
                        credential.Credential.GetValue(), credential.Credential.ExpiresAt, credential.IssueWriteGranted);
                }
                finally { credential.Credential.Invalidate(); }
            })).AllowAnonymous();
        app.MapPost("/internal/issue-sync/grants/{grantId:guid}/validate",
            (Guid grantId, HttpContext context, IssueSyncGrantAuthority authority) =>
            ExecuteAsync(context, async () =>
            {
                IssueSyncServiceTransport.RequirePeer(context, IssueSyncGrantAuthority.WorkerChannel);
                return IssueSyncGrantAuthority.Receipt(await authority.RequireAsync(grantId, context.RequestAborted));
            })).AllowAnonymous();
    }

    private static async Task<T> ReadOwnerAsync<T>(HttpContext context, string projectId, string proofRoute) where T : class
    {
        var expiry = context.User.GetExpirationDate();
        var header = context.Request.Headers.Authorization.ToString();
        if (expiry is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            header.Length is <= 7 or > 16391 || header[7..].Any(char.IsWhiteSpace) ||
            context.Request.Headers[ProjectAuthorizationContextContract.TenantSelectorHeader].Count > 1)
            throw new RuntimeAuthorizationException("issue_sync_actor_invalid");
        var services = context.RequestServices;
        var identity = services.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(IssueSyncGrantAuthority.ProjectsChannel);
        var bearer = new SecretCredential(header[7..], expiry.Value, services.GetRequiredService<TimeProvider>());
        try
        {
            return await RuntimeOwnerHttpTransport.SendAsync<T>(
                services.GetRequiredService<IHttpClientFactory>().CreateClient(IssueSyncGrantAuthority.ProjectsChannel),
                identity.PeerOwnerAddress, $"/api/projects/{Uri.EscapeDataString(projectId)}/{proofRoute}",
                new RuntimeActorAuthorization(bearer,
                    context.Request.Headers[ProjectAuthorizationContextContract.TenantSelectorHeader].SingleOrDefault()),
                null, context.RequestAborted);
        }
        finally { bearer.Invalidate(); }
    }

    private static async Task ExecuteAsync<T>(HttpContext context, Func<Task<T>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { await Results.Json(await action()).ExecuteAsync(context); }
        catch (RuntimeAuthorizationException error)
        { await Error(context, error.Code, 403); }
        catch (GitHubRepoAppConnectionException error)
        { await Error(context, "issue_sync_connection_" + error.Failure.ToString().ToLowerInvariant(), 409); }
        catch (GitHubRepoAppProviderException)
        { await Error(context, "issue_sync_github_unavailable", 503); }
        catch (SourceControlOperationException)
        { await Error(context, "issue_sync_token_unavailable", 503); }
        catch (AzureKeyVaultSecretException)
        { await Error(context, "issue_sync_app_key_unavailable", 503); }
        catch (HttpRequestException)
        { await Error(context, "issue_sync_authority_unavailable", 503); }
        catch (System.Text.Json.JsonException)
        { await Error(context, "issue_sync_authority_response_invalid", 503); }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        { await Error(context, "issue_sync_persistence_unavailable", 503); }
        catch (ArgumentException)
        { await Error(context, "issue_sync_request_invalid", 400); }
    }

    private static Task Error(HttpContext context, string error, int status) =>
        Results.Json(new { error }, statusCode: status).ExecuteAsync(context);
}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(
    System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateIssueSyncGrant(string SelectionCode)
{
    public override string ToString() => nameof(CreateIssueSyncGrant) + " [REDACTED]";
}
