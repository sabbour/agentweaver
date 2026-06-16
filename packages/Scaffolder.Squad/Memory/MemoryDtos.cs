namespace Scaffolder.Squad.Memory;

public sealed record DecisionExportDto(
    string AgentName,
    string Type,
    string Status,
    string Title,
    string Content,
    string? Rationale,
    DateTimeOffset CreatedAt);

public sealed record InboxExportDto(
    string AgentName,
    string Slug,
    string Type,
    string Title,
    string Content,
    string? Rationale);

public sealed record MemoryExportDto(
    string AgentName,
    string Type,
    string Content,
    DateTimeOffset CreatedAt);

public sealed record SessionExportDto(
    string FocusArea,
    string? ActiveIssues);

/// <summary>DTO produced by SquadMemoryImporter.ScanInboxFiles().</summary>
public sealed record InboxImportDto(
    string AgentName,
    string Slug,
    string Type,
    string Title,
    string Content,
    string? Rationale);
