using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace Agentweaver.Api.Auth;

public enum EndpointAuthorizationKind
{
    OperationalAnonymous,
    ProtocolManaged,
    WebhookHmac,
    AuthenticatedSelf,
    AuthenticatedPlatform,
    PlatformOrMcp,
    InternalService,
    RunCapability,
}

public sealed record EndpointAuthorizationMetadata(EndpointAuthorizationKind Kind)
{
    public bool RequiresBearerAuthentication =>
        Kind is not EndpointAuthorizationKind.OperationalAnonymous
            and not EndpointAuthorizationKind.ProtocolManaged
            and not EndpointAuthorizationKind.WebhookHmac;

    public bool RequiresPlatformAccess =>
        Kind is EndpointAuthorizationKind.AuthenticatedPlatform
            or EndpointAuthorizationKind.PlatformOrMcp
            or EndpointAuthorizationKind.InternalService
            or EndpointAuthorizationKind.RunCapability;
}

public static class EndpointAuthorizationPolicies
{
    public const string AuthenticatedSelf = nameof(AuthenticatedSelf);
    public const string AuthenticatedPlatform = nameof(AuthenticatedPlatform);
    public const string PlatformOrMcp = nameof(PlatformOrMcp);
    public const string InternalService = nameof(InternalService);
    public const string RunCapability = nameof(RunCapability);

    public static string For(EndpointAuthorizationKind kind) => kind switch
    {
        EndpointAuthorizationKind.AuthenticatedSelf => AuthenticatedSelf,
        EndpointAuthorizationKind.AuthenticatedPlatform => AuthenticatedPlatform,
        EndpointAuthorizationKind.PlatformOrMcp => PlatformOrMcp,
        EndpointAuthorizationKind.InternalService => InternalService,
        EndpointAuthorizationKind.RunCapability => RunCapability,
        _ => throw new InvalidOperationException(
            $"Authorization kind '{kind}' does not use an ASP.NET authorization policy."),
    };
}

public sealed class ApplicationEndpointMetadata
{
    private ApplicationEndpointMetadata() { }

    public static ApplicationEndpointMetadata Instance { get; } = new();
}

public static class EndpointAuthorizationExtensions
{
    public static TBuilder AsApplicationEndpoint<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            if (!endpointBuilder.Metadata.OfType<ApplicationEndpointMetadata>().Any())
                endpointBuilder.Metadata.Add(ApplicationEndpointMetadata.Instance);
        });
        return builder;
    }

    public static TBuilder OperationalAnonymous<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithAuthorizationClassification(EndpointAuthorizationKind.OperationalAnonymous);

    public static TBuilder ProtocolManaged<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithAuthorizationClassification(EndpointAuthorizationKind.ProtocolManaged);

    public static TBuilder WebhookHmacAuthenticated<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithAuthorizationClassification(EndpointAuthorizationKind.WebhookHmac);

    public static TBuilder AuthenticatedSelf<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithAuthorizationClassification(EndpointAuthorizationKind.AuthenticatedSelf);

    public static TBuilder AuthenticatedPlatform<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithAuthorizationClassification(EndpointAuthorizationKind.AuthenticatedPlatform);

    public static TBuilder PlatformOrMcp<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithAuthorizationClassification(EndpointAuthorizationKind.PlatformOrMcp);

    public static TBuilder InternalService<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithAuthorizationClassification(EndpointAuthorizationKind.InternalService);

    public static TBuilder RunCapability<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithAuthorizationClassification(EndpointAuthorizationKind.RunCapability);

    private static TBuilder WithAuthorizationClassification<TBuilder>(
        this TBuilder builder,
        EndpointAuthorizationKind kind)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            for (var index = endpointBuilder.Metadata.Count - 1; index >= 0; index--)
            {
                if (endpointBuilder.Metadata[index] is EndpointAuthorizationMetadata
                    or IAllowAnonymous
                    or IAuthorizeData)
                {
                    endpointBuilder.Metadata.RemoveAt(index);
                }
            }

            var metadata = new EndpointAuthorizationMetadata(kind);
            endpointBuilder.Metadata.Add(metadata);
            if (!metadata.RequiresBearerAuthentication)
                endpointBuilder.Metadata.Add(new AllowAnonymousAttribute());
            else
                endpointBuilder.Metadata.Add(new AuthorizeAttribute(EndpointAuthorizationPolicies.For(kind)));
        });
        return builder;
    }
}

internal sealed class EndpointAuthorizationIntegrityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint() is { } endpoint)
        {
            var classifications = endpoint.Metadata
                .GetOrderedMetadata<EndpointAuthorizationMetadata>();
            var allowsAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var valid = classifications.Count == 1
                && allowsAnonymous == !classifications[0].RequiresBearerAuthentication;
            if (!valid)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    "{\"error\":\"endpoint_authorization_metadata_invalid\"}")
                    .ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }
}

internal sealed class UnmatchedEndpointMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint() is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
