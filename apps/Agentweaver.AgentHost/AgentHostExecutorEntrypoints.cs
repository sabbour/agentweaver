using Agentweaver.SandboxExec.PodExec;
using Microsoft.Extensions.Logging;

namespace Agentweaver.AgentHost;

/// <summary>
/// Alternate entrypoints hosted by the AgentHost image so the executor sidecar runs the exact same
/// toolchain, patch level, and sandbox code as the AgentHost it serves.
/// </summary>
internal static class AgentHostExecutorEntrypoints
{
    public const string ExecAgentArgument = "--exec-agent";

    /// <summary>
    /// Runs the executor sidecar daemon until the container is stopped. Startup is fail-closed: if
    /// the bubblewrap mount namespace cannot be created, the process exits non-zero and the pod
    /// never becomes ready, instead of serving commands without a boundary.
    /// </summary>
    public static async Task<int> RunExecutorSidecarAsync(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(logging =>
        {
            logging.AddSimpleConsole(options => options.TimestampFormat = "HH:mm:ss ");
            logging.SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger("Agentweaver.ExecutorSidecar");

        var socketPath = ResolveSocketArgument(args, ExecAgentArgument);
        await using var server = new PodExecServer(socketPath, logger);
        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Executor sidecar refused to start; the sandbox boundary is unavailable.");
            return 1;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

        try
        {
            await server.RunAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        logger.LogInformation("Executor sidecar stopped.");
        return 0;
    }

    /// <summary>Reads the optional socket path that follows a mode argument.</summary>
    public static string? ResolveSocketArgument(string[] args, string modeArgument)
    {
        var index = Array.IndexOf(args, modeArgument);
        if (index < 0 || index + 1 >= args.Length)
            return null;
        var candidate = args[index + 1];
        return candidate.StartsWith("--", StringComparison.Ordinal) ? null : candidate;
    }
}
