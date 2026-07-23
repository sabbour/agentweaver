using Agentweaver.SandboxFs;

namespace Agentweaver.SandboxExec;

/// <summary>
/// Validates that a shell command and its declared working directory are
/// safe to forward to the sandbox backend. This is a host-side defense-in-depth
/// layer; mxc filesystem policy is the primary enforcement mechanism.
/// </summary>
public static class ShellCommandValidator
{
    private const int MaxCommandLengthBytes = 65536;

    /// <summary>
    /// Validates working directory containment and basic command content safety.
    /// Returns (Allowed: true, Reason: null) on success, or (false, non-null reason) on failure.
    /// </summary>
    /// <param name="allowedRoots">
    /// The run's own allowed roots (working directory + filesystem-policy read/write and
    /// read-only roots). Used by the shared-mount guard (#476) so an absolute path that
    /// reaches into the shared <c>/workspace</c> PVC outside this run's own subtree is
    /// rejected even though the DECLARED working directory is legitimate. When null, only
    /// the sandbox root is treated as allowed.
    /// </param>
    /// <param name="protectedRoots">
    /// Shared-mount roots to protect; when null, resolved from environment/default
    /// (<see cref="SharedWorkspacePathGuard.DefaultProtectedRoots"/>).
    /// </param>
    public static (bool Allowed, string? Reason) Validate(
        string commandLine, string commandWorkingDir, string sandboxRoot,
        IReadOnlyList<string>? allowedRoots = null,
        IReadOnlyList<string>? protectedRoots = null)
    {
        // 1. Working directory must be inside the sandbox root.
        try
        {
            SandboxPathValidator.ValidateAbsoluteContained(commandWorkingDir, sandboxRoot);
        }
        catch (SandboxViolationException ex)
        {
            return (false, $"Working directory escape: {ex.Message}");
        }

        // 2. Command length cap — prevents resource exhaustion.
        if (commandLine.Length > MaxCommandLengthBytes)
            return (false, $"Command exceeds maximum length ({MaxCommandLengthBytes} bytes).");

        // 3. Null-byte rejection — prevents injection through string truncation.
        if (commandLine.Contains('\0'))
            return (false, "Command contains null byte (injection attempt).");

        // 4. Shared-mount escape (#476): reject absolute paths embedded in the command text
        // that reach into a shared cross-run mount (/workspace) outside this run's own roots.
        // The working-directory check above only sees the DECLARED cwd, not paths inside the
        // command (e.g. `git -C /workspace/<other-project>` or `cat /workspace/<other>/x`).
        var roots = allowedRoots is { Count: > 0 } ? allowedRoots : new[] { sandboxRoot };
        var (guardAllowed, guardReason) = SharedWorkspacePathGuard.Inspect(
            commandLine, roots, protectedRoots);
        if (!guardAllowed)
            return (false, guardReason);

        return (true, null);
    }
}
