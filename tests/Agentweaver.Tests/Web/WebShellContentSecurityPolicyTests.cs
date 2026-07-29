extern alias agentweaverweb;

using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

using WebProgram = agentweaverweb::Program;

namespace Agentweaver.Tests.Web;

public sealed class WebShellContentSecurityPolicyTests
{
    [Fact]
    public async Task DocsRedirect_ResponseAllowsGitHubAvatarsInImgSrc()
    {
        await using var factory = new WebApplicationFactory<WebProgram>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var response = await client.GetAsync("/docs");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Redirect);
        response.Headers.TryGetValues("Content-Security-Policy", out var values).Should().BeTrue();
        var contentSecurityPolicies = values.Should().NotBeNull().And.Subject;
        contentSecurityPolicies.Should().ContainSingle();
        contentSecurityPolicies.Single().Should().Contain("img-src 'self' data: https://avatars.githubusercontent.com;");
    }
}
