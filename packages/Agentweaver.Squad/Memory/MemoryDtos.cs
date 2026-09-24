namespace Agentweaver.Squad.Memory;

public sealed record DecisionExportDto(
    int RecordId,
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
    string SessionId,
    string FocusArea,
    string? ActiveIssues,
    string? Summary = null);

/// <summary>DTO produced by SquadMemoryImporter.ScanInboxFiles().</summary>
public sealed record InboxImportDto(
    string AgentName,
    string Slug,
    string Type,
    string Title,
    string Content,
    string? Rationale);

public sealed record InboxImportConflict(
    string Path,
    string Reason);

public sealed record InboxImportScanResult(
    IReadOnlyList<InboxImportDto> Entries,
    IReadOnlyList<InboxImportConflict> Conflicts);

public sealed record DecisionImportDto(
    int? RecordId,
    string? ContentHash,
    string AgentName,
    string Type,
    string Title,
    string Content,
    string? Rationale,
    bool IsExporterOwned);
