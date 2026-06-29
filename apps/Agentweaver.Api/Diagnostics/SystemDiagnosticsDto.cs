using System.Text.Json.Serialization;

namespace Agentweaver.Api.Diagnostics;

/// <summary>
/// A single executed diagnostic probe with its outcome (FR-016).
/// </summary>
public sealed record DiagnosticsCheckDto
{
    [JsonPropertyName("name")]        public required string Name       { get; init; }
    /// <summary><c>"pass"</c>, <c>"warn"</c>, or <c>"fail"</c>.</summary>
    [JsonPropertyName("status")]      public required string Status     { get; init; }
    [JsonPropertyName("detail")]      public required string Detail     { get; init; }
    [JsonPropertyName("duration_ms")] public required double DurationMs { get; init; }
}

/// <summary>
/// Global system diagnostics response (FR-016). All fields sourced from live state; no mocks.
/// Returned identically by the REST endpoint and the MCP tool (FR-016a).
/// </summary>
public sealed record SystemDiagnosticsDto
{
    [JsonPropertyName("api_version")]        public required string                         ApiVersion       { get; init; }
    [JsonPropertyName("process_started_utc")] public required DateTimeOffset               ProcessStartedUtc { get; init; }
    [JsonPropertyName("uptime_seconds")]     public required double                         UptimeSeconds    { get; init; }
    [JsonPropertyName("total_projects")]     public required int                            TotalProjects    { get; init; }
    [JsonPropertyName("total_runs")]         public required int                            TotalRuns        { get; init; }
    [JsonPropertyName("active_runs")]        public required int                            ActiveRuns       { get; init; }
    [JsonPropertyName("generated_utc")]      public required DateTimeOffset                 GeneratedUtc     { get; init; }
    [JsonPropertyName("total_duration_ms")]  public required double                         TotalDurationMs  { get; init; }
    [JsonPropertyName("checks")]             public required IReadOnlyList<DiagnosticsCheckDto> Checks       { get; init; }

    /// <summary>
    /// Agent-pod CPU quota headroom (spec: 24-core namespace quota, 2 CPU per agent pod). Null when
    /// the API is not running against a Kubernetes backend.
    /// </summary>
    [JsonPropertyName("agent_pod_quota")]    public AgentPodQuotaDiagnosticDto?            AgentPodQuota    { get; init; }
}

/// <summary>
/// Agent-pod CPU quota diagnostic (spec-006). Surfaces whether the namespace ResourceQuota still has
/// room to start another agent pod, so an exhausted quota is visible in Diagnostics instead of
/// silently failing every new run.
/// </summary>
public sealed record AgentPodQuotaDiagnosticDto
{
    [JsonPropertyName("name")]   public required string Name   { get; init; }
    /// <summary><c>"healthy"</c>, <c>"warning"</c>, <c>"critical"</c>, or <c>"unknown"</c>.</summary>
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("used")]   public double?         Used   { get; init; }
    [JsonPropertyName("limit")]  public double?         Limit  { get; init; }
    [JsonPropertyName("unit")]   public required string Unit   { get; init; }
}

/// <summary>
/// Project-scoped diagnostics response for a single project's workspace and configuration.
/// </summary>
public sealed record ProjectDiagnosticsDto
{
    [JsonPropertyName("project_id")]        public required string                         ProjectId       { get; init; }
    [JsonPropertyName("project_name")]      public required string                         ProjectName     { get; init; }
    [JsonPropertyName("generated_utc")]     public required DateTimeOffset                 GeneratedUtc    { get; init; }
    [JsonPropertyName("total_duration_ms")] public required double                         TotalDurationMs { get; init; }
    [JsonPropertyName("checks")]            public required IReadOnlyList<DiagnosticsCheckDto> Checks      { get; init; }
}
