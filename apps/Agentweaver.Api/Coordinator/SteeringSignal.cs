using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// UNIFIED AUTONOMOUS STEERING (rev8, §2). The single internal message that EVERY correction-feedback
/// source normalizes into — human review, the RAI gate, the rubber-duck gate, build-test, another
/// agent, the coordinator itself, or another workflow step. All sources call
/// <see cref="CoordinatorSteeringService.SubmitSteeringAsync"/> with one of these, and the coordinator
/// (the single decision-maker) then consciously chooses how to direct the subtasks. It is a thin
/// superset of the persisted <see cref="Agentweaver.Api.Memory.SteeringDirective"/> row: we add
/// <see cref="Source"/>, <see cref="Severity"/>, <see cref="TargetScope"/>, and <see cref="TreeHash"/>
/// so the coordinator has enough context to decide.
/// </summary>
/// <param name="CoordinatorRunId">The run whose coordinator decides (always present).</param>
/// <param name="Source">The source that emitted the signal (see <see cref="SteeringSource"/>).</param>
/// <param name="TargetScope">Which subtask(s) / work-plan / run the feedback addresses.</param>
/// <param name="Feedback">Free-text feedback — advisory reasoning context, NEVER parsed for routing.</param>
/// <param name="Severity">advisory | request-changes | blocking (see <see cref="SteeringSeverity"/>).</param>
/// <param name="Verb">Maps onto <see cref="SteeringKind"/> {send, redirect, amend, stop, dispatch-fresh} for delivery.</param>
/// <param name="TreeHash">Aggregate tree hash the feedback was produced against (staleness guard). Optional.</param>
/// <param name="TargetFiles">Optional explicit file hint from a source that actually knows (build-test). Never inferred from prose.</param>
/// <param name="CreatedBy">Actor id (user login, agent id, or "gate:&lt;kind&gt;").</param>
/// <param name="Timestamp">When the signal was produced.</param>
public sealed record SteeringSignal(
    string CoordinatorRunId,
    string Source,
    SteeringTargetScope TargetScope,
    string Feedback,
    string Severity,
    string Verb,
    string? TreeHash,
    IReadOnlyList<string>? TargetFiles,
    string CreatedBy,
    DateTimeOffset Timestamp)
{
    /// <summary>
    /// Convenience factory that normalizes a gate/human/agent feedback into a canonical signal with a
    /// UTC timestamp. Callers only supply the fields their source actually knows.
    /// </summary>
    public static SteeringSignal Create(
        string coordinatorRunId,
        string source,
        SteeringTargetScope targetScope,
        string feedback,
        string severity,
        string verb,
        string createdBy,
        string? treeHash = null,
        IReadOnlyList<string>? targetFiles = null) =>
        new(coordinatorRunId, source, targetScope, feedback ?? string.Empty, severity, verb,
            treeHash, targetFiles, createdBy, DateTimeOffset.UtcNow);
}

/// <summary>
/// The scope a <see cref="SteeringSignal"/> addresses. Persisted as <c>TargetScopeJson</c> on the
/// directive so the coordinator can recover the exact target after a claim/crash.
/// </summary>
/// <param name="Kind">subtask | work-plan | run.</param>
/// <param name="SubtaskIds">Target subtask ids when <see cref="Kind"/> is <c>subtask</c>.</param>
/// <param name="ChildRunId">Target child run id (in-context steer target) when known.</param>
public sealed record SteeringTargetScope(
    string Kind,
    IReadOnlyList<int>? SubtaskIds = null,
    string? ChildRunId = null)
{
    public static SteeringTargetScope Run() => new(SteeringScopeKind.Run);
    public static SteeringTargetScope WorkPlan() => new(SteeringScopeKind.WorkPlan);
    public static SteeringTargetScope ForSubtasks(params int[] ids) => new(SteeringScopeKind.Subtask, ids);
    public static SteeringTargetScope ForChild(string childRunId, params int[] ids) =>
        new(SteeringScopeKind.Subtask, ids.Length == 0 ? null : ids, childRunId);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static SteeringTargetScope? FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<SteeringTargetScope>(json, JsonOpts);
}

/// <summary>Canonical <see cref="SteeringTargetScope.Kind"/> values.</summary>
public static class SteeringScopeKind
{
    public const string Subtask = "subtask";
    public const string WorkPlan = "work-plan";
    public const string Run = "run";
}

/// <summary>
/// Canonical <see cref="SteeringSignal.Source"/> values (rev8 §2). <c>Agent</c> (agent-to-agent) is
/// future-ready: the enum value + a synthetic-emit unit test ship now; the live trigger site is a
/// documented TODO (§11.8/W5).
/// </summary>
public static class SteeringSource
{
    public const string HumanReview = "human-review";
    public const string Rai = "rai";
    public const string Rubberduck = "rubberduck";
    public const string BuildTest = "build-test";
    public const string Agent = "agent";
    public const string Coordinator = "coordinator";
    public const string Step = "step";

    private static readonly HashSet<string> Known =
        [HumanReview, Rai, Rubberduck, BuildTest, Agent, Coordinator, Step];

    public static bool IsKnown(string? source) => source is not null && Known.Contains(source);
}

/// <summary>Canonical <see cref="SteeringSignal.Severity"/> values (rev8 §2).</summary>
public static class SteeringSeverity
{
    public const string Advisory = "advisory";
    public const string RequestChanges = "request-changes";
    public const string Blocking = "blocking";

    public static bool IsKnown(string? severity) =>
        severity is Advisory or RequestChanges or Blocking;
}

/// <summary>
/// The four conscious directions the coordinator chooses among (rev8 §4). Persisted as
/// <see cref="Agentweaver.Api.Memory.SteeringDirective.DecidedAction"/>.
/// </summary>
public static class SteeringDirection
{
    /// <summary>A — in-place steer the existing subtask, preserving its session/context.</summary>
    public const string InPlaceSteer = "in_place_steer";

    /// <summary>B — CONSCIOUS, logged fresh dispatch (reset subtask + new pod). Never automatic.</summary>
    public const string DispatchFresh = "dispatch_fresh";

    /// <summary>C — proceed to human review / terminal.</summary>
    public const string Proceed = "proceed";

    /// <summary>D — advisory no-op (surfaced, not suppressed).</summary>
    public const string Advisory = "advisory";
}
