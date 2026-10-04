namespace Agentweaver.Domain;

public sealed record ExecutableWorkflowPin
{
    public const int CurrentSchemaVersion = 1;

    public required int ManifestSchemaVersion { get; init; }
    public required string DefinitionId { get; init; }
    public string? DefinitionVersion { get; init; }
    public required string Source { get; init; }
    public required string ContentDigest { get; init; }
    public required string DefinitionYaml { get; init; }
    public required DateTimeOffset PinnedAt { get; init; }
}
