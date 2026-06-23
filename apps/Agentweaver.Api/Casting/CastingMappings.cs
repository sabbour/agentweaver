using Agentweaver.Api.Contracts;
using Agentweaver.Squad.Model;
using Agentweaver.Squad.Squad;

namespace Agentweaver.Api.Casting;

/// <summary>
/// Static mapping helpers from Squad domain types to API contract DTOs.
/// </summary>
public static class CastingMappings
{
    public static RoleDto ToDto(Role role) => new()
    {
        Id = role.Id,
        Title = role.Title,
        Summary = role.Summary,
        DefaultModel = role.DefaultModel,
    };

    public static TeamTemplateDto ToDto(TeamTemplate template) => new()
    {
        Id = template.Id,
        Title = template.Title,
        Description = template.Description,
        Roles = template.Roles.Select(ToDto).ToList(),
    };

    public static ProposedMemberDto ToDto(ProposedMember member) => new()
    {
        ProposedName = member.ProposedName,
        Role = ToDto(member.Role),
        CharterMarkdown = member.CharterMarkdown,
        IsNamed = member.IsNamed,
        DefaultModel = member.DefaultModel,
        Justification = member.Justification,
    };

    public static CastProposalDto ToDto(CastProposal proposal) => new()
    {
        ProposalId = proposal.ProposalId,
        Mode = proposal.Mode.ToString().ToLower(),
        Universe = proposal.Universe,
        Members = proposal.Members.Select(ToDto).ToList(),
        ExistingTeamPresent = proposal.ExistingTeamPresent,
        RunId = proposal.RunId,
        Warnings = proposal.Warnings,
        Rationale = proposal.Rationale,
    };

    public static TeamMemberDto ToDto(CastMember member, DateTimeOffset? createdAt = null, DateTimeOffset? updatedAt = null) => new()
    {
        Name = member.Name,
        RoleTitle = member.Role.Title,
        CharterPath = member.CharterPath,
        Status = member.Status == CastMemberStatus.Retired ? "retired" : "active",
        DefaultModel = member.Role.DefaultModel,
        IsNamed = member.IsNamed,
        IsBuiltIn = BuiltInAgents.Contains(member.Name),
        CharterCreatedAt = createdAt,
        CharterUpdatedAt = updatedAt,
    };

    private static readonly HashSet<string> BuiltInAgents =
        new(StringComparer.OrdinalIgnoreCase) { "Scribe", "Ralph", "Rai", "Coordinator" };

    public static TeamDto ToDto(Team team, SquadLayoutInfo layout) => new()
    {
        ProjectName = team.ProjectName,
        Universe = team.Universe,
        Members = team.Members.Select(m => ToDto(m)).ToList(),
        Layout = layout.HasConflict ? "conflict"
            : layout.HasCanonical ? "canonical"
            : layout.HasLegacy ? "legacy"
            : "absent",
        MigrationAvailable = layout.HasLegacy && !layout.HasCanonical,
    };
}
