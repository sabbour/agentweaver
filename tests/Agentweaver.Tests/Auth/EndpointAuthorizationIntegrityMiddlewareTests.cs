using Agentweaver.Api.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Tests.Auth;

public sealed class EndpointAuthorizationIntegrityMiddlewareTests
{
    [Fact]
    public async Task UnclassifiedEndpoint_IsDenied()
    {
        var context = ContextWithMetadata();

        await InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task BareAllowAnonymousEndpoint_IsDenied()
    {
        var context = ContextWithMetadata(new AllowAnonymousAttribute());

        await InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task ClassificationAndAnonymousMetadataDisagreement_IsDenied()
    {
        var context = ContextWithMetadata(
            new EndpointAuthorizationMetadata(EndpointAuthorizationKind.AuthenticatedPlatform),
            new AllowAnonymousAttribute());

        await InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    private static DefaultHttpContext ContextWithMetadata(params object[] metadata)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(metadata),
            "test"));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static Task InvokeAsync(HttpContext context) =>
        new EndpointAuthorizationIntegrityMiddleware(_ =>
        {
            throw new InvalidOperationException("Invalid metadata must not reach the endpoint.");
        }).InvokeAsync(context);
}
