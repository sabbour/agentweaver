namespace Scaffolder.Domain;

public interface IProjectStore
{
    Task InsertAsync(Project project, CancellationToken ct = default);
    Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default);
    Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default);
    Task UpdateNameAsync(ProjectId id, string name, DateTimeOffset updatedAt, CancellationToken ct = default);
    Task UpdateProviderSettingsAsync(ProjectId id, ProjectProviderSettings settings, DateTimeOffset updatedAt, CancellationToken ct = default);
    Task UpdateWorkingDirectoryAsync(ProjectId id, string workingDirectory, string defaultBranch, DateTimeOffset updatedAt, CancellationToken ct = default);
    /// <summary>
    /// Atomically transitions state Active -> Deleting.
    /// Returns true if the CAS succeeded (the project was Active and is now Deleting).
    /// Returns false if the project was already Deleting or does not exist.
    /// </summary>
    Task<bool> TryBeginDeleteAsync(ProjectId id, CancellationToken ct = default);
    Task DeleteAsync(ProjectId id, CancellationToken ct = default);
}
