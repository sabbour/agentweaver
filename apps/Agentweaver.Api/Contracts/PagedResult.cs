using System.Text.Json.Serialization;

namespace Agentweaver.Api.Contracts;

/// <summary>
/// Standard pagination envelope for all list-returning GET endpoints. Every paginated endpoint
/// returns this shape instead of a bare array so callers can reliably read <see cref="TotalCount"/>
/// and <see cref="TotalPages"/> without a separate request. Field names are snake_case to match the
/// rest of the API's JSON contract (see <see cref="Dtos"/>).
/// </summary>
public sealed record PagedResult<T>
{
    [JsonPropertyName("items")]
    public required IReadOnlyList<T> Items { get; init; }

    [JsonPropertyName("page")]
    public required int Page { get; init; }

    [JsonPropertyName("page_size")]
    public required int PageSize { get; init; }

    [JsonPropertyName("total_count")]
    public required int TotalCount { get; init; }

    [JsonPropertyName("total_pages")]
    public required int TotalPages { get; init; }
}

/// <summary>
/// Shared query-parameter normalization and slicing for the <see cref="PagedResult{T}"/> contract.
/// <c>page</c> is 1-based; out-of-range or missing values fall back to sane defaults instead of
/// erroring, so a request for a page beyond the available data returns an empty <c>items</c> list
/// rather than a 400/404. <c>page_size</c> is clamped to <see cref="MaxPageSize"/> to bound response
/// size regardless of what a caller requests.
/// </summary>
public static class Paging
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    /// <summary>Normalizes raw <c>page</c>/<c>page_size</c> query values into valid, bounded values.</summary>
    public static (int Page, int PageSize) Normalize(int? page, int? pageSize)
    {
        var normalizedPage = page.GetValueOrDefault(1);
        if (normalizedPage < 1) normalizedPage = 1;

        var normalizedPageSize = pageSize.GetValueOrDefault(DefaultPageSize);
        if (normalizedPageSize < 1) normalizedPageSize = DefaultPageSize;
        if (normalizedPageSize > MaxPageSize) normalizedPageSize = MaxPageSize;

        return (normalizedPage, normalizedPageSize);
    }

    /// <summary>
    /// Slices an already-materialized, ordered list into a <see cref="PagedResult{T}"/> using
    /// normalized page/page_size values. Callers should apply filtering and ordering before calling
    /// this so the slice reflects the caller's requested view.
    /// </summary>
    public static PagedResult<T> Of<T>(IReadOnlyList<T> orderedItems, int? page, int? pageSize)
    {
        var (normalizedPage, normalizedPageSize) = Normalize(page, pageSize);
        var totalCount = orderedItems.Count;
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)normalizedPageSize);

        // Compute the skip count with `long` arithmetic so an absurdly large `page` (e.g.
        // 100_000_000) cannot overflow int32 into a negative value — which LINQ's Skip silently
        // clamps to 0, causing an out-of-range page to incorrectly return page 1's items while
        // echoing back the bogus page number. Any skip at or beyond totalCount yields an empty page.
        var skip = (long)(normalizedPage - 1) * normalizedPageSize;
        var pageItems = skip >= totalCount
            ? []
            : orderedItems
                .Skip((int)skip)
                .Take(normalizedPageSize)
                .ToList();

        return new PagedResult<T>
        {
            Items = pageItems,
            Page = normalizedPage,
            PageSize = normalizedPageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
        };
    }
}
