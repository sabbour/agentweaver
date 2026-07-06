namespace Agentweaver.Domain.Skills;

/// <summary>
/// Persistence abstraction for the per-project skill catalog and skill→agent assignments. Mirrors the
/// existing store conventions (project-scoped, dialect-neutral). Implemented by a SQLite store (dev)
/// and an EF Core store (Postgres).
/// </summary>
public interface ISkillStore
{
    // ── Catalog ────────────────────────────────────────────────────────────────
    Task InsertAsync(Skill skill, CancellationToken ct = default);
    Task UpdateAsync(Skill skill, CancellationToken ct = default);
    Task<Skill?> GetAsync(ProjectId projectId, SkillId id, CancellationToken ct = default);

    /// <summary>Case-insensitive lookup by skill name within a project (duplicate-name arbitration).</summary>
    Task<Skill?> GetByNameAsync(ProjectId projectId, string name, CancellationToken ct = default);

    Task<IReadOnlyList<Skill>> ListByProjectAsync(ProjectId projectId, CancellationToken ct = default);

    /// <summary>Deletes a skill and cascades its assignments. Returns true when a row was removed.</summary>
    Task<bool> DeleteAsync(ProjectId projectId, SkillId id, CancellationToken ct = default);

    // ── Assignments ──────────────────────────────────────────────────────────────
    /// <summary>Idempotently assigns a skill to an agent. No-op when already assigned.</summary>
    Task AssignAsync(ProjectId projectId, SkillId skillId, string agentName, DateTimeOffset createdAt, CancellationToken ct = default);

    /// <summary>Removes an assignment. Returns true when a row was removed.</summary>
    Task<bool> UnassignAsync(ProjectId projectId, SkillId skillId, string agentName, CancellationToken ct = default);

    /// <summary>All assignments in a project.</summary>
    Task<IReadOnlyList<SkillAssignment>> ListAssignmentsByProjectAsync(ProjectId projectId, CancellationToken ct = default);

    /// <summary>Active skills assigned to a specific agent — the progressive-disclosure input at prompt time.</summary>
    Task<IReadOnlyList<Skill>> ListActiveSkillsForAgentAsync(ProjectId projectId, string agentName, CancellationToken ct = default);
}
