using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Agentweaver.Domain;

/// <summary>Explicit purpose assigned to a run-scoped AgentHost configuration.</summary>
public enum AgentHostPurpose
{
    /// <summary>Preserves the existing shared-worktree AgentHost behavior.</summary>
    Default = 0,

    /// <summary>Runs the assembly Build/Test gate and preview from a pod-local checkout.</summary>
    AssemblyBuildTest = 1,

    /// <summary>
    /// Reserved for implementation-agent turns that will use a writable pod-local checkout.
    /// The write-back/finalization path is implemented by issue #253, not by the assembly flow.
    /// </summary>
    ImplementationTurn = 2,
}

/// <summary>Policy controlling where an AgentHost executes and whether local changes may be finalized.</summary>
public enum ExecutionWorkspaceMode
{
    /// <summary>Use the existing shared workspace path without materializing a local checkout.</summary>
    Shared = 0,

    /// <summary>Use a verified pod-local checkout that cannot be prepared for write-back.</summary>
    LocalReadOnly = 1,

    /// <summary>Use a verified pod-local checkout whose changes may later be finalized.</summary>
    LocalWritable = 2,
}

/// <summary>Shared deterministic path contract for pod-local execution workspaces.</summary>
public static partial class PodLocalExecutionWorkspace
{
    public const string DefaultScratchRoot = "/local-workspace";
    public const string WritebackRefPrefix = "refs/agentweaver/writeback/";

    public static string GetRunHash(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId)))[..16]
            .ToLowerInvariant();
    }

    public static string GetWorkspacePath(string scratchRoot, string runId, string treeHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        if (!IsGitObjectId(treeHash))
            throw new ArgumentException("Tree hash must be a 40-64 character hexadecimal git object id.", nameof(treeHash));

        return Path.GetFullPath(Path.Combine(
            scratchRoot,
            GetRunHash(runId),
            treeHash.ToLowerInvariant()));
    }

    public static bool IsGitObjectId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && GitObjectIdRegex().IsMatch(value);

    [GeneratedRegex("^[0-9a-fA-F]{40,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex GitObjectIdRegex();
}
