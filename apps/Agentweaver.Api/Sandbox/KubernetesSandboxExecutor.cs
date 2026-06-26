using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using k8s;
using Agentweaver.SandboxExec;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Configures the Kubernetes SandboxClaim backend.
/// Bound from the <c>Sandbox:Kubernetes</c> configuration section.
/// </summary>
public sealed class KubernetesSandboxOptions
{
    public string Namespace { get; init; } = "agentweaver";
    public string TemplateRef { get; init; } = "agentweaver-sandbox";
    /// <summary>Path where the shared workspace PVC is mounted inside API and sandbox pods.</summary>
    public string WorkspaceMountPath { get; init; } = "/workspace";
    /// <summary>SandboxClaim TTL. Command timeouts are capped below this so controller GC cannot interrupt exec.</summary>
    public int TimeoutSeconds { get; init; } = 600;
    /// <summary>Cluster service CIDR that must be excluded by sandbox egress policy.</summary>
    public string? ServiceCidr { get; init; }
    public IReadOnlyList<string> SandboxEgressCidrExclusions { get; init; } = [];
}

/// <summary>
/// Executes sandboxed commands inside a pre-warmed Kubernetes pod obtained via a
/// <c>SandboxClaim</c> CRD.  Lifecycle:
/// <list type="number">
///   <item>Create a <c>SandboxClaim</c> resource (adopts a warm pod from the pool).</item>
///   <item>Poll until the claim transitions to <c>phase: Bound</c> and reports a pod name.</item>
///   <item>Run the command via pod-exec (Kubernetes WebSocket exec API).</item>
///   <item>Delete the claim on completion (controller GC cleans up the pod and service).</item>
/// </list>
/// Automatically selected by the API when <c>KUBERNETES_SERVICE_HOST</c> is present
/// (see <see cref="SandboxExecutorFactory.IsInCluster"/>).
/// </summary>
internal sealed class KubernetesSandboxExecutor : ISandboxExecutor
{
    private const string ApiGroup = "extensions.agents.x-k8s.io";
    private const string ApiVersion = "v1alpha1";
    private const string ClaimPlural = "sandboxclaims";
    private const string ContainerName = "agentweaver-sandbox";

    private readonly IKubernetes _client;
    private readonly KubernetesSandboxOptions _options;
    private readonly ILogger<KubernetesSandboxExecutor> _logger;
    private readonly IPodNameRegistry? _podRegistry;

    public bool IsRealIsolation => true;
    public string BackendName => "kubernetes-sandbox-claim";
    public string SelectionReason =>
        "Kubernetes-native sandbox via SandboxClaim warm pool (Kata VM isolation, NetworkPolicy egress restriction).";
    public bool HasNetworkWarning => false;
    public string? NetworkWarningMessage => null;

    internal KubernetesSandboxExecutor(
        IKubernetes client,
        KubernetesSandboxOptions options,
        ILogger<KubernetesSandboxExecutor> logger,
        IPodNameRegistry? podRegistry = null)
    {
        _client = client;
        _options = options;
        _logger = logger;
        _podRegistry = podRegistry;
    }

    public async Task<SandboxExecResult> ExecuteAsync(
        SandboxCommand command, CancellationToken ct = default)
    {
        // Use the Agentweaver run ID as the claim name when available so the pod can be
        // looked up by run ID later (preview port-forward). Fall back to a random ID.
        string claimBase = string.IsNullOrEmpty(command.AgentweaverRunId)
            ? Guid.NewGuid().ToString("N")[..16]
            : command.AgentweaverRunId.Replace("-", "")[..Math.Min(16, command.AgentweaverRunId.Replace("-", "").Length)];
        var claimName = $"run-{claimBase}";

        var requestedTimeoutMs = command.TimeoutMs > 0
            ? command.TimeoutMs
            : _options.TimeoutSeconds * 1000;
        var maxCommandTimeoutMs = Math.Max(1000, (_options.TimeoutSeconds * 1000) - 30_000);
        var timeoutMs = Math.Min(requestedTimeoutMs, maxCommandTimeoutMs);
        if (timeoutMs < requestedTimeoutMs)
        {
            _logger.LogWarning(
                "KubernetesSandboxExecutor: command timeout clamped from {RequestedMs}ms to {TimeoutMs}ms so it stays below SandboxClaim TTL ({TtlSeconds}s)",
                requestedTimeoutMs, timeoutMs, _options.TimeoutSeconds);
        }

        string podWorkingDirectory;
        try
        {
            podWorkingDirectory = ResolvePodWorkingDirectory(command.WorkingDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "KubernetesSandboxExecutor: invalid workspace path {WorkingDirectory}; configured mount is {WorkspaceMountPath}",
                command.WorkingDirectory, _options.WorkspaceMountPath);
            return new SandboxExecResult(1, "", ex.Message, false, false);
        }

        _logger.LogInformation(
            "KubernetesSandboxExecutor: using workspace path {WorkspacePath} for claim {Claim} (requested {RequestedWorkingDirectory})",
            podWorkingDirectory, claimName, command.WorkingDirectory);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeoutMs);
        var token = linked.Token;
        var claimCreated = false;

        try
        {
            _logger.LogInformation(
                "KubernetesSandboxExecutor: creating SandboxClaim {Claim}", claimName);
            await CreateClaimAsync(claimName, token);
            claimCreated = true;

            var podName = await WaitForBoundAsync(claimName, token);
            _logger.LogInformation(
                "KubernetesSandboxExecutor: claim {Claim} bound to pod {Pod}", claimName, podName);

            // Register pod name so PortForwardService can locate it by Agentweaver run ID.
            // Run-scoped mappings are cleared by run lifecycle cleanup, not per command, so
            // preview tunnels can remain available for the whole run while the claim TTL is valid.
            if (!string.IsNullOrEmpty(command.AgentweaverRunId))
                _podRegistry?.Register(command.AgentweaverRunId, podName);

            return await ExecInPodAsync(podName, command, podWorkingDirectory, token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "KubernetesSandboxExecutor: timed out waiting for claim {Claim}", claimName);
            return new SandboxExecResult(-1, "", "Timed out waiting for sandbox pod.", true, false);
        }
        finally
        {
            if (claimCreated && string.IsNullOrEmpty(command.AgentweaverRunId))
                await DeleteClaimAsync(claimName);
            else if (claimCreated)
                _logger.LogDebug(
                    "KubernetesSandboxExecutor: retaining SandboxClaim {Claim} for run {RunId} preview until run cleanup or TTL",
                    claimName, command.AgentweaverRunId);
        }
    }

    public async IAsyncEnumerable<SandboxOutputChunk> StreamAsync(
        SandboxCommand command,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var result = await ExecuteAsync(command, ct);
        foreach (var line in result.Stdout.Split('\n'))
            yield return new SandboxOutputChunk(SandboxOutputStream.Stdout, line);
        if (!string.IsNullOrEmpty(result.Stderr))
            foreach (var line in result.Stderr.Split('\n'))
                yield return new SandboxOutputChunk(SandboxOutputStream.Stderr, line);
        yield return new SandboxOutputChunk(SandboxOutputStream.ExitCode, result.ExitCode.ToString());
    }

    // ── Claim management ──────────────────────────────────────────────────────────

    private Task CreateClaimAsync(string claimName, CancellationToken ct)
    {
        // The cluster service CIDR must be present in SandboxEgressCidrExclusions so
        // sandbox NetworkPolicy does not accidentally allow in-cluster service egress.
        var manifest = new
        {
            apiVersion = $"{ApiGroup}/{ApiVersion}",
            kind = "SandboxClaim",
            metadata = new { name = claimName, @namespace = _options.Namespace },
            spec = new
            {
                templateRef = _options.TemplateRef,
                ttl = $"{_options.TimeoutSeconds}s",
            },
        };

        return _client.CustomObjects.CreateNamespacedCustomObjectAsync(
            manifest, ApiGroup, ApiVersion, _options.Namespace, ClaimPlural,
            cancellationToken: ct);
    }

    /// <summary>
    /// Polls every 2 s until <c>status.phase == "Bound"</c>; returns the pod name
    /// from <c>status.sandbox.name</c> (preferred) or <c>status.podName</c>.
    /// </summary>
    private async Task<string> WaitForBoundAsync(string claimName, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var raw = await _client.CustomObjects.GetNamespacedCustomObjectAsync(
                ApiGroup, ApiVersion, _options.Namespace, ClaimPlural, claimName,
                cancellationToken: ct);

            var json = JsonSerializer.Serialize(raw);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var status))
            {
                var phase = status.TryGetProperty("phase", out var p) ? p.GetString() : null;
                if (phase == "Bound")
                {
                    string? podName = null;

                    // status.sandbox.name is the primary field (agent-sandbox controller shape)
                    if (status.TryGetProperty("sandbox", out var sandbox) &&
                        sandbox.TryGetProperty("name", out var sn))
                        podName = sn.GetString();

                    if (string.IsNullOrEmpty(podName) &&
                        status.TryGetProperty("podName", out var pn))
                        podName = pn.GetString();

                    if (!string.IsNullOrEmpty(podName))
                        return podName;
                }
            }

            await Task.Delay(2000, ct);
        }
    }

    private async Task DeleteClaimAsync(string claimName)
    {
        try
        {
            await _client.CustomObjects.DeleteNamespacedCustomObjectAsync(
                ApiGroup, ApiVersion, _options.Namespace, ClaimPlural, claimName);
            _logger.LogInformation(
                "KubernetesSandboxExecutor: deleted claim {Claim}", claimName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "KubernetesSandboxExecutor: could not delete claim {Claim} (best-effort)", claimName);
        }
    }

    // ── Command execution ─────────────────────────────────────────────────────────

    private async Task<SandboxExecResult> ExecInPodAsync(
        string podName, SandboxCommand command, string podWorkingDirectory, CancellationToken ct)
    {
        const int maxOutputBytes = 4 * 1024 * 1024;

        var shellScript = BuildShellScript(command, podWorkingDirectory);

        var ws = await _client.WebSocketNamespacedPodExecAsync(
            podName, _options.Namespace,
            new[] { "/bin/sh", "-c", shellScript },
            container: ContainerName,
            stdin: false, stdout: true, stderr: true, tty: false,
            cancellationToken: ct);

        using var demux = new StreamDemuxer(ws, StreamType.RemoteCommand);
        demux.Start();

        using var stdoutStream = demux.GetStream(ChannelIndex.StdOut, null);
        using var stderrStream = demux.GetStream(ChannelIndex.StdErr, null);
        // Channel 3 (Error) carries the terminal v1.Status payload with the real exit code.
        using var statusStream = demux.GetStream(ChannelIndex.Error, null);

        var stdoutTask = ReadBoundedAsync(stdoutStream, maxOutputBytes, ct);
        var stderrTask = ReadBoundedAsync(stderrStream, maxOutputBytes, ct);
        var statusTask = ReadBoundedAsync(statusStream, maxOutputBytes, ct);

        await Task.WhenAll(stdoutTask, stderrTask, statusTask);

        var (stdoutBytes, stdoutTruncated) = await stdoutTask;
        var (stderrBytes, stderrTruncated) = await stderrTask;
        var (statusBytes, _) = await statusTask;

        var stdout = SandboxOutputRedactor.Default.Redact(Encoding.UTF8.GetString(stdoutBytes));
        var stderr = SandboxOutputRedactor.Default.Redact(Encoding.UTF8.GetString(stderrBytes));
        var exitCode = ParseExitCode(Encoding.UTF8.GetString(statusBytes));

        return new SandboxExecResult(
            exitCode, stdout, stderr, false, stdoutTruncated || stderrTruncated);
    }

    /// <summary>
    /// Reads up to <paramref name="maxBytes"/> from a stream, stopping at the cap.
    /// Returns the bytes collected and whether the output was truncated.
    /// </summary>
    private static async Task<(byte[] Bytes, bool Truncated)> ReadBoundedAsync(
        Stream stream, int maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        bool truncated = false;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            int remaining = maxBytes - (int)buffer.Length;
            if (remaining <= 0) { truncated = true; break; }
            int take = Math.Min(read, remaining);
            buffer.Write(chunk, 0, take);
            if (take < read) { truncated = true; break; }
        }
        return (buffer.ToArray(), truncated);
    }

    /// <summary>
    /// Parses the terminal v1.Status JSON emitted on channel 3.
    /// <c>status: "Success"</c> → exit 0. <c>status: "Failure"</c> → the ExitCode
    /// cause from <c>details.causes</c> (defaulting to 1 if not present).
    /// </summary>
    private static int ParseExitCode(string statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson))
            return 0;

        try
        {
            using var doc = JsonDocument.Parse(statusJson);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase))
                return 0;

            if (root.TryGetProperty("details", out var details) &&
                details.TryGetProperty("causes", out var causes) &&
                causes.ValueKind == JsonValueKind.Array)
            {
                foreach (var cause in causes.EnumerateArray())
                {
                    var reason = cause.TryGetProperty("reason", out var r) ? r.GetString() : null;
                    if (string.Equals(reason, "ExitCode", StringComparison.OrdinalIgnoreCase) &&
                        cause.TryGetProperty("message", out var m) &&
                        int.TryParse(m.GetString(), out var code))
                        return code;
                }
            }

            // Failure status with no parseable ExitCode cause → non-zero.
            return 1;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private string ResolvePodWorkingDirectory(string requestedWorkingDirectory)
    {
        var mountPath = NormalizeUnixPath(_options.WorkspaceMountPath, forceAbsolute: true);
        if (string.IsNullOrWhiteSpace(requestedWorkingDirectory))
            return mountPath;

        var requested = NormalizeUnixPath(requestedWorkingDirectory, forceAbsolute: false);
        if (IsSameOrChildPath(requested, mountPath))
            return requested;

        throw new InvalidOperationException(
            $"Kubernetes sandbox working directory '{requestedWorkingDirectory}' is not under mounted workspace '{mountPath}'. " +
            "Configure Workspace:PersistentVolume:MountRoot/Workspace:Path to match the workspace PVC mount used by sandbox pods.");
    }

    private static bool IsSameOrChildPath(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal)
        || (root == "/" && path.StartsWith("/", StringComparison.Ordinal))
        || path.StartsWith(root + "/", StringComparison.Ordinal);

    private static string NormalizeUnixPath(string path, bool forceAbsolute)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        if (forceAbsolute && !normalized.StartsWith("/", StringComparison.Ordinal))
            normalized = "/" + normalized;
        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }

    private static string BuildShellScript(SandboxCommand command, string podWorkingDirectory)
    {
        var sb = new StringBuilder();

        if (command.Environment is { Count: > 0 })
        {
            foreach (var (key, value) in command.Environment)
                sb.AppendLine($"export {key}={ShellSingleQuote(value)}");
        }

        sb.AppendLine($"cd {ShellSingleQuote(podWorkingDirectory)}");

        sb.Append(command.CommandLine);
        return sb.ToString();
    }

    private static string ShellSingleQuote(string s) =>
        "'" + s.Replace("'", "'\\''") + "'";
}
