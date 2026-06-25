using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Agentweaver.Api.Sandbox;

/// <summary>Represents an active port-forward session for a sandbox pod.</summary>
public sealed record PortForwardSession(
    string SessionId,
    string RunId,
    string PodName,
    int LocalPort,
    int TargetPort,
    DateTimeOffset StartedAt);

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
    private sealed record ActiveSession(PortForwardSession Meta, Process Process);

    private readonly IPodNameRegistry _podRegistry;
    private readonly IConfiguration _config;
    private readonly ILogger<PortForwardService> _logger;
    private readonly ConcurrentDictionary<string, ActiveSession> _sessions = new(StringComparer.Ordinal);

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
        var podName = _podRegistry.TryGet(runId);
        if (string.IsNullOrEmpty(podName))
            throw new InvalidOperationException(
                $"No sandbox pod registered for run {runId}. " +
                "The run must be in_progress with an active Kubernetes sandbox to use port-forward.");

        return StartForPodAsync(runId, podName, targetPort, ct);
    }

    private Task<PortForwardSession> StartForPodAsync(
        string runId, string podName, int targetPort, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var ns = _config["Sandbox:Kubernetes:Namespace"] ?? "agentweaver";
        var localPort = FindFreePort();
        var sessionId = Guid.NewGuid().ToString("N")[..12];

        var kubectlPath = _config["Sandbox:KubectlPath"] ?? "kubectl";
        var args = $"port-forward pod/{podName} {localPort}:{targetPort} -n {ns}";

        _logger.LogInformation(
            "PortForwardService: starting session {Session} for run {RunId}: {Kubectl} {Args}",
            sessionId, runId, kubectlPath, args);

        var psi = new ProcessStartInfo(kubectlPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        Process process;
        try { process = Process.Start(psi)!; }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to start kubectl port-forward. Ensure kubectl is installed and on PATH " +
                $"(or configure Sandbox:KubectlPath). Error: {ex.Message}", ex);
        }

        var session = new PortForwardSession(sessionId, runId, podName, localPort, targetPort, DateTimeOffset.UtcNow);
        var active   = new ActiveSession(session, process);
        _sessions[sessionId] = active;

        // Clean up automatically when the process exits.
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            _sessions.TryRemove(sessionId, out _);
            _logger.LogInformation(
                "PortForwardService: session {Session} process exited (run {RunId})", sessionId, runId);
        };

        return Task.FromResult(session);
    }

    /// <summary>
    /// Terminates the port-forward session identified by <paramref name="sessionId"/>.
    /// Returns <see langword="true"/> when found and stopped; <see langword="false"/> when not found.
    /// </summary>
    public bool Stop(string runId, string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var active))
            return false;

        if (active.Meta.RunId != runId)
        {
            // Run ID mismatch — put it back and deny.
            _sessions[sessionId] = active;
            return false;
        }

        KillProcess(active.Process, sessionId);
        return true;
    }

    /// <summary>Returns a snapshot of all active sessions for the given run.</summary>
    public IReadOnlyList<PortForwardSession> ListForRun(string runId) =>
        _sessions.Values
            .Where(s => string.Equals(s.Meta.RunId, runId, StringComparison.Ordinal))
            .Select(s => s.Meta)
            .ToList();

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
            KillProcess(active.Process, sessionId);
        }
    }
}
