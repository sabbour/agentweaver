using Agentweaver.SandboxExec.PodExec;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// Starts a real <see cref="PodExecServer"/> over a temporary Unix domain socket so tests exercise
/// the same NDJSON protocol, token check, and bubblewrap boundary the executor sidecar uses in the
/// pod. Nothing here is a stub: only the container boundary (which the pod spec provides) is absent,
/// which is exactly why the server's own probe reports a shared PID namespace in-process.
/// </summary>
internal sealed class PodExecTestHarness : IAsyncDisposable
{
    private readonly PodExecServer _server;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private PodExecTestHarness(PodExecServer server, string socketPath)
    {
        _server = server;
        SocketPath = socketPath;
        _loop = server.RunAsync(_cts.Token);
    }

    public string SocketPath { get; }

    public static PodExecTestHarness StartServer(string root)
    {
        // AF_UNIX paths are capped at ~108 bytes, so keep the directory short and outside the
        // per-test workspace tree.
        var socketDirectory = Path.Combine(
            Path.GetTempPath(),
            $"awx-{Guid.NewGuid().ToString("n")[..8]}");
        Directory.CreateDirectory(socketDirectory);
        var socketPath = Path.Combine(socketDirectory, PodExecEndpoint.SocketFileName);
        var server = new PodExecServer(socketPath);
        server.Start();
        return new PodExecTestHarness(server, socketPath);
    }

    /// <summary>
    /// A client wired to the AgentHost apphost as its relay binary. The AgentHost executable is
    /// copied into the test output by the project reference, and <c>--exec-relay</c> is one of its
    /// three entrypoints, so the relay path under test is the production one.
    /// </summary>
    public static PodExecSandboxClient CreateClient(string socketPath) =>
        new(
            socketPath,
            logger: null,
            relayCommand: Path.Combine(
                AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "Agentweaver.AgentHost.exe" : "Agentweaver.AgentHost"),
            relayAssembly: string.Empty);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Shutdown races are not interesting to the assertions.
        }
        await _server.DisposeAsync();
        _cts.Dispose();
        try
        {
            var directory = Path.GetDirectoryName(SocketPath);
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }
}
