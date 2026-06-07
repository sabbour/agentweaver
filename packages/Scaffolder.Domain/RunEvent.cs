namespace Scaffolder.Domain;

/// <summary>
/// Common event envelope (FR-018). The payload is JSON and never contains raw
/// file contents. <see cref="Sequence"/> is a per-run monotonic counter
/// (FR-019); <see cref="Timestamp"/> is informational only (FR-018).
/// </summary>
public sealed record RunEvent
{
    public required RunId RunId { get; init; }
    public required int Sequence { get; init; }
    public required string Type { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Payload { get; init; }
    public string? CallId { get; init; }
}
