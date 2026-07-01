using System.Text.Json;
using Agentweaver.Api.Contracts;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

public sealed class DurableShellApprovalStore(DurableRunControlState state) : IShellApprovalStore
{
    private const string ShellApproved = "shell.approved";
    private const string ShellDenied = "shell.denied";
    private const string ShellApprovalConsumed = "shell.approval_consumed";
    private const string ShellApprovalsCleared = "shell.approvals_cleared";

    private readonly DurableRunControlState _state = state;

    public void Approve(string runId, string commandHash) =>
        _state.Append(runId, ShellApproved, new ShellDecision(commandHash));

    public bool IsApproved(string runId, string commandHash)
    {
        var decisions = Decisions(runId);
        if (decisions.Any(e => IsDecision(e, ShellDenied, commandHash)) ||
            decisions.Any(e => IsDecision(e, ShellApprovalConsumed, commandHash)) ||
            !decisions.Any(e => IsDecision(e, ShellApproved, commandHash)))
            return false;

        _state.Append(runId, ShellApprovalConsumed, new ShellDecision(commandHash));
        return true;
    }

    public void Deny(string runId, string commandHash) =>
        _state.Append(runId, ShellDenied, new ShellDecision(commandHash));

    public bool IsDenied(string runId, string commandHash) =>
        Decisions(runId).Any(e => IsDecision(e, ShellDenied, commandHash));

    public void Clear(string runId) =>
        _state.Append(runId, ShellApprovalsCleared, new { });

    private IReadOnlyList<RunEventRecord> Decisions(string runId) =>
        _state.Load(runId, ShellApproved, ShellDenied, ShellApprovalConsumed, ShellApprovalsCleared)
            .TakeLastShellEventsAfterClear()
            .ToList();

    private static bool IsDecision(RunEventRecord evt, string eventType, string commandHash) =>
        evt.EventType == eventType &&
        JsonSerializer.Deserialize<ShellDecision>(evt.PayloadJson, JsonDefaults.Options)?.CommandHash == commandHash;

    private sealed record ShellDecision(string CommandHash);
}

file static class DurableShellEventExtensions
{
    public static IEnumerable<RunEventRecord> TakeLastShellEventsAfterClear(this IReadOnlyList<RunEventRecord> events)
    {
        var lastClear = events.LastOrDefault(e => e.EventType == "shell.approvals_cleared");
        return lastClear is null ? events : events.Where(e => e.Sequence > lastClear.Sequence);
    }
}
