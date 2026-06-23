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
    /// available review policy (from <c>.scaffolders/review-policies/</c> or the built-in default)
    /// injects its review steps into the project's runs. Null means "use the built-in default policy"
    /// (Rubber-duck + RAI, FR-032).
    /// </summary>
    public string? ActiveReviewPolicyName { get; init; }
}
