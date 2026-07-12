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
}

/// <summary>Shared path contract for the assembly Build/Test pod-local checkout.</summary>
public static partial class AssemblyBuildTestExecution
{
    public const string DefaultScratchRoot = "/local-workspace";

    public static string GetRunHash(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId)))[..16]
            .ToLowerInvariant();
    }

    public static string GetCheckoutPath(string scratchRoot, string runId, string treeHash)
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
