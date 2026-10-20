using Agentweaver.EventsAndSessions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

/// <summary>Checks that usage queries cannot silently discard unsupported or repeated parameters.</summary>
public sealed class ProjectUsageQueryTests
{
    /// <summary>Rejects invalid ranges and any query parameter outside the usage route contract.</summary>
    [Theory]
    [InlineData("?from=x")]
    [InlineData("?from=2026-10-01&from=2026-10-02")]
    [InlineData("?to=2026-10-01&to=2026-10-02")]
    [InlineData("?bucket=day")]
    [InlineData("?runId=foreign")]
    [InlineData("?from=2026-10-02&to=2026-10-01")]
    [InlineData("?from=2026-01-01&to=2026-10-01")]
    public void InvalidQueriesFail(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new(query);
        Assert.Equal("query_invalid", Assert.Throws<ArgumentException>(() =>
            ProjectUsageEndpoints.ReadQuery(context.Request.Query, DateTimeOffset.UtcNow)).Message);
    }

    /// <summary>Defaults to exactly the last 30 days and accepts explicit empty intervals.</summary>
    [Fact]
    public void DefaultsAndEmptyIntervalsAreExplicit()
    {
        var context = new DefaultHttpContext();
        var now = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
        var range = ProjectUsageEndpoints.ReadQuery(context.Request.Query, now);
        Assert.Equal(now, range.To);
        Assert.Equal(now.AddDays(-30), range.From);
        context.Request.QueryString = new("?from=2026-10-01&to=2026-10-01");
        range = ProjectUsageEndpoints.ReadQuery(context.Request.Query, now);
        Assert.Equal(range.From, range.To);
    }
}
