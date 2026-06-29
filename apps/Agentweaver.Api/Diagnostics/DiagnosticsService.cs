using System.Diagnostics;
using System.Reflection;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.ReviewPolicies;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using k8s;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly IServiceScopeFactory _scopeFactory;
    // Optional in-cluster Kubernetes client for the agent-pod quota check. Null outside Kubernetes
    // (local dev / CI), in which case the quota diagnostic reports status "unknown".
    private readonly IKubernetes? _k8s;

    // Optional dependencies for the detailed multi-dependency health suite (Change to Task 3c).
    // Null in minimal/test hosts — the corresponding check then reports "unknown".
    private readonly IGitHubTokenStore? _gitHubTokenStore;
    private readonly ISecretStore? _secretStore;

    /// <summary>Namespace ResourceQuota that caps total agent-pod CPU (spec: 24 cores).</summary>
    private const string ResourceQuotaName = "agentweaver-quota";

    /// <summary>Key Vault secret probed by the detailed Key Vault health check.</summary>
    private const string McpApiKeySecretName = "mcp-api-key";

    /// <summary>Name prefix of warm-pool sandbox pods (<c>agentweaver-sandbox-*</c>).</summary>
    private const string WarmPoolPodPrefix = "agentweaver-sandbox-";

    /// <summary>Per-check timeout for the detailed diagnostics suite.</summary>
    private static readonly TimeSpan DetailedCheckTimeout = TimeSpan.FromSeconds(5);

    public DiagnosticsService(
        SqliteDb db,
        IProjectStore projectStore,
        IProjectWorkspaceProvider workspaceProvider,
        HeartbeatStatusStore heartbeatStore,
        WorkflowRegistry workflowRegistry,
        ReviewPolicyRegistry reviewPolicyRegistry,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        IKubernetes? k8s = null,
        IGitHubTokenStore? gitHubTokenStore = null,
        ISecretStore? secretStore = null)
    {
        _db = db;
        _projectStore = projectStore;
        _workspaceProvider = workspaceProvider;
        _heartbeatStore = heartbeatStore;
        _workflowRegistry = workflowRegistry;
        _reviewPolicyRegistry = reviewPolicyRegistry;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _k8s = k8s;
        _gitHubTokenStore = gitHubTokenStore;
        _secretStore = secretStore;
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

        // Pull counts from the live run store. Provider-aware (spec-018): EF over MemoryDbContext for
        // Postgres, raw SQLite SQL over SqliteDb otherwise. The concrete SqliteDb has no `runs` table
        // in Postgres mode (data lives in MemoryDbContext), so a raw SQLite query would 500.
        var projects = await _projectStore.ListAsync(ct).ConfigureAwait(false);

        var (totalRuns, activeRuns) = await CountRunsAsync(ct).ConfigureAwait(false);

        var agentPodQuota = await CheckAgentPodQuotaAsync(ct).ConfigureAwait(false);

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
            AgentPodQuota     = agentPodQuota,
        };
    }

    /// <summary>
    /// Reports agent-pod CPU quota headroom from the namespace ResourceQuota
    /// (<see cref="ResourceQuotaName"/>). Status thresholds (each agent pod needs 2 CPU):
    /// headroom &gt;= 4 → healthy, 2–4 → warning, &lt; 2 → critical (cannot start a new agent pod).
    /// Returns <c>null</c> outside Kubernetes and status <c>"unknown"</c> when the quota is missing
    /// or the read fails — diagnostics must never throw.
    /// </summary>
    private async Task<AgentPodQuotaDiagnosticDto?> CheckAgentPodQuotaAsync(CancellationToken ct)
    {
        if (_k8s is null)
            return null;

        var ns = _configuration["Sandbox:Kubernetes:Namespace"] ?? "agentweaver";

        try
        {
            var quota = await _k8s.CoreV1.ReadNamespacedResourceQuotaAsync(
                ResourceQuotaName, ns, cancellationToken: ct).ConfigureAwait(false);

            var usedStr = TryGetQuotaCpu(quota?.Status?.Used);
            var hardStr = TryGetQuotaCpu(quota?.Status?.Hard);

            if (usedStr is null || hardStr is null ||
                !KubernetesSandboxExecutor.TryParseCpu(usedStr, out var used) ||
                !KubernetesSandboxExecutor.TryParseCpu(hardStr, out var hard))
            {
                return UnknownQuota();
            }

            var headroom = hard - used;
            var status = headroom >= 4.0 ? "healthy" : headroom >= 2.0 ? "warning" : "critical";

            return new AgentPodQuotaDiagnosticDto
            {
                Name   = "agent_pod_quota",
                Status = status,
                Used   = used,
                Limit  = hard,
                Unit   = "CPU cores",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return UnknownQuota();
        }
    }

    private static AgentPodQuotaDiagnosticDto UnknownQuota() => new()
    {
        Name   = "agent_pod_quota",
        Status = "unknown",
        Used   = null,
        Limit  = null,
        Unit   = "CPU cores",
    };

    private static string? TryGetQuotaCpu(IDictionary<string, k8s.Models.ResourceQuantity>? map)
    {
        if (map is not null && map.TryGetValue("limits.cpu", out var quantity) && quantity is not null)
            return quantity.ToString();
        return null;
    }

    // -------------------------------------------------------------------------
    // Detailed multi-dependency health suite (spec-006, Change to Task 3c)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs every critical-dependency health check concurrently, each bounded by
    /// <see cref="DetailedCheckTimeout"/>. A check that exceeds its timeout reports status
    /// <c>"unknown"</c> ("check timed out") and never blocks the overall response. Designed so a
    /// genuinely broken dependency (DB down, expired GitHub token, exhausted quota, empty warm pool)
    /// surfaces in the Diagnostics panel instead of being masked by a green overall status.
    /// </summary>
    public async Task<DetailedDiagnosticsDto> GetDetailedDiagnosticsAsync(CancellationToken ct = default)
    {
        var overallSw = Stopwatch.StartNew();
        var generatedUtc = DateTimeOffset.UtcNow;

        var checks = await Task.WhenAll(
            RunGuardedAsync("postgresql", CheckPostgresAsync, ct),
            RunGuardedAsync("github_installation_token", CheckGitHubInstallationTokenAsync, ct),
            RunGuardedAsync("key_vault", CheckKeyVaultAsync, ct),
            RunGuardedAsync("agent_pod_quota", CheckAgentPodQuotaDetailedAsync, ct),
            RunGuardedAsync("warm_pool", CheckWarmPoolAsync, ct),
            RunGuardedAsync("k8s_api", CheckK8sApiAsync, ct)).ConfigureAwait(false);

        overallSw.Stop();

        return new DetailedDiagnosticsDto
        {
            GeneratedUtc    = generatedUtc,
            TotalDurationMs = overallSw.Elapsed.TotalMilliseconds,
            Checks          = checks,
        };
    }

    /// <summary>
    /// Wraps a single check with a per-check timeout and a catch-all. On timeout the check reports
    /// <c>"unknown"</c> / "check timed out"; on an unexpected throw it reports <c>"critical"</c> with
    /// the error message. Never propagates — one failing check cannot break the suite.
    /// </summary>
    private static async Task<DetailedHealthCheckDto> RunGuardedAsync(
        string name, Func<CancellationToken, Task<DetailedHealthCheckDto>> check, CancellationToken outerCt)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        cts.CancelAfter(DetailedCheckTimeout);
        try
        {
            return await check(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !outerCt.IsCancellationRequested)
        {
            sw.Stop();
            return Detailed(name, "unknown", "check timed out", sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Detailed(name, "critical", ex.Message, sw.Elapsed.TotalMilliseconds);
        }
    }

    private static DetailedHealthCheckDto Detailed(
        string name, string status, string message, double latencyMs) => new()
    {
        Name      = name,
        Status    = status,
        Message   = message,
        LatencyMs = latencyMs,
    };

    /// <summary>PostgreSQL/primary DB connectivity: <c>SELECT 1</c> via the EF <c>MemoryDbContext</c>.
    /// healthy &lt; 500 ms, degraded &gt; 500 ms, critical on connection refused / timeout.</summary>
    private async Task<DetailedHealthCheckDto> CheckPostgresAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct).ConfigureAwait(false);
            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;
            var status = ms < 500 ? "healthy" : "degraded";
            return Detailed("postgresql", status, $"SELECT 1 returned in {ms:F0}ms", ms);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Detailed("postgresql", "critical", $"database unreachable: {ex.Message}", sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>GitHub Installation token: inspects the STORED token's presence and expiry only (no
    /// live GitHub call). healthy when present and unexpired; critical when missing or expired
    /// (agents cannot run).</summary>
    private async Task<DetailedHealthCheckDto> CheckGitHubInstallationTokenAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (_gitHubTokenStore is null)
        {
            sw.Stop();
            return Detailed("github_installation_token", "unknown",
                "no token store configured", sw.Elapsed.TotalMilliseconds);
        }

        try
        {
            var token = await _gitHubTokenStore.GetTokenAsync(GitHubTokenScope.Installation, ct).ConfigureAwait(false);
            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;

            if (token is null || string.IsNullOrEmpty(token.AccessToken))
                return Detailed("github_installation_token", "critical",
                    "no installation token stored — agents cannot run", ms);

            if (token.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow)
                return Detailed("github_installation_token", "critical",
                    $"installation token expired at {exp:O} — agents cannot run", ms);

            var detail = token.ExpiresAt is { } e
                ? $"installation token valid (expires {e:O})"
                : "installation token present (no expiry)";
            return Detailed("github_installation_token", "healthy", detail, ms);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Detailed("github_installation_token", "critical",
                $"token read failed: {ex.Message}", sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Key Vault: resolves the mounted <c>mcp-api-key</c> secret via
    /// <see cref="ISecretStore"/>. healthy when it resolves; critical on any failure.</summary>
    private async Task<DetailedHealthCheckDto> CheckKeyVaultAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (_secretStore is null)
        {
            sw.Stop();
            return Detailed("key_vault", "unknown", "no secret store configured", sw.Elapsed.TotalMilliseconds);
        }

        try
        {
            var result = await _secretStore.GetSecretAsync(McpApiKeySecretName, ct).ConfigureAwait(false);
            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;
            return result.Found
                ? Detailed("key_vault", "healthy", $"secret '{McpApiKeySecretName}' resolved", ms)
                : Detailed("key_vault", "critical", $"secret '{McpApiKeySecretName}' not found", ms);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Detailed("key_vault", "critical",
                $"secret read failed: {ex.Message}", sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Agent-pod CPU quota with subtask PendingCapacity backlog. headroom &gt;= 4 → healthy,
    /// 2–4 → warning (one pod can still start), &lt; 2 → critical (no new agent pod can start).</summary>
    private async Task<DetailedHealthCheckDto> CheckAgentPodQuotaDetailedAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var pendingCount = await CountPendingCapacitySubtasksAsync(ct).ConfigureAwait(false);

        if (_k8s is null)
        {
            sw.Stop();
            return Detailed("agent_pod_quota", "unknown", "not running on Kubernetes", sw.Elapsed.TotalMilliseconds)
                with { Unit = "CPU cores", PendingCount = pendingCount };
        }

        var ns = _configuration["Sandbox:Kubernetes:Namespace"] ?? "agentweaver";
        try
        {
            var quota = await _k8s.CoreV1.ReadNamespacedResourceQuotaAsync(
                ResourceQuotaName, ns, cancellationToken: ct).ConfigureAwait(false);

            var usedStr = TryGetQuotaCpu(quota?.Status?.Used);
            var hardStr = TryGetQuotaCpu(quota?.Status?.Hard);
            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;

            if (usedStr is null || hardStr is null ||
                !KubernetesSandboxExecutor.TryParseCpu(usedStr, out var used) ||
                !KubernetesSandboxExecutor.TryParseCpu(hardStr, out var hard))
            {
                return Detailed("agent_pod_quota", "unknown", "quota missing or unparseable", ms)
                    with { Unit = "CPU cores", PendingCount = pendingCount };
            }

            var headroom = hard - used;
            var status = headroom >= 4.0 ? "healthy" : headroom >= 2.0 ? "warning" : "critical";
            var msg = status == "critical"
                ? $"no headroom for a new agent pod ({used}/{hard} CPU used)"
                : $"{headroom} CPU headroom ({used}/{hard} CPU used)";
            return Detailed("agent_pod_quota", status, msg, ms)
                with { Used = used, Limit = hard, Unit = "CPU cores", PendingCount = pendingCount };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Detailed("agent_pod_quota", "unknown", $"quota read failed: {ex.Message}", sw.Elapsed.TotalMilliseconds)
                with { Unit = "CPU cores", PendingCount = pendingCount };
        }
    }

    private async Task<int> CountPendingCapacitySubtasksAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            return await db.Subtasks
                .CountAsync(s => s.Status == "pending_capacity", ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Warm-pool readiness: counts ready <c>agentweaver-sandbox-*</c> pods.
    /// healthy &gt;= 2 ready, warning == 1, critical == 0.</summary>
    private async Task<DetailedHealthCheckDto> CheckWarmPoolAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (_k8s is null)
        {
            sw.Stop();
            return Detailed("warm_pool", "unknown", "not running on Kubernetes", sw.Elapsed.TotalMilliseconds);
        }

        var ns = _configuration["Sandbox:Kubernetes:Namespace"] ?? "agentweaver";
        try
        {
            var pods = await _k8s.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: ct).ConfigureAwait(false);
            var ready = pods.Items.Count(p =>
                (p.Metadata?.Name?.StartsWith(WarmPoolPodPrefix, StringComparison.Ordinal) ?? false)
                && IsPodReady(p));
            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;
            var status = ready >= 2 ? "healthy" : ready == 1 ? "warning" : "critical";
            return Detailed("warm_pool", status, $"{ready} warm-pool pod(s) ready", ms)
                with { Used = ready, Unit = "ready pods" };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Detailed("warm_pool", "unknown", $"pod list failed: {ex.Message}", sw.Elapsed.TotalMilliseconds);
        }
    }

    private static bool IsPodReady(k8s.Models.V1Pod pod)
    {
        var conditions = pod.Status?.Conditions;
        if (conditions is null) return false;
        foreach (var c in conditions)
        {
            if (string.Equals(c.Type, "Ready", StringComparison.Ordinal))
                return string.Equals(c.Status, "True", StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>Kubernetes API reachability: lists pods (capped) with the per-check timeout.
    /// healthy when the API responds; critical when unreachable.</summary>
    private async Task<DetailedHealthCheckDto> CheckK8sApiAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (_k8s is null)
        {
            sw.Stop();
            return Detailed("k8s_api", "unknown", "not running on Kubernetes", sw.Elapsed.TotalMilliseconds);
        }

        var ns = _configuration["Sandbox:Kubernetes:Namespace"] ?? "agentweaver";
        try
        {
            await _k8s.CoreV1.ListNamespacedPodAsync(ns, limit: 1, cancellationToken: ct).ConfigureAwait(false);
            sw.Stop();
            return Detailed("k8s_api", "healthy", "Kubernetes API reachable", sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Detailed("k8s_api", "critical", $"Kubernetes API unreachable: {ex.Message}", sw.Elapsed.TotalMilliseconds);
        }
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

    /// <summary>
    /// Returns the enriched coordinator heartbeat status snapshot, aggregated across all replicas.
    /// Reads every persisted per-pod row from <see cref="MemoryDbContext"/> so the endpoint is correct
    /// even when the reader pod differs from the writer pod. Falls back to the local in-memory store
    /// when the table is empty or unavailable.
    /// </summary>
    public async Task<HeartbeatStatusDto> GetHeartbeatStatusAsync(CancellationToken ct = default)
    {
        var recentActivity = _heartbeatStore.GetRecentActivity();
        var lastRecord = recentActivity.Length > 0 ? recentActivity[0] : (TickRecord?)null;

        // Cross-pod rows (best-effort; fall back to local-only on any failure).
        List<HeartbeatStatusRecord> podRows = [];
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var memDb = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            podRows = await memDb.HeartbeatStatuses
                .AsNoTracking()
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics must never throw; degrade gracefully to the local pod's view.
            podRows = [];
        }

        // Most-recent tick across all pods, falling back to the local store.
        DateTimeOffset? lastTickUtc = _heartbeatStore.LastTickUtc;
        if (podRows.Count > 0)
        {
            var maxRow = podRows.Max(r => r.LastTickUtc);
            if (lastTickUtc is null || maxRow > lastTickUtc.Value)
                lastTickUtc = maxRow;
        }

        var status = _heartbeatStore.Enabled
            ? (lastTickUtc.HasValue ? "running" : "waiting_first_tick")
            : "disabled";

        // Surface the local sticky error first, otherwise the newest cross-pod error.
        var lastError = _heartbeatStore.LastError
            ?? podRows.Where(r => r.Error is not null)
                      .OrderByDescending(r => r.LastTickUtc)
                      .Select(r => r.Error)
                      .FirstOrDefault();

        var pods = podRows
            .OrderBy(r => r.PodName, StringComparer.Ordinal)
            .Select(r => new HeartbeatPodStatusDto
            {
                PodName     = r.PodName,
                LastTickUtc = r.LastTickUtc,
                ActedCount  = r.ActedCount,
                ErrorCount  = r.ErrorCount,
                DurationMs  = r.DurationMs,
                Error       = r.Error,
                Enabled     = r.Enabled,
            })
            .ToList();

        var automations = new List<AutomationDto>
        {
            new()
            {
                Name           = "Coordinator Heartbeat",
                Description    = "Picks up Ready backlog tasks and starts coordinator runs",
                CadenceSeconds = _heartbeatStore.Interval.TotalSeconds,
                LastRunUtc     = lastTickUtc,
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
            LastTickUtc    = lastTickUtc,
            ServiceStatus  = status,
            LastError      = lastError,
            RecentActivity = recentActivity.Select(r => new TickRecordDto
            {
                TimestampUtc   = r.TimestampUtc,
                AutomationName = r.AutomationName,
                ActedCount     = r.ActedCount,
                ErrorCount     = r.ErrorCount,
                DurationMs     = r.DurationMs,
                Error          = r.Error,
            }).ToList(),
            Automations = automations,
            Pods        = pods,
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
        var dir = Path.Combine(project.WorkingDirectory, ".agentweaver", "workflows");
        sw.Stop();
        if (!Directory.Exists(dir))
            return Warn("workflows_directory",
                $".agentweaver/workflows/ not present — built-in default workflow in use", sw);

        int count;
        try
        {
            count = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Count(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".yml",  StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            return Warn("workflows_directory", $"Could not enumerate .agentweaver/workflows/: {ex.Message}", sw);
        }
        return Pass("workflows_directory", $".agentweaver/workflows/ present; {count} YAML file(s)", sw);
    }

    private static DiagnosticsCheckDto CheckReviewPoliciesDirectory(Project project)
    {
        var sw = Stopwatch.StartNew();
        var dir = Path.Combine(project.WorkingDirectory, ".agentweaver", "review-policies");
        sw.Stop();
        if (!Directory.Exists(dir))
            return Warn("review_policies_directory",
                $".agentweaver/review-policies/ not present — built-in default policy in use", sw);

        int count;
        try
        {
            count = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Count(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".yml",  StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            return Warn("review_policies_directory", $"Could not enumerate .agentweaver/review-policies/: {ex.Message}", sw);
        }
        return Pass("review_policies_directory", $".agentweaver/review-policies/ present; {count} YAML file(s)", sw);
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

    /// <summary>
    /// Provider-agnostic (spec-018) total/active run counts. In Postgres mode the run rows live in
    /// <see cref="MemoryDbContext"/>; the concrete <see cref="SqliteDb"/> has no <c>runs</c> table and
    /// a raw SQLite query would throw "no such table: runs" (HTTP 500). EF over MemoryDbContext when
    /// Postgres, raw SQLite SQL over SqliteDb otherwise.
    /// </summary>
    private async Task<(int total, int active)> CountRunsAsync(CancellationToken ct)
    {
        var provider = _configuration["Database:Provider"]?.ToLowerInvariant() ?? "sqlite";
        if (provider is "postgres" or "postgresql")
        {
            using var scope = _scopeFactory.CreateScope();
            var memDb = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var total = await memDb.Runs.AsNoTracking().CountAsync(ct).ConfigureAwait(false);
            var active = await memDb.Runs.AsNoTracking()
                .CountAsync(r => r.Status == "in_progress", ct).ConfigureAwait(false);
            return (total, active);
        }

        await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        var totalRuns = await ScalarCountAsync(conn, "SELECT COUNT(*) FROM runs", ct).ConfigureAwait(false);
        var activeRuns = await ScalarCountAsync(
            conn, "SELECT COUNT(*) FROM runs WHERE status = 'in_progress'", ct).ConfigureAwait(false);
        return (totalRuns, activeRuns);
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
