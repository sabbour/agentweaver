using System.Text.Json.Serialization;

namespace Agentweaver.Api.Diagnostics;

/// <summary>
/// Wire shape of a single heartbeat tick outcome in the ring buffer.
/// </summary>
public sealed record TickRecordDto
{
    [JsonPropertyName("timestamp_utc")] public required DateTimeOffset TimestampUtc { get; init; }
    [JsonPropertyName("acted_count")]   public required int            ActedCount   { get; init; }
    [JsonPropertyName("error_count")]   public required int            ErrorCount   { get; init; }
    [JsonPropertyName("duration_ms")]   public required double         DurationMs   { get; init; }
    [JsonPropertyName("error")]         public required string?        Error        { get; init; }
}

/// <summary>
/// Describes one real background automation running in the API process.
/// </summary>
public sealed record AutomationDto
{
    [JsonPropertyName("name")]              public required string         Name            { get; init; }
    [JsonPropertyName("description")]       public required string         Description     { get; init; }
    [JsonPropertyName("cadence_seconds")]   public required double         CadenceSeconds  { get; init; }
    [JsonPropertyName("last_run_utc")]      public required DateTimeOffset? LastRunUtc     { get; init; }
    [JsonPropertyName("last_acted_count")]  public required int?           LastActedCount  { get; init; }
    [JsonPropertyName("status")]            public required string         Status          { get; init; }
}

/// <summary>
/// Read-only snapshot of the coordinator heartbeat service's observable state (FR-017).
/// Returned identically by the REST endpoint and the MCP tool (FR-017a).
/// </summary>
public sealed record HeartbeatStatusDto
{
    [JsonPropertyName("enabled")]          public required bool                        Enabled        { get; init; }
    [JsonPropertyName("interval_seconds")] public required double                      IntervalSeconds { get; init; }
    [JsonPropertyName("last_tick_utc")]    public required DateTimeOffset?             LastTickUtc    { get; init; }
    [JsonPropertyName("service_status")]   public required string                      ServiceStatus  { get; init; }
    [JsonPropertyName("last_error")]       public required string?                     LastError      { get; init; }
    [JsonPropertyName("recent_activity")]  public required IReadOnlyList<TickRecordDto> RecentActivity { get; init; }
    [JsonPropertyName("automations")]      public required IReadOnlyList<AutomationDto> Automations    { get; init; }
}
