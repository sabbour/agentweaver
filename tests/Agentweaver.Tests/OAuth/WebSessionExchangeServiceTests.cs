using FluentAssertions;
using Agentweaver.Api.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.OAuth;

/// <summary>
/// F5 — Web sign-in one-time code exchange.
///
/// Validates that the GitHub access token is exchanged via a server-side single-use code instead
/// of being leaked in the redirect URL: happy-path redemption returns the token + login, codes are
/// single-use (replay fails), and invalid/missing codes are rejected.
/// </summary>
public class WebSessionExchangeServiceTests
{
    private static WebSessionExchangeService NewService() =>
        new(NullLogger<WebSessionExchangeService>.Instance);

    [Fact]
    public void Issue_ThenRedeem_ReturnsTokenAndLogin()
    {
        var svc = NewService();
        var code = svc.Issue("gho_secret_token", "octocat");

        var ok = svc.TryRedeem(code, out var token, out var login);

        ok.Should().BeTrue();
        token.Should().Be("gho_secret_token");
        login.Should().Be("octocat");
    }

    [Fact]
    public void Redeem_IsSingleUse()
    {
        var svc = NewService();
        var code = svc.Issue("gho_secret_token", "octocat");

        svc.TryRedeem(code, out _, out _).Should().BeTrue();
        svc.TryRedeem(code, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-real-code")]
    public void Redeem_InvalidOrMissingCode_ReturnsFalse(string? code)
    {
        var svc = NewService();
        svc.Issue("gho_secret_token", "octocat");

        svc.TryRedeem(code, out var token, out var login).Should().BeFalse();
        token.Should().BeEmpty();
        login.Should().BeEmpty();
    }

    [Fact]
    public void Issue_GeneratesUniqueOpaqueCodes()
    {
        var svc = NewService();
        var a = svc.Issue("t1", "u1");
        var b = svc.Issue("t2", "u2");

        a.Should().NotBe(b);
        a.Should().NotContain("t1");
    }
}
