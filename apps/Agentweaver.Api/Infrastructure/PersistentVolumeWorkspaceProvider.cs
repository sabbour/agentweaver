using Microsoft.Extensions.Configuration;
using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Cloud workspace provider. The working directory is a per-project path under a
/// configured persistent-volume mount root. Physical volume allocation/attachment is
/// environment-supplied (outside this seam); this provider validates that the mount is
/// present and writable, maps paths deterministically, and releases the handle on delete.
/// Selected when Workspace:Provider is "persistent-volume".
/// </summary>
public sealed class PersistentVolumeWorkspaceProvider : IProjectWorkspaceProvider
{
    private readonly string _mountRoot;

    public PersistentVolumeWorkspaceProvider(IConfiguration configuration)
    {
        _mountRoot = configuration["Workspace:PersistentVolume:MountRoot"]
            ?? throw new InvalidOperationException(
                "Workspace:PersistentVolume:MountRoot must be configured when using the persistent-volume workspace provider.");
    }

    public string BackendName => "persistent-volume";

    public Task<string> ResolveWorkingDirectoryAsync(
        ProjectId id, string requestedPath, CancellationToken ct = default)
    {
        // Cloud: the per-project directory is deterministically under the mount root.
        // requestedPath is ignored; identity comes from the project id.
        var resolved = Path.Combine(_mountRoot, id.ToString());
        return Task.FromResult(Path.GetFullPath(resolved));
    }

    public Task<WorkspaceHandle> EnsureWorkspaceAsync(
        ProjectId id, string workingDirectory, CancellationToken ct = default)
    {
        // Create per-project directory if it doesn't exist (mount root must be present).
        // NOTE: We intentionally skip Directory.Exists(_mountRoot) here. On Linux, .NET uses
        // statx(2) for Directory.Exists checks; some kernel/CIFS combinations return ENOENT for
        // statx() on the CIFS mount root even though stat(2) and mkdir(2) work correctly.
        // Attempting CreateDirectory directly is the reliable probe: if the mount root truly
        // isn't present, mkdir will fail with ENOENT and the catch block below surfaces that.
        if (!Directory.Exists(workingDirectory))
        {
            try
            {
                Directory.CreateDirectory(workingDirectory);
            }
            catch (Exception ex)
            {
                throw new WorkspaceUnavailableException(
                    $"Persistent volume for project '{id}' is not mounted at '{workingDirectory}'. " +
                    $"Ensure the volume is provisioned, attached, and writable. " +
                    $"Detail: {ex.GetType().Name}: {ex.Message}", ex);
            }
        }

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
                $"Persistent volume at '{workingDirectory}' is not writable.", ex);
        }
        return Task.FromResult(new WorkspaceHandle(workingDirectory, BackendName));
    }

    public bool IsAvailable(string workingDirectory)
    {
        // NOTE: Do not use Directory.Exists() here. On CIFS mounts with actimeo caching, .NET's
        // statx(2)-based existence check can return ENOENT on reachable directories, producing a
        // false negative that makes healthy projects appear unavailable. A write-probe is the
        // reliable check: if the directory is accessible and writable the probe succeeds; if it
        // is genuinely missing or the mount is gone the write fails (ENOENT → false).
        var probe = Path.Combine(workingDirectory, $".agentweaver-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Probes the mount root itself (Workspace:PersistentVolume:MountRoot). Returns true when
    /// a temp file can be created and deleted at the mount root. Used by the
    /// /healthz/workspace readiness endpoint so Kubernetes drops unmounted pods from the Service.
    ///
    /// IMPORTANT: Uses a write-probe only — no Directory.Exists() / statx(2). On Azure Files
    /// CIFS mounts with actimeo caching, statx(2) returns ENOENT on the mount root even when
    /// mkdir(2) and file writes work correctly. A false healthy=false here would permanently
    /// exclude the pod from the Service. Healthy = write+delete succeeds; unhealthy = it throws.
    /// </summary>
    public bool IsMountRootHealthy()
    {
        var probe = Path.Combine(_mountRoot, $".agentweaver-mount-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task ReleaseAsync(ProjectId id, string workingDirectory, CancellationToken ct = default)
    {
        // Flush/detach the volume handle. Physical volume and user content are preserved.
        // On cloud platforms this would signal the infrastructure layer; here we record the release.
        return Task.CompletedTask;
    }
}
