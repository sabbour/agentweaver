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

    public Task<bool> TryPrepareRunAsync(
        ProjectId expectedProjectId, BacklogTaskId backlogTaskId, string runId, CancellationToken ct = default) =>
        Task.FromResult(true);
}
