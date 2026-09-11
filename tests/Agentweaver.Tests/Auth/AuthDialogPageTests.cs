using Agentweaver.Api.Endpoints;
using FluentAssertions;

namespace Agentweaver.Tests.Auth;

public sealed class AuthDialogPageTests
{
    [Theory]
    [InlineData(0, "tone-info", "role=\"status\"", ">i</span>")]
    [InlineData(1, "tone-success", "role=\"status\"", ">✓</span>")]
    [InlineData(2, "tone-pending", "role=\"status\"", ">…</span>")]
    [InlineData(3, "tone-warning", "role=\"status\"", ">!</span>")]
    [InlineData(4, "tone-error", "role=\"alert\"", ">×</span>")]
    public void Render_UsesOneAccessibleResponsiveShellForEveryStatus(
        int toneValue,
        string toneClass,
        string role,
        string icon)
    {
        var tone = (AuthDialogTone)toneValue;
        var html = AuthDialogPage.Render(new(
            "Page title",
            "Clear status",
            "Take the next safe action.",
            tone,
            ActionsHtml: AuthDialogPage.Link("Return", "/settings", primary: true),
            StyleNonce: "style-nonce"));

        html.Should().Contain("<main class=\"card " + toneClass + "\" aria-labelledby=\"dialog-title\">")
            .And.Contain(role)
            .And.Contain("aria-live=")
            .And.Contain(icon)
            .And.Contain("<h1 id=\"dialog-title\">Clear status</h1>")
            .And.Contain("@media (max-width: 480px)")
            .And.Contain("@media (prefers-reduced-motion: reduce)")
            .And.Contain("<style nonce=\"style-nonce\">")
            .And.Contain("href=\"/settings\"")
            .And.NotContain("javascript:");
    }

    [Fact]
    public void Render_EncodesUserVisibleTextAndActionUrls()
    {
        var html = AuthDialogPage.Render(new(
            "<script>title</script>",
            "<img src=x onerror=alert(1)>",
            "state=secret&code=secret",
            ActionsHtml: AuthDialogPage.Link("Return", "/settings?value=\"unsafe\"")));

        html.Should().NotContain("<script>title</script>")
            .And.NotContain("<img src=x onerror=alert(1)>")
            .And.Contain("&lt;script&gt;title&lt;/script&gt;")
            .And.Contain("state=secret&amp;code=secret")
            .And.Contain("value=&quot;unsafe&quot;");
    }
}
