namespace Agentweaver.SandboxFs;

/// <summary>
/// Thrown whenever a requested path is rejected by the sandbox. The exception
/// is caught by the tool layer and surfaced as a structured rejection; it is
/// never propagated into the agent loop.
/// </summary>
public sealed class SandboxViolationException : Exception
{
    public string AttemptedPath { get; }
    public string SandboxRoot { get; }

    public SandboxViolationException(string attemptedPath, string sandboxRoot, string reason)
        : base($"Path '{attemptedPath}' rejected: {reason}. Sandbox root: '{sandboxRoot}'")
    {
        AttemptedPath = attemptedPath;
        SandboxRoot = sandboxRoot;
    }
}
