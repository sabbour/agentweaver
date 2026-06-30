using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Runs;

/// <summary>
/// A coordinator confirm/revise decision deferred by a secondary replica that failed to restore
/// the MAF checkpoint (MAF SDK <c>$type</c>-ordering bug). The primary replica polls this table
/// from <c>CoordinatorRunService.PollDeferredDecisionsAsync</c> and applies the decision to its
/// live in-process <c>StreamingRun</c>, so the user sees a fast 200 OK on first click regardless
/// of which replica the HTTP request lands on.
/// </summary>
public sealed class CoordinatorDeferredDecisionRecord
{
    [Key] public int Id { get; set; }

    /// <summary>The coordinator run this decision is for. Unique — at most one deferred decision per run.</summary>
    public required string RunId { get; set; }

    /// <summary>Serialized <c>CoordinatorOutcomeSpecDecision</c>.</summary>
    public required string DecisionJson { get; set; }

    /// <summary>When this decision was deferred.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
