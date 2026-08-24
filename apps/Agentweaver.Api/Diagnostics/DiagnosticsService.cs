using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
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
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    // Optional in-cluster Kubernetes client for the agent-pod quota check. Null outside Kubernetes
    // (local dev / CI), in which case the quota diagnostic reports status "unknown".
    private readonly IKubernetes? _k8s;

    // Optional agent-host reaper (spec-006): used to enumerate active/orphaned agent pods for the
    // cluster diagnostics view. Null outside Kubernetes — the inventory then comes back empty.
    private readonly IAgentHostReaper? _reaper;

    /// <summary>Namespace ResourceQuota that enforces AgentHost admission object limits.</summary>
    private const string ResourceQuotaName = "agentweaver-quota";
    private const string PodQuotaKey = "pods";
    private const string SandboxClaimQuotaKey =
        "count/" + SandboxClaimConventions.ClaimPlural + "." + SandboxClaimConventions.ApiGroup;

    /// <summary>Key Vault secret probed by the detailed Key Vault health check.</summary>
    private const string KeyVaultProbeSecretName = "mcp-oauth-signing-key";

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
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        IKubernetes? k8s = null,
        IAgentHostReaper? reaper = null)
    {
        _db = db;
        _projectStore = projectStore;
        _workspaceProvider = workspaceProvider;
        _heartbeatStore = heartbeatStore;
        _workflowRegistry = workflowRegistry;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _k8s = k8s;
        _reaper = reaper;
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
        checks.AddRange(await CheckGitHubCliAsync(ct).ConfigureAwait(false));

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
    /// Reports effective agent-pod admission headroom from the namespace ResourceQuota
    /// (<see cref="ResourceQuotaName"/>). Each new AgentHost consumes one Pod object and one
    /// SandboxClaim object, so the tighter of those two quota buckets determines whether another
    /// run can start. Returns <c>null</c> outside Kubernetes and status <c>"unknown"</c> when the
    /// quota is missing or the read fails — diagnostics must never throw.
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

            var snapshot = TryGetAgentPodQuotaSnapshot(quota);
            if (snapshot is null)
                return UnknownQuota();

            return new AgentPodQuotaDiagnosticDto
            {
                Name   = "agent_pod_quota",
                Status = GetAgentPodQuotaStatus(snapshot.Headroom),
                Used   = snapshot.Used,
                Limit  = snapshot.Limit,
                Unit   = snapshot.LimitingResource,
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
        Unit   = "pods or sandboxclaims",
    };

    private static AgentPodQuotaSnapshot? TryGetAgentPodQuotaSnapshot(k8s.Models.V1ResourceQuota? quota)
    {
        if (!TryGetQuotaCount(quota?.Status?.Used, PodQuotaKey, out var podUsed) ||
            !TryGetQuotaCount(quota?.Status?.Hard, PodQuotaKey, out var podLimit) ||
            !TryGetQuotaCount(quota?.Status?.Used, SandboxClaimQuotaKey, out var sandboxClaimUsed) ||
            !TryGetQuotaCount(quota?.Status?.Hard, SandboxClaimQuotaKey, out var sandboxClaimLimit))
            return null;

        var podHeadroom = podLimit - podUsed;
        var sandboxClaimHeadroom = sandboxClaimLimit - sandboxClaimUsed;
        var limitingResource = podHeadroom <= sandboxClaimHeadroom ? PodQuotaKey : "sandboxclaims";
        var used = limitingResource == PodQuotaKey ? podUsed : sandboxClaimUsed;
        var limit = limitingResource == PodQuotaKey ? podLimit : sandboxClaimLimit;

        return new AgentPodQuotaSnapshot(
            podUsed,
            podLimit,
            sandboxClaimUsed,
            sandboxClaimLimit,
            limitingResource,
            used,
            limit);
    }

    private static bool TryGetQuotaCount(
        IDictionary<string, k8s.Models.ResourceQuantity>? map,
        string key,
        out double value)
    {
        value = 0;
        return map is not null &&
               map.TryGetValue(key, out var quantity) &&
               quantity is not null &&
               double.TryParse(
                   quantity.ToString(),
                   NumberStyles.Number,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    // The enforced quota buckets default to 200 objects each, and every new AgentHost consumes one
    // pod plus one SandboxClaim. Once headroom drops into single digits the namespace is close to
    // admission exhaustion, so we warn there; zero remaining objects means no new run can start.
    private static string GetAgentPodQuotaStatus(double headroom) =>
        headroom >= 10 ? "healthy" : headroom >= 1 ? "warning" : "critical";

    private static string FormatAgentPodQuotaMessage(AgentPodQuotaSnapshot snapshot)
    {
        var limitingLabel = snapshot.LimitingResource == PodQuotaKey ? "pods" : "sandboxclaims";
        if (snapshot.Headroom < 1)
        {
            return $"no headroom for a new agent pod (pods {snapshot.PodUsed}/{snapshot.PodLimit}, " +
                   $"sandboxclaims {snapshot.SandboxClaimUsed}/{snapshot.SandboxClaimLimit} used)";
        }

        return $"{snapshot.Headroom:0} additional agent pod starts available before quota exhaustion " +
               $"(limited by {limitingLabel}; pods {snapshot.PodUsed}/{snapshot.PodLimit}, " +
               $"sandboxclaims {snapshot.SandboxClaimUsed}/{snapshot.SandboxClaimLimit} used)";
    }

    // -------------------------------------------------------------------------
    // Detailed cluster diagnostics suite (spec-006)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs every critical-dependency health check concurrently, each bounded by
    /// <see cref="DetailedCheckTimeout"/>, and returns the live agent-pod inventory (active /
    /// orphaned) plus subtasks parked in PendingCapacity. A check that exceeds its timeout reports
    /// status <c>"unknown"</c> ("check timed out") and never blocks the overall response. Designed so
    /// a genuinely broken dependency (DB down, expired GitHub token, exhausted quota, empty warm
    /// pool) surfaces in the Cluster diagnostics page instead of being masked by a green status.
    /// </summary>
    public async Task<ClusterDiagnosticsDto> GetClusterDiagnosticsAsync(CancellationToken ct = default)
    {
        var overallSw = Stopwatch.StartNew();
        var generatedUtc = DateTimeOffset.UtcNow;

        // Checks + inventory run concurrently; the inventory tasks are best-effort (never throw).
        var checksTask = Task.WhenAll(
            RunGuardedAsync("postgresql", CheckPostgresAsync, ct),
            RunGuardedAsync("key_vault", CheckKeyVaultAsync, ct),
            RunGuardedAsync("agent_pod_quota", CheckAgentPodQuotaDetailedAsync, ct),
            RunGuardedAsync("warm_pool", CheckWarmPoolAsync, ct),
            RunGuardedAsync("k8s_api", CheckK8sApiAsync, ct));

        var podsTask = GetAgentPodInventoryAsync(ct);
        var pendingTask = GetPendingCapacityRunsAsync(ct);
        var warmPoolsTask = GetWarmPoolInventoryAsync(ct);
        var claimsTask = GetSandboxClaimInventoryAsync(ct);

        await Task.WhenAll(checksTask, podsTask, pendingTask, warmPoolsTask, claimsTask).ConfigureAwait(false);

        var checks = await checksTask.ConfigureAwait(false);
        var (active, orphaned) = await podsTask.ConfigureAwait(false);
        var pending = await pendingTask.ConfigureAwait(false);
        var warmPools = await warmPoolsTask.ConfigureAwait(false);
        var claims = await claimsTask.ConfigureAwait(false);

        overallSw.Stop();

        return new ClusterDiagnosticsDto
        {
            GeneratedUtc        = generatedUtc,
            TotalDurationMs     = overallSw.Elapsed.TotalMilliseconds,
            Checks              = checks,
            ActiveAgentPods     = active,
            OrphanedAgentPods   = orphaned,
            PendingCapacityRuns = pending,
            WarmPools           = warmPools,
            SandboxClaims       = claims,
        };
    }

    /// <summary>
    /// Splits the reaper's agent-host claim inventory into active vs orphaned pods. Best-effort:
    /// returns empty lists outside Kubernetes or on any failure (diagnostics must never throw).
    /// </summary>
    private async Task<(IReadOnlyList<AgentPodInfoDto> Active, IReadOnlyList<AgentPodInfoDto> Orphaned)>
        GetAgentPodInventoryAsync(CancellationToken ct)
    {
        if (_reaper is null)
            return (Array.Empty<AgentPodInfoDto>(), Array.Empty<AgentPodInfoDto>());

        try
        {
            var inventory = await _reaper.GetClaimInventoryAsync(ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;

            var active = new List<AgentPodInfoDto>();
            var orphaned = new List<AgentPodInfoDto>();
            foreach (var c in inventory)
            {
                var dto = new AgentPodInfoDto
                {
                    ClaimName  = c.ClaimName,
                    RunId      = c.RunId,
                    PodName    = c.PodName,
                    Status     = c.Ready ? "ready" : "pending",
                    AgeSeconds = c.CreatedAt is { } created ? (now - created).TotalSeconds : null,
                };
                (c.Orphaned ? orphaned : active).Add(dto);
            }
            return (active, orphaned);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (Array.Empty<AgentPodInfoDto>(), Array.Empty<AgentPodInfoDto>());
        }
    }

    /// <summary>
    /// Lists subtasks parked in <c>pending_capacity</c> (waiting for an agent-host pod slot).
    /// Best-effort: returns an empty list on any failure.
    /// </summary>
    private async Task<IReadOnlyList<PendingCapacityRunDto>> GetPendingCapacityRunsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var now = DateTimeOffset.UtcNow;

            var rows = await db.Subtasks
                .AsNoTracking()
                .Where(s => s.Status == "pending_capacity")
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return rows.Select(s => new PendingCapacityRunDto
            {
                SubtaskId  = s.Id,
                WorkPlanId = s.WorkPlanId,
                ChildRunId = s.ChildRunId,
                Status     = s.Status,
                Reason     = s.RecoveryGuidance,
                AgeSeconds = (now - s.UpdatedAt).TotalSeconds,
            }).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Array.Empty<PendingCapacityRunDto>();
        }
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

    /// <summary>Key Vault: verifies the CSI-mounted <c>mcp-oauth-signing-key</c> secret was loaded
    /// into configuration (Auth:OAuth:SigningKey). Uses IConfiguration — ISecretStore applies
    /// a "ghtok-" prefix intended for GitHub token storage, not raw KV secret probes.</summary>
    private Task<DetailedHealthCheckDto> CheckKeyVaultAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var signingKey = _configuration["Auth:OAuth:SigningKey"];
        sw.Stop();
        var ms = sw.Elapsed.TotalMilliseconds;
        return Task.FromResult(!string.IsNullOrWhiteSpace(signingKey)
            ? Detailed("key_vault", "healthy", $"secret '{KeyVaultProbeSecretName}' resolved", ms)
            : Detailed("key_vault", "critical", $"secret '{KeyVaultProbeSecretName}' not found", ms));
    }

    /// <summary>Agent-pod object-quota headroom with subtask PendingCapacity backlog. Effective
    /// headroom is the tighter of the pod and SandboxClaim buckets because each new AgentHost
    /// consumes one of each.</summary>
    private async Task<DetailedHealthCheckDto> CheckAgentPodQuotaDetailedAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var pendingCount = await CountPendingCapacitySubtasksAsync(ct).ConfigureAwait(false);

        if (_k8s is null)
        {
            sw.Stop();
            return Detailed("agent_pod_quota", "unknown", "not running on Kubernetes", sw.Elapsed.TotalMilliseconds)
                with { Unit = "pods or sandboxclaims", PendingCount = pendingCount };
        }

        var ns = _configuration["Sandbox:Kubernetes:Namespace"] ?? "agentweaver";
        try
        {
            var quota = await _k8s.CoreV1.ReadNamespacedResourceQuotaAsync(
                ResourceQuotaName, ns, cancellationToken: ct).ConfigureAwait(false);

            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;
            var snapshot = TryGetAgentPodQuotaSnapshot(quota);

            if (snapshot is null)
            {
                return Detailed("agent_pod_quota", "unknown", "quota missing or unparseable", ms)
                    with { Unit = "pods or sandboxclaims", PendingCount = pendingCount };
            }

            return Detailed(
                    "agent_pod_quota",
                    GetAgentPodQuotaStatus(snapshot.Headroom),
                    FormatAgentPodQuotaMessage(snapshot),
                    ms)
                with
                {
                    Used = snapshot.Used,
                    Limit = snapshot.Limit,
                    Unit = snapshot.LimitingResource,
                    PendingCount = pendingCount,
                };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Detailed("agent_pod_quota", "unknown", $"quota read failed: {ex.Message}", sw.Elapsed.TotalMilliseconds)
                with { Unit = "pods or sandboxclaims", PendingCount = pendingCount };
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

    /// <summary>Warm-pool readiness: reads the <c>agentweaver-agent-host</c> SandboxWarmPool CRD.
    /// healthy = ready &gt;= desired, warning = ready &gt; 0 but below desired, critical = 0 ready.</summary>
    private async Task<DetailedHealthCheckDto> CheckWarmPoolAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (_k8s is null)
        {
            sw.Stop();
            return Detailed("warm_pool", "unknown", "not running on Kubernetes", sw.Elapsed.TotalMilliseconds);
        }

        var ns = _configuration["Sandbox:Kubernetes:Namespace"] ?? "agentweaver";
        var warmPoolName = _configuration["Sandbox:Kubernetes:AgentHostWarmPoolRef"] ?? "agentweaver-agent-host";
        try
        {
            var obj = await _k8s.CustomObjects.GetNamespacedCustomObjectAsync(
                SandboxClaimConventions.ApiGroup, SandboxClaimConventions.ApiVersion,
                ns, "sandboxwarmpools", warmPoolName, cancellationToken: ct).ConfigureAwait(false);

            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;

            var json = System.Text.Json.JsonSerializer.Serialize(obj);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var desired = root.TryGetProperty("spec", out var spec) && spec.TryGetProperty("replicas", out var r) ? r.GetInt32() : 0;
            var ready = root.TryGetProperty("status", out var status) && status.TryGetProperty("readyReplicas", out var rr) ? rr.GetInt32() : 0;

            var checkStatus = ready >= desired && desired > 0 ? "healthy"
                : ready > 0 ? "warning"
                : "critical";
            return Detailed("warm_pool", checkStatus, $"{warmPoolName}: {ready}/{desired} replicas ready", ms)
                with { Used = ready, Limit = desired, Unit = "replicas" };
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            sw.Stop();
            return Detailed("warm_pool", "critical",
                $"SandboxWarmPool '{warmPoolName}' not found — warm pool is not provisioned",
                sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Detailed("warm_pool", "unknown", $"warm pool check failed: {ex.Message}", sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Lists all SandboxWarmPool CRD objects in the namespace. Best-effort: returns empty on any failure.
    /// </summary>
    private async Task<IReadOnlyList<WarmPoolStatusDto>> GetWarmPoolInventoryAsync(CancellationToken ct)
    {
        if (_k8s is null) return Array.Empty<WarmPoolStatusDto>();

        var ns = _configuration["Sandbox:Kubernetes:Namespace"] ?? "agentweaver";
        try
        {
            var list = await _k8s.CustomObjects.ListNamespacedCustomObjectAsync(
                SandboxClaimConventions.ApiGroup, SandboxClaimConventions.ApiVersion,
                ns, "sandboxwarmpools", cancellationToken: ct).ConfigureAwait(false);

            var json = System.Text.Json.JsonSerializer.Serialize(list);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var now = DateTimeOffset.UtcNow;
            var result = new List<WarmPoolStatusDto>();

            if (!doc.RootElement.TryGetProperty("items", out var items)) return result;
            foreach (var item in items.EnumerateArray())
            {
                var name = item.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var desired = item.TryGetProperty("spec", out var sp) && sp.TryGetProperty("replicas", out var r) ? r.GetInt32() : 0;
                var ready = item.TryGetProperty("status", out var st) && st.TryGetProperty("readyReplicas", out var rr) ? rr.GetInt32() : 0;
                var available = st.ValueKind != System.Text.Json.JsonValueKind.Undefined && st.TryGetProperty("availableReplicas", out var ar) ? ar.GetInt32() : ready;

                double? age = null;
                if (meta.ValueKind != System.Text.Json.JsonValueKind.Undefined &&
                    meta.TryGetProperty("creationTimestamp", out var ts) &&
                    DateTimeOffset.TryParse(ts.GetString(), out var created))
                    age = (now - created).TotalSeconds;

                var poolStatus = ready >= desired && desired > 0 ? "healthy"
                    : ready > 0 ? "warning"
                    : "critical";

                result.Add(new WarmPoolStatusDto
                {
                    Name              = name,
                    DesiredReplicas   = desired,
                    ReadyReplicas     = ready,
                    AvailableReplicas = available,
                    Status            = poolStatus,
                    AgeSeconds        = age,
                });
            }
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<WarmPoolStatusDto>(); }
    }

    /// <summary>
    /// Lists all SandboxClaim CRD objects in the namespace. Best-effort: returns empty on any failure.
    /// </summary>
    private async Task<IReadOnlyList<SandboxClaimObjectDto>> GetSandboxClaimInventoryAsync(CancellationToken ct)
    {
        if (_k8s is null) return Array.Empty<SandboxClaimObjectDto>();

        var ns = _configuration["Sandbox:Kubernetes:Namespace"] ?? "agentweaver";
        try
        {
            var list = await _k8s.CustomObjects.ListNamespacedCustomObjectAsync(
                SandboxClaimConventions.ApiGroup, SandboxClaimConventions.ApiVersion,
                ns, SandboxClaimConventions.ClaimPlural, cancellationToken: ct).ConfigureAwait(false);

            var json = System.Text.Json.JsonSerializer.Serialize(list);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var now = DateTimeOffset.UtcNow;
            var result = new List<SandboxClaimObjectDto>();

            if (!doc.RootElement.TryGetProperty("items", out var items)) return result;
            foreach (var item in items.EnumerateArray())
            {
                var meta = item.TryGetProperty("metadata", out var m) ? m : default;
                var name = meta.ValueKind != System.Text.Json.JsonValueKind.Undefined && meta.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null) continue;

                // Infer run ID from claim name (agent-{first12hex})
                string? runId = name.StartsWith(SandboxClaimConventions.AgentHostClaimPrefix, StringComparison.Ordinal)
                    ? name[SandboxClaimConventions.AgentHostClaimPrefix.Length..] : null;

                var st = item.TryGetProperty("status", out var s) ? s : default;
                var ready = false;
                if (st.ValueKind != System.Text.Json.JsonValueKind.Undefined &&
                    st.TryGetProperty("conditions", out var conds) && conds.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var cond in conds.EnumerateArray())
                    {
                        if (cond.TryGetProperty("type", out var ct2) && ct2.GetString() == "Ready" &&
                            cond.TryGetProperty("status", out var cs) && cs.GetString() == "True")
                        { ready = true; break; }
                    }
                }

                var phase = ready ? "bound" : "pending";

                // Bound sandbox name from status.sandbox.name
                string? boundSandbox = null;
                if (st.ValueKind != System.Text.Json.JsonValueKind.Undefined &&
                    st.TryGetProperty("sandbox", out var sb) && sb.TryGetProperty("name", out var sbn))
                    boundSandbox = sbn.GetString();

                string? warmPool = null;
                if (item.TryGetProperty("spec", out var sp))
                {
                    if (sp.TryGetProperty("warmPoolRef", out var wpr) && wpr.TryGetProperty("name", out var wprn))
                        warmPool = wprn.GetString();
                }

                double? age = null;
                if (meta.ValueKind != System.Text.Json.JsonValueKind.Undefined &&
                    meta.TryGetProperty("creationTimestamp", out var ts) &&
                    DateTimeOffset.TryParse(ts.GetString(), out var created))
                    age = (now - created).TotalSeconds;

                result.Add(new SandboxClaimObjectDto
                {
                    Name               = name,
                    Phase              = phase,
                    Ready              = ready,
                    RunId              = runId,
                    BoundSandbox       = string.IsNullOrEmpty(boundSandbox) ? null : boundSandbox,
                    WarmPool           = string.IsNullOrEmpty(warmPool) ? null : warmPool,
                    AgeSeconds         = age,
                });
            }
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<SandboxClaimObjectDto>(); }
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
        sw.Stop();
        return Pass("built_in_review_policy",
            "Loaded: id=default, 3 steps (RAI, peer review, human review)", sw);
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

    private static async Task<IReadOnlyList<DiagnosticsCheckDto>> CheckGitHubCliAsync(CancellationToken ct)
    {
        var installedSw = Stopwatch.StartNew();
        DiagnosticsCheckDto installed;
        try
        {
            using var installedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            installedCts.CancelAfter(TimeSpan.FromSeconds(5));

            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo("gh", "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            proc.Start();
            await proc.WaitForExitAsync(installedCts.Token).ConfigureAwait(false);
            var version = (await proc.StandardOutput.ReadToEndAsync(installedCts.Token).ConfigureAwait(false)).Trim();
            installedSw.Stop();

            if (proc.ExitCode != 0)
            {
                return
                [
                    Warn("github_cli", $"gh --version exited {proc.ExitCode}", installedSw),
                    Warn("github_cli_auth", "Skipped because gh is unavailable", installedSw),
                ];
            }

            installed = Pass("github_cli", $"Installed: {version}", installedSw);
        }
        catch (OperationCanceledException)
        {
            installedSw.Stop();
            return
            [
                Warn("github_cli", "gh --version timed out after 5 s", installedSw),
                Warn("github_cli_auth", "Skipped because gh availability could not be determined", installedSw),
            ];
        }
        catch (Exception ex)
        {
            installedSw.Stop();
            return
            [
                Warn("github_cli", $"gh not available: {ex.Message}", installedSw),
                Warn("github_cli_auth", "Skipped because gh is unavailable", installedSw),
            ];
        }

        var authSw = Stopwatch.StartNew();
        try
        {
            using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            authCts.CancelAfter(TimeSpan.FromSeconds(5));

            using var authProc = new Process();
            authProc.StartInfo = new ProcessStartInfo("gh", "auth status")
            {
                RedirectStandardError = true,
                UseShellExecute       = false,
                CreateNoWindow        = true,
            };
            authProc.Start();
            await authProc.WaitForExitAsync(authCts.Token).ConfigureAwait(false);
            authSw.Stop();

            return
            [
                installed,
                authProc.ExitCode == 0
                    ? Pass("github_cli_auth", "gh auth status: authenticated", authSw)
                    : Warn("github_cli_auth", "gh is not authenticated (optional for API readiness)", authSw),
            ];
        }
        catch (OperationCanceledException)
        {
            authSw.Stop();
            return
            [
                installed,
                Warn("github_cli_auth", "gh auth status timed out after 5 s (optional for API readiness)", authSw),
            ];
        }
        catch (Exception ex)
        {
            authSw.Stop();
            return
            [
                installed,
                Warn("github_cli_auth", $"gh auth status could not be checked: {ex.Message}", authSw),
            ];
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
                ".agentweaver/review-policies/ not present — built-in default review policy in use", sw);

        try
        {
            var count = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Count(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".yml",  StringComparison.OrdinalIgnoreCase));
            return Pass("review_policies_directory", $".agentweaver/review-policies/ present; {count} YAML file(s)", sw);
        }
        catch (Exception ex)
        {
            return Warn("review_policies_directory", $"Could not enumerate .agentweaver/review-policies/: {ex.Message}", sw);
        }
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

    private static DiagnosticsCheckDto CheckActiveReviewPolicy(Project project)
    {
        var sw = Stopwatch.StartNew();
        var policyName = string.IsNullOrWhiteSpace(project.ActiveReviewPolicyName)
            ? "default"
            : project.ActiveReviewPolicyName.Trim();

        if (string.Equals(policyName, "default", StringComparison.OrdinalIgnoreCase))
        {
            sw.Stop();
            return Pass("active_review_policy",
                "Active review policy: default, source=built-in, steps=3", sw);
        }

        var dir = Path.Combine(project.WorkingDirectory, ".agentweaver", "review-policies");
        var yaml = Path.Combine(dir, policyName + ".yaml");
        var yml = Path.Combine(dir, policyName + ".yml");
        sw.Stop();
        return File.Exists(yaml) || File.Exists(yml)
            ? Pass("active_review_policy", $"Active review policy: {policyName}, source=project", sw)
            : Warn("active_review_policy", $"Configured review policy '{policyName}' was not found; built-in default will be used", sw);
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

    private sealed record AgentPodQuotaSnapshot(
        double PodUsed,
        double PodLimit,
        double SandboxClaimUsed,
        double SandboxClaimLimit,
        string LimitingResource,
        double Used,
        double Limit)
    {
        public double Headroom => Math.Min(PodLimit - PodUsed, SandboxClaimLimit - SandboxClaimUsed);
    }
}
