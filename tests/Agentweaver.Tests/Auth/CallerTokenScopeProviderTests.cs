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
    public async Task ResolveAsync_AlwaysReturnsCallerOwnScope_RegardlessOfProjectOrRequestContextOverride()
    {
        // There is no project-level GitHub identity override anymore: the submitting user's own
        // per-user identity must always be used, even if a request context carries stale override
        // state (e.g. from an older client, a replayed request, or any other caller-supplied hint).
        var context = new DefaultHttpContext();
        var accessor = new HttpContextAccessor { HttpContext = context };
        var provider = new CallerTokenScopeProvider(accessor);
        var projectId = ProjectId.Parse("00000000-0000-0000-0000-000000000001");
        var otherProjectId = "00000000-0000-0000-0000-000000000002";

        provider.Resolve("entra-user").Should().BeEquivalentTo(GitHubTokenScope.ForUser("entra-user"));

        (await provider.ResolveAsync("entra-user", projectId.ToString())).Should()
            .BeEquivalentTo(GitHubTokenScope.ForUser("entra-user"));
        (await provider.ResolveAsync("entra-user", otherProjectId)).Should()
            .BeEquivalentTo(GitHubTokenScope.ForUser("entra-user"));
        (await provider.ResolveAsync("entra-user", projectId: null)).Should()
            .BeEquivalentTo(GitHubTokenScope.ForUser("entra-user"));
    }
}
