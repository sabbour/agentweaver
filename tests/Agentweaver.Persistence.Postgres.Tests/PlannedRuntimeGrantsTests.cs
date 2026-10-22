using System.Security.Cryptography;
using System.Text;
using Agentweaver.Persistence.Postgres;
using Xunit;

namespace Agentweaver.Persistence.Postgres.Tests;

// Pure tests for the plan gates. They need no database: every gate throws before a connection opens.
public sealed class PlannedRuntimeGrantsTests
{
    private const string ReviewedPlanSha256 = "ff069555878416f3c4111eb70973c41f0d0eb9e907425ccc72c7270208aa3fbc";
    private static readonly int[] CoreVersions = [.. Enumerable.Range(1, 16), .. Enumerable.Range(18, 14), 33];
    private static readonly int[] EventsVersions = [.. Enumerable.Range(1, 9)];

    [Fact]
    public void EmbeddedPlanIsTheReviewedPlan()
    {
        var bytes = PlannedRuntimeGrants.ReadEmbeddedPlan();
        Assert.Equal(ReviewedPlanSha256, Sha256(bytes));
    }

    [Fact]
    public void SelectsOrchestratorAndEventsGrantsFromTheReviewedPlan()
    {
        var core = PlannedRuntimeGrants.Load(
            "orchestrator", "orchestrator", "agentweaver-id-orchestrator", ReviewedPlanSha256, CoreVersions);
        Assert.Equal(CoreVersions, core.Versions);
        Assert.DoesNotContain(17, core.Versions);
        Assert.DoesNotContain(32, core.Versions);
        Assert.Contains(33, core.Versions);
        Assert.NotEmpty(core.Tables);

        var events = PlannedRuntimeGrants.Load(
            "events-and-sessions", "events_sessions", "agentweaver-id-events-and-sessions", ReviewedPlanSha256, EventsVersions);
        Assert.Equal(EventsVersions, events.Versions);
        Assert.NotEmpty(events.Tables);
    }

    [Fact]
    public void RejectsAnyPlanByteChange()
    {
        var changed = PlannedRuntimeGrants.ReadEmbeddedPlan().Concat(Encoding.UTF8.GetBytes(" ")).ToArray();
        var error = Assert.Throws<InvalidOperationException>(() => PlannedRuntimeGrants.Select(
            changed, "orchestrator", "orchestrator", "agentweaver-id-orchestrator", ReviewedPlanSha256, CoreVersions));
        Assert.Contains("hash", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("FF069555878416F3C4111EB70973C41F0D0EB9E907425CCC72C7270208AA3FBC")]
    public void RejectsMissingOrMalformedExpectedHash(string expected)
    {
        Assert.Throws<InvalidOperationException>(() => PlannedRuntimeGrants.Load(
            "orchestrator", "orchestrator", "agentweaver-id-orchestrator", expected, CoreVersions));
    }

    [Fact]
    public void RejectsAnUnplannedRuntimeRoleOrSchema()
    {
        Assert.Throws<InvalidOperationException>(() => PlannedRuntimeGrants.Load(
            "orchestrator", "orchestrator", "agentweaver-id-events-and-sessions", ReviewedPlanSha256, CoreVersions));
        Assert.Throws<InvalidOperationException>(() => PlannedRuntimeGrants.Load(
            "orchestrator", "public", "agentweaver-id-orchestrator", ReviewedPlanSha256, CoreVersions));
        Assert.Throws<InvalidOperationException>(() => PlannedRuntimeGrants.Load(
            "identity-broker", "identity", "agentweaver-id-identity-broker", ReviewedPlanSha256, CoreVersions));
    }

    [Fact]
    public void RejectsACatalogThatAddsOrDropsAMigration()
    {
        // An image that compiled 032 in must stop before migrating.
        int[] with032 = [.. CoreVersions, 32];
        var error = Assert.Throws<InvalidOperationException>(() => PlannedRuntimeGrants.Load(
            "orchestrator", "orchestrator", "agentweaver-id-orchestrator", ReviewedPlanSha256, with032));
        Assert.Contains("catalog", error.Message);

        Assert.Throws<InvalidOperationException>(() => PlannedRuntimeGrants.Load(
            "events-and-sessions", "events_sessions", "agentweaver-id-events-and-sessions", ReviewedPlanSha256, [.. Enumerable.Range(1, 8)]));
    }

    [Fact]
    public void MarkerNamesServicePlanAndLedger()
    {
        var plan = PlannedRuntimeGrants.Load(
            "events-and-sessions", "events_sessions", "agentweaver-id-events-and-sessions", ReviewedPlanSha256, EventsVersions);
        var marker = PlannedRuntimeGrants.FormatMarker(plan, EventsVersions);
        Assert.StartsWith(PlannedRuntimeGrants.Marker, marker);
        Assert.Contains("events-and-sessions", marker);
        Assert.Contains(ReviewedPlanSha256, marker);
    }

    [Fact]
    public void QuotesIdentifiers() =>
        Assert.Equal("\"a\"\"b\"", PlannedRuntimeGrants.Quote("a\"b"));

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
