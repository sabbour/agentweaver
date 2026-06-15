namespace Scaffolder.Squad.Model;

public enum CastMemberStatus { Active, Retired }
public enum CastMode { Scenario, FreeText, Analysis, Manual }
public enum CastIntent { New, Augment, Recast }
public enum SyncChangeKind { Added, Modified, Removed }

public sealed record Role(
    string Id,
    string Title,
    string Summary,
    string DefaultModel,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Responsibilities,
    IReadOnlyList<string> Boundaries);

public sealed record CastMember(
    string Name,
    Role Role,
    string CharterPath,
    CastMemberStatus Status,
    bool IsNamed);

public sealed record Team(
    string ProjectName,
    string Universe,
    IReadOnlyList<CastMember> Members);

public sealed record ProposedMember(
    string ProposedName,
    Role Role,
    string CharterMarkdown,
    bool IsNamed,
    string DefaultModel,
    string? Justification);

public sealed record CastProposal(
    string ProposalId,
    CastMode Mode,
    string Universe,
    IReadOnlyList<ProposedMember> Members,
    bool ExistingTeamPresent,
    string? RunId,
    IReadOnlyList<string> Warnings);

public sealed record TeamTemplate(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<Role> Roles);

public sealed record CastingPolicy(
    string Version,
    IReadOnlyList<string> AllowlistUniverses,
    IReadOnlyDictionary<string, int> UniverseCapacity);

public sealed record RegistryMember(
    string Name,
    string PersistentName,
    string Universe,
    string DefaultModel,
    CastMemberStatus Status,
    DateTimeOffset CreatedAt,
    string? PreviousName,
    string? SucceededBy,
    DateTimeOffset? RetiredAt,
    string? CharterPath);

public sealed record CastingRegistry(
    IReadOnlyDictionary<string, RegistryMember> Agents);

public sealed record CastSnapshot(
    string SnapshotId,
    string Universe,
    CastMode Mode,
    CastIntent Intent,
    IReadOnlyList<string> Members,
    IReadOnlyList<string> AddedMembers,
    IReadOnlyList<string> RetiredMembers,
    DateTimeOffset CreatedAt);

public sealed record CastHistory(
    IReadOnlyList<CastSnapshot> Snapshots,
    IReadOnlyList<string> UniverseUsageHistory);

public sealed record SyncChange(
    string RelativePath,
    SyncChangeKind Kind);
