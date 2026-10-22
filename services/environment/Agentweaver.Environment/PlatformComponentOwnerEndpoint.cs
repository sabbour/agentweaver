using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Agentweaver.Environment;

/// <summary>
/// Private owner route that reports this service's platform component areas to Projects.
/// It accepts only the private service token configured in <c>PlatformComponents:ServiceAuthentication</c>.
/// </summary>
internal static class PlatformComponentOwnerEndpoint
{
    /// <summary>Adds the private service-token scheme and policy when the settings are configured.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="options">Validated settings, or null to leave the route unmapped.</param>
    public static IServiceCollection AddPlatformComponentServiceAuthentication(
        this IServiceCollection services,
        PlatformComponentServiceAuthenticationOptions? options)
    {
        if (options is null)
            return services;

        services.AddAuthentication().AddJwtBearer(
            PlatformComponentServiceAuthenticationOptions.SchemeName,
            jwt =>
            {
                jwt.Authority = options.Authority.AbsoluteUri;
                jwt.Audience = options.Audience.AbsoluteUri;
                jwt.RequireHttpsMetadata = true;
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.ValidateIssuer = true;
                jwt.TokenValidationParameters.ValidateAudience = true;
                jwt.TokenValidationParameters.ValidAudience = options.Audience.AbsoluteUri;
                jwt.TokenValidationParameters.ValidateLifetime = true;
                jwt.TokenValidationParameters.RequireExpirationTime = true;
                jwt.TokenValidationParameters.RequireSignedTokens = true;
                jwt.TokenValidationParameters.ValidateIssuerSigningKey = true;
                jwt.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
                jwt.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
            });
        services.AddAuthorization(authorization => authorization.AddPolicy(
            PlatformComponentServiceAuthenticationOptions.PolicyName,
            policy => policy
                .AddAuthenticationSchemes(PlatformComponentServiceAuthenticationOptions.SchemeName)
                .RequireAuthenticatedUser()
                .RequireClaim("sub", options.CallerSubject)));
        return services;
    }

    /// <summary>Maps the owner route when the settings are configured. The response is never cached.</summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="options">Validated settings, or null to leave the route unmapped.</param>
    /// <param name="createReport">Builds this owner's report for one request.</param>
    public static IEndpointRouteBuilder MapPlatformComponentOwner(
        this IEndpointRouteBuilder endpoints,
        PlatformComponentServiceAuthenticationOptions? options,
        Func<IServiceProvider, CancellationToken, Task<PlatformComponentOwnerReport>> createReport)
    {
        ArgumentNullException.ThrowIfNull(createReport);
        if (options is null)
            return endpoints;

        endpoints.MapGet(PlatformComponentServiceAuthenticationOptions.OwnerRoute,
                async (HttpContext context, CancellationToken cancellationToken) =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    return Results.Ok(await createReport(context.RequestServices, cancellationToken));
                })
            .RequireAuthorization(PlatformComponentServiceAuthenticationOptions.PolicyName)
            .ExcludeFromDescription();
        return endpoints;
    }
}