namespace Scaffolder.Domain;

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
}
