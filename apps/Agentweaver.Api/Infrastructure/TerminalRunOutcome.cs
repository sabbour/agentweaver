using System.Text.Json;
using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// The immutable, event-shaped winner for one terminal lifecycle generation.
/// It is written in the run store transaction before its separately stored timeline projection.
/// </summary>
public sealed record TerminalRunOutcome(
    RunStatus Status,
    string EventType,
    JsonElement Payload,
    DateTimeOffset OccurredAt,
    int ExpectedLifecycleGeneration)
{
    public static TerminalRunOutcome Create(
        RunStatus status,
        string eventType,
        object payload,
        DateTimeOffset occurredAt,
        int expectedLifecycleGeneration) =>
        new(status, eventType, JsonSerializer.SerializeToElement(payload), occurredAt, expectedLifecycleGeneration);

    public RunEvent ToRunEvent(int sequence = 0) =>
        new(sequence, EventType, Payload, OccurredAt);

    /// <summary>
    /// Compatibility bridge for legacy status-only terminal writers. New writers must pass their
    /// richer event payload explicitly; this preserves a typed taxonomy while they are migrated.
    /// </summary>
    public static TerminalRunOutcome FromLegacyStatus(
        RunStatus status,
        string? result,
        DateTimeOffset occurredAt,
        int expectedLifecycleGeneration)
    {
        var eventType = status switch
        {
            RunStatus.Merged => EventTypes.MergeCompleted,
            RunStatus.MergeFailed => EventTypes.MergeFailed,
            RunStatus.Declined => EventTypes.ReviewDeclined,
            RunStatus.AssembleReady => EventTypes.RunAssembleReady,
            RunStatus.Failed => EventTypes.RunFailed,
            _ => EventTypes.RunCompleted,
        };
        object payload = status switch
        {
            RunStatus.Merged => new { result },
            RunStatus.MergeFailed => new { reason = result },
            RunStatus.Declined => new { reason = result },
            RunStatus.AssembleReady => new { result },
            RunStatus.Failed => new { reason = result },
            _ => new { result },
        };
        return Create(status, eventType, payload, occurredAt, expectedLifecycleGeneration);
    }
}

/// <summary>Stored run-database outbox row. Projection is deliberately post-commit.</summary>
public sealed record PendingTerminalRunOutcome(
    RunId RunId,
    int LifecycleGeneration,
    TerminalRunOutcome Outcome);

/// <summary>
/// Extra run-row data coupled to a typed terminal winner. A store applies this command in the
/// same transaction as status/outbox persistence, never as a follow-up update.
/// </summary>
public sealed record TerminalRunMutation(
    TerminalRunOutcome Outcome,
    string? Result,
    IReadOnlySet<RunStatus>? ExpectedStatuses = null,
    string? Reviewer = null,
    string? MergeConflicts = null,
    string? MergedCommitHash = null,
    string? TreeHash = null,
    string? WorktreeBranch = null,
    string? Diff = null);
