namespace Agentweaver.Domain;

public sealed record Project
{
    public required ProjectId Id { get; init; }
    public required string Name { get; init; }
    public required ProjectOrigin Origin { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string DefaultBranch { get; init; }
    public required string Owner { get; init; }
    public required ProjectProviderSettings ProviderSettings { get; init; }
    public required ProjectState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Max Ready tasks claimed per heartbeat tick (FR-008a). Valid range [1, 20]. Default 3.</summary>
    public int MaxReadyPerHeartbeat { get; init; } = 3;

    /// <summary>Auto-answer child clarifying questions on heartbeat-created coordinator runs. Default on.</summary>
    public bool PickupAutopilot { get; init; } = true;

    /// <summary>Auto-approve allow-with-approval tools ONLY on heartbeat-created runs; never the
    /// destructive/irreversible safety floor (Principle X). Default off.</summary>
    public bool PickupAutoApproveTools { get; init; } = false;

    /// <summary>
    /// The project's default workflow, referenced by workflow id/name (Feature 010, FR-041). Selects
    /// which available YAML/predefined workflow applies to the project's work items absent a per-task
    /// override. Null means "use the built-in default workflow".
    /// </summary>
    public string? DefaultWorkflowId { get; init; }

    /// <summary>
    /// The project's active review policy, referenced BY NAME (Feature 010, FR-027/033). Selects which
    /// available review policy (from <c>.agentweaver/review-policies/</c> or the built-in default)
    /// overlays its review steps onto the project's runs. Null means "use the built-in default policy"
    /// (RAI + human-review, absorbed by the built-in workflow for identity/parity).
    /// </summary>
    public string? ActiveReviewPolicyName { get; init; }

    /// <summary>
    /// The project's sandbox profile, set when a blueprint is applied at creation (Feature 012). A
    /// named preset (e.g. <c>default</c> or <c>restricted</c>) selecting the shell/network/destructive
    /// gating posture for the project's runs. Null means "use the built-in default sandbox posture".
    /// </summary>
    public string? SandboxProfile { get; init; }

    /// <summary>
    /// The id of the blueprint applied when the project was created (Feature 012). For predefined
    /// blueprints this is the blueprint id (e.g. "web-fullstack"); for inline blueprints it is
    /// "inline". Null when no blueprint was applied.
    /// </summary>
    public string? SourceBlueprintId { get; init; }

    /// <summary>
    /// The type of blueprint source: "predefined" for catalog blueprints, "inline" for request-body
    /// blueprints, null when no blueprint was applied.
    /// </summary>
    public string? SourceBlueprintType { get; init; }

    /// <summary>
    /// The set of workflow ids the project is allowed to use, declared by the applied blueprint's
    /// <c>workflows</c> set (Feature 015 US3). When this is null or empty the project may use ALL
    /// catalog/library workflows (backward compatible). When non-empty the workflow registry filters
    /// available workflows to this set, always including the built-in default so a project never ends
    /// up with zero workflows.
    /// </summary>
    public IReadOnlyList<string>? AllowedWorkflowIds { get; init; }
}
