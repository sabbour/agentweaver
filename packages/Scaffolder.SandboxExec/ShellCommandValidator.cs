using Scaffolder.SandboxFs;

namespace Scaffolder.SandboxExec;

/// <summary>
/// Validates that a shell command and its declared working directory are
/// safe to forward to the sandbox backend. This is a host-side defense-in-depth
/// layer; mxc filesystem policy is the primary enforcement mechanism.
/// </summary>
internal static class ShellCommandValidator
{
    private const int MaxCommandLengthBytes = 65536;

    /// <summary>
    /// Validates working directory containment and basic command content safety.
    /// Returns (Allowed: true, Reason: null) on success, or (false, non-null reason) on failure.
    /// </summary>
    public static (bool Allowed, string? Reason) Validate(
        string commandLine, string commandWorkingDir, string sandboxRoot)
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

        return (true, null);
    }
}
