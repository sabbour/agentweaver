using System.Globalization;

namespace Agentweaver.Abstractions;

/// <summary>A bounded half-open UTC reporting interval shared by the two project read owners.</summary>
public sealed record ProjectMetricsRange(DateTimeOffset From, DateTimeOffset To)
{
    /// <summary>Parses ISO dates or timestamps with explicit offsets; defaults to the preceding 30 days.</summary>
    /// <exception cref="ArgumentException">The range or bucket is invalid, outside supported dates, reversed, or exceeds 92 days.</exception>
    public static ProjectMetricsRange Parse(string? from, string? to, string? bucket, DateTimeOffset now)
    {
        if (bucket is not null && bucket != "day")
            throw new ArgumentException("query_invalid");
        var end = to is null ? now.ToUniversalTime() : ReadDate(to);
        if (from is null && end < DateTimeOffset.MinValue.AddDays(30))
            throw new ArgumentException("query_invalid");
        var start = from is null ? end.AddDays(-30) : ReadDate(from);
        if (start > end || end - start > TimeSpan.FromDays(92))
            throw new ArgumentException("query_invalid");
        return new(start, end);
    }

    /// <summary>Rejects culture-dependent dates and timestamps without an explicit UTC offset.</summary>
    private static DateTimeOffset ReadDate(string value)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            return new(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        if (DateTimeOffset.TryParseExact(value,
                ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp) &&
            (value.EndsWith('Z') || value.Length >= 6 && value[^6] is '+' or '-'))
            return timestamp.ToUniversalTime();
        throw new ArgumentException("query_invalid");
    }
}

/// <summary>Counts run creation and terminal completion on one UTC calendar day.</summary>
public sealed record ProjectMetricsDay(string Date, long Created, long Completed);

/// <summary>Current state counts, lifetime acceptance count, and interval-specific run outcomes.</summary>
public sealed record ProjectMetrics(
    string ProjectId, DateTimeOffset From, DateTimeOffset To,
    long ActiveRuns, long BlockedRuns, long RunsInRange, long RunsTotal,
    long CompletedRuns, long SucceededRuns, IReadOnlyList<ProjectMetricsDay> Series);

/// <summary>One persisted cost subtotal; amounts from different units or dispositions remain separate.</summary>
public sealed record ProjectUsageCost(string? PriceUnit, string Disposition, decimal? Amount);

/// <summary>Ledger measurements for one model or agent; null denotes an incomplete measurement.</summary>
public sealed record ProjectUsageItem(
    string Id, long? RequestCount, decimal? InputTokens, decimal? OutputTokens,
    decimal? CachedTokens, decimal? CacheWriteTokens, decimal? ReasoningTokens,
    decimal? AvgDurationMs, long Runs, long? UnpricedRequestCount,
    IReadOnlyList<ProjectUsageCost> Cost);

/// <summary>Project usage grouped independently by persisted model ID and agent ID.</summary>
public sealed record ProjectUsage(
    string ProjectId, DateTimeOffset From, DateTimeOffset To,
    IReadOnlyList<ProjectUsageItem> UsageByModel, IReadOnlyList<ProjectUsageItem> UsageByAgent);
