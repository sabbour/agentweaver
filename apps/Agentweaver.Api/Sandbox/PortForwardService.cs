using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Agentweaver.Api.Sandbox;

/// <summary>Represents an active port-forward session for a sandbox pod.</summary>
public sealed record PortForwardSession(
    string SessionId,
    string RunId,
    string PodName,
    int LocalPort,
    int TargetPort,
    DateTimeOffset StartedAt);

public sealed class PortForwardLimitExceededException(string message) : InvalidOperationException(message);

/// <summary>
/// Manages kubectl port-forward processes that tunnel a port from a sandbox pod to a
/// local port on the API server. Sessions are tracked in memory; they are cleaned up
/// when stopped explicitly or when the process exits on its own.
///
/// Requires:
///   - <see cref="IPodNameRegistry"/> to resolve pod name from run ID.
///   - kubectl available on PATH (or via Sandbox:KubectlPath config key).
///   - The API server must be running in the same network as the Kubernetes cluster
///     (i.e., inside the cluster or with kubeconfig access).
/// </summary>
public sealed class PortForwardService : IDisposable
{
    private static readonly Regex Dns1123LabelRegex = new(
        @"^[a-z0-9]([-a-z0-9]*[a-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex ForwardingPortRegex = new(
        @"Forwarding\s+from\s+127\.0\.0\.1:(?<port>\d+)\s+->",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly TimeSpan KubectlReadyTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TcpProbeInterval = TimeSpan.FromMilliseconds(100);

    private sealed record ActiveSession(PortForwardSession Meta, Process Process);

    private readonly IPodNameRegistry _podRegistry;
    private readonly IConfiguration _config;
    private readonly ILogger<PortForwardService> _logger;
    private readonly ConcurrentDictionary<string, ActiveSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sessionsByRun = new(StringComparer.Ordinal);
    private readonly object _sessionGate = new();

    public PortForwardService(
        IPodNameRegistry podRegistry,
        IConfiguration config,
        ILogger<PortForwardService> logger)
    {
        _podRegistry = podRegistry;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Starts a kubectl port-forward from <paramref name="targetPort"/> on the sandbox pod
    /// bound to <paramref name="runId"/> to a randomly chosen local port.
    /// </summary>
    /// <returns>The session descriptor (session ID + local port).</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no pod is registered for the run (sandbox not yet bound or already finished).
    /// </exception>
    public Task<PortForwardSession> StartAsync(string runId, int targetPort, CancellationToken ct = default)
    {
        if (targetPort is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(targetPort), "targetPort must be between 1 and 65535.");

        var podName = _podRegistry.TryGet(runId);
        if (string.IsNullOrEmpty(podName))
            throw new InvalidOperationException(
                $"No sandbox pod registered for run {runId}. " +
                "The run must be in_progress with an active Kubernetes sandbox to use port-forward.");

        return StartForPodAsync(runId, podName, targetPort, ct);
    }

    private async Task<PortForwardSession> StartForPodAsync(
        string runId, string podName, int targetPort, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var ns = _config["Sandbox:Kubernetes:Namespace"] ?? "agentweaver";
        ValidateDns1123Label(podName, nameof(podName));
        ValidateDns1123Label(ns, "namespace");

        var sessionId = Guid.NewGuid().ToString("N")[..12];
        ReserveSessionSlot(runId, sessionId);

        var kubectlPath = _config["Sandbox:KubectlPath"] ?? "kubectl";

        _logger.LogInformation(
            "PortForwardService: starting session {Session} for run {RunId}: {Kubectl} port-forward --address 127.0.0.1 pod/{Pod} :{TargetPort} -n {Namespace}",
            sessionId, runId, kubectlPath, podName, targetPort, ns);

        var psi = new ProcessStartInfo
        {
            FileName               = kubectlPath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("port-forward");
        psi.ArgumentList.Add("--address");
        psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add($"pod/{podName}");
        psi.ArgumentList.Add($":{targetPort}");
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(ns);

        var outputLines = new ConcurrentQueue<string>();
        var localPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => CaptureKubectlLine(e.Data, outputLines, localPortTcs);
        process.ErrorDataReceived += (_, e) => CaptureKubectlLine(e.Data, outputLines, localPortTcs);
        process.Exited += (_, _) =>
        {
            if (_sessions.TryRemove(sessionId, out _))
                ReleaseSessionSlot(runId, sessionId);
            else
                ReleaseSessionSlot(runId, sessionId);

            _logger.LogInformation(
                "PortForwardService: session {Session} process exited (run {RunId})", sessionId, runId);
            process.Dispose();
        };

        int localPort;
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("kubectl process did not start.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            localPort = await WaitForForwardedPortAsync(process, localPortTcs, outputLines, ct)
                .ConfigureAwait(false);

            if (!await WaitForTcpReadyAsync(localPort, process, ct).ConfigureAwait(false))
                throw new InvalidOperationException(
                    $"kubectl port-forward did not become ready on 127.0.0.1:{localPort} within {KubectlReadyTimeout.TotalSeconds:n0}s.");
        }
        catch (Exception ex)
        {
            CleanupFailedStart(process, runId, sessionId);
            throw new InvalidOperationException(
                $"Failed to start kubectl port-forward. Ensure kubectl is installed and on PATH " +
                $"(or configure Sandbox:KubectlPath). Error: {ex.Message}. " +
                $"kubectl output: {FormatKubectlOutput(outputLines)}", ex);
        }

        var session = new PortForwardSession(sessionId, runId, podName, localPort, targetPort, DateTimeOffset.UtcNow);
        var active   = new ActiveSession(session, process);
        _sessions[sessionId] = active;

        return session;
    }

    /// <summary>
    /// Terminates the port-forward session identified by <paramref name="sessionId"/>.
    /// Returns <see langword="true"/> when found and stopped; <see langword="false"/> when not found.
    /// </summary>
    public bool Stop(string runId, string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var active))
            return false;

        if (active.Meta.RunId != runId)
            return false;

        if (!_sessions.TryRemove(sessionId, out active))
            return false;

        ReleaseSessionSlot(runId, sessionId);

        KillProcess(active.Process, sessionId);
        return true;
    }

    /// <summary>Returns a snapshot of all active sessions for the given run.</summary>
    public IReadOnlyList<PortForwardSession> ListForRun(string runId) =>
        _sessions.Values
            .Where(s => string.Equals(s.Meta.RunId, runId, StringComparison.Ordinal))
            .Select(s => s.Meta)
            .ToList();

    private static void ValidateDns1123Label(string value, string name)
    {
        if (value.Length is < 1 or > 63 || !Dns1123LabelRegex.IsMatch(value))
            throw new InvalidOperationException(
                $"{name} must be a DNS-1123 label (lowercase alphanumeric or '-', 1-63 chars).");
    }

    private void ReserveSessionSlot(string runId, string sessionId)
    {
        var maxPerRun = GetConfiguredLimit("Sandbox:PortForward:MaxConcurrentSessionsPerRun", "Sandbox:PortForward:MaxPerRun", 3);
        var globalMax = GetConfiguredLimit("Sandbox:PortForward:MaxConcurrentSessionsGlobal", "Sandbox:PortForward:MaxGlobal", 20);

        lock (_sessionGate)
        {
            var runSessions = _sessionsByRun.GetOrAdd(runId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            var runCount = runSessions.Count;
            var globalCount = _sessionsByRun.Values.Sum(s => s.Count);

            if (runCount >= maxPerRun)
                throw new PortForwardLimitExceededException(
                    $"Port-forward session limit exceeded for run {runId}. Limit: {maxPerRun}.");

            if (globalCount >= globalMax)
                throw new PortForwardLimitExceededException(
                    $"Global port-forward session limit exceeded. Limit: {globalMax}.");

            runSessions[sessionId] = 0;
        }
    }

    private int GetConfiguredLimit(string primaryKey, string fallbackKey, int defaultValue)
    {
        var value = _config.GetValue<int?>(primaryKey)
            ?? _config.GetValue<int?>(fallbackKey)
            ?? defaultValue;
        return Math.Max(1, value);
    }

    private void ReleaseSessionSlot(string runId, string sessionId)
    {
        lock (_sessionGate)
        {
            if (!_sessionsByRun.TryGetValue(runId, out var runSessions))
                return;

            runSessions.TryRemove(sessionId, out _);
            if (runSessions.IsEmpty)
                _sessionsByRun.TryRemove(runId, out _);
        }
    }

    private static void CaptureKubectlLine(
        string? line,
        ConcurrentQueue<string> outputLines,
        TaskCompletionSource<int> localPortTcs)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        outputLines.Enqueue(line);
        var match = ForwardingPortRegex.Match(line);
        if (match.Success &&
            int.TryParse(match.Groups["port"].Value, out var port) &&
            port is > 0 and <= 65535)
        {
            localPortTcs.TrySetResult(port);
        }
    }

    private static async Task<int> WaitForForwardedPortAsync(
        Process process,
        TaskCompletionSource<int> localPortTcs,
        ConcurrentQueue<string> outputLines,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeoutTask = Task.Delay(KubectlReadyTimeout, timeoutCts.Token);
        var exitTask = process.WaitForExitAsync(timeoutCts.Token);

        var completed = await Task.WhenAny(localPortTcs.Task, exitTask, timeoutTask).ConfigureAwait(false);
        if (completed == localPortTcs.Task)
        {
            await timeoutCts.CancelAsync().ConfigureAwait(false);
            return await localPortTcs.Task.ConfigureAwait(false);
        }

        if (ct.IsCancellationRequested)
            ct.ThrowIfCancellationRequested();

        if (completed == exitTask)
            throw new InvalidOperationException(
                $"kubectl exited before reporting a local port. Output: {FormatKubectlOutput(outputLines)}");

        throw new InvalidOperationException(
            $"kubectl did not report a local port within {KubectlReadyTimeout.TotalSeconds:n0}s.");
    }

    private static async Task<bool> WaitForTcpReadyAsync(int localPort, Process process, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.Add(KubectlReadyTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
                return false;

            try
            {
                using var client = new TcpClient();
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromMilliseconds(250));
                await client.ConnectAsync(IPAddress.Loopback, localPort, attemptCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { }

            await Task.Delay(TcpProbeInterval, ct).ConfigureAwait(false);
        }

        return false;
    }

    private void CleanupFailedStart(Process process, string runId, string sessionId)
    {
        ReleaseSessionSlot(runId, sessionId);
        _sessions.TryRemove(sessionId, out _);

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PortForwardService: could not kill failed session {Session}", sessionId);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string FormatKubectlOutput(ConcurrentQueue<string> outputLines)
    {
        var lines = outputLines.ToArray();
        return lines.Length == 0
            ? "(none)"
            : string.Join(" | ", lines.TakeLast(8));
    }

    private void KillProcess(Process process, string sessionId)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _logger.LogInformation("PortForwardService: killed session {Session}", sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PortForwardService: could not kill session {Session}", sessionId);
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var (sessionId, active) in _sessions)
        {
            _sessions.TryRemove(sessionId, out _);
            ReleaseSessionSlot(active.Meta.RunId, sessionId);
            KillProcess(active.Process, sessionId);
        }
    }
}
