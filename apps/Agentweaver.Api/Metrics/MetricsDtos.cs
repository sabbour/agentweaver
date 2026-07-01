using System.Text.Json.Serialization;

namespace Agentweaver.Api.Metrics;

// All metrics DTOs below are sourced exclusively from live SQLite stores (runs, backlog_tasks,
// projects) and the in-process coordinator heartbeat surface. No values are fabricated, estimated,
// or mocked (Constitution Principle VII). Where Agentweaver has no real source for a metric
// (notably $ COST, and per-workflow health because a run row does not record which workflow
// definition it used) the field is OMITTED entirely rather than invented.

// =====================================================================================
// ENDPOINT 1 — Per-project Dashboard: GET /api/projects/{id}/dashboard
// =====================================================================================

/// <summary>Headline counters for a single project's dashboard.</summary>
public sealed record DashboardSummaryDto
{
    [JsonPropertyName("runs_this_week")]      public required int RunsThisWeek      { get; init; }
    [JsonPropertyName("runs_total")]          public required int RunsTotal         { get; init; }
    [JsonPropertyName("active_runs")]         public required int ActiveRuns        { get; init; }
    [JsonPropertyName("active_agents")]       public required int ActiveAgents      { get; init; }
    /// <summary>
    /// Runs that reached a terminal SUCCESS status (merged or completed) in the last 7 days.
    /// This is the only real "done" signal available — backlog tasks have no terminal "done"
    /// state (states are backlog/ready/claimed), so completed runs are used as the done signal.
    /// </summary>
    [JsonPropertyName("tasks_done_this_week")] public required int TasksDoneThisWeek { get; init; }
}

/// <summary>One day in the 30-day throughput series.</summary>
public sealed record ThroughputPointDto
{
    [JsonPropertyName("date")]    public required string Date    { get; init; }
    [JsonPropertyName("created")] public required int    Created { get; init; }
    [JsonPropertyName("done")]    public required int    Done    { get; init; }
}

/// <summary>Per-agent activity and quality on a single project, sourced from Application Insights.</summary>
public sealed record AgentLeaderboardEntryDto
{
    [JsonPropertyName("agentName")]      public required string AgentName      { get; init; }
    [JsonPropertyName("role")]           public string?         Role           { get; init; }
    [JsonPropertyName("runsThisWeek")]   public required int    RunsThisWeek   { get; init; }
    [JsonPropertyName("runsTotal")]      public required int    RunsTotal      { get; init; }
    [JsonPropertyName("successRate")]    public required int    SuccessRate    { get; init; }
    [JsonPropertyName("successfulRuns")] public required int    SuccessfulRuns { get; init; }
    [JsonPropertyName("terminalRuns")]   public required int    TerminalRuns   { get; init; }
    [JsonPropertyName("avgDurationMs")]  public long?           AvgDurationMs  { get; init; }
    [JsonPropertyName("costAic")]        public decimal         CostAic        { get; init; }
}

/// <summary>Legacy dashboard leaderboard shape preserved on /dashboard for compatibility.</summary>
public sealed record DashboardAgentLeaderboardEntryDto
{
    [JsonPropertyName("agent")]           public required string  Agent          { get; init; }
    [JsonPropertyName("role_title")]      public string?          RoleTitle      { get; init; }
    [JsonPropertyName("runs_this_week")]  public required int     RunsThisWeek   { get; init; }
    [JsonPropertyName("runs_total")]      public required int     RunsTotal      { get; init; }
    [JsonPropertyName("success_rate")]    public required double  SuccessRate    { get; init; }
    [JsonPropertyName("successful_runs")] public required int     SuccessfulRuns { get; init; }
    [JsonPropertyName("terminal_runs")]   public required int     TerminalRuns   { get; init; }
    [JsonPropertyName("avg_duration_ms")] public required double? AvgDurationMs  { get; init; }
}

/// <summary>Per-project dashboard response.</summary>
public sealed record ProjectDashboardDto
{
    [JsonPropertyName("project_id")]        public required string                                       ProjectId        { get; init; }
    [JsonPropertyName("project_name")]      public required string                                       ProjectName      { get; init; }
    [JsonPropertyName("generated_utc")]     public required DateTimeOffset                               GeneratedUtc     { get; init; }
    [JsonPropertyName("summary")]           public required DashboardSummaryDto                          Summary          { get; init; }
    [JsonPropertyName("throughput")]        public required IReadOnlyList<ThroughputPointDto>            Throughput       { get; init; }
    [JsonPropertyName("agent_leaderboard")] public required IReadOnlyList<DashboardAgentLeaderboardEntryDto> AgentLeaderboard { get; init; }
}

/// <summary>Per-project dashboard widgets sourced from Application Insights.</summary>
public sealed record ProjectMetricsDto
{
    [JsonPropertyName("throughput")]  public required IReadOnlyList<ThroughputPointDto> Throughput { get; init; }
    [JsonPropertyName("leaderboard")] public required IReadOnlyList<AgentLeaderboardEntryDto> Leaderboard { get; init; }
}

// =====================================================================================
// ENDPOINT 2 — Global Overview ("Now"): GET /api/overview
// =====================================================================================

/// <summary>Cross-project headline counters. No cost field.</summary>
public sealed record AtAGlanceDto
{
    /// <summary>Runs currently in_progress across all projects (each is one agent actively working).</summary>
    [JsonPropertyName("in_flight")]       public required int    InFlight       { get; init; }
    /// <summary>Ready backlog tasks plus pending (not-yet-started) runs, across all projects.</summary>
    [JsonPropertyName("queued_work")]     public required int    QueuedWork     { get; init; }
    /// <summary>Runs that reached terminal SUCCESS (merged/completed) today (UTC, by ended_at).</summary>
    [JsonPropertyName("done_today")]      public required int    DoneToday      { get; init; }
    /// <summary>Projects with at least one in_progress run or one ready backlog task.</summary>
    [JsonPropertyName("active_projects")] public required int    ActiveProjects { get; init; }
    /// <summary>
    /// "healthy" or "degraded". Degraded when the coordinator heartbeat is disabled, OR the most
    /// recent heartbeat tick recorded an error, OR any run is currently in 'merge_failed'.
    /// </summary>
    [JsonPropertyName("health")]          public required string Health         { get; init; }
}

/// <summary>An active run surfaced as a live session.</summary>
public sealed record LiveSessionDto
{
    [JsonPropertyName("project_id")]       public required string         ProjectId       { get; init; }
    [JsonPropertyName("project_name")]     public required string         ProjectName     { get; init; }
    [JsonPropertyName("agent")]            public required string?        Agent           { get; init; }
    [JsonPropertyName("status")]           public required string         Status          { get; init; }
    [JsonPropertyName("started_utc")]      public required DateTimeOffset StartedUtc      { get; init; }
    /// <summary>The run's most recent persisted state-change timestamp (ended_at if set, else started_at).</summary>
    [JsonPropertyName("last_activity_utc")] public required DateTimeOffset LastActivityUtc { get; init; }
}

/// <summary>
/// An in-progress/pending orchestration (workflow) run. The 'workflow' name and 'current_step'
/// sub-fields are omitted because a run row records neither a workflow-definition reference nor a
/// named current step. 'trigger' IS real (the run origin: interactive vs backlog_pickup).
/// </summary>
public sealed record ActiveWorkflowRunDto
{
    [JsonPropertyName("project_id")]   public required string         ProjectId   { get; init; }
    [JsonPropertyName("project_name")] public required string         ProjectName { get; init; }
    [JsonPropertyName("trigger")]      public required string         Trigger     { get; init; }
    [JsonPropertyName("status")]       public required string         Status      { get; init; }
    [JsonPropertyName("started_utc")]  public required DateTimeOffset StartedUtc  { get; init; }
}

/// <summary>Per-project rollup of active and queued work.</summary>
public sealed record ActiveProjectDto
{
    [JsonPropertyName("project_id")]        public required string          ProjectId       { get; init; }
    [JsonPropertyName("project_name")]      public required string          ProjectName     { get; init; }
    [JsonPropertyName("active_count")]      public required int             ActiveCount     { get; init; }
    [JsonPropertyName("queued_count")]      public required int             QueuedCount     { get; init; }
    [JsonPropertyName("last_activity_utc")] public required DateTimeOffset? LastActivityUtc { get; init; }
}

/// <summary>A recent run/orchestration lifecycle event.</summary>
public sealed record RecentActivityDto
{
    [JsonPropertyName("project_id")]    public required string         ProjectId    { get; init; }
    [JsonPropertyName("project_name")]  public required string         ProjectName  { get; init; }
    [JsonPropertyName("label")]         public required string         Label        { get; init; }
    [JsonPropertyName("kind")]          public required string         Kind         { get; init; }
    [JsonPropertyName("timestamp_utc")] public required DateTimeOffset TimestampUtc { get; init; }
}

/// <summary>Global cross-project "Now" overview response. No cost field anywhere.</summary>
public sealed record OverviewDto
{
    [JsonPropertyName("generated_utc")]        public required DateTimeOffset                          GeneratedUtc       { get; init; }
    [JsonPropertyName("at_a_glance")]          public required AtAGlanceDto                            AtAGlance          { get; init; }
    [JsonPropertyName("live_sessions")]        public required IReadOnlyList<LiveSessionDto>           LiveSessions       { get; init; }
    [JsonPropertyName("active_workflow_runs")] public required IReadOnlyList<ActiveWorkflowRunDto>     ActiveWorkflowRuns { get; init; }
    [JsonPropertyName("active_projects")]      public required IReadOnlyList<ActiveProjectDto>         ActiveProjects     { get; init; }
    [JsonPropertyName("recent_activity")]      public required IReadOnlyList<RecentActivityDto>        RecentActivity     { get; init; }
}
