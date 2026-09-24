using System.Text.Json.Serialization;
using Agentweaver.Api.Contracts;

namespace Agentweaver.Api.Workflows;

/// <summary>A workflow in a list response: identity and validation status (FR-002/039/040).</summary>
public sealed record WorkflowSummaryDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("valid")] public required bool Valid { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("warnings")] public IReadOnlyList<string> Warnings { get; init; } = [];
    [JsonPropertyName("is_built_in")] public required bool IsBuiltIn { get; init; }
    [JsonPropertyName("is_default")] public required bool IsDefault { get; init; }
    /// <summary>Legacy first-trigger alias retained for API compatibility.</summary>
    [JsonPropertyName("trigger")] public WorkflowTriggerDto? Trigger { get; init; }
    /// <summary>All automation triggers in declaration order.</summary>
    [JsonPropertyName("triggers")] public IReadOnlyList<WorkflowTriggerDto> Triggers { get; init; } = [];
}

/// <summary>A workflow's automation trigger in an API response (issue #53).</summary>
public sealed record WorkflowTriggerDto
{
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("interval")] public string? Interval { get; init; }
    [JsonPropertyName("day_of_week")] public string? DayOfWeek { get; init; }
    [JsonPropertyName("day_of_month")] public int? DayOfMonth { get; init; }
    [JsonPropertyName("time_of_day")] public string? TimeOfDay { get; init; }
    [JsonPropertyName("event_name")] public string? EventName { get; init; }
    [JsonPropertyName("if")] public IReadOnlyList<WorkflowTriggerPredicateDto>? If { get; init; }
}

public sealed record WorkflowTriggerPredicateDto
{
    [JsonPropertyName("hasLabel")] public WorkflowTriggerLabelPredicateDto? HasLabel { get; init; }
    [JsonPropertyName("isNotLabeledWith")] public WorkflowTriggerLabelPredicateDto? IsNotLabeledWith { get; init; }
    [JsonPropertyName("baseBranch")] public WorkflowTriggerBaseBranchPredicateDto? BaseBranch { get; init; }
    [JsonPropertyName("reviewState")] public WorkflowTriggerReviewStatePredicateDto? ReviewState { get; init; }
    [JsonPropertyName("ref")] public WorkflowTriggerRefPredicateDto? Ref { get; init; }
    [JsonPropertyName("category")] public WorkflowTriggerCategoryPredicateDto? Category { get; init; }
    [JsonPropertyName("commentMatches")] public WorkflowTriggerCommentMatchesPredicateDto? CommentMatches { get; init; }
    [JsonPropertyName("or")] public IReadOnlyList<WorkflowTriggerPredicateDto>? Or { get; init; }
    [JsonPropertyName("not")] public WorkflowTriggerPredicateDto? Not { get; init; }
}

public sealed record WorkflowTriggerLabelPredicateDto
{
    [JsonPropertyName("label")] public string? Label { get; init; }
}

public sealed record WorkflowTriggerBaseBranchPredicateDto
{
    [JsonPropertyName("branch")] public string? Branch { get; init; }
}

public sealed record WorkflowTriggerReviewStatePredicateDto
{
    [JsonPropertyName("state")] public string? State { get; init; }
}

public sealed record WorkflowTriggerRefPredicateDto
{
    [JsonPropertyName("branch")] public string? Branch { get; init; }
    [JsonPropertyName("matchMode")] public string? MatchMode { get; init; }
}

public sealed record WorkflowTriggerCategoryPredicateDto
{
    [JsonPropertyName("name")] public string? Name { get; init; }
}

public sealed record WorkflowTriggerCommentMatchesPredicateDto
{
    [JsonPropertyName("pattern")] public string? Pattern { get; init; }
}

public sealed record WorkflowTriggerConfigResponse
{
    /// <summary>Legacy first-trigger alias retained for API compatibility.</summary>
    [JsonPropertyName("trigger")] public WorkflowTriggerDto? Trigger { get; init; }
    [JsonPropertyName("triggers")] public IReadOnlyList<WorkflowTriggerDto> Triggers { get; init; } = [];
}

public sealed record WorkflowTriggerPatchRequest
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("interval")] public string? Interval { get; init; }
    [JsonPropertyName("day_of_week")] public string? DayOfWeek { get; init; }
    [JsonPropertyName("day_of_month")] public int? DayOfMonth { get; init; }
    [JsonPropertyName("time_of_day")] public string? TimeOfDay { get; init; }
    [JsonPropertyName("event_name")] public string? EventName { get; init; }
    [JsonPropertyName("if")] public IReadOnlyList<WorkflowTriggerPredicateDto>? If { get; init; }
}

/// <summary>Response body for GET/POST the project's workflows list.</summary>
public sealed record WorkflowListResponse
{
    [JsonPropertyName("default_workflow_id")] public required string DefaultWorkflowId { get; init; }
    [JsonPropertyName("workflows")] public required IReadOnlyList<WorkflowSummaryDto> Workflows { get; init; }
}

/// <summary>Request body to set (or clear) a workflow selection — the project default (FR-041) or a
/// per-task override (FR-042). A null/omitted <c>workflow_id</c> clears the selection.</summary>
public sealed record SetWorkflowSelectionRequest
{
    [JsonPropertyName("workflow_id")] public string? WorkflowId { get; init; }
}

/// <summary>Response body after setting a per-task workflow override (FR-042).</summary>
public sealed record WorkflowOverrideResponse
{
    [JsonPropertyName("task_id")] public required string TaskId { get; init; }
    [JsonPropertyName("workflow_override_id")] public string? WorkflowOverrideId { get; init; }
}

/// <summary>A node in a workflow detail response.</summary>
public sealed record WorkflowNodeDto
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("label")] public required string Label { get; init; }
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("gate_kind")] public string? GateKind { get; init; }
    [JsonPropertyName("agent")] public string? Agent { get; init; }
    [JsonPropertyName("prompt")] public string? Prompt { get; init; }
    [JsonPropertyName("charter")] public string? Charter { get; init; }
    [JsonPropertyName("target")] public string? Target { get; init; }
    [JsonPropertyName("steps")] public IReadOnlyList<string>? Steps { get; init; }
    [JsonPropertyName("branches")] public IReadOnlyList<string>? Branches { get; init; }
}

/// <summary>An edge in a workflow detail response.</summary>
public sealed record WorkflowEdgeDto
{
    [JsonPropertyName("from")] public required string From { get; init; }
    [JsonPropertyName("to")] public required string To { get; init; }
    [JsonPropertyName("when")] public string? When { get; init; }
}

/// <summary>Full definition for GET a single workflow.</summary>
public sealed record WorkflowDetailDto
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("start")] public required string Start { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("is_built_in")] public required bool IsBuiltIn { get; init; }
    [JsonPropertyName("is_default")] public required bool IsDefault { get; init; }
    [JsonPropertyName("warnings")] public IReadOnlyList<string> Warnings { get; init; } = [];
    [JsonPropertyName("nodes")] public required IReadOnlyList<WorkflowNodeDto> Nodes { get; init; }
    [JsonPropertyName("edges")] public required IReadOnlyList<WorkflowEdgeDto> Edges { get; init; }
    /// <summary>Legacy first-trigger alias retained for API compatibility.</summary>
    [JsonPropertyName("trigger")] public WorkflowTriggerDto? Trigger { get; init; }
    [JsonPropertyName("triggers")] public IReadOnlyList<WorkflowTriggerDto> Triggers { get; init; } = [];
}

/// <summary>Request body to save (create or update) a workflow definition by YAML (US7).</summary>
public sealed record SaveWorkflowRequest
{
    [JsonPropertyName("yaml")] public required string Yaml { get; init; }
}

/// <summary>Response body for GET raw YAML content of a project workflow file (US7).</summary>
public sealed record WorkflowYamlResponse
{
    [JsonPropertyName("yaml")] public required string Yaml { get; init; }
}

public sealed record WorkflowGrammarDto
{
    [JsonPropertyName("grammar_version")] public required string GrammarVersion { get; init; }
    [JsonPropertyName("format")] public required string Format { get; init; }
    [JsonPropertyName("root")] public required WorkflowRootGrammarDto Root { get; init; }
    [JsonPropertyName("node_fields")] public required WorkflowNodeFieldsGrammarDto NodeFields { get; init; }
    [JsonPropertyName("node_types")] public required IReadOnlyList<WorkflowNodeTypeGrammarDto> NodeTypes { get; init; }
    [JsonPropertyName("edge")] public required WorkflowEdgeGrammarDto Edge { get; init; }
    [JsonPropertyName("triggers")] public required WorkflowTriggerGrammarDto Triggers { get; init; }
    [JsonPropertyName("compatibility")] public required WorkflowCompatibilityGrammarDto Compatibility { get; init; }
}

public sealed record WorkflowRootGrammarDto
{
    [JsonPropertyName("required_fields")] public required IReadOnlyList<string> RequiredFields { get; init; }
    [JsonPropertyName("optional_fields")] public required IReadOnlyList<string> OptionalFields { get; init; }
    [JsonPropertyName("maximum_document_characters")] public required int MaximumDocumentCharacters { get; init; }
    [JsonPropertyName("maximum_nodes")] public required int MaximumNodes { get; init; }
    [JsonPropertyName("maximum_edges")] public required int MaximumEdges { get; init; }
    [JsonPropertyName("maximum_triggers")] public required int MaximumTriggers { get; init; }
}

public sealed record WorkflowNodeFieldsGrammarDto
{
    [JsonPropertyName("required_fields")] public required IReadOnlyList<string> RequiredFields { get; init; }
    [JsonPropertyName("optional_fields")] public required IReadOnlyList<string> OptionalFields { get; init; }
    [JsonPropertyName("maximum_prompt_characters")] public required int MaximumPromptCharacters { get; init; }
    [JsonPropertyName("maximum_charter_characters")] public required int MaximumCharterCharacters { get; init; }
}

public sealed record WorkflowNodeTypeGrammarDto
{
    [JsonPropertyName("yaml_type")] public required string YamlType { get; init; }
    [JsonPropertyName("api_type")] public required string ApiType { get; init; }
    [JsonPropertyName("label")] public required string Label { get; init; }
    [JsonPropertyName("authorable")] public required bool Authorable { get; init; }
    [JsonPropertyName("runtime_bindable")] public required bool RuntimeBindable { get; init; }
    [JsonPropertyName("runtime_kinds")] public required IReadOnlyList<string> RuntimeKinds { get; init; }
    [JsonPropertyName("required_fields")] public required IReadOnlyList<string> RequiredFields { get; init; }
    [JsonPropertyName("allowed_gate_kinds")] public required IReadOnlyList<string> AllowedGateKinds { get; init; }
}

public sealed record WorkflowEdgeGrammarDto
{
    [JsonPropertyName("required_fields")] public required IReadOnlyList<string> RequiredFields { get; init; }
    [JsonPropertyName("optional_fields")] public required IReadOnlyList<string> OptionalFields { get; init; }
    [JsonPropertyName("conditions_are_case_sensitive")] public required bool ConditionsAreCaseSensitive { get; init; }
    [JsonPropertyName("transitions")] public required IReadOnlyList<WorkflowTransitionGrammarDto> Transitions { get; init; }
}

public sealed record WorkflowTransitionGrammarDto
{
    [JsonPropertyName("from_kind")] public required string FromKind { get; init; }
    [JsonPropertyName("to_kind")] public required string ToKind { get; init; }
    [JsonPropertyName("unconditional")] public required bool Unconditional { get; init; }
    [JsonPropertyName("when")] public required IReadOnlyList<string> When { get; init; }
}

public sealed record WorkflowTriggerGrammarDto
{
    [JsonPropertyName("types")] public required IReadOnlyList<string> Types { get; init; }
    [JsonPropertyName("schedule_intervals")] public required IReadOnlyList<string> ScheduleIntervals { get; init; }
    [JsonPropertyName("review_states")] public required IReadOnlyList<string> ReviewStates { get; init; }
    [JsonPropertyName("ref_match_modes")] public required IReadOnlyList<string> RefMatchModes { get; init; }
    [JsonPropertyName("predicate_types")] public required IReadOnlyList<string> PredicateTypes { get; init; }
}

public sealed record WorkflowCompatibilityGrammarDto
{
    [JsonPropertyName("legacy_loading_only")] public required bool LegacyLoadingOnly { get; init; }
    [JsonPropertyName("check_gate_id_matching")] public required string CheckGateIdMatching { get; init; }
    [JsonPropertyName("check_gate_id_fallbacks")] public required IReadOnlyDictionary<string, string> CheckGateIdFallbacks { get; init; }
}

/// <summary>A node in a workflow graph descriptor (US6). role/node_type match the GraphNode shape
/// consumed by WorkflowGraphPanel on the frontend; kind is always "planned".</summary>
public sealed record WorkflowGraphNodeDto
{
    [JsonPropertyName("id")]        public required string Id       { get; init; }
    [JsonPropertyName("label")]     public required string Label    { get; init; }
    [JsonPropertyName("role")]      public required string Role     { get; init; }
    [JsonPropertyName("kind")]      public required string Kind     { get; init; }
    [JsonPropertyName("node_type")] public string? NodeType { get; init; }
}

/// <summary>An edge in a workflow graph descriptor (US6). cardinality is always "direct";
/// loopback is true when the edge forms a back-edge in topological order (cycle).</summary>
public sealed record WorkflowGraphEdgeDto
{
    [JsonPropertyName("from")]        public required string From        { get; init; }
    [JsonPropertyName("to")]          public required string To          { get; init; }
    [JsonPropertyName("cardinality")] public required string Cardinality { get; init; }
    [JsonPropertyName("loopback")]    public required bool   Loopback    { get; init; }
    [JsonPropertyName("label")]       public string? Label { get; init; }
}

/// <summary>Response body for GET workflow graph (US6). Matches the GraphDescriptor shape
/// consumed by the WorkflowGraphPanel renderer on the frontend.</summary>
public sealed record WorkflowGraphDto
{
    [JsonPropertyName("graph_id")]      public required string GraphId     { get; init; }
    [JsonPropertyName("variant")]       public required string Variant     { get; init; }
    [JsonPropertyName("start_node_id")] public required string StartNodeId { get; init; }
    [JsonPropertyName("nodes")]         public required IReadOnlyList<WorkflowGraphNodeDto> Nodes { get; init; }
    [JsonPropertyName("edges")]         public required IReadOnlyList<WorkflowGraphEdgeDto> Edges { get; init; }
}

/// <summary>Request body to generate a workflow draft from a natural-language description (US10).</summary>
public sealed record GenerateWorkflowRequest
{
    [JsonPropertyName("description")] public required string Description { get; init; }
    /// <summary>Optional saved/built-in workflow id to edit instead of creating from scratch.</summary>
    [JsonPropertyName("base_workflow_id")] public string? BaseWorkflowId { get; init; }
    /// <summary>Optional current draft YAML for iterative edits before the draft is saved.</summary>
    [JsonPropertyName("base_yaml")] public string? BaseYaml { get; init; }
    /// <summary>Explicitly exempts a pure content workflow from mandatory software delivery gates.</summary>
    [JsonPropertyName("content_only")] public bool ContentOnly { get; init; }
}

public sealed record WorkflowGenerationProviderSnapshotDto
{
    [JsonPropertyName("provider_kind")] public required string ProviderKind { get; init; }
    [JsonPropertyName("provider_type")] public string? ProviderType { get; init; }
    [JsonPropertyName("provider_key")] public required string ProviderKey { get; init; }
    [JsonPropertyName("provider_scope")] public required string ProviderScope { get; init; }
    [JsonPropertyName("resolution_scope")] public required string ResolutionScope { get; init; }
    [JsonPropertyName("workflow_model")] public string? WorkflowModel { get; init; }
    [JsonPropertyName("credential_binding_version")] public string? CredentialBindingVersion { get; init; }
}

public sealed record WorkflowGenerationFailureDto
{
    [JsonPropertyName("code")] public required string Code { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("retryable")] public bool Retryable { get; init; }
}

public sealed record WorkflowGenerationArtifactDto
{
    [JsonPropertyName("artifact_id")] public required string ArtifactId { get; init; }
    [JsonPropertyName("workflow_id")] public required string WorkflowId { get; init; }
    [JsonPropertyName("version")] public int Version { get; init; }
}

public sealed record WorkflowGenerationJobResponse
{
    [JsonPropertyName("job_id")] public required string JobId { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("attempt")] public int Attempt { get; init; }
    [JsonPropertyName("project_id")] public required string ProjectId { get; init; }
    [JsonPropertyName("provider_snapshot")] public required WorkflowGenerationProviderSnapshotDto ProviderSnapshot { get; init; }
    [JsonPropertyName("artifact")] public WorkflowGenerationArtifactDto? Artifact { get; init; }
    [JsonPropertyName("failure")] public WorkflowGenerationFailureDto? Failure { get; init; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; init; }
    [JsonPropertyName("status_url")] public required string StatusUrl { get; init; }
    [JsonPropertyName("result_url")] public required string ResultUrl { get; init; }
    [JsonPropertyName("cancel_url")] public required string CancelUrl { get; init; }
    [JsonPropertyName("retry_url")] public required string RetryUrl { get; init; }
    [JsonPropertyName("ai_execution_context")] public AiExecutionContextResponse? AiExecutionContext { get; init; }
}

public sealed record WorkflowGenerationResultResponse
{
    [JsonPropertyName("job_id")] public required string JobId { get; init; }
    [JsonPropertyName("artifact_id")] public required string ArtifactId { get; init; }
    [JsonPropertyName("workflow_id")] public required string WorkflowId { get; init; }
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("yaml")] public required string Yaml { get; init; }
    [JsonPropertyName("was_corrected")] public bool WasCorrected { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("base_workflow_id")] public string? BaseWorkflowId { get; init; }
    [JsonPropertyName("base_workflow_is_built_in")] public bool BaseWorkflowIsBuiltIn { get; init; }
    [JsonPropertyName("graph")] public required WorkflowGraphDto Graph { get; init; }
}

/// <summary>Maps the workflow domain model to API DTOs (server-side only, Principles III/IV).</summary>
public static class WorkflowDtoMapper
{
    public static string NodeTypeToApi(WorkflowNodeType t) => WorkflowGrammarContract.ApiType(t);

    public static WorkflowSummaryDto ToSummary(WorkflowLoadResult result, string effectiveDefaultId)
    {
        var def = result.Definition;
        return new WorkflowSummaryDto
        {
            Id = def?.Id,
            Name = def?.Name,
            Description = def?.Description,
            Source = result.Source,
            Valid = result.IsValid,
            Error = result.Error,
            Warnings = result.Warnings,
            IsBuiltIn = result.IsBuiltIn,
            IsDefault = def is not null && string.Equals(def.Id, effectiveDefaultId, StringComparison.Ordinal),
            Trigger = def?.Triggers.FirstOrDefault() is { } trigger ? ToTriggerDto(trigger) : null,
            Triggers = def?.Triggers.Select(ToTriggerDto).ToList() ?? [],
        };
    }

    public static WorkflowTriggerDto ToTriggerDto(WorkflowTrigger trigger) => new()
    {
        Type = trigger.Type switch
        {
            WorkflowTriggerType.Schedule => "schedule",
            WorkflowTriggerType.Event => "event",
            _ => throw new ArgumentOutOfRangeException(nameof(trigger)),
        },
        Interval = trigger.Interval switch
        {
            WorkflowScheduleInterval.Daily => "daily",
            WorkflowScheduleInterval.Weekly => "weekly",
            WorkflowScheduleInterval.Monthly => "monthly",
            _ => null,
        },
        DayOfWeek = trigger.DayOfWeek?.ToString().ToLowerInvariant(),
        DayOfMonth = trigger.DayOfMonth,
        TimeOfDay = trigger.TimeOfDay?.ToString("HH:mm"),
        EventName = trigger.EventName,
        If = trigger.If.Count == 0 ? null : trigger.If.Select(ToTriggerPredicateDto).ToList(),
    };

    public static WorkflowTriggerConfigResponse ToTriggerConfigResponse(IReadOnlyList<WorkflowTrigger> triggers)
    {
        var legacyTrigger = triggers.FirstOrDefault();
        return new WorkflowTriggerConfigResponse
        {
            Trigger = legacyTrigger is null ? null : ToTriggerDto(legacyTrigger),
            Triggers = triggers.Select(ToTriggerDto).ToList(),
        };
    }

    internal static TriggerYamlDto ToTriggerYamlDto(WorkflowTriggerDto trigger) => new()
    {
        Type = trigger.Type,
        Interval = trigger.Interval,
        DayOfWeek = trigger.DayOfWeek,
        DayOfMonth = trigger.DayOfMonth,
        TimeOfDay = trigger.TimeOfDay,
        EventName = trigger.EventName,
        If = trigger.If?.Select(ToTriggerPredicateYamlDto).ToList(),
    };

    internal static WorkflowTriggerDto MergeTriggerPatch(WorkflowTriggerDto? current, WorkflowTriggerPatchRequest patch)
    {
        var type = patch.Type ?? current?.Type;
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Trigger type is required.", nameof(patch));

        var normalizedType = type.Trim().ToLowerInvariant();
        var ifPredicates = normalizedType == "schedule"
            ? []
            : patch.If ?? current?.If;

        return new WorkflowTriggerDto
        {
            Type = type,
            Interval = patch.Interval ?? current?.Interval,
            DayOfWeek = patch.DayOfWeek ?? current?.DayOfWeek,
            DayOfMonth = patch.DayOfMonth ?? current?.DayOfMonth,
            TimeOfDay = patch.TimeOfDay ?? current?.TimeOfDay,
            EventName = patch.EventName ?? current?.EventName,
            If = ifPredicates,
        };
    }

    private static WorkflowTriggerPredicateDto ToTriggerPredicateDto(WorkflowTriggerPredicate predicate) => new()
    {
        HasLabel = predicate.HasLabel is null ? null : new WorkflowTriggerLabelPredicateDto { Label = predicate.HasLabel.Label },
        IsNotLabeledWith = predicate.IsNotLabeledWith is null ? null : new WorkflowTriggerLabelPredicateDto { Label = predicate.IsNotLabeledWith.Label },
        BaseBranch = predicate.BaseBranch is null ? null : new WorkflowTriggerBaseBranchPredicateDto { Branch = predicate.BaseBranch.Branch },
        ReviewState = predicate.ReviewState is null ? null : new WorkflowTriggerReviewStatePredicateDto { State = ReviewStateToApi(predicate.ReviewState.State) },
        Ref = predicate.Ref is null ? null : new WorkflowTriggerRefPredicateDto
        {
            Branch = predicate.Ref.Branch,
            MatchMode = MatchModeToApi(predicate.Ref.MatchMode),
        },
        Category = predicate.Category is null ? null : new WorkflowTriggerCategoryPredicateDto { Name = predicate.Category.Name },
        CommentMatches = predicate.CommentMatches is null ? null : new WorkflowTriggerCommentMatchesPredicateDto { Pattern = predicate.CommentMatches.Pattern },
        Or = predicate.Or.Count == 0 ? null : predicate.Or.Select(ToTriggerPredicateDto).ToList(),
        Not = predicate.Not is null ? null : ToTriggerPredicateDto(predicate.Not),
    };

    private static TriggerPredicateYamlDto ToTriggerPredicateYamlDto(WorkflowTriggerPredicateDto predicate) => new()
    {
        HasLabel = predicate.HasLabel is null ? null : new TriggerLabelPredicateYamlDto { Label = predicate.HasLabel.Label },
        IsNotLabeledWith = predicate.IsNotLabeledWith is null ? null : new TriggerLabelPredicateYamlDto { Label = predicate.IsNotLabeledWith.Label },
        BaseBranch = predicate.BaseBranch is null ? null : new TriggerBaseBranchPredicateYamlDto { Branch = predicate.BaseBranch.Branch },
        ReviewState = predicate.ReviewState is null ? null : new TriggerReviewStatePredicateYamlDto { State = predicate.ReviewState.State },
        Ref = predicate.Ref is null ? null : new TriggerRefPredicateYamlDto
        {
            Branch = predicate.Ref.Branch,
            MatchMode = predicate.Ref.MatchMode,
        },
        Category = predicate.Category is null ? null : new TriggerCategoryPredicateYamlDto { Name = predicate.Category.Name },
        CommentMatches = predicate.CommentMatches is null ? null : new TriggerCommentMatchesPredicateYamlDto { Pattern = predicate.CommentMatches.Pattern },
        Or = predicate.Or?.Select(ToTriggerPredicateYamlDto).ToList(),
        Not = predicate.Not is null ? null : ToTriggerPredicateYamlDto(predicate.Not),
    };

    private static string ReviewStateToApi(WorkflowTriggerReviewState state) => state switch
    {
        WorkflowTriggerReviewState.Approved => "approved",
        WorkflowTriggerReviewState.ChangesRequested => "changes_requested",
        WorkflowTriggerReviewState.Commented => "commented",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static string MatchModeToApi(WorkflowTriggerMatchMode mode) => mode switch
    {
        WorkflowTriggerMatchMode.Equals => "equals",
        WorkflowTriggerMatchMode.Prefix => "prefix",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string NodeRoleForGraph(WorkflowNodeType t) => t switch
    {
        WorkflowNodeType.Check              => "rai",
        WorkflowNodeType.PeerReview         => "review",
        WorkflowNodeType.BuildTest          => "review",
        WorkflowNodeType.OpenPullRequest    => "action",
        WorkflowNodeType.Merge              => "merge",
        WorkflowNodeType.Scribe             => "scribe",
        WorkflowNodeType.CoordinatorComposed => "coordinator",
        WorkflowNodeType.Terminal           => "assembly",
        _                                   => "agent",
    };

    private static string NodeTypeForGraph(WorkflowNodeType t) => t switch
    {
        WorkflowNodeType.Terminal   => "terminal",
        WorkflowNodeType.Check      => "gate",
        WorkflowNodeType.PeerReview => "gate",
        WorkflowNodeType.BuildTest  => "gate",
        WorkflowNodeType.OpenPullRequest => "action",
        WorkflowNodeType.FanOut     => "action",
        WorkflowNodeType.FanIn      => "action",
        WorkflowNodeType.Merge      => "action",
        WorkflowNodeType.Scribe     => "action",
        _                           => "agent",
    };

    /// <summary>Detects back-edges (loopbacks) via DFS so dagre layout can skip them.</summary>
    private static HashSet<(string From, string To)> DetectLoopbacks(WorkflowDefinition def)
    {
        var adjacency = def.Edges
            .GroupBy(e => e.From)
            .ToDictionary(g => g.Key, g => g.Select(e => e.To).ToList());

        var visited  = new HashSet<string>(StringComparer.Ordinal);
        var inStack  = new HashSet<string>(StringComparer.Ordinal);
        var loopbacks = new HashSet<(string, string)>();

        void Dfs(string node)
        {
            visited.Add(node);
            inStack.Add(node);
            foreach (var neighbor in adjacency.GetValueOrDefault(node, []))
            {
                if (inStack.Contains(neighbor))
                    loopbacks.Add((node, neighbor));
                else if (!visited.Contains(neighbor))
                    Dfs(neighbor);
            }
            inStack.Remove(node);
        }

        foreach (var n in def.Nodes)
            if (!visited.Contains(n.Id))
                Dfs(n.Id);

        return loopbacks;
    }

    public static WorkflowGraphDto ToGraph(WorkflowDefinition def)
    {
        var loopbacks = DetectLoopbacks(def);
        return new WorkflowGraphDto
        {
            GraphId     = def.Id,
            Variant     = "workflow",
            StartNodeId = def.Start,
            Nodes = def.Nodes.Select(n => new WorkflowGraphNodeDto
            {
                Id       = n.Id,
                Label    = n.Label,
                Role     = NodeRoleForGraph(n.Type),
                Kind     = "planned",
                NodeType = NodeTypeForGraph(n.Type),
            }).ToList(),
            Edges = def.Edges.Select(e => new WorkflowGraphEdgeDto
            {
                From        = e.From,
                To          = e.To,
                Cardinality = "direct",
                Loopback    = loopbacks.Contains((e.From, e.To)),
                Label       = e.When,
            }).ToList(),
        };
    }

    public static WorkflowDetailDto ToDetail(WorkflowLoadResult result, string effectiveDefaultId)
    {
        var def = result.Definition!;
        return new WorkflowDetailDto
        {
            Id = def.Id,
            Name = def.Name,
            Description = def.Description,
            Start = def.Start,
            Source = result.Source,
            IsBuiltIn = result.IsBuiltIn,
            IsDefault = string.Equals(def.Id, effectiveDefaultId, StringComparison.Ordinal),
            Warnings = result.Warnings,
            Nodes = def.Nodes.Select(n => new WorkflowNodeDto
            {
                Id = n.Id,
                Type = NodeTypeToApi(n.Type),
                Label = n.Label,
                Role = n.Role,
                Kind = n.Kind,
                GateKind = n.GateKind,
                Agent = n.Agent,
                Prompt = n.Prompt,
                Charter = n.Charter,
                Target = n.Target,
                Steps = n.Steps.Count == 0 ? null : n.Steps,
                Branches = n.Branches.Count == 0 ? null : n.Branches,
            }).ToList(),
            Edges = def.Edges.Select(e => new WorkflowEdgeDto
            {
                From = e.From,
                To = e.To,
                When = e.When,
            }).ToList(),
            Trigger = def.Triggers.FirstOrDefault() is { } trigger ? ToTriggerDto(trigger) : null,
            Triggers = def.Triggers.Select(ToTriggerDto).ToList(),
        };
    }
}
