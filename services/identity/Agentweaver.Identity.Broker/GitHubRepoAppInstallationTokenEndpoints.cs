using System.Security.Claims;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Secrets.AzureKeyVault;
using Agentweaver.SourceControl;
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Agentweaver.Identity.Broker;

internal static class GitHubRepoAppInstallationTokenEndpoints
{
    internal static void MapGitHubRepoAppInstallationTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/source-control/github-app/installations/token", MintAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme
            });
    }

    private static async Task MintAsync(
        HttpContext context,
        GitHubRepoAppInstallationTokenInput input,
        GitHubRepoAppConnectionService connectionService,
        IdentityGrantAuthority grantAuthority,
        GitHubAppInstallationTokenIssuer tokenIssuer,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        var actorId = SingleClaim(context.User, Claims.Subject);
        var projectId = SingleClaim(context.User, SecretRedemptionEndpoints.ProjectIdClaim);
        var runId = SingleClaim(context.User, SecretRedemptionEndpoints.RunIdClaim);
        var bearer = context.Request.Headers.Authorization.ToString();
        var expiresAt = context.User.GetExpirationDate();
        if (context.User.Identity?.IsAuthenticated != true ||
            !Guid.TryParseExact(actorId, "D", out var ownerId) ||
            ownerId == Guid.Empty ||
            projectId is null ||
            runId is null ||
            expiresAt is null ||
            !bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            bearer.Length is < 8 or > 16_391 ||
            bearer[7..].Any(char.IsWhiteSpace) ||
            context.Request.Headers["X-Agentweaver-Tenant"].Count > 1)
        {
            await DenyAsync(context, "caller_invalid", StatusCodes.Status401Unauthorized).ConfigureAwait(false);
            return;
        }

        GitHubRepoAppInstallationTokenResult? result = null;
        try
        {
            result = await connectionService.MintInstallationTokenAsync(
                ownerId,
                projectId,
                runId,
                new GitHubRepoAppInstallationTokenRequest(
                    input.SelectionCode,
                    input.SelectionHash,
                    input.ConnectionId,
                    input.ConnectionRevision,
                    input.InstallationId,
                    input.RepositoryId,
                    input.PermissionDigest,
                    input.ExpectedRepositoryFullName ?? string.Empty),
                grantAuthority,
                tokenIssuer,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await Results.Json(new GitHubRepoAppInstallationTokenResponse(
                result.Credential.Credential.GetValue(),
                result.Credential.Credential.ExpiresAt,
                result.ConnectionId,
                result.ConnectionRevision,
                result.InstallationId,
                result.RepositoryId,
                result.RepositoryFullName,
                result.DefaultBranch,
                result.IsPrivate,
                result.Credential.PermissionDigest,
                result.SelectionHash)).ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (GitHubRepoAppConnectionException error)
        {
            var (status, code) = error.Failure switch
            {
                GitHubRepoAppConnectionFailure.RunBindingInvalid =>
                    (StatusCodes.Status403Forbidden, "run_binding_invalid"),
                GitHubRepoAppConnectionFailure.AuthorizationInvalid =>
                    (StatusCodes.Status400BadRequest, "selection_invalid"),
                GitHubRepoAppConnectionFailure.PermissionsChanged =>
                    (StatusCodes.Status409Conflict, "permissions_changed"),
                GitHubRepoAppConnectionFailure.NotConnected =>
                    (StatusCodes.Status409Conflict, "connection_unavailable"),
                GitHubRepoAppConnectionFailure.Revoked =>
                    (StatusCodes.Status409Conflict, "connection_revoked"),
                GitHubRepoAppConnectionFailure.RotationUncertain =>
                    (StatusCodes.Status409Conflict, "rotation_uncertain"),
                GitHubRepoAppConnectionFailure.RefreshInProgress =>
                    (StatusCodes.Status503ServiceUnavailable, "refresh_in_progress"),
                GitHubRepoAppConnectionFailure.ProviderUnavailable =>
                    (StatusCodes.Status502BadGateway, "provider_unavailable"),
                _ => (StatusCodes.Status404NotFound, "repository_unavailable")
            };
            await DenyAsync(context, code, status).ConfigureAwait(false);
        }
        catch (SourceControlOperationException)
        {
            await DenyAsync(context, "installation_token_unavailable", StatusCodes.Status502BadGateway)
                .ConfigureAwait(false);
        }
        catch (AzureKeyVaultSecretException)
        {
            await DenyAsync(context, "app_key_unavailable", StatusCodes.Status502BadGateway)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            await DenyAsync(context, "request_invalid", StatusCodes.Status400BadRequest).ConfigureAwait(false);
        }
        finally
        {
            result?.Credential.Credential.Invalidate();
        }
    }

    private static string? SingleClaim(ClaimsPrincipal principal, string type)
    {
        var claims = principal.FindAll(type).Take(2).ToArray();
        return claims.Length == 1 && !string.IsNullOrWhiteSpace(claims[0].Value)
            ? claims[0].Value
            : null;
    }

    private static Task DenyAsync(HttpContext context, string error, int statusCode) =>
        Results.Json(new { error }, statusCode: statusCode).ExecuteAsync(context);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GitHubRepoAppInstallationTokenInput(
    string? SelectionCode,
    string? SelectionHash,
    string? ConnectionId,
    long? ConnectionRevision,
    long? InstallationId,
    long? RepositoryId,
    string? PermissionDigest,
    string? ExpectedRepositoryFullName);

internal sealed record GitHubRepoAppInstallationTokenResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string ConnectionId,
    long ConnectionRevision,
    long InstallationId,
    long RepositoryId,
    string RepositoryFullName,
    string DefaultBranch,
    bool IsPrivate,
    string PermissionDigest,
    string SelectionHash);
