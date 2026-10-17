using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Exposes the token-protected secret redemption route. It builds the trusted actor from
/// validated token claims, delegates grant checks to <see cref="AuthorizedSecretRedemption"/>,
/// and never returns backend diagnostics from this credential-bearing response.
/// </summary>
public static class SecretRedemptionEndpoints
{
    public const string ProjectIdClaim = "project_id";
    public const string RunIdClaim = "run_id";

    /// <summary>Maps the single authenticated secret redemption endpoint.</summary>
    public static void MapIdentitySecretRedemptionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/secrets/redeem", RedeemAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
            });
    }

    /// <summary>
    /// Redeems only a secret reference, purpose and run that match the authenticated
    /// issuer claims and active grant authority. Returns a generic server error for
    /// unexpected backend failures and invalidates the in-memory credential after writing.
    /// </summary>
    private static async Task RedeemAsync(
        HttpContext context,
        SecretRedemptionInput input,
        IGrantAuthority grantAuthority,
        ISecretRedemption backend,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        var actorId = SingleClaim(context.User, Claims.Subject);
        var projectId = SingleClaim(context.User, ProjectIdClaim);
        var runId = SingleClaim(context.User, RunIdClaim);
        if (actorId is null || projectId is null || runId is null)
        {
            await Results.Unauthorized().ExecuteAsync(context);
            return;
        }

        TrustedActorContext actor;
        try
        {
            actor = new TrustedActorContext(actorId, projectId, runId);
        }
        catch (ArgumentException)
        {
            await Results.Unauthorized().ExecuteAsync(context);
            return;
        }

        SecretCredential? credential = null;
        try
        {
            var request = new SecretRedemptionRequest(
                new SecretRef(input.SecretId ?? string.Empty, input.SecretVersion ?? string.Empty),
                input.Purpose ?? string.Empty,
                input.RunId ?? string.Empty);
            var redemption = new AuthorizedSecretRedemption(actor, grantAuthority, backend, timeProvider);
            credential = await redemption.RedeemAsync(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var result = new SecretRedemptionResult(
                request.Secret.Id,
                request.Secret.Version,
                credential.ExpiresAt,
                credential.GetValue());
            cancellationToken.ThrowIfCancellationRequested();
            await Results.Json(result).ExecuteAsync(context);
        }
        catch (SecretAuthorizationDeniedException)
        {
            await Results.Json(new { error = "not_authorized" }, statusCode: StatusCodes.Status403Forbidden)
                .ExecuteAsync(context);
        }
        catch (ArgumentException)
        {
            await Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Backend errors can contain transport details. Never reflect or log them
            // from this credential-bearing endpoint.
            await Results.Json(new { error = "redemption_failed" }, statusCode: StatusCodes.Status502BadGateway)
                .ExecuteAsync(context);
        }
        finally
        {
            credential?.Invalidate();
        }
    }

    private static string? SingleClaim(System.Security.Claims.ClaimsPrincipal principal, string type)
    {
        var claims = principal.FindAll(type).Take(2).ToArray();
        return claims.Length == 1 && !string.IsNullOrWhiteSpace(claims[0].Value) ? claims[0].Value : null;
    }
}

/// <summary>Strict request contract containing identifiers only; the caller never supplies secret bytes.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SecretRedemptionInput(
    string? SecretId,
    string? SecretVersion,
    string? Purpose,
    string? RunId);

/// <summary>Successful secret-redemption response, including its bounded expiry.</summary>
public sealed record SecretRedemptionResult(
    string SecretId,
    string SecretVersion,
    DateTimeOffset ExpiresAt,
    string Value);
