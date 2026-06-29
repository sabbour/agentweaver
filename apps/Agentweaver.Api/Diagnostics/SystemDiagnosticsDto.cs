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

/// <summary>
/// A single detailed dependency health check (spec-006, Change to Task 3c). Uniform shape across
/// every critical dependency so the Diagnostics panel can surface a broken dependency instead of
/// reporting "everything fine". <see cref="Status"/> is one of <c>"healthy"</c>, <c>"degraded"</c>,
/// <c>"warning"</c>, <c>"critical"</c>, or <c>"unknown"</c>.
/// </summary>
public sealed record DetailedHealthCheckDto
{
    [JsonPropertyName("name")]      public required string Name      { get; init; }
    [JsonPropertyName("status")]    public required string Status    { get; init; }
    [JsonPropertyName("message")]   public required string Message   { get; init; }
    [JsonPropertyName("latencyMs")] public required double LatencyMs { get; init; }

    // ── Optional, check-specific detail (omitted from JSON when null). ──────────────

    /// <summary>Agent-pod quota: CPU cores currently used against the namespace quota.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("used")] public double? Used { get; init; }

    /// <summary>Agent-pod quota: hard CPU-core limit configured on the namespace quota.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("limit")] public double? Limit { get; init; }

    /// <summary>Agent-pod quota / warm pool: unit of <see cref="Used"/>/<see cref="Limit"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("unit")] public string? Unit { get; init; }

    /// <summary>Agent-pod quota: number of subtasks currently parked in PendingCapacity.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("pendingCount")] public int? PendingCount { get; init; }
}

/// <summary>
/// Detailed cluster diagnostics response (spec-006). The critical-dependency checks plus a live
/// inventory of agent-host pods (active vs orphaned) and subtasks waiting on capacity. Powers the
/// dedicated frontend "Cluster" page (<c>GET /api/diagnostics/cluster</c>).
/// </summary>
public sealed record ClusterDiagnosticsDto
{
    [JsonPropertyName("generated_utc")]     public required DateTimeOffset                        GeneratedUtc        { get; init; }
    [JsonPropertyName("total_duration_ms")] public required double                                TotalDurationMs     { get; init; }
    [JsonPropertyName("checks")]            public required IReadOnlyList<DetailedHealthCheckDto>  Checks              { get; init; }

    /// <summary>Running <c>agent-*</c> pods that belong to a currently active run.</summary>
    [JsonPropertyName("active_agent_pods")]    public required IReadOnlyList<AgentPodInfoDto>      ActiveAgentPods    { get; init; }

    /// <summary>Running <c>agent-*</c> pods whose run is finished/failed/gone — the reaper's targets.</summary>
    [JsonPropertyName("orphaned_agent_pods")]  public required IReadOnlyList<AgentPodInfoDto>      OrphanedAgentPods  { get; init; }

    /// <summary>Subtasks parked in PendingCapacity waiting for an agent pod slot to free up.</summary>
    [JsonPropertyName("pending_capacity_runs")] public required IReadOnlyList<PendingCapacityRunDto> PendingCapacityRuns { get; init; }
}

/// <summary>A single agent-host pod / SandboxClaim in the cluster inventory.</summary>
public sealed record AgentPodInfoDto
{
    [JsonPropertyName("claim_name")] public required string  ClaimName  { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("run_id")]     public string?          RunId      { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("pod_name")]   public string?          PodName    { get; init; }
    /// <summary><c>"ready"</c> or <c>"pending"</c>.</summary>
    [JsonPropertyName("status")]     public required string  Status     { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("age_seconds")] public double?         AgeSeconds { get; init; }
}

/// <summary>A subtask parked in PendingCapacity awaiting an agent-host pod slot.</summary>
public sealed record PendingCapacityRunDto
{
    [JsonPropertyName("subtask_id")]   public required int    SubtaskId   { get; init; }
    [JsonPropertyName("work_plan_id")] public required int    WorkPlanId  { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("child_run_id")] public string?         ChildRunId  { get; init; }
    [JsonPropertyName("status")]       public required string Status      { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("reason")]       public string?         Reason      { get; init; }
    [JsonPropertyName("age_seconds")]  public required double AgeSeconds  { get; init; }
}
