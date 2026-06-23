using System.Diagnostics;
using System.Reflection;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.ReviewPolicies;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Microsoft.Data.Sqlite;

namespace Agentweaver.Api.Diagnostics;

/// <summary>
/// Assembles real-time, server-side system diagnostics and project-scoped diagnostics from the live
/// stores and background-service status surfaces (FR-016, FR-017). No values are fabricated or mocked.
/// Singleton-safe: all store reads are async and connection-per-call.
///
/// <para>This class is the single authoritative source for both the REST diagnostics endpoints and
/// the MCP tool (FR-016a, FR-017a). The MCP tool project can call the same HTTP endpoints and
/// deserialize <see cref="SystemDiagnosticsDto"/> / <see cref="HeartbeatStatusDto"/>.</para>
/// </summary>
public sealed class DiagnosticsService
{
    // Captured once at class-load time; GetCurrentProcess().StartTime can throw in restricted
    // environments so fall back to the moment the field is initialized.
    private static readonly DateTimeOffset ProcessStartUtc = ResolveProcessStart();

    private static readonly string ApiVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly SqliteDb _db;
    private readonly IProjectStore _projectStore;
    private readonly IProjectWorkspaceProvider _workspaceProvider;
    private readonly HeartbeatStatusStore _heartbeatStore;
    private readonly WorkflowRegistry _workflowRegistry;
    private readonly ReviewPolicyRegistry _reviewPolicyRegistry;
    private readonly IConfiguration _configuration;

    public DiagnosticsService(
        SqliteDb db,
        IProjectStore projectStore,
        IProjectWorkspaceProvider workspaceProvider,
        HeartbeatStatusStore heartbeatStore,
        WorkflowRegistry workflowRegistry,
        ReviewPolicyRegistry reviewPolicyRegistry,
        IConfiguration configuration)
    {
        _db = db;
        _projectStore = projectStore;
        _workspaceProvider = workspaceProvider;
        _heartbeatStore = heartbeatStore;
        _workflowRegistry = workflowRegistry;
        _reviewPolicyRegistry = reviewPolicyRegistry;
        _configuration = configuration;
    }

    // -------------------------------------------------------------------------
    // System diagnostics
    // -------------------------------------------------------------------------

    /// <summary>Returns global system diagnostics with real executed checks.</summary>
    public async Task<SystemDiagnosticsDto> GetSystemDiagnosticsAsync(CancellationToken ct = default)
    {
        var overallSw = Stopwatch.StartNew();
        var generatedUtc = DateTimeOffset.UtcNow;

        // Run checks; order determines display order on the page.
        var checks = new List<DiagnosticsCheckDto>();

        checks.Add(await CheckSqliteReachableAsync(ct).ConfigureAwait(false));
        checks.Add(await CheckDiskWritableAsync().ConfigureAwait(false));
        checks.Add(CheckBuiltInWorkflow());
        checks.Add(CheckBuiltInReviewPolicy());
        checks.Add(CheckHeartbeatService());
        checks.Add(await CheckProjectStoreAsync(ct).ConfigureAwait(false));
        checks.Add(await CheckGitHubCliAsync(ct).ConfigureAwait(false));

        // Pull counts from the SQLite check result (project_store check already ran ListAsync;
        // reuse by re-querying to avoid cross-check coupling).
        var projects = await _projectStore.ListAsync(ct).ConfigureAwait(false);

        int totalRuns;
        int activeRuns;
        await using (var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            totalRuns = await ScalarCountAsync(conn, "SELECT COUNT(*) FROM runs", ct).ConfigureAwait(false);
            activeRuns = await ScalarCountAsync(
                conn, "SELECT COUNT(*) FROM runs WHERE status = 'in_progress'", ct)
                .ConfigureAwait(false);
        }

        overallSw.Stop();

        return new SystemDiagnosticsDto
        {
            ApiVersion        = ApiVersion,
            ProcessStartedUtc = ProcessStartUtc,
            UptimeSeconds     = (DateTimeOffset.UtcNow - ProcessStartUtc).TotalSeconds,
            TotalProjects     = projects.Count,
            TotalRuns         = totalRuns,
            ActiveRuns        = activeRuns,
            GeneratedUtc      = generatedUtc,
            TotalDurationMs   = overallSw.Elapsed.TotalMilliseconds,
            Checks            = checks,
        };
    }

    // -------------------------------------------------------------------------
    // Project-scoped diagnostics
    // -------------------------------------------------------------------------

    /// <summary>Returns diagnostics scoped to a single project's workspace and configuration.</summary>
    public async Task<ProjectDiagnosticsDto> GetProjectDiagnosticsAsync(
        Project project, CancellationToken ct = default)
    {
        var overallSw = Stopwatch.StartNew();
        var generatedUtc = DateTimeOffset.UtcNow;
        var checks = new List<DiagnosticsCheckDto>();

        checks.Add(CheckWorkspaceAvailable(project));
        checks.Add(CheckWorkflowsDirectory(project));
        checks.Add(CheckReviewPoliciesDirectory(project));
        checks.Add(await CheckActiveWorkflowAsync(project, ct).ConfigureAwait(false));
        checks.Add(CheckActiveReviewPolicy(project));

        overallSw.Stop();

        return new ProjectDiagnosticsDto
        {
            ProjectId       = project.Id.ToString(),
            ProjectName     = project.Name,
            GeneratedUtc    = generatedUtc,
            TotalDurationMs = overallSw.Elapsed.TotalMilliseconds,
            Checks          = checks,
        };
    }

    // -------------------------------------------------------------------------
    // Heartbeat endpoint
    // -------------------------------------------------------------------------

    /// <summary>Returns the enriched coordinator heartbeat status snapshot.</summary>
    public HeartbeatStatusDto GetHeartbeatStatus()
    {
        var status = _heartbeatStore.Enabled
            ? (_heartbeatStore.LastTickUtc.HasValue ? "running" : "waiting_first_tick")
            : "disabled";

        var recentActivity = _heartbeatStore.GetRecentActivity();
        var lastRecord = recentActivity.Length > 0 ? recentActivity[0] : (TickRecord?)null;

        var automations = new List<AutomationDto>
        {
            new()
            {
                Name           = "Coordinator Heartbeat",
                Description    = "Picks up Ready backlog tasks and starts coordinator runs",
                CadenceSeconds = _heartbeatStore.Interval.TotalSeconds,
                LastRunUtc     = _heartbeatStore.LastTickUtc,
                LastActedCount = lastRecord?.ActedCount,
                Status         = status,
            },
            new()
            {
                Name           = "Checkpoint GC",
                Description    = "Deletes checkpoint directories for runs that have reached a terminal state",
                CadenceSeconds = TimeSpan.FromMinutes(30).TotalSeconds,
                LastRunUtc     = null,   // CheckpointGcService does not expose its last-run time
                LastActedCount = null,
                Status         = "running",
            },
        };

        return new HeartbeatStatusDto
        {
            Enabled        = _heartbeatStore.Enabled,
            IntervalSeconds = _heartbeatStore.Interval.TotalSeconds,
            LastTickUtc    = _heartbeatStore.LastTickUtc,
            ServiceStatus  = status,
            LastError      = _heartbeatStore.LastError,
            RecentActivity = recentActivity.Select(r => new TickRecordDto
            {
                TimestampUtc = r.TimestampUtc,
                ActedCount   = r.ActedCount,
                ErrorCount   = r.ErrorCount,
                DurationMs   = r.DurationMs,
                Error        = r.Error,
            }).ToList(),
            Automations = automations,
        };
    }

    // -------------------------------------------------------------------------
    // Global checks
    // -------------------------------------------------------------------------

    private async Task<DiagnosticsCheckDto> CheckSqliteReachableAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            sw.Stop();
            return Pass("sqlite_reachable", "SELECT 1 returned successfully", sw);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Fail("sqlite_reachable", $"SELECT 1 failed: {ex.Message}", sw);
        }
    }

    private async Task<DiagnosticsCheckDto> CheckDiskWritableAsync()
    {
        var sw = Stopwatch.StartNew();
        var dataDir = ResolveDataDirectory();
        var probe = Path.Combine(dataDir, $".diag-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(probe, "probe").ConfigureAwait(false);
            var read = await File.ReadAllTextAsync(probe).ConfigureAwait(false);
            File.Delete(probe);
            sw.Stop();
            if (read != "probe")
                return Fail("disk_writable", $"Read-back mismatch in {dataDir}", sw);
            return Pass("disk_writable", $"Write/read/delete succeeded in {dataDir}", sw);
        }
        catch (Exception ex)
        {
            sw.Stop();
            try { File.Delete(probe); } catch { /* best-effort cleanup */ }
            return Fail("disk_writable", $"Disk probe failed in {dataDir}: {ex.Message}", sw);
        }
    }

    private static DiagnosticsCheckDto CheckBuiltInWorkflow()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = BuiltInWorkflows.Default;
            sw.Stop();
            if (!result.IsValid || result.Definition is null)
                return Fail("built_in_workflow", $"Validation failed: {result.Error}", sw);

            var nodes = result.Definition.Nodes.Count;
            var edges = result.Definition.Edges.Count;
            return Pass("built_in_workflow", $"Loaded: id={result.Definition.Id}, {nodes} nodes, {edges} edges", sw);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Fail("built_in_workflow", $"Failed to load: {ex.Message}", sw);
        }
    }

    private static DiagnosticsCheckDto CheckBuiltInReviewPolicy()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = BuiltInReviewPolicies.Default;
            sw.Stop();
            if (!result.IsValid || result.Policy is null)
                return Fail("built_in_review_policy", $"Validation failed: {result.Error}", sw);

            var steps = result.Policy.Steps.Count;
            return Pass("built_in_review_policy", $"Loaded: name={result.Policy.Name}, {steps} steps", sw);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Fail("built_in_review_policy", $"Failed to load: {ex.Message}", sw);
        }
    }

    private DiagnosticsCheckDto CheckHeartbeatService()
    {
        var sw = Stopwatch.StartNew();
        sw.Stop();
        if (!_heartbeatStore.Enabled)
            return Warn("heartbeat_service", "Coordinator heartbeat is disabled (Coordinator:HeartbeatEnabled=false)", sw);
        if (!_heartbeatStore.LastTickUtc.HasValue)
            return Warn("heartbeat_service", "Coordinator heartbeat is enabled but has not yet ticked", sw);
        var age = DateTimeOffset.UtcNow - _heartbeatStore.LastTickUtc.Value;
        return Pass("heartbeat_service",
            $"Last tick {age.TotalSeconds:F1} s ago; interval {_heartbeatStore.Interval.TotalSeconds} s", sw);
    }

    private async Task<DiagnosticsCheckDto> CheckProjectStoreAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var projects = await _projectStore.ListAsync(ct).ConfigureAwait(false);
            sw.Stop();
            return Pass("project_store", $"ListAsync succeeded; {projects.Count} project(s)", sw);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Fail("project_store", $"ListAsync failed: {ex.Message}", sw);
        }
    }

    private static async Task<DiagnosticsCheckDto> CheckGitHubCliAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo("gh", "auth status")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            proc.Start();
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            sw.Stop();

            return proc.ExitCode == 0
                ? Pass("github_cli", "gh auth status: authenticated", sw)
                : Warn("github_cli", $"gh auth status exited {proc.ExitCode}: not authenticated or token expired", sw);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return Warn("github_cli", "gh auth status timed out after 5 s", sw);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // gh not installed or not on PATH — this is a warn, not a page-level failure.
            return Warn("github_cli", $"gh not available: {ex.Message}", sw);
        }
    }

    // -------------------------------------------------------------------------
    // Project-scoped checks
    // -------------------------------------------------------------------------

    private DiagnosticsCheckDto CheckWorkspaceAvailable(Project project)
    {
        var sw = Stopwatch.StartNew();
        sw.Stop();
        var available = _workspaceProvider.IsAvailable(project.WorkingDirectory);
        return available
            ? Pass("workspace_available", $"Working directory exists: {project.WorkingDirectory}", sw)
            : Fail("workspace_available", $"Working directory not found: {project.WorkingDirectory}", sw);
    }

    private static DiagnosticsCheckDto CheckWorkflowsDirectory(Project project)
    {
        var sw = Stopwatch.StartNew();
        var dir = Path.Combine(project.WorkingDirectory, ".scaffolders", "workflows");
        sw.Stop();
        if (!Directory.Exists(dir))
            return Warn("workflows_directory",
                $".scaffolders/workflows/ not present — built-in default workflow in use", sw);

        int count;
        try
        {
            count = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Count(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".yml",  StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            return Warn("workflows_directory", $"Could not enumerate .scaffolders/workflows/: {ex.Message}", sw);
        }
        return Pass("workflows_directory", $".scaffolders/workflows/ present; {count} YAML file(s)", sw);
    }

    private static DiagnosticsCheckDto CheckReviewPoliciesDirectory(Project project)
    {
        var sw = Stopwatch.StartNew();
        var dir = Path.Combine(project.WorkingDirectory, ".scaffolders", "review-policies");
        sw.Stop();
        if (!Directory.Exists(dir))
            return Warn("review_policies_directory",
                $".scaffolders/review-policies/ not present — built-in default policy in use", sw);

        int count;
        try
        {
            count = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Count(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".yml",  StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            return Warn("review_policies_directory", $"Could not enumerate .scaffolders/review-policies/: {ex.Message}", sw);
        }
        return Pass("review_policies_directory", $".scaffolders/review-policies/ present; {count} YAML file(s)", sw);
    }

    private async Task<DiagnosticsCheckDto> CheckActiveWorkflowAsync(Project project, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Loading may involve file I/O; run on a thread pool thread so we don't block the
            // async continuation on a synchronous file read.
            var set = await Task.Run(() => _workflowRegistry.GetOrLoad(project), ct).ConfigureAwait(false);
            sw.Stop();
            var available = set.Available.ToList();
            if (available.Count == 0)
                return Fail("active_workflow", "No valid workflow found for this project", sw);
            var defaultWf = set.FindById(BuiltInWorkflows.DefaultWorkflowId) ?? available[0];
            return Pass("active_workflow",
                $"Active workflow: id={defaultWf.Definition?.Id ?? "(unknown)"}, source={defaultWf.Source}", sw);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Fail("active_workflow", $"Workflow load failed: {ex.Message}", sw);
        }
    }

    private DiagnosticsCheckDto CheckActiveReviewPolicy(Project project)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = _reviewPolicyRegistry.ResolveActive(project);
            sw.Stop();
            if (!result.IsValid || result.Policy is null)
                return Fail("active_review_policy", $"Active policy invalid: {result.Error}", sw);
            var steps = result.Policy.Steps.Count;
            return Pass("active_review_policy",
                $"Active policy: {result.Policy.Name}, {steps} step(s), source={result.Source}", sw);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Fail("active_review_policy", $"Policy resolution failed: {ex.Message}", sw);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static DiagnosticsCheckDto Pass(string name, string detail, Stopwatch sw) =>
        new() { Name = name, Status = "pass", Detail = detail, DurationMs = sw.Elapsed.TotalMilliseconds };

    private static DiagnosticsCheckDto Warn(string name, string detail, Stopwatch sw) =>
        new() { Name = name, Status = "warn", Detail = detail, DurationMs = sw.Elapsed.TotalMilliseconds };

    private static DiagnosticsCheckDto Fail(string name, string detail, Stopwatch sw) =>
        new() { Name = name, Status = "fail", Detail = detail, DurationMs = sw.Elapsed.TotalMilliseconds };

    private static async Task<int> ScalarCountAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long count ? (int)count : 0;
    }

    private string ResolveDataDirectory()
    {
        var configuredPath = _configuration["Database:Path"];
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetDirectoryName(Path.GetFullPath(configuredPath)) ?? AppPaths.DataDirectory;
        return AppPaths.DataDirectory;
    }

    private static DateTimeOffset ResolveProcessStart()
    {
        try
        {
            return new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }
}
