using System.Security.Claims;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

namespace Agentweaver.Identity.Broker;

public static class RuntimeCredentialEndpoints
{
    public static void MapIdentityRuntimeCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/internal/runtime").RequireAuthorization(new AuthorizeAttribute
        {
            AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme
        });
        routes.MapPost("/bootstrap/request", RequestAsync);
        routes.MapPost("/bootstrap/verify-pending", VerifyPendingAsync);
        routes.MapPost("/bootstrap/consume", (HttpContext context, RuntimeCredentialHttpRequest input) =>
            ApplyProofAsync(context, input, "consume"));
        routes.MapPost("/bootstrap/exchange", (HttpContext context, RuntimeCredentialHttpRequest input) =>
            ApplyProofAsync(context, input, "exchange"));
        routes.MapPost("/source/verify", (HttpContext context, RuntimeCredentialHttpRequest input) =>
            ApplyProofAsync(context, input, "verify"));
        routes.MapPost("/source/rotate", (HttpContext context, RuntimeCredentialHttpRequest input) =>
            ApplyProofAsync(context, input, "rotate"));
        routes.MapPost("/source/revoke", (HttpContext context, RuntimeCredentialHttpRequest input) =>
            ApplyProofAsync(context, input, "revoke"));
    }

    private static Task RequestAsync(HttpContext context, RuntimeBootstrapRequest input) =>
        ExecuteAsync(context, authority => authority.DeliverBootstrapAsync(
            input.RuntimeInstanceId, input.ConfigurationHash, input.OperationId, context.RequestAborted));

    private static Task VerifyPendingAsync(HttpContext context, PendingBootstrapVerificationRequest input) =>
        ApplyProofAsync(context, new RuntimeCredentialHttpRequest(
            input.GrantId, input.RuntimeInstanceId, input.Revision, RuntimeCredentialPurpose.Configure,
            input.Audience, input.ConfigurationHash, input.CredentialValue,
            input.CredentialExpiresAt, input.DeliveryOperationId), "pending");

    private static async Task ApplyProofAsync(
        HttpContext context, RuntimeCredentialHttpRequest input, string operation)
    {
        SecretCredential? credential = null;
        RuntimeCredentialExchange? exchange = null;
        try
        {
            RuntimeContractValidation.ValidateHash(input.CredentialValue);
            var time = context.RequestServices.GetRequiredService<TimeProvider>();
            credential = new SecretCredential(input.CredentialValue, input.CredentialExpiresAt, time);
            var proof = new RuntimeCredentialProof(
                input.GrantId, input.RuntimeInstanceId, input.Revision, input.Purpose,
                input.Audience, input.ConfigurationHash, credential);
            await ExecuteAsync<object>(context, async authority =>
            {
                if (operation is "exchange" or "rotate")
                {
                    exchange = operation == "exchange"
                        ? await authority.ExchangeBootstrapAsync(proof, input.OperationId, context.RequestAborted)
                        : await authority.RotateSourceAsync(proof, input.OperationId, context.RequestAborted);
                    return new RuntimeCredentialExchangeResponse(
                        exchange.Receipt, exchange.Credential?.GetValue(), exchange.IsReplay);
                }
                return operation switch
                {
                    "pending" => await authority.VerifyPendingBootstrapDeliveryAsync(
                        proof, input.OperationId, context.RequestAborted),
                    "consume" => await authority.ConsumeBootstrapAsync(
                        proof, input.OperationId, context.RequestAborted),
                    "verify" => await authority.VerifySourceAsync(proof, context.RequestAborted),
                    "revoke" => await authority.RevokeAsync(proof, input.OperationId, context.RequestAborted),
                    _ => throw new InvalidOperationException("The runtime credential route is not mapped.")
                };
            });
        }
        catch (RuntimeAuthorizationException error)
        {
            await DenyAsync(context, error.Code, StatusCodes.Status403Forbidden);
        }
        catch (ArgumentException)
        {
            await DenyAsync(context, "runtime_request_invalid", StatusCodes.Status400BadRequest);
        }
        finally
        {
            credential?.Invalidate();
            exchange?.Credential?.Invalidate();
        }
    }

    private static async Task ExecuteAsync<T>(
        HttpContext context, Func<RuntimeGrantAuthority, Task<T>> action) where T : class
    {
        context.Response.Headers.CacheControl = "no-store";
        SecretCredential? bearer = null;
        try
        {
            var subject = SingleClaim(context.User, "sub");
            var expiry = context.User.GetExpirationDate();
            var authorization = context.Request.Headers.Authorization.ToString();
            // OpenIddict validates the issuer and stores expiry in private principal metadata.
            if (context.User.Identity?.IsAuthenticated != true || !Guid.TryParseExact(subject, "D", out _) ||
                expiry is null ||
                !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                authorization.Length > 16_391 || authorization[7..].Any(char.IsWhiteSpace) ||
                context.Request.Headers["X-Agentweaver-Tenant"].Count > 1)
                throw new RuntimeAuthorizationException("runtime_actor_invalid");
            var time = context.RequestServices.GetRequiredService<TimeProvider>();
            bearer = new SecretCredential(authorization[7..], expiry.Value, time);
            var actor = new RuntimeActorAuthorization(
                bearer, context.Request.Headers["X-Agentweaver-Tenant"].SingleOrDefault());
            var authority = new RuntimeGrantAuthority(
                context.RequestServices.GetRequiredService<IdentityBrokerDbContext>(),
                context.RequestServices.GetRequiredService<IRuntimeRegistrationOwner>(),
                context.RequestServices.GetRequiredService<IRuntimeBootstrapDelivery>(),
                context.RequestServices.GetRequiredService<RuntimeCredentialPolicy>(),
                actor, time);
            var result = await action(authority);
            context.RequestAborted.ThrowIfCancellationRequested();
            await Results.Json(result).ExecuteAsync(context);
        }
        catch (RuntimeAuthorizationException error)
        {
            await DenyAsync(context, error.Code, StatusCodes.Status403Forbidden);
        }
        catch (ArgumentException)
        {
            await DenyAsync(context, "runtime_request_invalid", StatusCodes.Status400BadRequest);
        }
        finally
        {
            bearer?.Invalidate();
        }
    }

    private static Task DenyAsync(HttpContext context, string code, int statusCode)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { error = code }, statusCode: statusCode).ExecuteAsync(context);
    }

    private static string? SingleClaim(ClaimsPrincipal principal, string name)
    {
        var claims = principal.FindAll(name).Take(2).ToArray();
        return claims.Length == 1 ? claims[0].Value : null;
    }
}
