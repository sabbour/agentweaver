using Agentweaver.Api.Auth;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

/// <summary>Test-only seam for workflow parsing/mechanism tests that do not exercise authorization.</summary>
public sealed class AlwaysAvailableAutomationInvocationService : IAutomationInvocationService
{
    public Task<AutomationInvocationClaim?> TryClaimForProjectAsync(
        ProjectId projectId, string occurrenceKey, string? deliveryId, string? eventName, CancellationToken ct = default) =>
        Task.FromResult<AutomationInvocationClaim?>(new(SnapshotRef.Create().Value));

    public Task<bool> TryBindBacklogTaskAsync(
        string invocationId, ProjectId projectId, BacklogTaskId backlogTaskId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<AutomationInvocationTaskReservation?> TryReserveBacklogTaskAsync(
        string invocationId, ProjectId projectId, CancellationToken ct = default) =>
        Task.FromResult<AutomationInvocationTaskReservation?>(new(BacklogTaskId.New(), IsBound: false));

    public Task<bool> TryAdoptLegacyProvisionalTaskAsync(
        string invocationId, ProjectId projectId, BacklogTaskId backlogTaskId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<IReadOnlyList<OutstandingScheduleInvocation>> ListOutstandingScheduleInvocationsAsync(
        ProjectId projectId, string occurrenceKeyPrefix, IReadOnlyCollection<string> legacyProvisionalOccurrenceKeys,
        int maximumCount, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OutstandingScheduleInvocation>>([]);

    public Task<bool> TryCompleteBacklogTaskReservationAsync(
        string invocationId, ProjectId projectId, BacklogTaskId backlogTaskId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<bool> TryDiscardInvocationForTaskAsync(
        string invocationId, ProjectId projectId, BacklogTaskId backlogTaskId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<bool> TryPrepareRunAsync(
        ProjectId expectedProjectId, BacklogTaskId backlogTaskId, string runId, CancellationToken ct = default) =>
        Task.FromResult(true);
}

/// <summary>
/// Test seam that attempts the real coordinator claim at the precise point after a trigger task has
/// been inserted but before its invocation is bound.
/// </summary>
public sealed class CoordinatorInterleavingAutomationInvocationService(IBacklogTaskStore backlogStore)
    : IAutomationInvocationService
{
    public ClaimReserveResult? ClaimResultDuringBinding { get; private set; }

    public Task<AutomationInvocationClaim?> TryClaimForProjectAsync(
        ProjectId projectId, string occurrenceKey, string? deliveryId, string? eventName, CancellationToken ct = default) =>
        Task.FromResult<AutomationInvocationClaim?>(new(SnapshotRef.Create().Value));

    public async Task<bool> TryBindBacklogTaskAsync(
        string invocationId, ProjectId projectId, BacklogTaskId backlogTaskId, CancellationToken ct = default)
    {
        ClaimResultDuringBinding = await backlogStore.TryClaimAndReserveCoordinatorRunAsync(
            projectId,
            backlogTaskId,
            new Run
            {
                Id = RunId.New(),
                RepositoryPath = "interleaving-test",
                OriginatingBranch = "main",
                ModelSource = ModelSource.GitHubCopilot,
                Task = "must not be claimed before binding",
                SubmittingUser = "test",
                Status = RunStatus.InProgress,
                StartedAt = DateTimeOffset.UtcNow,
            },
            DateTimeOffset.UtcNow,
            ct);
        return true;
    }

    public Task<AutomationInvocationTaskReservation?> TryReserveBacklogTaskAsync(
        string invocationId, ProjectId projectId, CancellationToken ct = default) =>
        Task.FromResult<AutomationInvocationTaskReservation?>(new(BacklogTaskId.New(), IsBound: false));

    public Task<bool> TryAdoptLegacyProvisionalTaskAsync(
        string invocationId, ProjectId projectId, BacklogTaskId backlogTaskId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<IReadOnlyList<OutstandingScheduleInvocation>> ListOutstandingScheduleInvocationsAsync(
        ProjectId projectId, string occurrenceKeyPrefix, IReadOnlyCollection<string> legacyProvisionalOccurrenceKeys,
        int maximumCount, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OutstandingScheduleInvocation>>([]);

    public Task<bool> TryCompleteBacklogTaskReservationAsync(
        string invocationId, ProjectId projectId, BacklogTaskId backlogTaskId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<bool> TryDiscardInvocationForTaskAsync(
        string invocationId, ProjectId projectId, BacklogTaskId backlogTaskId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<bool> TryPrepareRunAsync(
        ProjectId expectedProjectId, BacklogTaskId backlogTaskId, string runId, CancellationToken ct = default) =>
        Task.FromResult(true);
}
