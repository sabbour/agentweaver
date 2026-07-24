namespace Agentweaver.Api.Memory;

/// <summary>
/// EF Core record for a project-scoped, user-added skill marketplace source. Postgres-only (see
/// MemoryDbContext); the SQLite path uses the raw <c>skill_marketplace_sources</c> table.
/// </summary>
public sealed class SkillMarketplaceSourceRecord
{
    public string SourceId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Repository { get; set; } = "";
    public string? Branch { get; set; }
    public string? Subpath { get; set; }
    public string? ParseStrategy { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
