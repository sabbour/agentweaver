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
    public async Task Resolve_AfterProjectIdentitySelection_ReturnsSelectedLinkedScopeForSameUserAndProjectOnly()
    {
        var context = new DefaultHttpContext();
        var accessor = new HttpContextAccessor { HttpContext = context };
        var provider = new CallerTokenScopeProvider(accessor);
        var selectedProjectId = ProjectId.Parse("00000000-0000-0000-0000-000000000001");
        CallerTokenScopeProvider.SelectProjectIdentity(
            context,
            selectedProjectId,
            "entra-user",
            "altcat");

        provider.Resolve("entra-user").Should()
            .BeEquivalentTo(GitHubTokenScope.ForLinkedIdentity("entra-user", "altcat"));
        provider.Resolve("other-user").Should()
            .BeEquivalentTo(GitHubTokenScope.ForUser("other-user"));
        (await provider.ResolveAsync("entra-user", selectedProjectId.ToString())).Should()
            .BeEquivalentTo(GitHubTokenScope.ForLinkedIdentity("entra-user", "altcat"));
        (await provider.ResolveAsync("entra-user", "00000000-0000-0000-0000-000000000002")).Should()
            .BeEquivalentTo(GitHubTokenScope.ForUser("entra-user"));
    }
}
