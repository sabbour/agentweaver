using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

/// <summary>Exercises reporting query validation without a PostgreSQL dependency.</summary>
public sealed class ProjectMetricsQueryTests
{
    /// <summary>Accepts only the daily bucket and a bounded, half-open, explicitly zoned interval.</summary>
    [Theory]
    [InlineData("?from=2026-10-01&to=2026-10-03&bucket=day", 2)]
    [InlineData("?from=2026-10-01T02%3A00%3A00%2B02%3A00&to=2026-10-03T00%3A00%3A00Z", 2)]
    [InlineData("?from=2026-10-01&to=2026-10-01", 0)]
    [InlineData("?from=2026-07-01&to=2026-10-01", 92)]
    [InlineData("", 30)]
    public void AcceptsBoundedRanges(string query, int days)
    {
        var range = Read(query);
        Assert.Equal(TimeSpan.FromDays(days), range.To - range.From);
        Assert.Equal(TimeSpan.Zero, range.From.Offset);
    }

    /// <summary>Rejects duplicate keys, unknown names, ambiguous dates, and invalid intervals.</summary>
    [Theory]
    [InlineData("?from=2026-10-01&from=2026-10-02")]
    [InlineData("?extra=x")]
    [InlineData("?bucket=week")]
    [InlineData("?bucket=")]
    [InlineData("?from=2026-01-01&to=2026-10-01")]
    [InlineData("?from=2026-10-02&to=2026-10-01")]
    [InlineData("?from=2026-02-30")]
    [InlineData("?from=10/01/2026")]
    [InlineData("?from=2026-10-01T00:00:00")]
    [InlineData("?to=")]
    public void RejectsInvalidRanges(string query)
    {
        var exception = Record.Exception(() => Read(query));
        Assert.True(exception is ArgumentException or CoordinationException);
        Assert.Equal("query_invalid", exception is CoordinationException owner ? owner.Code : exception!.Message);
    }

    /// <summary>Uses the owner query allow list and shared range contract with a fixed clock.</summary>
    private static ProjectMetricsRange Read(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new(query);
        var values = ProjectRunEndpoints.ReadSingleValuedQuery(
            context.Request.Query, new(["from", "to", "bucket"], StringComparer.Ordinal));
        return ProjectMetricsRange.Parse(values.GetValueOrDefault("from"), values.GetValueOrDefault("to"),
            values.GetValueOrDefault("bucket"), DateTimeOffset.Parse("2026-10-20T00:00:00Z"));
    }
}
