using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Microsoft.Net.Http.Headers;

namespace Agentweaver.EventsAndSessions.Cost;

internal static class RuntimeRateCardOwnerReadEndpoint
{
    internal const string Route = "/internal/runtime-rate-cards/{id}/{version}";

    internal static IEndpointRouteBuilder MapRuntimeRateCardOwnerRead(
        this IEndpointRouteBuilder endpoints,
        PlatformComponentServiceAuthenticationOptions? authentication)
    {
        if (authentication is null)
            return endpoints;

        endpoints.MapGet(Route, async (
                HttpContext context,
                string id,
                string version,
                RuntimeRateCardStore store,
                CancellationToken cancellationToken) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                try
                {
                    RuntimeRateCardContract.ValidateIdentity(id, version);
                }
                catch (ArgumentException)
                {
                    return Results.NotFound();
                }

                var rateCard = await store.GetAsync(id, version, cancellationToken).ConfigureAwait(false);
                if (rateCard is null)
                    return Results.NotFound();

                var canonical = RuntimeRateCardContract.SerializeCanonical(rateCard);
                var bytes = Encoding.UTF8.GetBytes(canonical);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                context.Response.Headers.ETag = new EntityTagHeaderValue($"\"{hash}\"").ToString();
                return Results.Text(canonical, "application/json", Encoding.UTF8, StatusCodes.Status200OK);
            })
            .RequireAuthorization(PlatformComponentServiceAuthenticationOptions.PolicyName)
            .ExcludeFromDescription();
        return endpoints;
    }
}
