namespace Agentweaver.SandboxExec;

/// <summary>
/// Abstraction over sandboxed command execution. Implementations are selected
/// by the platform probe at construction time.
/// </summary>
public interface ISandboxExecutor
{
    /// <summary>True when this executor provides real process isolation.</summary>
    bool IsRealIsolation { get; }

    /// <summary>Human-readable backend name (e.g. "processcontainer", "wsl-lxc", "direct").</summary>
    string BackendName { get; }

    /// <summary>Reason string from the platform probe or selection logic.</summary>
    string SelectionReason { get; }

    /// <summary>
    /// True when this executor is running with unrestricted network access and
    /// cannot enforce a network allowlist (Windows gap — F5).
    /// Callers should emit a sandbox.warning event when this is true.
    /// </summary>
    bool HasNetworkWarning { get; }

    /// <summary>Warning message when HasNetworkWarning is true. Null otherwise.</summary>
    string? NetworkWarningMessage { get; }

    /// <summary>Buffered one-shot execution.</summary>
    Task<SandboxExecResult> ExecuteAsync(SandboxCommand command, CancellationToken ct = default);

    /// <summary>Streaming execution yielding ordered output chunks and a terminal exit-code chunk.</summary>
    IAsyncEnumerable<SandboxOutputChunk> StreamAsync(SandboxCommand command, CancellationToken ct = default);
}

/// <summary>A command to execute inside the sandbox.</summary>
public sealed record SandboxCommand(
    string CommandLine,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment,
    SandboxFsPolicy FilesystemPolicy,
    int TimeoutMs,
    bool NetworkEnabled = false);

/// <summary>
/// Filesystem policy handed to the sandbox engine. DeniedPaths maps to
/// FilesystemPolicy.HiddenPaths in the Mxc SDK (see MxcSandboxExecutor).
/// </summary>
public sealed record SandboxFsPolicy(
    IReadOnlyList<string> ReadWritePaths,
    IReadOnlyList<string> ReadOnlyPaths,
    IReadOnlyList<string> DeniedPaths);

/// <summary>Terminal execution result.</summary>
public sealed record SandboxExecResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    bool OutputTruncated);

/// <summary>A streamed output chunk (stdout line, stderr line, or terminal exit-code).</summary>
public sealed record SandboxOutputChunk(SandboxOutputStream Stream, string Data);

public enum SandboxOutputStream { Stdout, Stderr, ExitCode }
