using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Default workspace provider for developer machines. The working directory is a
/// plain filesystem path: resolve maps to the absolute canonical path, ensure creates
/// the directory if absent and validates it is writable, IsAvailable is a directory-exists
/// probe, and Release is a no-op. Selected when Workspace:Provider is "local" (default).
/// </summary>
public sealed class LocalFilesystemWorkspaceProvider : IProjectWorkspaceProvider
{
    public string BackendName => "local-filesystem";

    public Task<string> ResolveWorkingDirectoryAsync(
        ProjectId id, string requestedPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            throw new ArgumentException("Working directory path must not be empty.", nameof(requestedPath));
        return Task.FromResult(Path.GetFullPath(requestedPath));
    }

    public Task<WorkspaceHandle> EnsureWorkspaceAsync(
        ProjectId id, string workingDirectory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(workingDirectory);
        // Writable probe
        var probe = Path.Combine(workingDirectory, $".agentweaver-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new WorkspaceUnavailableException(
                $"Working directory '{workingDirectory}' is not writable.", ex);
        }
        return Task.FromResult(new WorkspaceHandle(workingDirectory, BackendName));
    }

    public bool IsAvailable(string workingDirectory) =>
        Directory.Exists(workingDirectory);

    // Local filesystem has no volume mount; always report healthy.
    public bool IsMountRootHealthy() => true;

    public Task ReleaseAsync(ProjectId id, string workingDirectory, CancellationToken ct = default) =>
        Task.CompletedTask;
}
