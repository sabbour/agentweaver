using Scaffolder.Domain;

namespace Scaffolder.Api.Projects;

/// <summary>
/// Read model for a project: the project record plus the computed availability flag.
/// Availability is not stored (it is runtime-computed by IProjectWorkspaceProvider.IsAvailable).
/// </summary>
public sealed record ProjectView
{
    public required Project Project { get; init; }
    public required bool Available { get; init; }
}
