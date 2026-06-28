using Agentweaver.Api.Sandbox.Preview;
using FluentAssertions;

namespace Agentweaver.Tests;

/// <summary>
/// Unit tests for the Gateway-direct sandbox preview (feat/sandbox-preview-proxy):
/// the capability-token generator (<see cref="PreviewToken"/>) and the pure reaper
/// decision logic + label helpers (<see cref="PreviewReaper"/>). No live cluster required.
/// </summary>
public class SandboxPreviewTests
{
    // ── PreviewToken ─────────────────────────────────────────────────────────────

    [Fact]
    public void Wordlist_has_enough_entropy_and_is_label_safe()
    {
        // Wordlist >= 48 words. Combined brute-force space with the prescribed
        // "3 words + 4 hex" format is Words^3 * 16^4; with 64 words that is ~2^34,
        // far beyond an online-guessable space for a single capability URL.
        PreviewToken.Words.Length.Should().BeGreaterThanOrEqualTo(48);

        var bruteForceSpace = Math.Pow(PreviewToken.Words.Length, 3) * Math.Pow(16, 4);
        bruteForceSpace.Should().BeGreaterThan(Math.Pow(2, 32));

        foreach (var word in PreviewToken.Words)
        {
            word.Should().MatchRegex("^[a-z]+$");
            word.Should().NotBe(PreviewToken.Reserved);
        }

        PreviewToken.Words.Distinct(StringComparer.Ordinal).Should()
            .HaveCount(PreviewToken.Words.Length, "wordlist must not contain duplicates");
    }

    [Fact]
    public void Generate_produces_valid_dns_label_shape()
    {
        for (var i = 0; i < 500; i++)
        {
            var token = PreviewToken.Generate();

            PreviewToken.IsValidLabel(token).Should().BeTrue($"'{token}' should be a valid DNS label");
            token.Length.Should().BeLessThanOrEqualTo(63);
            token.Should().NotBe(PreviewToken.Reserved);
            token.Should().MatchRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$");

            var parts = token.Split('-');
            parts.Should().HaveCount(4, "three words plus a 4-hex suffix joined by '-'");
            parts[3].Should().MatchRegex("^[0-9a-f]{4}$");
        }
    }

    [Fact]
    public void Generate_is_highly_unlikely_to_collide()
    {
        var tokens = Enumerable.Range(0, 2000).Select(_ => PreviewToken.Generate()).ToList();
        tokens.Distinct(StringComparer.Ordinal).Count()
            .Should().BeGreaterThan(1990, "tokens should be drawn from a large random space");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("agentweaver", false)]            // reserved
    [InlineData("-leading", false)]
    [InlineData("trailing-", false)]
    [InlineData("Upper-Case", false)]
    [InlineData("has_underscore", false)]
    [InlineData("has.dot", false)]
    [InlineData("swift-falcon-amber-7a3f", true)]
    [InlineData("a", true)]
    public void IsValidLabel_enforces_dns_rules(string? token, bool expected) =>
        PreviewToken.IsValidLabel(token).Should().Be(expected);

    [Fact]
    public void IsValidLabel_rejects_labels_over_63_chars() =>
        PreviewToken.IsValidLabel(new string('a', 64)).Should().BeFalse();

    // ── PreviewReaper.Decide ─────────────────────────────────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Decide_alive_when_within_idle_and_max_and_pod_present()
    {
        PreviewReaper.Decide(Now, Now.AddMinutes(10), Now.AddHours(4), podExists: true)
            .Should().Be(PreviewReapReason.Alive);
    }

    [Fact]
    public void Decide_orphan_takes_priority_when_pod_missing()
    {
        // Pod gone trumps even a still-valid idle/max window.
        PreviewReaper.Decide(Now, Now.AddMinutes(10), Now.AddHours(4), podExists: false)
            .Should().Be(PreviewReapReason.Orphan);
    }

    [Fact]
    public void Decide_expired_max_when_past_hard_cap()
    {
        PreviewReaper.Decide(Now, Now.AddMinutes(10), Now.AddMinutes(-1), podExists: true)
            .Should().Be(PreviewReapReason.ExpiredMax);
    }

    [Fact]
    public void Decide_max_takes_priority_over_idle()
    {
        PreviewReaper.Decide(Now, Now.AddMinutes(-5), Now.AddMinutes(-1), podExists: true)
            .Should().Be(PreviewReapReason.ExpiredMax);
    }

    [Fact]
    public void Decide_expired_idle_when_past_idle_but_within_max()
    {
        PreviewReaper.Decide(Now, Now.AddMinutes(-1), Now.AddHours(4), podExists: true)
            .Should().Be(PreviewReapReason.ExpiredIdle);
    }

    [Fact]
    public void Decide_missing_timestamps_never_reap_a_live_pod()
    {
        // Null timestamps => no idle/max constraint; only orphan can reap.
        PreviewReaper.Decide(Now, expiresAt: null, maxUntil: null, podExists: true)
            .Should().Be(PreviewReapReason.Alive);
    }

    [Fact]
    public void ShouldReap_matches_decide()
    {
        PreviewReaper.ShouldReap(Now, Now.AddMinutes(10), Now.AddHours(4), true).Should().BeFalse();
        PreviewReaper.ShouldReap(Now, Now.AddMinutes(-1), Now.AddHours(4), true).Should().BeTrue();
        PreviewReaper.ShouldReap(Now, Now.AddMinutes(10), Now.AddHours(4), false).Should().BeTrue();
    }

    // ── PreviewReaper.ParseTimestamp ─────────────────────────────────────────────

    [Fact]
    public void ParseTimestamp_parses_rfc3339_utc()
    {
        var parsed = PreviewReaper.ParseTimestamp("2026-06-28T12:30:00Z");
        parsed.Should().NotBeNull();
        parsed!.Value.ToUniversalTime().Should().Be(new DateTimeOffset(2026, 6, 28, 12, 30, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    public void ParseTimestamp_returns_null_for_invalid(string? value) =>
        PreviewReaper.ParseTimestamp(value).Should().BeNull();

    // ── PreviewReaper.SanitizeLabel / ServiceName ────────────────────────────────

    [Theory]
    [InlineData("run-123", "run-123")]
    [InlineData("Run_With.Mixed/Chars", "run-with-mixed-chars")]
    [InlineData("--leading-and-trailing--", "leading-and-trailing")]
    [InlineData("", "run")]
    [InlineData("   ", "run")]
    public void SanitizeLabel_produces_valid_label_values(string input, string expected) =>
        PreviewReaper.SanitizeLabel(input).Should().Be(expected);

    [Fact]
    public void SanitizeLabel_truncates_to_63_chars()
    {
        var result = PreviewReaper.SanitizeLabel(new string('a', 200));
        result.Length.Should().BeLessThanOrEqualTo(63);
    }

    [Fact]
    public void ServiceName_prefixes_token_and_caps_length()
    {
        PreviewReaper.ServiceName("swift-falcon-amber-7a3f").Should().Be("preview-swift-falcon-amber-7a3f");

        var longName = PreviewReaper.ServiceName(new string('a', 100));
        longName.Length.Should().BeLessThanOrEqualTo(63);
        longName.Should().NotEndWith("-");
    }
}
