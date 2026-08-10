using FluentAssertions;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Tests.Auth;

public sealed class CallerTokenScopeProviderTests
{
    [Fact]
    public void Resolve_WithUserId_ReturnsPerUserScope()
    {
        var provider = new CallerTokenScopeProvider();

        provider.Resolve("octocat").Should().BeEquivalentTo(GitHubTokenScope.ForUser("octocat"));
    }

    [Fact]
    public void Resolve_WithoutUserId_FailsClosed()
    {
        var provider = new CallerTokenScopeProvider();

        var act = () => provider.Resolve(null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*explicit user identity*");
    }

    [Fact]
    public void Resolve_AfterProjectIdentitySelection_ReturnsSelectedLinkedScopeForSameUserOnly()
    {
        var context = new DefaultHttpContext();
        var accessor = new HttpContextAccessor { HttpContext = context };
        var provider = new CallerTokenScopeProvider(accessor);
        CallerTokenScopeProvider.SelectProjectIdentity(context, "entra-user", "altcat");

        provider.Resolve("entra-user").Should()
            .BeEquivalentTo(GitHubTokenScope.ForLinkedIdentity("entra-user", "altcat"));
        provider.Resolve("other-user").Should()
            .BeEquivalentTo(GitHubTokenScope.ForUser("other-user"));
    }
}
