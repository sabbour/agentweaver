namespace Agentweaver.Domain;

/// <summary>
/// Value type returned by EnsureWorkspaceAsync once the workspace is ready.
/// </summary>
public sealed record WorkspaceHandle(string WorkingDirectory, string BackendName);

public interface IProjectWorkspaceProvider
{
    /// <summary>
    /// Short name identifying the backend implementation (e.g. "local-filesystem",
    /// "persistent-volume"). Used for diagnostics and audit records.
    /// </summary>
    string BackendName { get; }

    /// <summary>
    /// Resolves the canonical absolute working-directory path for the project.
    /// Does not create or validate the directory; call EnsureWorkspaceAsync for that.
    /// </summary>
    Task<string> ResolveWorkingDirectoryAsync(ProjectId id, string requestedPath, CancellationToken ct = default);

    /// <summary>
    /// Ensures the workspace directory is present, accessible, and writable.
    /// Returns a WorkspaceHandle on success.
    /// Throws WorkspaceUnavailableException if the workspace cannot be made available
    /// (e.g. a persistent volume mount is missing in a cloud deployment).
    /// </summary>
    Task<WorkspaceHandle> EnsureWorkspaceAsync(ProjectId id, string workingDirectory, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the working directory exists and is accessible.
    /// </summary>
    bool IsAvailable(string workingDirectory);

    /// <summary>
    /// Releases any runtime resources held for the workspace (e.g. unmounting a
    /// persistent volume in cloud). No-op for the local filesystem provider.
    /// </summary>
    Task ReleaseAsync(ProjectId id, string workingDirectory, CancellationToken ct = default);
}
