using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Tests.Blueprints;

public sealed class HttpContextAuthenticatedOwnerContextTests
{
    [Fact]
    public void OwnerId_UsesAuthenticatedCallerIdentity()
    {
        var http = new DefaultHttpContext();
        http.User = CallerContextClaimsAdapter.ToPrincipal(
            new CallerContext
            {
                User = "owner-a",
                GitHubLogin = "owner-a",
            },
            AgentweaverAuthenticationSchemes.Entra);
        var accessor = new HttpContextAccessor { HttpContext = http };

        new HttpContextAuthenticatedOwnerContext(accessor).OwnerId.Should().Be("owner-a");
    }

    [Fact]
    public void OwnerId_WithoutRequest_FailsClosed()
    {
        var context = new HttpContextAuthenticatedOwnerContext(new HttpContextAccessor());

        var read = () => context.OwnerId;

        read.Should().Throw<InvalidOperationException>()
            .WithMessage("*authenticated HTTP request*");
    }

    [Fact]
    public void OwnerId_WithoutAuthenticatedCaller_FailsClosed()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var context = new HttpContextAuthenticatedOwnerContext(accessor);

        var read = () => context.OwnerId;

        read.Should().Throw<InvalidOperationException>()
            .WithMessage("*authenticated caller*");
    }
}
