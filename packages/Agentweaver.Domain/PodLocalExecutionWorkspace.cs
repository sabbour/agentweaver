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

    /// <summary>
    /// Runs the MCP-driven operator assistant chat loop (narrow AgentHost cutover). The pod hosts
    /// <c>OperatorAssistantAgent</c> instead of the sandboxed <c>CopilotAIAgent</c> workflow turn:
    /// tools are sourced exclusively from the AgentweaverMCP server, native/shell tools are
    /// rejected, and consequential tool calls are gated by the pod's own <c>IToolApprovalGate</c>.
    /// No workspace/repository checkout is prepared for this purpose.
    /// </summary>
    OperatorAssistant = 3,
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
    public const string AgentScratchDirectoryName = "agent-scratch";
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

    /// <summary>
    /// Gets the per-run non-deliverable working directory. This is deliberately a sibling of
    /// execution workspaces rather than a child, so it can never be included in a worktree write-back.
    /// </summary>
    public static string GetAgentScratchPath(string scratchRoot, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        return Path.GetFullPath(Path.Combine(
            scratchRoot,
            AgentScratchDirectoryName,
            GetRunHash(runId)));
    }

    public static bool IsGitObjectId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && GitObjectIdRegex().IsMatch(value);

    [GeneratedRegex("^[0-9a-fA-F]{40,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex GitObjectIdRegex();
}
