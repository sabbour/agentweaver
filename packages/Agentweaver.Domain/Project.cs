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
}
