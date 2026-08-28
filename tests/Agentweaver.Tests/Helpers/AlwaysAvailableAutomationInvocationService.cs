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

    public Task<IReadOnlyList<OutstandingScheduleInvocation>> ListOutstandingScheduleInvocationsAsync(
        ProjectId projectId, string occurrenceKeyPrefix, int maximumCount, CancellationToken ct = default) =>
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

    public Task<IReadOnlyList<OutstandingScheduleInvocation>> ListOutstandingScheduleInvocationsAsync(
        ProjectId projectId, string occurrenceKeyPrefix, int maximumCount, CancellationToken ct = default) =>
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

/// <summary>Deterministic durable-state seam for trigger recovery boundary tests.</summary>
public sealed class RecoverableAutomationInvocationService : IAutomationInvocationService
{
    private sealed class InvocationState(string occurrenceKey, bool isBound)
    {
        public AutomationInvocationClaim Claim { get; } = new(SnapshotRef.Create().Value);
        public BacklogTaskId TaskId { get; } = BacklogTaskId.New();
        public string OccurrenceKey { get; } = occurrenceKey;
        public bool IsBound { get; set; } = isBound;
        public bool IsComplete { get; set; }
    }

    private readonly List<InvocationState> _invocations = [];

    public bool ThrowOnNextBind { get; set; }
    public CancellationTokenSource? CancelOnNextReservation { get; set; }
    public int BindAttempts { get; private set; }
    public BacklogTaskId ReservedTaskId => _invocations.Single().TaskId;

    public BacklogTaskId ReserveOutstandingScheduleInvocation(string occurrenceKey, bool isBound = false)
    {
        var state = new InvocationState(occurrenceKey, isBound);
        _invocations.Add(state);
        return state.TaskId;
    }

    public Task<AutomationInvocationClaim?> TryClaimForProjectAsync(
        ProjectId projectId, string occurrenceKey, string? deliveryId, string? eventName, CancellationToken ct = default)
    {
        var state = _invocations.SingleOrDefault(x => x.OccurrenceKey == occurrenceKey)
                    ?? new InvocationState(occurrenceKey, isBound: false);
        if (!_invocations.Contains(state))
            _invocations.Add(state);
        return Task.FromResult<AutomationInvocationClaim?>(state.Claim);
    }

    public Task<AutomationInvocationTaskReservation?> TryReserveBacklogTaskAsync(
        string invocationId, ProjectId projectId, CancellationToken ct = default)
    {
        CancelOnNextReservation?.Cancel();
        CancelOnNextReservation = null;
        var state = _invocations.SingleOrDefault(x => x.Claim.InvocationId == invocationId);
        return Task.FromResult<AutomationInvocationTaskReservation?>(
            state is null ? null : new(state.TaskId, state.IsBound));
    }

    public Task<bool> TryBindBacklogTaskAsync(
        string invocationId, ProjectId projectId, BacklogTaskId backlogTaskId, CancellationToken ct = default)
    {
        BindAttempts++;
        if (ThrowOnNextBind)
        {
            ThrowOnNextBind = false;
            throw new InvalidOperationException("injected bind interruption");
        }

        var state = _invocations.SingleOrDefault(x => x.Claim.InvocationId == invocationId && x.TaskId == backlogTaskId);
        if (state is not null)
            state.IsBound = true;
        return Task.FromResult(state is not null);
    }

    public Task<IReadOnlyList<OutstandingScheduleInvocation>> ListOutstandingScheduleInvocationsAsync(
        ProjectId projectId, string occurrenceKeyPrefix, int maximumCount, CancellationToken ct = default)
    {
        var pending = _invocations
            .Where(x => !x.IsComplete && x.OccurrenceKey.StartsWith(occurrenceKeyPrefix, StringComparison.Ordinal))
            .Take(maximumCount + 1)
            .Select(x => new OutstandingScheduleInvocation(x.Claim.InvocationId, x.OccurrenceKey))
            .ToList();
        if (pending.Count > maximumCount)
            throw new InvalidOperationException("injected outstanding schedule invocation limit");
        return Task.FromResult<IReadOnlyList<OutstandingScheduleInvocation>>(pending);
    }

    public Task<bool> TryCompleteBacklogTaskReservationAsync(
        string invocationId, ProjectId projectId, BacklogTaskId backlogTaskId, CancellationToken ct = default)
    {
        var state = _invocations.SingleOrDefault(x => x.Claim.InvocationId == invocationId && x.TaskId == backlogTaskId);
        if (state is not null)
            state.IsComplete = true;
        return Task.FromResult(state is not null);
    }

    public Task<bool> TryDiscardInvocationForTaskAsync(
        string invocationId, ProjectId projectId, BacklogTaskId backlogTaskId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<bool> TryPrepareRunAsync(
        ProjectId expectedProjectId, BacklogTaskId backlogTaskId, string runId, CancellationToken ct = default) =>
        Task.FromResult(true);
}
