namespace Scaffolder.Domain;

/// <summary>
/// Enforced limits for a run (FR-029). A run ends with <c>run.bounded</c> when
/// either limit is breached (Principle X).
/// </summary>
public sealed record RunBounds
{
    public int MaxSteps { get; init; } = 50;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(10);
}
