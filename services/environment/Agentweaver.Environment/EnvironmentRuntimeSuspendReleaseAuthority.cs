using System.Net.Http.Headers;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;

namespace Agentweaver.Environment;

public sealed class EnvironmentRuntimeSuspendReleaseAuthority(
    HttpClient client,
    EnvironmentRuntimeBootstrapDeliveryOptions options,
    IHttpContextAccessor httpContextAccessor,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RequireCurrentAsync(
        CurrentCallerRequest caller, RuntimeSuspendPlacementReleaseRequest request,
        CancellationToken cancellationToken)
    {
        request.Validate();
        var context = httpContextAccessor.HttpContext
            ?? throw new RuntimeAuthorizationException("runtime_suspend_release_actor_unavailable");
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        if (!authentication.Succeeded || authentication.Properties?.ExpiresUtc is not { } expiresAt ||
            expiresAt <= timeProvider.GetUtcNow() ||
            !AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(header.Parameter, caller.BearerToken, StringComparison.Ordinal) ||
            context.Request.Headers["X-Agentweaver-Tenant"].Count > 1 ||
            context.Request.Headers["X-Agentweaver-Tenant"].SingleOrDefault() != caller.TenantSelector)
            throw new RuntimeAuthorizationException("runtime_suspend_release_actor_unavailable");
        var bearer = new SecretCredential(caller.BearerToken, expiresAt, timeProvider);
        try
        {
            var confirmed = await RuntimeOwnerHttpTransport.SendBoundedAsync<RuntimeSuspendPlacementReleaseRequest>(
                client, options.OrchestratorOwnerAddress, "/internal/runtime/suspend/require-release-current",
                new(bearer, caller.TenantSelector), request, SessionMaterialValidation.MaximumBytes,
                false, cancellationToken).ConfigureAwait(false);
            if (confirmed is null || !bearer.IsUsable() ||
                !JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(request, JsonOptions),
                    JsonSerializer.SerializeToElement(confirmed, JsonOptions)))
                throw new RuntimeAuthorizationException("runtime_suspend_release_authority_stale");
        }
        finally
        {
            bearer.Invalidate();
        }
    }
}
