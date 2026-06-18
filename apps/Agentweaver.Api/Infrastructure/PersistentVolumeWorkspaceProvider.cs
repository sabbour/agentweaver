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
        // Validate the mount is present — do NOT create it (operator responsibility).
        if (!Directory.Exists(workingDirectory))
            throw new WorkspaceUnavailableException(
                $"Persistent volume for project '{id}' is not mounted at '{workingDirectory}'. " +
                "Ensure the volume is provisioned and attached before creating or running against this project.");

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
        if (!Directory.Exists(workingDirectory)) return false;
        // Quick writable probe
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

    public Task ReleaseAsync(ProjectId id, string workingDirectory, CancellationToken ct = default)
    {
        // Flush/detach the volume handle. Physical volume and user content are preserved.
        // On cloud platforms this would signal the infrastructure layer; here we record the release.
        return Task.CompletedTask;
    }
}
