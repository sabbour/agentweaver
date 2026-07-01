using Agentweaver.Domain;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Default workspace provider for developer machines. The working directory is a
/// plain filesystem path: resolve maps to the absolute canonical path for absolute paths,
/// or to a per-project subdirectory under the configured <c>Workspace:Local:RootPath</c>
/// for relative or empty paths. Ensure creates the directory if absent and validates it
/// is writable, IsAvailable is a directory-exists probe, and Release is a no-op.
/// Selected when Workspace:Provider is "local" (default).
/// </summary>
public sealed class LocalFilesystemWorkspaceProvider : IProjectWorkspaceProvider
{
    private readonly string _workspaceBase;

    public LocalFilesystemWorkspaceProvider(IConfiguration configuration)
    {
        var configured = configuration["Workspace:Local:RootPath"];
        _workspaceBase = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".agentweaver", "workspaces")
            : configured;
    }

    public string BackendName => "local-filesystem";

    /// <inheritdoc />
    public bool AutoAssignsPath => false;

    public Task<string> ResolveWorkingDirectoryAsync(
        ProjectId id, string requestedPath, CancellationToken ct = default)
    {
        // Absolute path supplied → use it as-is (developer pointing at an existing repo clone).
        if (!string.IsNullOrWhiteSpace(requestedPath) && Path.IsPathRooted(requestedPath))
            return Task.FromResult(Path.GetFullPath(requestedPath));

        // Relative or empty path → resolve to a per-project directory under the configured
        // workspace base so Path.GetFullPath never silently anchors to the application CWD.
        var projectRoot = Path.Combine(_workspaceBase, id.ToString());
        return Task.FromResult(Path.GetFullPath(projectRoot));
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
