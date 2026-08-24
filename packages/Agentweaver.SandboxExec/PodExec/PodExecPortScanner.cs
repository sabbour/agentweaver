using System.Globalization;

namespace Agentweaver.SandboxExec.PodExec;

/// <summary>
/// Resolves the TCP ports a sandboxed process group is listening on, from inside the executor
/// sidecar container.
///
/// <para>The AgentHost used to walk <c>/proc/&lt;pid&gt;/fd</c> for the preview process itself. With
/// the process boundary moved into the executor sidecar, those PIDs no longer exist in the
/// AgentHost's PID namespace, so the scan runs here — where the sandboxed process group is visible —
/// and only its result (a port list) crosses the boundary.</para>
///
/// <para><b>Network-namespace invariant (#849 review):</b> this reads <c>/proc/net/tcp*</c> from THIS
/// process's own network namespace, not the workload's. That is only ever the workload's namespace
/// too when the workload was started with <c>networkEnabled=true</c> — <see cref="KataBwrapExecutor"/>
/// omits <c>--unshare-net</c> in that case, so the sandboxed process shares this namespace, matching
/// the pod's single, gateway-reachable network namespace. A workload started with
/// <c>networkEnabled=false</c> gets its own, unshared namespace, and this scan can never observe its
/// sockets no matter how long it waits — <see cref="PodExecServer"/> must (and does) reject a port
/// query for such a session with an explicit error instead of calling this method, rather than let it
/// return a silently-empty, indistinguishable-from-"not listening yet" list. Preview sessions
/// (<c>PreviewRunner.StartPreviewProcessAsync</c>) always pass <c>networkEnabled: true</c>, which is
/// what makes this scan valid for the <c>observe_bound_port</c>/<c>health_check</c> path.</para>
/// </summary>
internal static class PodExecPortScanner
{
    private static readonly string[] ProcNetTcpFiles = ["/proc/net/tcp", "/proc/net/tcp6"];

    /// <summary>Listening ports owned by <paramref name="processGroupId"/> and its descendants.</summary>
    public static IReadOnlyList<int> ListeningPortsForProcessGroup(int processGroupId)
    {
        if (processGroupId <= 0 || !OperatingSystem.IsLinux())
            return [];

        var socketInodes = CollectProcessGroupSocketInodes(processGroupId);
        if (socketInodes.Count == 0)
            return [];

        var ports = new SortedSet<int>();
        foreach (var file in ProcNetTcpFiles)
        {
            string contents;
            try
            {
                contents = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var (inode, port) in ParseListeningSocketPorts(contents))
            {
                if (socketInodes.Contains(inode))
                    ports.Add(port);
            }
        }

        return [.. ports];
    }

    /// <summary>Parses listening (state 0A) sockets from a <c>/proc/net/tcp[6]</c> table.</summary>
    internal static IEnumerable<(ulong Inode, int Port)> ParseListeningSocketPorts(string procNetContents)
    {
        var results = new List<(ulong, int)>();
        var lines = procNetContents.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Skip(1))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10)
                continue;
            if (!string.Equals(fields[3], "0A", StringComparison.OrdinalIgnoreCase))
                continue;

            var localAddress = fields[1];
            var separator = localAddress.LastIndexOf(':');
            if (separator < 0
                || !int.TryParse(
                    localAddress.AsSpan(separator + 1),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var port)
                || port <= 0)
            {
                continue;
            }

            if (ulong.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out var inode)
                && inode > 0)
            {
                results.Add((inode, port));
            }
        }
        return results;
    }

    private static HashSet<ulong> CollectProcessGroupSocketInodes(int processGroupId)
    {
        var inodes = new HashSet<ulong>();
        foreach (var pid in EnumerateProcessGroupMembers(processGroupId))
        {
            IEnumerable<string> descriptors;
            try
            {
                descriptors = Directory.EnumerateFiles($"/proc/{pid}/fd");
            }
            catch (Exception)
            {
                // The process exited, or its /proc entry is not readable; skip it.
                continue;
            }

            foreach (var descriptor in descriptors)
            {
                try
                {
                    var target = new FileInfo(descriptor).LinkTarget;
                    if (TryParseSocketInode(target, out var inode))
                        inodes.Add(inode);
                }
                catch (Exception)
                {
                    // Racing teardown; ignore this descriptor.
                }
            }
        }
        return inodes;
    }

    private static IEnumerable<int> EnumerateProcessGroupMembers(int processGroupId)
    {
        IEnumerable<string> processDirectories;
        try
        {
            processDirectories = Directory.EnumerateDirectories("/proc");
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var directory in processDirectories)
        {
            var name = Path.GetFileName(directory);
            if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                continue;
            if (TryReadProcessGroupId(pid, out var pgid) && pgid == processGroupId)
                yield return pid;
        }
    }

    private static bool TryReadProcessGroupId(int pid, out int processGroupId)
    {
        processGroupId = 0;
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var commandEnd = stat.LastIndexOf(')');
            if (commandEnd < 0 || commandEnd + 2 >= stat.Length)
                return false;
            var fields = stat[(commandEnd + 1)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 2
                && int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out processGroupId);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool TryParseSocketInode(string? linkTarget, out ulong inode)
    {
        inode = 0;
        const string prefix = "socket:[";
        if (linkTarget is null
            || !linkTarget.StartsWith(prefix, StringComparison.Ordinal)
            || !linkTarget.EndsWith(']'))
        {
            return false;
        }

        return ulong.TryParse(
            linkTarget.AsSpan(prefix.Length, linkTarget.Length - prefix.Length - 1),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out inode);
    }
}
