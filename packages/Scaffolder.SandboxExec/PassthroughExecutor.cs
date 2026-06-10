using System.Runtime.CompilerServices;

namespace Scaffolder.SandboxExec;

/// <summary>
/// Deny-by-default fallback executor used when no real isolation backend is available.
/// Never spawns a process. All execution attempts are immediately denied with a diagnostic reason.
/// </summary>
internal sealed class PassthroughExecutor : ISandboxExecutor
{
    public bool IsRealIsolation => false;
    public string BackendName => "passthrough-deny";
    public string SelectionReason { get; }

    internal PassthroughExecutor(string reason)
    {
        SelectionReason = reason;
    }

    public Task<SandboxExecResult> ExecuteAsync(SandboxCommand command, CancellationToken ct = default)
    {
        _ = command;
        _ = ct;
        return Task.FromResult(new SandboxExecResult(
            ExitCode: -1,
            Stdout: "",
            Stderr: "Shell execution denied: no real isolation available. " + SelectionReason,
            TimedOut: false,
            OutputTruncated: false));
    }

    public async IAsyncEnumerable<SandboxOutputChunk> StreamAsync(
        SandboxCommand command,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _ = command;
        await Task.CompletedTask;
        yield return new SandboxOutputChunk(
            SandboxOutputStream.ExitCode,
            "Shell execution denied: no real isolation available. " + SelectionReason);
    }
}
