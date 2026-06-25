namespace Agentweaver.Domain;

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
    /// <summary>Updates the per-project backlog pickup settings (FR-008a + unattended seeding).</summary>
    Task UpdatePickupSettingsAsync(
        ProjectId id, int maxReadyPerHeartbeat, bool autopilot, bool autoApproveTools, DateTimeOffset updatedAt, CancellationToken ct = default);

    /// <summary>
    /// Sets (or clears, when <paramref name="workflowId"/> is null) the project's default workflow
    /// reference (Feature 010, FR-041).
    /// </summary>
    Task UpdateDefaultWorkflowAsync(ProjectId id, string? workflowId, DateTimeOffset updatedAt, CancellationToken ct = default);

    /// <summary>
    /// Sets (or clears, when <paramref name="policyName"/> is null) the project's active review-policy
    /// name (Feature 010, FR-027/033).
    /// </summary>
    Task UpdateActiveReviewPolicyAsync(ProjectId id, string? policyName, DateTimeOffset updatedAt, CancellationToken ct = default);

    /// <summary>
    /// Sets (or clears, when <paramref name="sandboxProfile"/> is null) the project's sandbox profile,
    /// applied when a blueprint is selected at creation (Feature 012).
    /// </summary>
    Task UpdateSandboxProfileAsync(ProjectId id, string? sandboxProfile, DateTimeOffset updatedAt, CancellationToken ct = default);

    /// <summary>
    /// Sets the blueprint provenance on a project (Feature 012).
    /// </summary>
    Task UpdateSourceBlueprintAsync(ProjectId id, string? blueprintId, string? blueprintType, DateTimeOffset updatedAt, CancellationToken ct = default);

    /// <summary>
    /// Sets (or clears, when <paramref name="allowedWorkflowIds"/> is null/empty) the project's allowed
    /// workflow set, declared by the applied blueprint's <c>workflows</c> set (Feature 015 US3). Null or
    /// empty means "all catalog workflows allowed" (backward compatible).
    /// </summary>
    Task UpdateAllowedWorkflowIdsAsync(ProjectId id, IReadOnlyList<string>? allowedWorkflowIds, DateTimeOffset updatedAt, CancellationToken ct = default);
}
