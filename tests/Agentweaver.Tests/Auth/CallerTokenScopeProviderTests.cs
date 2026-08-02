using FluentAssertions;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;

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
}
