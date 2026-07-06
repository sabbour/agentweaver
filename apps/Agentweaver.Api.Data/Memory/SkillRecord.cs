namespace Agentweaver.Api.Memory;

/// <summary>EF Core record for a per-project catalog skill. Postgres-only (see MemoryDbContext).</summary>
public sealed class SkillRecord
{
    public string SkillId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Instructions { get; set; } = "";

    /// <summary>JSON array of bundled resources ({relativePath, content}).</summary>
    public string? Resources { get; set; }

    public string Provenance { get; set; } = "";
    public string? SourceRepository { get; set; }
    public string? SourceLocation { get; set; }
    public string ContentHash { get; set; } = "";
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>EF Core record for a skill→agent assignment. Postgres-only (see MemoryDbContext).</summary>
public sealed class SkillAssignmentRecord
{
    public string ProjectId { get; set; } = "";
    public string SkillId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
