using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentTools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentweaver.AgentHost;

internal sealed class PreviewRunnerOptions
{
    public int RingBufferLines { get; init; } = 500;
    public int ObserveTimeoutSeconds { get; init; } = 60;
    public int HealthTimeoutSeconds { get; init; } = 2;
    public int StopGraceSeconds { get; init; } = 5;
    public int IdleTimeoutMinutes { get; init; } = 30;
    public int MaxLifetimeHours { get; init; } = 8;
    public int ReaperIntervalSeconds { get; init; } = 60;
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
    string HealthEvidence);

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

internal sealed class PreviewRunnerToolProvider(IPreviewRunner runner) : IAgentRuntimeToolProvider
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
                var result = await runner.StartPreviewProcessAsync(
                    command,
                    string.IsNullOrWhiteSpace(cwd) ? context.WorkingDirectory : cwd!,
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
                    TimeSpan.FromSeconds(Math.Max(1, timeout_seconds ?? 60)),
                    "/",
                    ct).ConfigureAwait(false);

                return $"bound_port_observed: session_id={observed.SessionId}, port={observed.Port}, healthy={observed.Healthy}, evidence={observed.Evidence}, health={observed.HealthEvidence}. Call start_preview(port={observed.Port}) next.";
            },
            "observe_bound_port",
            "Observe the actual port the supervised preview process bound to. Parses stdout/stderr and falls back to socket diff; verifies HTTP before returning.");

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
    }
}

internal sealed class PreviewRunner : BackgroundService, IPreviewRunner
{
    private static readonly Regex[] PortPatterns =
    [
        new(@"\bLISTENING\s+ON\s+PORT\s+(?<port>\d{2,5})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bLocal:\s*https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[[^\]]+\]|[^:\s/]+):(?<port>\d{2,5})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bNow\s+listening\s+on:\s*https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[[^\]]+\]|[^:\s/]+):(?<port>\d{2,5})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private static readonly Regex SsPortPattern =
        new(@"\bLISTEN\s+\d+\s+\d+\s+(?<addr>\S+):(?<port>\d{2,5})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, PreviewProcessState> _sessions = new(StringComparer.Ordinal);
    private readonly PreviewRunnerOptions _options;
    private readonly ILogger<PreviewRunner> _logger;
    private readonly TimeProvider _clock;

    public PreviewRunner(IOptions<PreviewRunnerOptions> options, ILogger<PreviewRunner> logger, TimeProvider? clock = null)
    {
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
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

        var fullCwd = Path.GetFullPath(cwd);
        if (!Directory.Exists(fullCwd))
            throw new DirectoryNotFoundException($"Preview working directory does not exist: {fullCwd}");

        var beforePorts = await SnapshotListeningPortsAsync(ct).ConfigureAwait(false);
        var sessionId = Guid.NewGuid().ToString("n")[..12];
        var process = BuildProcess(command, fullCwd);
        var state = new PreviewProcessState(
            sessionId,
            runId ?? string.Empty,
            workPlanId,
            treeHash,
            command,
            fullCwd,
            beforePorts,
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
            state.AttachProcess(process);
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
        var deadline = _clock.GetUtcNow() + (timeout ?? TimeSpan.FromSeconds(_options.ObserveTimeoutSeconds));
        Exception? lastHealthFailure = null;

        while (_clock.GetUtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            state.Touch(_clock.GetUtcNow());

            if (state.HasExited)
                throw new InvalidOperationException(
                    $"Preview process exited before a healthy port was observed. exitCode={state.ExitCode}; logs={state.Buffer.SnapshotText(20)}");

            foreach (var (port, evidence) in CandidatePortsFromLogs(state.Buffer.SnapshotLines()))
            {
                var health = await HealthCheckAsync(sessionId, port, healthPath, ct).ConfigureAwait(false);
                if (health.Healthy)
                {
                    state.MarkPort(port);
                    return new PreviewPortObservation(sessionId, port, evidence, true, health.Evidence);
                }
                lastHealthFailure = new InvalidOperationException(health.Evidence);
            }

            var afterPorts = await SnapshotListeningPortsAsync(ct).ConfigureAwait(false);
            foreach (var port in afterPorts.Except(state.BaselinePorts).OrderBy(p => p))
            {
                var health = await HealthCheckAsync(sessionId, port, healthPath, ct).ConfigureAwait(false);
                if (health.Healthy)
                {
                    var evidence = $"socket_diff:ss -ltnp reported new listening port {port}";
                    state.MarkPort(port);
                    return new PreviewPortObservation(sessionId, port, evidence, true, health.Evidence);
                }
                lastHealthFailure = new InvalidOperationException(health.Evidence);
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Timed out waiting for preview process {sessionId} to expose a healthy HTTP port. " +
            $"Last health failure: {lastHealthFailure?.Message ?? "none"}. Logs: {state.Buffer.SnapshotText(20)}");
    }

    public async Task<PreviewHealthResult> HealthCheckAsync(
        string sessionId,
        int port,
        string path = "/",
        CancellationToken ct = default)
    {
        var state = GetSession(sessionId);
        state.Touch(_clock.GetUtcNow());
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
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
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

    private static async Task<HashSet<int>> SnapshotListeningPortsAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/ss") && !File.Exists("/bin/ss"))
            return [];

        var psi = new ProcessStartInfo
        {
            FileName = File.Exists("/usr/bin/ss") ? "/usr/bin/ss" : "/bin/ss",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-ltnp");

        using var process = Process.Start(psi);
        if (process is null)
            return [];
        var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        var ports = new HashSet<int>();
        foreach (var line in output.Split('\n'))
        {
            var match = SsPortPattern.Match(line);
            if (match.Success &&
                int.TryParse(match.Groups["port"].Value, out var port) &&
                port is > 0 and <= 65535)
            {
                ports.Add(port);
            }
        }

        return ports;
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

    private sealed class PreviewProcessState : IDisposable
    {
        private int _exited;

        public PreviewProcessState(
            string sessionId,
            string runId,
            string? workPlanId,
            string? treeHash,
            string command,
            string workingDirectory,
            HashSet<int> baselinePorts,
            RingBuffer buffer,
            DateTimeOffset startedAt)
        {
            SessionId = sessionId;
            RunId = runId;
            WorkPlanId = workPlanId;
            TreeHash = treeHash;
            Command = command;
            WorkingDirectory = workingDirectory;
            BaselinePorts = baselinePorts;
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
        public HashSet<int> BaselinePorts { get; }
        public RingBuffer Buffer { get; }
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset LastTouchedAt { get; private set; }
        public Process? Process { get; private set; }
        public int? ExitCode { get; private set; }
        public int? ObservedPort { get; private set; }
        public bool HasExited => Volatile.Read(ref _exited) == 1;

        public void AttachProcess(Process process) => Process = process;
        public void Touch(DateTimeOffset now) => LastTouchedAt = now;
        public void MarkPort(int port) => ObservedPort = port;
        public void MarkExited(int exitCode, DateTimeOffset now)
        {
            ExitCode = exitCode;
            LastTouchedAt = now;
            Volatile.Write(ref _exited, 1);
        }

        public void Dispose() => Process?.Dispose();
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
