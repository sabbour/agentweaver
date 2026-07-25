namespace Agentweaver.Domain.Skills;

/// <summary>
/// A project-scoped, user-added skill marketplace source (step-1b: "add a marketplace by GitHub repo
/// URL"). Unlike the administrator-curated, image-baked config sources, these live in the database so a
/// user can add a source at runtime without a redeploy. A source is defined primarily by
/// <see cref="Repository"/> (<c>owner/repo</c>); <see cref="Subpath"/> is OPTIONAL — when null/blank the
/// catalog indexer auto-detects where the skills live (heuristic SKILL.md discovery, with a bounded LLM
/// fallback) instead of relying on a hardcoded layout.
/// </summary>
public sealed record ProjectMarketplaceSource
{
    public required ProjectId ProjectId { get; init; }

    /// <summary>Stable per-source identifier (GUID string).</summary>
    public required string SourceId { get; init; }

    /// <summary>Human-friendly, project-unique (case-insensitive) display name used in browse/import routes.</summary>
    public required string Name { get; init; }

    /// <summary><c>owner/repo</c> slug the source points at.</summary>
    public required string Repository { get; init; }

    /// <summary>Branch to read; null/blank means the built-in default ("main").</summary>
    public string? Branch { get; init; }

    /// <summary>Optional subpath; null/blank means "auto-detect the skill layout".</summary>
    public string? Subpath { get; init; }

    /// <summary>
    /// Parsing hint: <c>auto</c> (default) | <c>skillmd</c> (force the deterministic SKILL.md heuristic)
    /// | <c>llm</c> (force the LLM classifier). Null is treated as <c>auto</c>.
    /// </summary>
    public string? ParseStrategy { get; init; }

    public bool Enabled { get; init; } = true;

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Persistence abstraction for project-scoped marketplace sources. Mirrors the existing store
/// conventions (project-scoped, dialect-neutral). Implemented by a SQLite store (dev/staging) and an EF
/// Core store (Postgres).
/// </summary>
public interface IProjectMarketplaceSourceStore
{
    /// <summary>All enabled + disabled sources for a project, ordered by name (case-insensitive).</summary>
    Task<IReadOnlyList<ProjectMarketplaceSource>> ListByProjectAsync(ProjectId projectId, CancellationToken ct = default);

    /// <summary>Case-insensitive lookup by source name within a project.</summary>
    Task<ProjectMarketplaceSource?> GetByNameAsync(ProjectId projectId, string name, CancellationToken ct = default);

    /// <summary>
    /// Inserts a new source. Returns <c>false</c> when a source with the same (case-insensitive) name
    /// already exists in the project (the caller surfaces a conflict).
    /// </summary>
    Task<bool> InsertAsync(ProjectMarketplaceSource source, CancellationToken ct = default);

    /// <summary>Deletes a source by name. Returns <c>true</c> when a row was removed.</summary>
    Task<bool> DeleteByNameAsync(ProjectId projectId, string name, CancellationToken ct = default);
}
