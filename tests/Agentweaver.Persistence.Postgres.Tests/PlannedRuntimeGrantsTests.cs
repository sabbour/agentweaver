using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Persistence.Postgres;
using Xunit;

namespace Agentweaver.Persistence.Postgres.Tests;

// Pure tests for the plan gates. They need no database: every gate throws before a connection opens.
public sealed class PlannedRuntimeGrantsTests
{
    private const string ReviewedPlanSha256 = "8adecd6c19d060282f69ea96fd15eb430c2b213d69db7b0bb7706807a9486e1c";
    private static readonly int[] CoreVersions = [.. Enumerable.Range(1, 16), .. Enumerable.Range(18, 15), 33, 35];
    private static readonly int[] EventsVersions = [.. Enumerable.Range(1, 9)];
    private static readonly int[] EnvironmentVersions = [.. Enumerable.Range(1, 11)];

    [Fact]
    public void EmbeddedPlanIsTheReviewedPlan()
    {
        var bytes = PlannedRuntimeGrants.ReadEmbeddedPlan();
        Assert.Equal(ReviewedPlanSha256, CanonicalSha256(bytes));
    }

    [Fact]
    public void SelectsOrchestratorAndEventsGrantsFromTheReviewedPlan()
    {
        var core = PlannedRuntimeGrants.Load(
            "orchestrator", "orchestrator", "agentweaver-id-orchestrator", ReviewedPlanSha256, CoreVersions);
        Assert.Equal(CoreVersions, core.Versions);
        Assert.DoesNotContain(17, core.Versions);
        Assert.Contains(32, core.Versions);
        Assert.Contains(33, core.Versions);
        Assert.Contains(35, core.Versions);
        Assert.NotEmpty(core.Tables);
        var pullRequestRecords = Assert.Single(core.Tables, table => table.Table == "source_control_pull_requests");
        Assert.Equal(["SELECT", "INSERT", "UPDATE"], pullRequestRecords.Privileges);

        var events = PlannedRuntimeGrants.Load(
            "events-and-sessions", "events_sessions", "agentweaver-id-events-and-sessions", ReviewedPlanSha256, EventsVersions);
        Assert.Equal(EventsVersions, events.Versions);
        Assert.NotEmpty(events.Tables);

        var environment = PlannedRuntimeGrants.Load(
            "environment", "environment", "agentweaver-id-environment", ReviewedPlanSha256, EnvironmentVersions);
        Assert.Equal(EnvironmentVersions, environment.Versions);
        Assert.Contains(environment.Tables, table => table.Table == "remote_mcp_connections");
        Assert.Contains(environment.Tables, table => table.Table == "remote_mcp_connection_configurations");
    }

    [Fact]
    public void CanonicalPlanHashIgnoresWhitespaceAndRejectsContentChanges()
    {
        var bytes = PlannedRuntimeGrants.ReadEmbeddedPlan();
        var formatted = bytes.Concat(Encoding.UTF8.GetBytes(" \n")).ToArray();
        var plan = PlannedRuntimeGrants.Select(
            formatted, "orchestrator", "orchestrator", "agentweaver-id-orchestrator", ReviewedPlanSha256, CoreVersions);
        Assert.Equal("orchestrator", plan.Service);

        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes)
            .Replace("agentweaver-id-orchestrator", "agentweaver-id-attacker", StringComparison.Ordinal));
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
        // A compiled migrator that omits reviewed 032 must stop before applying a different plan.
        int[] without032 = [.. CoreVersions.Where(version => version != 32)];
        var error = Assert.Throws<InvalidOperationException>(() => PlannedRuntimeGrants.Load(
            "orchestrator", "orchestrator", "agentweaver-id-orchestrator", ReviewedPlanSha256, without032));
        Assert.Contains("catalog", error.Message);

        Assert.Throws<InvalidOperationException>(() => PlannedRuntimeGrants.Load(
            "events-and-sessions", "events_sessions", "agentweaver-id-events-and-sessions", ReviewedPlanSha256, [.. Enumerable.Range(1, 8)]));
        Assert.Throws<InvalidOperationException>(() => PlannedRuntimeGrants.Load(
            "environment", "environment", "agentweaver-id-environment", ReviewedPlanSha256, [.. Enumerable.Range(1, 10)]));
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

    private static string CanonicalSha256(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var canonical = JsonSerializer.SerializeToUtf8Bytes(document.RootElement);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }
}
