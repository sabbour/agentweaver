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
    public int TimeoutSeconds { get; init; } = 600;
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

    public bool IsRealIsolation => true;
    public string BackendName => "kubernetes-sandbox-claim";
    public string SelectionReason =>
        "Kubernetes-native sandbox via SandboxClaim warm pool (Kata VM isolation, NetworkPolicy egress restriction).";
    public bool HasNetworkWarning => false;
    public string? NetworkWarningMessage => null;

    internal KubernetesSandboxExecutor(
        IKubernetes client,
        KubernetesSandboxOptions options,
        ILogger<KubernetesSandboxExecutor> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public async Task<SandboxExecResult> ExecuteAsync(
        SandboxCommand command, CancellationToken ct = default)
    {
        var runId = Guid.NewGuid().ToString("N")[..16];
        var claimName = $"run-{runId}";

        var timeoutMs = command.TimeoutMs > 0
            ? command.TimeoutMs
            : _options.TimeoutSeconds * 1000;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeoutMs);
        var token = linked.Token;

        try
        {
            _logger.LogInformation(
                "KubernetesSandboxExecutor: creating SandboxClaim {Claim}", claimName);
            await CreateClaimAsync(claimName, token);

            var podName = await WaitForBoundAsync(claimName, token);
            _logger.LogInformation(
                "KubernetesSandboxExecutor: claim {Claim} bound to pod {Pod}", claimName, podName);

            return await ExecInPodAsync(podName, command, token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "KubernetesSandboxExecutor: timed out waiting for claim {Claim}", claimName);
            return new SandboxExecResult(-1, "", "Timed out waiting for sandbox pod.", true, false);
        }
        finally
        {
            await DeleteClaimAsync(claimName);
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
        string podName, SandboxCommand command, CancellationToken ct)
    {
        const int maxOutputBytes = 4 * 1024 * 1024;

        var shellScript = BuildShellScript(command);

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

    private static string BuildShellScript(SandboxCommand command)
    {
        var sb = new StringBuilder();

        if (command.Environment is { Count: > 0 })
        {
            foreach (var (key, value) in command.Environment)
                sb.AppendLine($"export {key}={ShellSingleQuote(value)}");
        }

        if (!string.IsNullOrWhiteSpace(command.WorkingDirectory))
            sb.AppendLine($"cd {ShellSingleQuote(command.WorkingDirectory)}");

        sb.Append(command.CommandLine);
        return sb.ToString();
    }

    private static string ShellSingleQuote(string s) =>
        "'" + s.Replace("'", "'\\''") + "'";
}
