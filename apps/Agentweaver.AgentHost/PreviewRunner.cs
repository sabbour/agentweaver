using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentTools;
using Agentweaver.SandboxFs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentweaver.AgentHost;

internal sealed class PreviewRunnerOptions
{
    public int RingBufferLines { get; init; } = 500;
    public int ObserveTimeoutSeconds { get; init; } = 60;
    public int MaxObserveTimeoutSeconds { get; init; } = 120;
    public int HealthTimeoutSeconds { get; init; } = 2;
    public int StopGraceSeconds { get; init; } = 5;
    public int IdleTimeoutMinutes { get; init; } = 30;
    public int MaxLifetimeHours { get; init; } = 8;
    public int ReaperIntervalSeconds { get; init; } = 60;

    // Public-port range for the pod-local TCP forwarder (spec-006 preview-forwarder). MUST MIRROR
    // SandboxPreviewOptions.AllowedPortMin/Max AND k8s/networkpolicy-sandbox.yaml (ingress
    // "port 3000 endPort 9000"): a public port outside this range is rejected by the Gateway or
    // black-holed by the NetworkPolicy. Keep these three in lockstep.
    public int PublicPortRangeMin { get; init; } = 3000;
    public int PublicPortRangeMax { get; init; } = 9000;
}

internal sealed record PreviewProcessStartResult(
    string SessionId,
    int Pid,
    DateTimeOffset StartedAt,
    string WorkingDirectory);

internal sealed record PreviewPortObservation(
    string SessionId,
    int Port,
    string Evidence,
    bool Healthy,
    string HealthEvidence,
    int AppPort = 0,
    string? Reason = null);

internal sealed record PreviewHealthResult(
    string SessionId,
    int Port,
    string Path,
    bool Healthy,
    int? StatusCode,
    string Evidence);

internal sealed record PreviewStopResult(
    string SessionId,
    bool Stopped,
    string Reason);

internal interface IPreviewRunner
{
    Task<PreviewProcessStartResult> StartPreviewProcessAsync(
        string command,
        string cwd,
        string? runId,
        string? workPlanId,
        string? treeHash,
        CancellationToken ct = default);

    Task<PreviewPortObservation> ObserveBoundPortAsync(
        string sessionId,
        TimeSpan? timeout = null,
        string healthPath = "/",
        CancellationToken ct = default);

    Task<PreviewHealthResult> HealthCheckAsync(
        string sessionId,
        int port,
        string path = "/",
        CancellationToken ct = default);

    Task<PreviewStopResult> StopPreviewProcessAsync(
        string sessionId,
        string reason,
        CancellationToken ct = default);
}

internal sealed class PreviewRunnerToolProvider(
    IPreviewRunner runner,
    IOptions<AgentHostOptions> agentHostOptions) : IAgentRuntimeToolProvider
{
    public IEnumerable<AIFunction> BuildTools(SandboxToolContext context)
    {
        yield return AIFunctionFactory.Create(
            async (
                [Description("Command that starts the preview app/server. Use the project script directly, e.g. npm run dev -- --host 0.0.0.0.")] string command,
                [Description("Working directory for the command. Defaults to the current run worktree.")] string? cwd = null,
                [Description("Optional assembly work plan id for metadata correlation.")] string? work_plan_id = null,
                [Description("Optional assembly tree hash for metadata correlation.")] string? tree_hash = null,
                CancellationToken ct = default) =>
            {
                var resolvedCwd = string.IsNullOrWhiteSpace(cwd)
                    ? context.WorkingDirectory
                    : SandboxPathValidator.ValidateAndResolve(cwd, context.WorkingDirectory);
                var result = await runner.StartPreviewProcessAsync(
                    command,
                    resolvedCwd,
                    context.RunId,
                    work_plan_id,
                    tree_hash,
                    ct).ConfigureAwait(false);

                return $"preview_process_started: session_id={result.SessionId}, pid={result.Pid}, cwd={result.WorkingDirectory}";
            },
            "start_preview_process",
            "Start a preview app/server under AgentHost supervision. The process stays alive after the tool returns; call observe_bound_port next.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Preview process session id returned by start_preview_process.")] string session_id,
                [Description("How long to wait for a bound port, in seconds. Default 60.")] int? timeout_seconds = null,
                CancellationToken ct = default) =>
            {
                var observed = await runner.ObserveBoundPortAsync(
                    session_id,
                    TimeSpan.FromSeconds(Math.Clamp(timeout_seconds ?? 60, 1, 120)),
                    "/",
                    ct).ConfigureAwait(false);

                var nextStep = observed.Healthy
                    ? $"Call start_preview(port={observed.Port}) next."
                    : $"Observation is NOT healthy (reason={observed.Reason ?? "unknown"}); do NOT call start_preview. Fix the app so it comes up on the discovered port and retry observe_bound_port.";
                return $"bound_port_observed: session_id={observed.SessionId}, port={observed.Port}, app_port={observed.AppPort}, healthy={observed.Healthy}, reason={observed.Reason ?? "n/a"}, evidence={observed.Evidence}, health={observed.HealthEvidence}. {nextStep}";
            },
            "observe_bound_port",
            "Observe the actual port the supervised preview process tree owns. Cross-references PID-owned socket inodes with /proc TCP tables and verifies HTTP before returning.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Preview process session id returned by start_preview_process.")] string session_id,
                [Description("Port to check on localhost inside the AgentHost pod.")] int port,
                [Description("HTTP path to probe. Defaults to /.")] string? path = "/",
                CancellationToken ct = default) =>
            {
                var health = await runner.HealthCheckAsync(session_id, port, path ?? "/", ct).ConfigureAwait(false);
                return $"preview_health: session_id={health.SessionId}, port={health.Port}, path={health.Path}, healthy={health.Healthy}, status={health.StatusCode?.ToString() ?? "n/a"}, evidence={health.Evidence}";
            },
            "health_check",
            "Verify that the supervised preview server responds over HTTP on the discovered port.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Preview process session id returned by start_preview_process.")] string session_id,
                [Description("Reason for stopping the preview process.")] string? reason = null,
                CancellationToken ct = default) =>
            {
                var stopped = await runner.StopPreviewProcessAsync(
                    session_id,
                    string.IsNullOrWhiteSpace(reason) ? "tool_stop" : reason!,
                    ct).ConfigureAwait(false);
                return $"preview_process_stopped: session_id={stopped.SessionId}, stopped={stopped.Stopped}, reason={stopped.Reason}";
            },
            "stop_preview_process",
            "Stop a supervised preview process and its child process tree.");

        // start_preview finalizes/publishes the durable, externally-reachable preview URL for the
        // port observe_bound_port just confirmed healthy (issue #334: observe_bound_port's own
        // response text tells the model to call this next, but until now no tool by this name was
        // ever registered for sandboxed subtask agents — only PreviewRunnerToolProvider's other
        // three preview tools were, since AgentweaverApiTools.Build's `start_preview` is gated on
        // projectId/agentName being set, which subtask agents intentionally don't receive (#268)).
        // Registering it here — alongside the tools observe_bound_port's hint assumes are always
        // available — keeps the dead-end from recurring regardless of that gating.
        if (!string.IsNullOrEmpty(context.RunId))
        {
            var options = agentHostOptions.Value;
            // #335 P1: prefer the per-turn ApiBaseUrl/ApiKey (delivered via AgentSetupParams on every
            // turn -- see CopilotAIAgent.BuildSessionConfigTools) over the static AgentHostOptions,
            // which the AgentHost pod template never populates on the warm-pool path. Falling back to
            // the static option (env-var launch path) then finally to localhost keeps this safe for
            // any context that predates the per-turn value (e.g. direct/CLI/test launches).
            var apiBaseUrl = !string.IsNullOrWhiteSpace(context.ApiBaseUrl)
                ? context.ApiBaseUrl!
                : string.IsNullOrWhiteSpace(options.ApiBaseUrl)
                    ? "http://localhost:5000"
                    : options.ApiBaseUrl!;
            var apiKey = string.IsNullOrWhiteSpace(context.ApiKey) ? options.ApiKey : context.ApiKey;
            yield return PreviewPublishTool.Build(apiBaseUrl, apiKey, context.RunId);
        }
    }
}

internal sealed class PreviewRunner : BackgroundService, IPreviewRunner
{
    /// <summary>
    /// Host header used when health-probing the app, matching the Host the preview gateway rewrites
    /// external traffic to (see SandboxPreviewService.PreviewUpstreamHost). Keeping the probe and the
    /// gateway on the same Host means readiness reflects real browser reachability (#312).
    /// </summary>
    private const string PreviewUpstreamHost = "localhost";

    private static readonly Regex[] PortPatterns =
    [
        new(@"\bLISTENING\s+ON\s+PORT\s+(?<port>\d{2,5})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bLocal:\s*https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[[^\]]+\]|[^:\s/]+):(?<port>\d{2,5})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bNow\s+listening\s+on:\s*https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[[^\]]+\]|[^:\s/]+):(?<port>\d{2,5})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    // Kernel socket tables (Linux). tcp6 is required: node's server.listen(port) binds ::(IPv6-any)
    // on dual-stack Linux, so the listening socket lands in tcp6, not tcp.
    private static readonly string[] ProcNetTcpFiles = ["/proc/net/tcp", "/proc/net/tcp6"];

    private readonly ConcurrentDictionary<string, PreviewProcessState> _sessions = new(StringComparer.Ordinal);
    private readonly PreviewRunnerOptions _options;
    private readonly ILogger<PreviewRunner> _logger;
    private readonly TimeProvider _clock;
    private readonly AgentHostRuntimeState? _runtimeState;

    public PreviewRunner(IOptions<PreviewRunnerOptions> options, ILogger<PreviewRunner> logger, TimeProvider? clock = null, AgentHostRuntimeState? runtimeState = null)
    {
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _runtimeState = runtimeState;
    }

    public async Task<PreviewProcessStartResult> StartPreviewProcessAsync(
        string command,
        string cwd,
        string? runId,
        string? workPlanId,
        string? treeHash,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Preview command is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(cwd))
            throw new ArgumentException("Preview working directory is required.", nameof(cwd));

        var sandboxRoot = _runtimeState?.EffectiveWorkingDirectory;
        var fullCwd = string.IsNullOrWhiteSpace(sandboxRoot)
            ? Path.GetFullPath(cwd)
            : Path.IsPathRooted(cwd)
                ? SandboxPathValidator.ValidateAbsoluteContained(cwd, sandboxRoot)
                : SandboxPathValidator.ValidateAndResolve(cwd, sandboxRoot);
        if (!Directory.Exists(fullCwd))
            throw new DirectoryNotFoundException($"Preview working directory does not exist: {fullCwd}");

        var sessionId = Guid.NewGuid().ToString("n")[..12];
        var process = BuildProcess(command, fullCwd);
        ScrubChildEnvironment(process.StartInfo);
        var state = new PreviewProcessState(
            sessionId,
            runId ?? string.Empty,
            workPlanId,
            treeHash,
            command,
            fullCwd,
            new RingBuffer(_options.RingBufferLines),
            _clock.GetUtcNow());

        process.OutputDataReceived += (_, e) => CaptureLine(state, "stdout", e.Data);
        process.ErrorDataReceived += (_, e) => CaptureLine(state, "stderr", e.Data);
        process.Exited += (_, _) =>
        {
            state.MarkExited(process.ExitCode, _clock.GetUtcNow());
            _logger.LogInformation(
                "PreviewRunner: process exited session={SessionId} pid={Pid} exitCode={ExitCode}",
                sessionId, SafeProcessId(process), process.ExitCode);
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Preview process did not start.");
            state.AttachProcess(process, CaptureProcessIdentity(process.Id));
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        if (!_sessions.TryAdd(sessionId, state))
        {
            await StopProcessTreeAsync(process, TimeSpan.FromSeconds(_options.StopGraceSeconds), ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Duplicate preview session id {sessionId}.");
        }

        _logger.LogInformation(
            "PreviewRunner: started session={SessionId} pid={Pid} run={RunId} cwd={Cwd}",
            sessionId, process.Id, runId, fullCwd);

        return new PreviewProcessStartResult(sessionId, process.Id, state.StartedAt, fullCwd);
    }

    public async Task<PreviewPortObservation> ObserveBoundPortAsync(
        string sessionId,
        TimeSpan? timeout = null,
        string healthPath = "/",
        CancellationToken ct = default)
    {
        var state = GetSession(sessionId);
        var requestedTimeout = timeout ?? TimeSpan.FromSeconds(_options.ObserveTimeoutSeconds);
        var effectiveTimeout = TimeSpan.FromSeconds(Math.Clamp(
            requestedTimeout.TotalSeconds,
            1,
            Math.Max(1, _options.MaxObserveTimeoutSeconds)));
        var deadline = _clock.GetUtcNow() + effectiveTimeout;
        Exception? lastHealthFailure = null;

        while (_clock.GetUtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            state.Touch(_clock.GetUtcNow());

            if (state.HasExited)
                return new PreviewPortObservation(
                    state.SessionId,
                    0,
                    $"process_exited: exitCode={state.ExitCode}; logs={state.Buffer.SnapshotText(20)}",
                    false,
                    $"Preview process exited before a healthy port was observed. exitCode={state.ExitCode}",
                    0,
                    $"process_exited:exit={state.ExitCode}");

            var sessionCandidates = await SnapshotProcessTreeListeningPortsAsync(state.RootIdentity, ct)
                .ConfigureAwait(false);

            foreach (var (port, evidence) in CandidatePortsFromLogs(state.Buffer.SnapshotLines())
                         .Where(candidate => sessionCandidates.Contains(candidate.Port)))
            {
                var health = await ProbeHealthAsync(sessionId, port, healthPath, ct).ConfigureAwait(false);
                if (health.Healthy)
                {
                    state.MarkPort(port);
                    return await BuildForwardedObservationAsync(state, port, evidence, health.Evidence, healthPath, ct)
                        .ConfigureAwait(false);
                }
                lastHealthFailure = new InvalidOperationException(health.Evidence);
            }

            foreach (var port in sessionCandidates)
            {
                var health = await ProbeHealthAsync(sessionId, port, healthPath, ct).ConfigureAwait(false);
                if (health.Healthy)
                {
                    var evidence = $"process_tree_socket:/proc reported PID-owned listening port {port}";
                    state.MarkPort(port);
                    return await BuildForwardedObservationAsync(state, port, evidence, health.Evidence, healthPath, ct)
                        .ConfigureAwait(false);
                }
                lastHealthFailure = new InvalidOperationException(health.Evidence);
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return new PreviewPortObservation(
            state.SessionId,
            0,
            $"no_listening_port_discovered: last_health_failure={lastHealthFailure?.Message ?? "none"}; " +
            $"logs={state.Buffer.SnapshotText(20)}",
            false,
            $"Timed out after {effectiveTimeout.TotalSeconds:0}s " +
            $"waiting for preview process {sessionId} to expose a healthy HTTP port.",
            0,
            "no_listening_port_discovered");
    }

    /// <summary>
    /// Ensures the pod-local <see cref="TcpPortForwarder"/> is running for the discovered app port and
    /// verifies reachability THROUGH the forwarder's public port before greenlighting registration
    /// (spec-006 preview-forwarder, observe/register consistency). The returned observation reports the
    /// public (pod-IP-reachable) port as <see cref="PreviewPortObservation.Port"/> — that is what the
    /// Gateway registers — and keeps the app's real loopback port in <see cref="PreviewPortObservation.AppPort"/>
    /// and the evidence string. If the forwarder public port itself does not pass a health check, the
    /// observation is returned unhealthy with a distinct <c>bound_unreachable</c> reason (never silent).
    /// </summary>
    private async Task<PreviewPortObservation> BuildForwardedObservationAsync(
        PreviewProcessState state,
        int appPort,
        string appEvidence,
        string appHealthEvidence,
        string healthPath,
        CancellationToken ct)
    {
        TcpPortForwarder forwarder;
        try
        {
            forwarder = state.EnsureForwarder(appPort, _options.PublicPortRangeMin, _options.PublicPortRangeMax, _logger);
        }
        catch (NoPublicPortAvailableException ex)
        {
            _logger.LogWarning(ex, "PreviewRunner: no free public port for session {SessionId} app port {AppPort}", state.SessionId, appPort);
            return new PreviewPortObservation(
                state.SessionId,
                0,
                $"public_port_exhausted:[{_options.PublicPortRangeMin},{_options.PublicPortRangeMax}]; app_evidence={appEvidence}",
                false,
                ex.Message,
                appPort,
                "no_public_port_available");
        }

        var publicPort = forwarder.PublicPort;

        var forwardedHealth = await ProbeHealthAsync(state.SessionId, publicPort, healthPath, ct).ConfigureAwait(false);
        var forwardEvidence = $"forwarder:0.0.0.0:{publicPort}->127.0.0.1:{appPort}; app_evidence={appEvidence}";

        if (!forwardedHealth.Healthy)
        {
            return new PreviewPortObservation(
                state.SessionId,
                publicPort,
                forwardEvidence,
                false,
                $"bound_unreachable: forwarder public port {publicPort} did not pass a health check ({forwardedHealth.Evidence})",
                appPort,
                "bound_unreachable");
        }

        return new PreviewPortObservation(
            state.SessionId,
            publicPort,
            forwardEvidence,
            true,
            $"reachable via forwarder public port {publicPort}: {forwardedHealth.Evidence} (app health: {appHealthEvidence})",
            appPort,
            null);
    }

    public async Task<PreviewHealthResult> HealthCheckAsync(
        string sessionId,
        int port,
        string path = "/",
        CancellationToken ct = default)
    {
        var state = GetSession(sessionId);
        state.Touch(_clock.GetUtcNow());
        if (state.HasExited)
            throw new InvalidOperationException(
                $"Preview session {sessionId} has exited; its ports are no longer attributable.");

        if (!state.TryGetAttributedAppPort(port, out var appPort))
            throw new InvalidOperationException(
                $"Port {port} is not attributed to preview session {sessionId}.");

        var ownedPorts = await SnapshotProcessTreeListeningPortsAsync(state.RootIdentity, ct).ConfigureAwait(false);
        if (!ownedPorts.Contains(appPort))
            throw new InvalidOperationException(
                $"Port {port} is no longer owned by preview session {sessionId}'s process tree.");

        return await ProbeHealthAsync(sessionId, port, path, ct).ConfigureAwait(false);
    }

    private async Task<PreviewHealthResult> ProbeHealthAsync(
        string sessionId,
        int port,
        string path,
        CancellationToken ct)
    {
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");

        var normalizedPath = string.IsNullOrWhiteSpace(path) ? "/" : path;
        if (!normalizedPath.StartsWith('/'))
            normalizedPath = "/" + normalizedPath;

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.HealthTimeoutSeconds)),
        };

        var url = $"http://127.0.0.1:{port}{normalizedPath}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Send the SAME Host the preview gateway rewrites external traffic to (#312). Connecting
            // to 127.0.0.1 keeps the probe pod-local and fast, but the app must see the production
            // Host so readiness reflects what a real browser request (Host -> "localhost" at the
            // gateway) will get. Without this, the probe's implicit "127.0.0.1" Host is an IP that
            // dev-server host allowlists (Vite/CRA/Angular) always accept, masking a host block.
            request.Headers.Host = PreviewUpstreamHost;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            var healthy = (int)response.StatusCode < 500;
            return new PreviewHealthResult(
                sessionId,
                port,
                normalizedPath,
                healthy,
                (int)response.StatusCode,
                healthy ? $"HTTP {(int)response.StatusCode} from {url}" : $"HTTP {(int)response.StatusCode} from {url}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PreviewHealthResult(sessionId, port, normalizedPath, false, null, $"Timed out probing {url}");
        }
        catch (HttpRequestException ex)
        {
            return new PreviewHealthResult(sessionId, port, normalizedPath, false, null, ex.Message);
        }
        catch (SocketException ex)
        {
            return new PreviewHealthResult(sessionId, port, normalizedPath, false, null, ex.Message);
        }
    }

    public async Task<PreviewStopResult> StopPreviewProcessAsync(
        string sessionId,
        string reason,
        CancellationToken ct = default)
    {
        if (!_sessions.TryRemove(sessionId, out var state))
            return new PreviewStopResult(sessionId, false, reason);

        await state.DisposeForwarderAsync().ConfigureAwait(false);

        var process = state.Process;
        if (process is not null)
            await StopProcessTreeAsync(process, TimeSpan.FromSeconds(_options.StopGraceSeconds), ct).ConfigureAwait(false);

        state.Dispose();
        _logger.LogInformation("PreviewRunner: stopped session={SessionId} reason={Reason}", sessionId, reason);
        return new PreviewStopResult(sessionId, true, reason);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _options.ReaperIntervalSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var now = _clock.GetUtcNow();
            foreach (var state in _sessions.Values)
            {
                var maxLifetimeExceeded = now - state.StartedAt > TimeSpan.FromHours(Math.Max(1, _options.MaxLifetimeHours));
                var idleExceeded = now - state.LastTouchedAt > TimeSpan.FromMinutes(Math.Max(1, _options.IdleTimeoutMinutes));
                var exited = state.HasExited;
                if (!maxLifetimeExceeded && !idleExceeded && !exited)
                    continue;

                var reason = exited
                    ? "process_exited"
                    : maxLifetimeExceeded ? "max_lifetime" : "idle_timeout";
                try
                {
                    await StopPreviewProcessAsync(state.SessionId, reason, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "PreviewRunner: reaper failed to stop session {SessionId}", state.SessionId);
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var sessionId in _sessions.Keys.ToArray())
            await StopPreviewProcessAsync(sessionId, "agenthost_shutdown", cancellationToken).ConfigureAwait(false);

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private PreviewProcessState GetSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
            throw new KeyNotFoundException($"Preview process session not found: {sessionId}");
        return state;
    }

    private static Process BuildProcess(string command, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            psi.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else if (File.Exists("/usr/bin/setsid"))
        {
            psi.FileName = "/usr/bin/setsid";
            psi.ArgumentList.Add("/bin/sh");
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(command);
        }

        return new Process { StartInfo = psi, EnableRaisingEvents = true };
    }

    /// <summary>
    /// Test-only seam (spec-006 §11 env-scrub assertion): builds the child <see cref="ProcessStartInfo"/>
    /// for <paramref name="command"/> and applies <see cref="ScrubChildEnvironment"/>, returning the
    /// scrubbed environment WITHOUT spawning a process.
    /// </summary>
    internal IReadOnlyDictionary<string, string?> BuildScrubbedChildEnvironmentForTest(string command, string cwd)
    {
        var process = BuildProcess(command, cwd);
        ScrubChildEnvironment(process.StartInfo);
        var env = process.StartInfo.Environment.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value);
        process.Dispose();
        return env;
    }

    // Defense-in-depth (spec-006 decouple-preview, BLOCKER A): the child preview process inherits the
    // parent AgentHost environment. Explicitly STRIP any auth-bearing variables — the current turn
    // token, the per-run preview-runner credential, and known secret env names — so the untrusted app
    // can never read a credential and drive /preview-runner/* itself. Belt-and-suspenders on top of
    // the in-memory (never-env) credential delivery.
    private static readonly string[] SecretEnvNames =
    [
        "AgentHost__TurnBearerToken",
        "AGENTHOST__TURNBEARERTOKEN",
        "AgentHost__PreviewRunnerCredential",
        "AGENTHOST__PREVIEWRUNNERCREDENTIAL",
        "GITHUB_ACCESS_TOKEN",
        "GITHUB_TOKEN",
        "GH_TOKEN",
        "TurnBearerToken",
        "PreviewRunnerCredential",
    ];

    private void ScrubChildEnvironment(ProcessStartInfo psi)
    {
        var env = psi.Environment;

        // Remove by known secret env var name (case-insensitive).
        foreach (var key in env.Keys.ToArray())
        {
            if (SecretEnvNames.Any(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase)))
                env.Remove(key);
            else if (key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                     || key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
                     || (key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                         && key.StartsWith("AgentHost", StringComparison.OrdinalIgnoreCase)))
                env.Remove(key);
        }

        // Remove by VALUE match against the live credentials (covers any aliased var name).
        var secrets = new[] { _runtimeState?.TurnBearerToken, _runtimeState?.PreviewRunnerCredential }
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();
        if (secrets.Length > 0)
        {
            foreach (var kvp in env.ToArray())
            {
                if (kvp.Value is not null && secrets.Any(s => string.Equals(s, kvp.Value, StringComparison.Ordinal)))
                    env.Remove(kvp.Key);
            }
        }
    }

    private static void CaptureLine(PreviewProcessState state, string stream, string? line)
    {
        if (line is null)
            return;
        state.Buffer.Add($"{DateTimeOffset.UtcNow:O} {stream}: {line}");
        state.Touch(DateTimeOffset.UtcNow);
    }

    private static IEnumerable<(int Port, string Evidence)> CandidatePortsFromLogs(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            foreach (var regex in PortPatterns)
            {
                var match = regex.Match(line);
                if (match.Success &&
                    int.TryParse(match.Groups["port"].Value, out var port) &&
                    port is > 0 and <= 65535)
                {
                    yield return (port, $"log:{line}");
                }
            }
        }
    }

    private static async Task<HashSet<int>> SnapshotProcessTreeListeningPortsAsync(
        ProcessIdentity rootIdentity,
        CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return [];

        var socketInodes = await SnapshotProcessTreeSocketInodesAsync(rootIdentity, ct).ConfigureAwait(false);
        if (socketInodes.Count == 0)
            return [];

        var procNetContents = new List<string>(ProcNetTcpFiles.Length);
        foreach (var procFile in ProcNetTcpFiles)
        {
            try
            {
                if (!File.Exists(procFile))
                    continue;
                var contents = await File.ReadAllTextAsync(procFile, ct).ConfigureAwait(false);
                procNetContents.Add(contents);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A transient /proc read race is retried on the next observation poll.
            }
        }

        return SelectOwnedListeningPorts(socketInodes, procNetContents);
    }

    /// <summary>
    /// Pure parser for the kernel's <c>/proc/net/tcp</c> and <c>/proc/net/tcp6</c> tables. Returns the
    /// set of ports whose socket is in the LISTEN state (<c>st == 0A</c>). Field layout (whitespace
    /// separated, first line is a header): field[1] = <c>local_address</c> as <c>HEXIP:HEXPORT</c>,
    /// field[3] = <c>st</c> (connection-state hex). Kept static + filesystem-free so it is unit-testable.
    /// </summary>
    internal static HashSet<int> ParseListeningPortsFromProcNet(string procNetContents)
        => ParseListeningSocketPortsFromProcNet(procNetContents).Values.ToHashSet();

    /// <summary>
    /// Cross-references socket inodes owned by one supervised process tree with LISTEN entries from
    /// the namespace-wide kernel TCP tables. Unrelated processes' sockets are excluded even if they
    /// start listening during this session's observation window.
    /// </summary>
    internal static HashSet<int> SelectOwnedListeningPorts(
        IEnumerable<ulong> ownedSocketInodes,
        IEnumerable<string> procNetContents)
    {
        var owned = ownedSocketInodes.ToHashSet();
        var ports = new HashSet<int>();
        if (owned.Count == 0)
            return ports;

        foreach (var contents in procNetContents)
        {
            foreach (var (inode, port) in ParseListeningSocketPortsFromProcNet(contents))
            {
                if (owned.Contains(inode))
                    ports.Add(port);
            }
        }

        return ports;
    }

    private static Dictionary<ulong, int> ParseListeningSocketPortsFromProcNet(string procNetContents)
    {
        var sockets = new Dictionary<ulong, int>();
        if (string.IsNullOrEmpty(procNetContents))
            return sockets;

        var lines = procNetContents.Split('\n');
        for (var i = 1; i < lines.Length; i++)
        {
            var fields = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10)
                continue;

            if (!string.Equals(fields[3], "0A", StringComparison.OrdinalIgnoreCase))
                continue;

            var local = fields[1];
            var colon = local.LastIndexOf(':');
            if (colon < 0 || colon == local.Length - 1)
                continue;

            var hexPort = local[(colon + 1)..];
            if (int.TryParse(hexPort, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port)
                && port is > 0 and <= 65535
                && ulong.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out var inode))
            {
                sockets[inode] = port;
            }
        }

        return sockets;
    }

    private static async Task<HashSet<ulong>> SnapshotProcessTreeSocketInodesAsync(
        ProcessIdentity rootIdentity,
        CancellationToken ct)
    {
        var socketInodes = new HashSet<ulong>();
        var visited = new HashSet<ProcessIdentity>();
        var pending = new Queue<ProcessIdentity>();
        pending.Enqueue(rootIdentity);

        while (pending.TryDequeue(out var identity))
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(identity) || !IsCurrentProcessIdentity(identity))
                continue;

            var childIdentities = new List<ProcessIdentity>();
            try
            {
                var taskDirectory = $"/proc/{identity.Pid}/task";
                if (Directory.Exists(taskDirectory))
                {
                    foreach (var threadDirectory in Directory.EnumerateDirectories(taskDirectory))
                    {
                        var childrenPath = Path.Combine(threadDirectory, "children");
                        if (!File.Exists(childrenPath))
                            continue;

                        var children = await File.ReadAllTextAsync(childrenPath, ct).ConfigureAwait(false);
                        foreach (var token in children.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var childPid)
                                && TryCaptureProcessIdentity(childPid, out var childIdentity))
                            {
                                childIdentities.Add(childIdentity);
                            }
                        }
                    }
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // The process may exit or fork while /proc is being traversed; the next poll retries.
            }

            var processSocketInodes = new HashSet<ulong>();
            await CollectSocketInodesAsync(identity, processSocketInodes, ct).ConfigureAwait(false);

            // Re-check after reading children and fds. If the process exited and Linux recycled its
            // PID during either enumeration, none of that data belongs to the supervised process.
            if (!IsCurrentProcessIdentity(identity))
                continue;

            socketInodes.UnionWith(processSocketInodes);
            foreach (var childIdentity in childIdentities)
                pending.Enqueue(childIdentity);
        }

        // Preview commands normally start through `setsid`, making the root process the leader of a
        // private session. A dev-server wrapper can double-fork (or otherwise reparent) Vite after
        // launch, which makes it disappear from the PPID tree while it remains safely in that private
        // session. Include those same-session sockets only when the root is its own session leader;
        // this preserves isolation and never attributes AgentHost or another preview's sockets.
        if (TryGetProcessSessionId(rootIdentity.Pid, out var rootSessionId) &&
            rootSessionId == rootIdentity.Pid &&
            IsCurrentProcessIdentity(rootIdentity))
        {
            await CollectPrivateSessionSocketInodesAsync(
                rootIdentity, rootSessionId, visited, socketInodes, ct).ConfigureAwait(false);
        }

        return socketInodes;
    }

    private static async Task CollectPrivateSessionSocketInodesAsync(
        ProcessIdentity rootIdentity,
        int sessionId,
        ISet<ProcessIdentity> knownTreeMembers,
        ISet<ulong> socketInodes,
        CancellationToken ct)
    {
        try
        {
            foreach (var processDirectory in Directory.EnumerateDirectories("/proc"))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(processDirectory);
                if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
                    pid == rootIdentity.Pid ||
                    !TryCaptureProcessIdentity(pid, out var identity) ||
                    knownTreeMembers.Contains(identity) ||
                    !TryGetProcessSessionId(pid, out var candidateSessionId) ||
                    candidateSessionId != sessionId)
                {
                    continue;
                }

                var processSocketInodes = new HashSet<ulong>();
                await CollectSocketInodesAsync(identity, processSocketInodes, ct).ConfigureAwait(false);
                if (IsCurrentProcessIdentity(identity))
                    socketInodes.UnionWith(processSocketInodes);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // /proc is inherently racy; normal tree attribution remains available and the next poll retries.
        }
    }

    private static async Task CollectSocketInodesAsync(
        ProcessIdentity identity,
        ISet<ulong> socketInodes,
        CancellationToken ct)
    {
        try
        {
            var fdDirectory = $"/proc/{identity.Pid}/fd";
            if (!Directory.Exists(fdDirectory))
                return;

            foreach (var fdPath in Directory.EnumerateFileSystemEntries(fdDirectory))
            {
                ct.ThrowIfCancellationRequested();
                string? target;
                try
                {
                    target = new FileInfo(fdPath).LinkTarget;
                }
                catch
                {
                    continue;
                }

                if (TryParseSocketInode(target, out var inode))
                    socketInodes.Add(inode);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // File descriptors are inherently racy; a later poll observes stable sockets.
        }
    }

    private static ProcessIdentity CaptureProcessIdentity(int pid)
        => TryCaptureProcessIdentity(pid, out var identity)
            ? identity
            : new ProcessIdentity(pid, null);

    private static bool TryCaptureProcessIdentity(int pid, out ProcessIdentity identity)
    {
        identity = default;
        if (!OperatingSystem.IsLinux())
        {
            identity = new ProcessIdentity(pid, null);
            return true;
        }

        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            if (TryParseProcessStartTime(stat, out var startTime))
            {
                identity = new ProcessIdentity(pid, startTime);
                return true;
            }
        }
        catch
        {
            // The process may have exited before its identity could be captured.
        }

        return false;
    }

    private static bool IsCurrentProcessIdentity(ProcessIdentity identity)
    {
        if (!OperatingSystem.IsLinux())
            return true;
        if (identity.StartTime is not { } expectedStartTime)
            return false;

        try
        {
            var stat = File.ReadAllText($"/proc/{identity.Pid}/stat");
            return TryParseProcessStartTime(stat, out var currentStartTime)
                && currentStartTime == expectedStartTime;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetProcessSessionId(int pid, out int sessionId)
    {
        sessionId = 0;
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var commandEnd = stat.LastIndexOf(')');
            if (commandEnd < 0 || commandEnd + 2 >= stat.Length)
                return false;

            // After "(comm)", tokens begin at field 3 (state); session is field 6.
            var fields = stat[(commandEnd + 1)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 3 &&
                int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out sessionId);
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryParseProcessStartTime(string statContents, out ulong startTime)
    {
        startTime = 0;
        var commandEnd = statContents.LastIndexOf(')');
        if (commandEnd < 0 || commandEnd + 2 >= statContents.Length)
            return false;

        // After "(comm)", tokens begin at field 3 (state); starttime is field 22.
        var fields = statContents[(commandEnd + 1)..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 19
            && ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out startTime);
    }

    internal static (int Pid, ulong? StartTime) CaptureProcessIdentityForTest(int pid)
    {
        var identity = CaptureProcessIdentity(pid);
        return (identity.Pid, identity.StartTime);
    }

    internal static Task<HashSet<int>> SnapshotProcessTreeListeningPortsForTestAsync(
        int pid,
        ulong? startTime,
        CancellationToken ct)
        => SnapshotProcessTreeListeningPortsAsync(new ProcessIdentity(pid, startTime), ct);

    /// <summary>
    /// Test seam: probe an app on <paramref name="port"/> directly, bypassing process-tree
    /// attribution. Exercises the real <see cref="ProbeHealthAsync"/> path, including the Host
    /// header the gateway rewrites external traffic to (#312).
    /// </summary>
    internal Task<PreviewHealthResult> ProbeHealthForTestAsync(int port, string path, CancellationToken ct)
        => ProbeHealthAsync("test-session", port, path, ct);

    private static bool TryParseSocketInode(string? linkTarget, out ulong inode)
    {
        const string prefix = "socket:[";
        inode = 0;
        if (linkTarget is null ||
            !linkTarget.StartsWith(prefix, StringComparison.Ordinal) ||
            !linkTarget.EndsWith(']'))
        {
            return false;
        }

        return ulong.TryParse(
            linkTarget.AsSpan(prefix.Length, linkTarget.Length - prefix.Length - 1),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out inode);
    }

    private async Task StopProcessTreeAsync(Process process, TimeSpan grace, CancellationToken ct)
    {
        if (process.HasExited)
            return;

        if (!OperatingSystem.IsWindows())
            await SendUnixProcessGroupSignalAsync(process.Id, "TERM", ct).ConfigureAwait(false);

        try
        {
            using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            graceCts.CancelAfter(grace);
            await process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // escalate below
        }

        if (!process.HasExited)
        {
            if (!OperatingSystem.IsWindows())
                await SendUnixProcessGroupSignalAsync(process.Id, "KILL", CancellationToken.None).ConfigureAwait(false);

            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }

        try { await process.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch { /* best effort during shutdown */ }
    }

    private static async Task SendUnixProcessGroupSignalAsync(int pid, string signal, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/kill",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-" + signal);
        psi.ArgumentList.Add("-" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            using var kill = Process.Start(psi);
            if (kill is not null)
                await kill.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Fallback caller will use Process.Kill where possible.
        }
    }

    private static int SafeProcessId(Process process)
    {
        try { return process.Id; }
        catch { return -1; }
    }

    private readonly record struct ProcessIdentity(int Pid, ulong? StartTime);

    private sealed class PreviewProcessState : IDisposable
    {
        private int _exited;
        private readonly object _forwarderLock = new();
        private readonly object _portsLock = new();
        private TcpPortForwarder? _forwarder;

        public PreviewProcessState(
            string sessionId,
            string runId,
            string? workPlanId,
            string? treeHash,
            string command,
            string workingDirectory,
            RingBuffer buffer,
            DateTimeOffset startedAt)
        {
            SessionId = sessionId;
            RunId = runId;
            WorkPlanId = workPlanId;
            TreeHash = treeHash;
            Command = command;
            WorkingDirectory = workingDirectory;
            Buffer = buffer;
            StartedAt = startedAt;
            LastTouchedAt = startedAt;
        }

        public string SessionId { get; }
        public string RunId { get; }
        public string? WorkPlanId { get; }
        public string? TreeHash { get; }
        public string Command { get; }
        public string WorkingDirectory { get; }
        public RingBuffer Buffer { get; }
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset LastTouchedAt { get; private set; }
        public Process? Process { get; private set; }
        public int? ExitCode { get; private set; }
        public int? ObservedPort { get; private set; }
        public ProcessIdentity RootIdentity { get; private set; }
        public bool HasExited => Volatile.Read(ref _exited) == 1;

        public void AttachProcess(Process process, ProcessIdentity rootIdentity)
        {
            Process = process;
            RootIdentity = rootIdentity;
        }
        public void Touch(DateTimeOffset now) => LastTouchedAt = now;
        public void MarkPort(int port)
        {
            lock (_portsLock)
                ObservedPort = port;
        }

        public bool TryGetAttributedAppPort(int requestedPort, out int appPort)
        {
            lock (_portsLock)
            {
                if (ObservedPort == requestedPort)
                {
                    appPort = requestedPort;
                    return true;
                }
            }

            lock (_forwarderLock)
            {
                if (_forwarder?.PublicPort == requestedPort)
                {
                    appPort = _forwarder.AppPort;
                    return true;
                }
            }

            appPort = 0;
            return false;
        }

        /// <summary>
        /// Idempotently starts (or returns the existing) pod-local TCP forwarder fronting
        /// <paramref name="appPort"/> on an in-range public port. One forwarder per session; a second
        /// observe reuses it. Propagates <see cref="NoPublicPortAvailableException"/> on range exhaustion.
        /// </summary>
        public TcpPortForwarder EnsureForwarder(int appPort, int rangeMin, int rangeMax, ILogger logger)
        {
            lock (_forwarderLock)
            {
                if (_forwarder is not null)
                    return _forwarder;

                var forwarder = new TcpPortForwarder(appPort, rangeMin, rangeMax, logger);
                forwarder.Start();
                _forwarder = forwarder;
                return forwarder;
            }
        }

        public async ValueTask DisposeForwarderAsync()
        {
            TcpPortForwarder? forwarder;
            lock (_forwarderLock)
            {
                forwarder = _forwarder;
                _forwarder = null;
            }

            if (forwarder is not null)
                await forwarder.DisposeAsync().ConfigureAwait(false);
        }

        public void MarkExited(int exitCode, DateTimeOffset now)
        {
            ExitCode = exitCode;
            LastTouchedAt = now;
            Volatile.Write(ref _exited, 1);
        }

        public void Dispose()
        {
            // Best-effort: forwarder is normally torn down via DisposeForwarderAsync in
            // StopPreviewProcessAsync; guard against a leak on any synchronous disposal path.
            TcpPortForwarder? forwarder;
            lock (_forwarderLock)
            {
                forwarder = _forwarder;
                _forwarder = null;
            }
            if (forwarder is not null)
                _ = forwarder.DisposeAsync().AsTask();

            Process?.Dispose();
        }
    }

    private sealed class RingBuffer
    {
        private readonly int _capacity;
        private readonly Queue<string> _lines = new();
        private readonly object _lock = new();

        public RingBuffer(int capacity) => _capacity = Math.Max(50, capacity);

        public void Add(string line)
        {
            lock (_lock)
            {
                _lines.Enqueue(line);
                while (_lines.Count > _capacity)
                    _lines.Dequeue();
            }
        }

        public IReadOnlyList<string> SnapshotLines()
        {
            lock (_lock)
                return _lines.ToArray();
        }

        public string SnapshotText(int maxLines)
        {
            lock (_lock)
                return string.Join('\n', _lines.TakeLast(Math.Max(1, maxLines)));
        }
    }
}
