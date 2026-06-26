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
    /// When true, the provider ignores the caller-supplied path and auto-generates the
    /// working directory from the project id (e.g. PersistentVolumeWorkspaceProvider on AKS).
    /// When false, the provider honours the caller-supplied path (e.g. local dev).
    /// Clients can use this to hide the "Repository folder" UI field when the path is irrelevant.
    /// </summary>
    bool AutoAssignsPath { get; }

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
    /// Probes the workspace mount root itself (not a per-project subdirectory). Returns true
    /// when the mount root exists and is writable. Used by the /healthz/workspace readiness
    /// endpoint so Kubernetes can drop unmounted pods from the Service before they serve traffic.
    /// LocalFilesystem implementations always return true (local dev has no mounted volume).
    /// </summary>
    bool IsMountRootHealthy() => true;

    /// <summary>
    /// Releases any runtime resources held for the workspace (e.g. unmounting a
    /// persistent volume in cloud). No-op for the local filesystem provider.
    /// </summary>
    Task ReleaseAsync(ProjectId id, string workingDirectory, CancellationToken ct = default);
}
