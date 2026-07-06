namespace Agentweaver.Domain.Skills;

/// <summary>
/// A bundled resource that ships alongside a skill's <c>SKILL.md</c> (e.g. a reference doc, template,
/// or script). Content is stored as text; binary resources are rejected at validation time.
/// </summary>
public sealed record SkillResource
{
    /// <summary>Path relative to the skill directory, forward-slash separated (e.g. "templates/pr.md").</summary>
    public required string RelativePath { get; init; }

    /// <summary>UTF-8 text content of the resource.</summary>
    public required string Content { get; init; }
}

/// <summary>
/// A standards-compatible instruction module in a project's skill catalog. Follows the Agent Skills /
/// GitHub Copilot convention: a named, described module backed by <c>SKILL.md</c> instructions plus
/// optional bundled resources, with provenance and a content hash for idempotent re-import/re-sync.
/// </summary>
public sealed record Skill
{
    public required SkillId Id { get; init; }
    public required ProjectId ProjectId { get; init; }

    /// <summary>Skill name (from SKILL.md frontmatter). Unique per project (case-insensitive).</summary>
    public required string Name { get; init; }

    /// <summary>Short description surfaced up front for progressive disclosure.</summary>
    public required string Description { get; init; }

    /// <summary>Full <c>SKILL.md</c> body (instructions) minus the frontmatter.</summary>
    public required string Instructions { get; init; }

    /// <summary>Optional bundled resources (text files shipped with the skill).</summary>
    public IReadOnlyList<SkillResource> Resources { get; init; } = Array.Empty<SkillResource>();

    public required SkillProvenance Provenance { get; init; }

    /// <summary>
    /// Source coordinates: repo URL for <see cref="SkillProvenance.RepoImport"/>, "owner/repo" or the
    /// connected repository identifier for <see cref="SkillProvenance.ConnectedRepoSync"/>, null for
    /// <see cref="SkillProvenance.FileUpload"/>.
    /// </summary>
    public string? SourceRepository { get; init; }

    /// <summary>Path within the source (e.g. ".github/skills/pr-review") when applicable.</summary>
    public string? SourceLocation { get; init; }

    /// <summary>Stable content hash over name + description + instructions + resources. Drives idempotency.</summary>
    public required string ContentHash { get; init; }

    public required SkillStatus Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>An assignment of a catalog skill to a named agent within a project.</summary>
public sealed record SkillAssignment
{
    public required ProjectId ProjectId { get; init; }
    public required SkillId SkillId { get; init; }

    /// <summary>Agent name the skill is assigned to (matches the run's AgentName).</summary>
    public required string AgentName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
