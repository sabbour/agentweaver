using System.Security.Cryptography;
using System.Text;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Model;
using Agentweaver.Squad.Squad;

namespace Agentweaver.Api.Skills;

/// <summary>
/// Plans and explicitly materializes a blueprint's bundled skill defaults. Planning is deliberately
/// side-effect free; apply recomputes the canonical digest and the store verifies its state guard in
/// the same transaction as catalog, activation, and assignment writes.
/// </summary>
public sealed class SkillDefaultsService
{
    private readonly ISkillStore _skills;
    private readonly IProjectStore _projects;
    private readonly SkillParser _parser = new();

    public SkillDefaultsService(ISkillStore skills, IProjectStore projects)
    {
        _skills = skills;
        _projects = projects;
    }

    public async Task<SkillDefaultsPreview> PreviewAsync(
        ProjectId projectId,
        Blueprint blueprint,
        CancellationToken ct = default)
    {
        var project = await _projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null)
            return SkillDefaultsPreview.Invalid("Project was not found.");

        Team? team;
        try
        {
            team = new SquadReader(project.WorkingDirectory).ReadTeam();
        }
        catch (Exception)
        {
            return SkillDefaultsPreview.Invalid("The confirmed team could not be read.");
        }

        return team is null
            ? SkillDefaultsPreview.Invalid("Skill defaults require a confirmed team.")
            : await PreviewAsync(project, blueprint, team, ct).ConfigureAwait(false);
    }

    public async Task<SkillDefaultsPreview> PreviewAsync(
        Project project,
        Blueprint blueprint,
        Team confirmedTeam,
        CancellationToken ct = default)
    {
        var errors = new List<string>();
        var activeMembers = confirmedTeam.Members
            .Where(m => m.Status == CastMemberStatus.Active)
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
        var resolved = new Dictionary<string, CastMember>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in blueprint.SkillBindings
            .OrderBy(b => b.RoleId, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(binding.RoleId))
            {
                errors.Add("A skill binding is missing role_id.");
                continue;
            }

            var matches = activeMembers
                .Where(member => string.Equals(member.Role.Id, binding.RoleId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count != 1)
            {
                errors.Add(matches.Count == 0
                    ? $"No active confirmed member holds role '{binding.RoleId}'."
                    : $"Role '{binding.RoleId}' is held by multiple active confirmed members.");
                continue;
            }
            resolved[binding.RoleId] = matches[0];
        }

        var currentSkills = await _skills.ListByProjectAsync(project.Id, ct).ConfigureAwait(false);
        var currentAssignments = await _skills.ListAssignmentsByProjectAsync(project.Id, ct).ConfigureAwait(false);
        var planned = new List<SkillDefaultAssignment>();
        var inserts = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
        var activations = new Dictionary<SkillId, Skill>();
        var assignments = new Dictionary<(SkillId SkillId, string Agent), SkillAssignment>();
        var bundled = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;

        foreach (var binding in blueprint.SkillBindings
            .OrderBy(b => b.RoleId, StringComparer.OrdinalIgnoreCase))
        {
            if (!resolved.TryGetValue(binding.RoleId, out var member))
                continue;

            foreach (var skillName in binding.Skills
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                var builtIn = LoadBuiltIn(project.Id, skillName, now, errors);
                if (builtIn is null)
                    continue;
                bundled[skillName] = builtIn;

                var existing = currentSkills.SingleOrDefault(skill =>
                    string.Equals(skill.Name, skillName, StringComparison.OrdinalIgnoreCase));
                if (existing is not null && existing.Provenance != SkillProvenance.BuiltIn)
                {
                    planned.Add(new SkillDefaultAssignment(binding.RoleId, member.Name, skillName, "blocked"));
                    continue;
                }

                Skill materialized;
                if (existing is null)
                {
                    materialized = builtIn;
                    inserts[skillName] = materialized;
                    planned.Add(new SkillDefaultAssignment(binding.RoleId, member.Name, skillName, "create"));
                }
                else
                {
                    if (!string.Equals(existing.ContentHash, builtIn.ContentHash, StringComparison.Ordinal))
                    {
                        errors.Add($"Built-in skill '{skillName}' differs from its bundled content and cannot be replaced.");
                        continue;
                    }

                    materialized = existing;
                    if (existing.Status != SkillStatus.Active)
                    {
                        materialized = existing with { Status = SkillStatus.Active, UpdatedAt = now };
                        activations[existing.Id] = materialized;
                        planned.Add(new SkillDefaultAssignment(binding.RoleId, member.Name, skillName, "reactivate"));
                    }
                    else
                    {
                        planned.Add(new SkillDefaultAssignment(binding.RoleId, member.Name, skillName, "assign"));
                    }
                }

                if (!currentAssignments.Any(assignment =>
                        assignment.SkillId == materialized.Id &&
                        string.Equals(assignment.AgentName, member.Name, StringComparison.Ordinal)))
                {
                    assignments[(materialized.Id, member.Name)] = new SkillAssignment
                    {
                        ProjectId = project.Id,
                        SkillId = materialized.Id,
                        AgentName = member.Name,
                        CreatedAt = now,
                    };
                }
            }
        }

        var stateFingerprint = SkillCatalogStateFingerprint.Compute(currentSkills, currentAssignments);
        var storePlan = new SkillDefaultsStorePlan(
            project.Id,
            stateFingerprint,
            inserts.Values.OrderBy(skill => skill.Name, StringComparer.Ordinal).ToList(),
            activations.Values.OrderBy(skill => skill.Name, StringComparer.Ordinal).ToList(),
            assignments.Values
                .OrderBy(assignment => assignment.SkillId.ToString(), StringComparer.Ordinal)
                .ThenBy(assignment => assignment.AgentName, StringComparer.Ordinal)
                .ToList());

        var digest = ComputeDigest(
            blueprint,
            confirmedTeam,
            resolved,
            bundled.Values,
            currentSkills,
            currentAssignments,
            stateFingerprint);
        return new SkillDefaultsPreview
        {
            BlueprintId = blueprint.Id,
            BlueprintVersion = BlueprintVersion(blueprint),
            Digest = digest,
            CanApply = errors.Count == 0,
            Errors = errors,
            Assignments = planned,
            StorePlan = storePlan,
        };
    }

    public async Task<SkillDefaultsApplyResult> ApplyAsync(
        ProjectId projectId,
        Blueprint blueprint,
        string previewDigest,
        CancellationToken ct = default)
    {
        var refreshed = await PreviewAsync(projectId, blueprint, ct).ConfigureAwait(false);
        if (!refreshed.CanApply)
            return SkillDefaultsApplyResult.Invalid(refreshed.Errors);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(refreshed.Digest),
                Encoding.UTF8.GetBytes(previewDigest ?? string.Empty)))
        {
            return SkillDefaultsApplyResult.Stale();
        }

        var result = await _skills.ApplyDefaultsAsync(refreshed.StorePlan!, ct).ConfigureAwait(false);
        return result == SkillDefaultsStoreApplyResult.Applied
            ? SkillDefaultsApplyResult.Applied(refreshed)
            : SkillDefaultsApplyResult.Stale();
    }

    private Skill? LoadBuiltIn(ProjectId projectId, string name, DateTimeOffset now, List<string> errors)
    {
        var normalized = name.Trim().ToLowerInvariant();
        var assembly = typeof(CatalogReader).Assembly;
        var resourceName = $"Agentweaver.Squad.Catalog.Resources.skills.{normalized.Replace('-', '_')}.SKILL.md";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            errors.Add($"Built-in skill '{name}' was not found.");
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var parsed = _parser.Parse(reader.ReadToEnd());
        if (!parsed.IsValid || !string.Equals(parsed.Name, normalized, StringComparison.Ordinal))
        {
            errors.Add($"Built-in skill '{name}' is malformed.");
            return null;
        }

        var resources = Array.Empty<SkillResource>();
        return new Skill
        {
            Id = SkillId.New(),
            ProjectId = projectId,
            Name = parsed.Name!,
            Description = parsed.Description!,
            Instructions = parsed.Instructions!,
            Resources = resources,
            Provenance = SkillProvenance.BuiltIn,
            SourceRepository = null,
            SourceLocation = $"catalog/skills/{normalized}",
            ContentHash = SkillParser.ComputeContentHash(parsed.Name!, parsed.Description!, parsed.Instructions!, resources),
            Status = SkillStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static string BlueprintVersion(Blueprint blueprint)
    {
        var canonical = new StringBuilder();
        Append(canonical, blueprint.Id);
        Append(canonical, blueprint.Name);
        Append(canonical, blueprint.Description);
        foreach (var role in blueprint.Roster.OrderBy(value => value, StringComparer.Ordinal)) Append(canonical, role);
        foreach (var workflow in blueprint.Workflows.OrderBy(value => value, StringComparer.Ordinal)) Append(canonical, workflow);
        Append(canonical, blueprint.ReviewPolicy);
        Append(canonical, blueprint.SandboxProfile);
        foreach (var binding in blueprint.SkillBindings.OrderBy(value => value.RoleId, StringComparer.Ordinal))
        {
            Append(canonical, binding.RoleId);
            foreach (var skill in binding.Skills.OrderBy(value => value, StringComparer.Ordinal)) Append(canonical, skill);
        }
        return Hash(canonical);
    }

    private static string ComputeDigest(
        Blueprint blueprint,
        Team team,
        IReadOnlyDictionary<string, CastMember> resolved,
        IEnumerable<Skill> bundled,
        IEnumerable<Skill> currentSkills,
        IEnumerable<SkillAssignment> currentAssignments,
        string stateFingerprint)
    {
        var canonical = new StringBuilder();
        Append(canonical, BlueprintVersion(blueprint));
        foreach (var member in team.Members.OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            Append(canonical, member.Name);
            Append(canonical, member.Role.Id);
            Append(canonical, member.Status.ToString());
        }
        foreach (var pair in resolved.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            Append(canonical, pair.Key);
            Append(canonical, pair.Value.Name);
        }
        foreach (var skill in bundled.OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            Append(canonical, skill.Name);
            Append(canonical, skill.ContentHash);
            Append(canonical, skill.Provenance.ToApiString());
        }
        Append(canonical, SkillCatalogStateFingerprint.Compute(currentSkills, currentAssignments));
        Append(canonical, stateFingerprint);
        return Hash(canonical);
    }

    private static string Hash(StringBuilder canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();

    private static void Append(StringBuilder builder, string? value) =>
        builder.Append(value?.Length ?? -1).Append(':').Append(value).Append('\0');
}

public sealed record SkillDefaultAssignment(string RoleId, string AgentName, string SkillName, string Action);

public sealed record SkillDefaultsPreview
{
    public required string BlueprintId { get; init; }
    public required string BlueprintVersion { get; init; }
    public required string Digest { get; init; }
    public required bool CanApply { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<SkillDefaultAssignment> Assignments { get; init; } = [];
    internal SkillDefaultsStorePlan? StorePlan { get; init; }

    public static SkillDefaultsPreview Invalid(string error) => new()
    {
        BlueprintId = string.Empty,
        BlueprintVersion = string.Empty,
        Digest = string.Empty,
        CanApply = false,
        Errors = [error],
    };
}

public sealed record SkillDefaultsApplyResult
{
    public required string Outcome { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public SkillDefaultsPreview? Preview { get; init; }

    public static SkillDefaultsApplyResult Applied(SkillDefaultsPreview preview) => new() { Outcome = "applied", Preview = preview };
    public static SkillDefaultsApplyResult Stale() => new() { Outcome = "stale", Errors = ["The preview is stale. Preview again before applying."] };
    public static SkillDefaultsApplyResult Invalid(IReadOnlyList<string> errors) => new() { Outcome = "invalid", Errors = errors };
}
