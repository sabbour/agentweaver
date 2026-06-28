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
    [Fact]
    public void HostLabel_appends_preview_suffix_as_single_dns_label()
    {
        for (var i = 0; i < 500; i++)
        {
            var token = PreviewToken.Generate();
            var hostLabel = PreviewToken.HostLabel(token);

            hostLabel.Should().Be($"{token}-preview");
            hostLabel.Should().EndWith("-preview");
            hostLabel.Length.Should().BeLessThanOrEqualTo(63, "host label must fit the DNS label limit");
            // Still a single valid DNS-1123 label (covered by the *.{zone} wildcard cert).
            PreviewToken.IsValidLabel(hostLabel).Should().BeTrue();
            hostLabel.Should().MatchRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$");
        }
    }

    [Fact]
    public void Host_and_preview_url_both_carry_the_preview_suffix()
    {
        // Mirror exactly how SandboxPreviewService builds the HTTPRoute hostname and preview_url
        // (both derive from PreviewToken.HostLabel(token)) to lock the self-identifying format.
        const string zone = "6a3de4fe60529400010f3fba.westus2.staging.aksapp.io";
        var token = PreviewToken.Generate();

        var hostname = $"{PreviewToken.HostLabel(token)}.{zone}";
        var previewUrl = $"https://{hostname}";

        hostname.Should().StartWith($"{token}-preview.");
        hostname.Should().Be($"{token}-preview.{zone}");
        previewUrl.Should().Be($"https://{token}-preview.{zone}");
        previewUrl.Should().StartWith("https://").And.Contain("-preview.");
    }

    // ── Preview port range (mirrors k8s NetworkPolicy gateway-only ingress) ───────

    [Theory]
    [InlineData(3000)]   // min boundary
    [InlineData(9000)]   // max boundary
    [InlineData(5173)]   // Vite
    [InlineData(8080)]   // generic dev server
    public void IsPortInRange_accepts_ports_within_allowed_range(int port) =>
        SandboxPreviewOptions.IsPortInRange(port, 3000, 9000).Should().BeTrue();

    [Theory]
    [InlineData(2999)]   // just below min
    [InlineData(9001)]   // just above max
    [InlineData(80)]
    [InlineData(443)]
    [InlineData(65535)]
    [InlineData(0)]
    public void IsPortInRange_rejects_ports_outside_allowed_range(int port) =>
        SandboxPreviewOptions.IsPortInRange(port, 3000, 9000).Should().BeFalse();

    [Fact]
    public void Options_default_port_range_is_3000_to_9000()
    {
        var options = new SandboxPreviewOptions();
        options.AllowedPortMin.Should().Be(3000);
        options.AllowedPortMax.Should().Be(9000);
    }

    // ── PreviewToken ─────────────────────────────────────────────────────────────

    [Fact]
    public void Wordlist_is_label_safe_but_words_are_cosmetic_only()
    {
        // Words are a cosmetic prefix only — they contribute NO security entropy (Seraph).
        // Security entropy comes entirely from the 128-bit CSPRNG suffix (see NewSuffix tests).
        PreviewToken.Words.Length.Should().BeGreaterThanOrEqualTo(48);

        foreach (var word in PreviewToken.Words)
        {
            word.Should().MatchRegex("^[a-z]+$");
            PreviewToken.Reserved.Should().NotContain(word);
        }

        PreviewToken.Words.Distinct(StringComparer.Ordinal).Should()
            .HaveCount(PreviewToken.Words.Length, "wordlist must not contain duplicates");
    }

    [Fact]
    public void NewSuffix_carries_at_least_64_bits_of_csprng_entropy()
    {
        // 16 bytes (128 bits) base32-encoded => 26 chars, well above the 13-char / 64-bit floor.
        var suffix = PreviewToken.NewSuffix();
        suffix.Length.Should().BeGreaterThanOrEqualTo(13);
        suffix.Should().MatchRegex("^[a-z2-7]+$", "RFC 4648 lowercase base32 alphabet (a-z2-7)");

        // Uniqueness across many draws is the practical entropy signal.
        var suffixes = Enumerable.Range(0, 5000).Select(_ => PreviewToken.NewSuffix()).ToHashSet(StringComparer.Ordinal);
        suffixes.Count.Should().Be(5000, "128-bit CSPRNG suffixes must not collide");
    }

    [Fact]
    public void Generate_produces_valid_dns_label_with_csprng_suffix()
    {
        for (var i = 0; i < 500; i++)
        {
            var token = PreviewToken.Generate();

            PreviewToken.IsValidLabel(token).Should().BeTrue($"'{token}' should be a valid DNS label");
            token.Length.Should().BeLessThanOrEqualTo(63);
            PreviewToken.Reserved.Should().NotContain(token);
            token.Should().MatchRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$");

            // 3 cosmetic words + a base32 suffix joined by '-'.
            var parts = token.Split('-');
            parts.Should().HaveCount(4);
            parts[3].Should().MatchRegex("^[a-z2-7]{13,}$", "suffix must be >= 64 bits of base32");
        }
    }

    [Fact]
    public void Generate_is_highly_unlikely_to_collide()
    {
        var tokens = Enumerable.Range(0, 5000).Select(_ => PreviewToken.Generate()).ToList();
        tokens.Distinct(StringComparer.Ordinal).Count()
            .Should().Be(5000, "128-bit CSPRNG suffix makes collisions astronomically unlikely");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("agentweaver", false)]            // reserved
    [InlineData("mcp", false)]                    // reserved
    [InlineData("api", false)]                    // reserved
    [InlineData("frontend", false)]               // reserved
    [InlineData("-leading", false)]
    [InlineData("trailing-", false)]
    [InlineData("Upper-Case", false)]
    [InlineData("has_underscore", false)]
    [InlineData("has.dot", false)]
    [InlineData("swift-falcon-amber-k7m2q9x4n8b3r6", true)]
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

    // ── PreviewReaper.PerRunLabel (M3 — collision-resistant per-run selector) ─────

    [Fact]
    public void PerRunLabel_is_a_valid_label_with_stable_hash_suffix()
    {
        var label = PreviewReaper.PerRunLabel("run-abc-123");
        label.Should().StartWith("run-abc-123-");
        label.Should().MatchRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$");
        label.Length.Should().BeLessThanOrEqualTo(63);
        // Deterministic for the same run.
        label.Should().Be(PreviewReaper.PerRunLabel("run-abc-123"));
    }

    [Fact]
    public void PerRunLabel_disambiguates_run_ids_that_sanitize_to_the_same_value()
    {
        // Both sanitize to "a-b" but must NOT share a per-run selector label.
        var a = PreviewReaper.PerRunLabel("a/b");
        var b = PreviewReaper.PerRunLabel("a.b");
        PreviewReaper.SanitizeLabel("a/b").Should().Be(PreviewReaper.SanitizeLabel("a.b"));
        a.Should().NotBe(b, "distinct run IDs must get distinct selector labels to avoid cross-run Service selectors");
    }

    [Fact]
    public void PerRunLabel_stays_within_63_chars_for_long_run_ids()
    {
        var label = PreviewReaper.PerRunLabel(new string('a', 200));
        label.Length.Should().BeLessThanOrEqualTo(63);
        label.Should().MatchRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$");
    }

    // ── PreviewReaper.FindOrphanServiceNames (S1 — orphan ClusterIP sweep) ────────

    [Fact]
    public void FindOrphanServiceNames_returns_services_without_a_matching_route()
    {
        var services = new[] { "preview-alive", "preview-orphan-1", "preview-orphan-2" };
        var routes = new[] { "preview-alive" };

        PreviewReaper.FindOrphanServiceNames(services, routes)
            .Should().BeEquivalentTo(new[] { "preview-orphan-1", "preview-orphan-2" });
    }

    [Fact]
    public void FindOrphanServiceNames_returns_empty_when_every_service_has_a_route()
    {
        var both = new[] { "preview-a", "preview-b" };
        PreviewReaper.FindOrphanServiceNames(both, both).Should().BeEmpty();
    }

    // ── PreviewReaper.RunMatches (M1 — run<->token binding) ───────────────────────

    [Fact]
    public void RunMatches_true_only_for_the_owning_run()
    {
        var annotation = PreviewReaper.PerRunLabel("run-owner");
        PreviewReaper.RunMatches(annotation, "run-owner").Should().BeTrue();
        PreviewReaper.RunMatches(annotation, "run-impostor").Should().BeFalse();
        PreviewReaper.RunMatches(null, "run-owner").Should().BeFalse();
        PreviewReaper.RunMatches("", "run-owner").Should().BeFalse();
    }
}
