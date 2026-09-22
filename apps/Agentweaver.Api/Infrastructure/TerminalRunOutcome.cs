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
    public static bool IsTerminal(RunStatus status) =>
        Endpoints.EndpointHelpers.IsTerminal(status) || status == RunStatus.AssembleReady;

    public static TerminalRunOutcome Create(
        RunStatus status,
        string eventType,
        object payload,
        DateTimeOffset occurredAt,
        int expectedLifecycleGeneration) =>
        new(status, eventType, JsonSerializer.SerializeToElement(payload), occurredAt, expectedLifecycleGeneration);

    public RunEvent ToRunEvent(int sequence = 0) =>
        new(sequence, EventType, Payload, OccurredAt);

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
