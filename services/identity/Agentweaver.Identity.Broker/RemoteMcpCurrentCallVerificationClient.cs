using System.Security.Claims;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Verifies Environment's proof for the current remote MCP call using its pinned mutual-TLS
/// owner endpoint. It checks the caller token and tenant against the proof, validates the
/// bounded owner acknowledgment, and invalidates the copied bearer after the call.
/// </summary>
public sealed class RemoteMcpCurrentCallVerificationClient(
    HttpClient client,
    [FromKeyedServices(RemoteMcpCredentialUseComposition.RecipientTlsKey)] RuntimeServiceTlsOptions peerOptions,
    IdentityBrokerOptions brokerOptions,
    TimeProvider timeProvider)
{
    private readonly Uri _ownerAddress = new(
        peerOptions.Validate("IdentityBroker", "Environment").PeerOwnerAddress!, UriKind.Absolute);

    /// <summary>Returns a validated acknowledgment or throws when the actor, proof or owner response is not current.</summary>
    public async Task<RemoteMcpCurrentCallVerificationAcknowledgment> VerifyAsync(
        HttpContext context, RemoteMcpCurrentCallVerificationRequest request, CancellationToken cancellationToken)
    {
        RemoteMcpCurrentCallContract.Validate(request);
        var authentication = await context.AuthenticateAsync(
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme).ConfigureAwait(false);
        var principal = authentication.Principal;
        var expiry = principal?.GetExpirationDate() ?? authentication.Properties?.ExpiresUtc;
        var proof = request.Proof;
        var authorization = context.Request.Headers.Authorization.ToString();
        var tenant = context.Request.Headers["X-Agentweaver-Tenant"];
        if (!authentication.Succeeded || principal?.Identity?.IsAuthenticated != true ||
            SingleClaim(principal, "sub") != proof.ActorId ||
            !Guid.TryParseExact(proof.ActorId, "D", out _) ||
            proof.ActorIssuer != new Uri(brokerOptions.Issuer, UriKind.Absolute).AbsoluteUri ||
            expiry is null || expiry <= timeProvider.GetUtcNow() ||
            context.Request.Headers.Authorization.Count != 1 ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            authorization.Length is <= 7 or > 16_391 || authorization[7..].Any(char.IsWhiteSpace) ||
            tenant.Count > 1 || tenant.Count == 1 && tenant[0] != proof.TenantId)
            throw new RuntimeAuthorizationException("remote_mcp_current_verification_actor_invalid");
        var bearer = new SecretCredential(authorization[7..], expiry.Value, timeProvider);
        try
        {
            var result = await RuntimeOwnerHttpTransport.SendBoundedAsync<RemoteMcpCurrentCallVerificationAcknowledgment>(
                client, _ownerAddress, RemoteMcpCurrentCallContract.VerificationPath,
                new RuntimeActorAuthorization(bearer, proof.TenantId), request,
                2 * SessionMaterialValidation.MaximumBytes, allowNotFound: false, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new RuntimeAuthorizationException("remote_mcp_current_verification_owner_invalid");
            RemoteMcpCurrentCallContract.ValidateAcknowledgment(request, result);
            if (!bearer.IsUsable())
                throw new RuntimeAuthorizationException("remote_mcp_current_verification_actor_expired");
            return result;
        }
        catch (ArgumentException)
        {
            throw new RuntimeAuthorizationException("remote_mcp_current_verification_owner_invalid");
        }
        finally
        {
            bearer.Invalidate();
        }
    }

    private static string? SingleClaim(ClaimsPrincipal principal, string type)
    {
        var claims = principal.FindAll(type).Take(2).ToArray();
        return claims.Length == 1 ? claims[0].Value : null;
    }
}
