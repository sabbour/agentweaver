using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Globalization;
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
        var materializedByName = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
        var blockedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalidNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                var normalizedName = skillName.Trim().ToLowerInvariant();
                if (blockedNames.Contains(normalizedName))
                {
                    planned.Add(new SkillDefaultAssignment(
                        binding.RoleId,
                        member.Name,
                        normalizedName,
                        "blocked"));
                    continue;
                }
                if (invalidNames.Contains(normalizedName))
                    continue;

                var action = "assign";
                if (!materializedByName.TryGetValue(normalizedName, out var materialized))
                {
                    var builtIn = LoadBuiltIn(project.Id, normalizedName, now, errors);
                    if (builtIn is null)
                    {
                        invalidNames.Add(normalizedName);
                        continue;
                    }
                    bundled[normalizedName] = builtIn;

                    var existing = currentSkills.SingleOrDefault(skill =>
                        string.Equals(skill.Name.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null && existing.Provenance != SkillProvenance.BuiltIn)
                    {
                        blockedNames.Add(normalizedName);
                        planned.Add(new SkillDefaultAssignment(
                            binding.RoleId,
                            member.Name,
                            builtIn.Name,
                            "blocked"));
                        continue;
                    }

                    if (existing is null)
                    {
                        materialized = builtIn;
                        inserts[normalizedName] = materialized;
                        action = "create";
                    }
                    else
                    {
                        if (!string.Equals(existing.ContentHash, builtIn.ContentHash, StringComparison.Ordinal))
                        {
                            errors.Add($"Built-in skill '{normalizedName}' differs from its bundled content and cannot be replaced.");
                            invalidNames.Add(normalizedName);
                            continue;
                        }

                        materialized = existing;
                        if (existing.Status != SkillStatus.Active)
                        {
                            materialized = existing with { Status = SkillStatus.Active, UpdatedAt = now };
                            activations[existing.Id] = materialized;
                            action = "reactivate";
                        }
                    }

                    materializedByName[normalizedName] = materialized;
                }

                planned.Add(new SkillDefaultAssignment(
                    binding.RoleId,
                    member.Name,
                    materialized.Name,
                    action));

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
            project.TeamRevision,
            stateFingerprint,
            inserts.Values.OrderBy(skill => skill.Name, StringComparer.Ordinal).ToList(),
            activations.Values.OrderBy(skill => skill.Name, StringComparer.Ordinal).ToList(),
            assignments.Values
                .OrderBy(assignment => assignment.SkillId.ToString(), StringComparer.Ordinal)
                .ThenBy(assignment => assignment.AgentName, StringComparer.Ordinal)
                .ToList());

        var digest = ComputeDigest(
            project,
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
        Project project,
        Blueprint blueprint,
        Team team,
        IReadOnlyDictionary<string, CastMember> resolved,
        IEnumerable<Skill> bundled,
        IEnumerable<Skill> currentSkills,
        IEnumerable<SkillAssignment> currentAssignments,
        string stateFingerprint)
    {
        var canonical = new StringBuilder();
        AppendField(canonical, "digest_schema", "skill-defaults-preview-v2");
        AppendField(canonical, "project_id", project.Id.ToString());
        AppendField(canonical, "team_revision", project.TeamRevision.ToString(CultureInfo.InvariantCulture));
        AppendField(canonical, "blueprint_version", BlueprintVersion(blueprint));
        Append(canonical, "team_members");
        foreach (var member in team.Members.OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            Append(canonical, member.Name);
            Append(canonical, member.Role.Id);
            Append(canonical, member.Status.ToString());
        }
        Append(canonical, "resolved_roles");
        foreach (var pair in resolved.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            Append(canonical, pair.Key);
            Append(canonical, pair.Value.Name);
        }
        Append(canonical, "bundled_skills");
        foreach (var skill in bundled.OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            Append(canonical, skill.Name);
            Append(canonical, skill.ContentHash);
            Append(canonical, skill.Provenance.ToApiString());
        }
        AppendField(
            canonical,
            "catalog_fingerprint",
            SkillCatalogStateFingerprint.Compute(currentSkills, currentAssignments));
        AppendField(canonical, "guarded_state_fingerprint", stateFingerprint);
        return Hash(canonical);
    }

    private static string Hash(StringBuilder canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();

    private static void Append(StringBuilder builder, string? value) =>
        builder.Append(value?.Length ?? -1).Append(':').Append(value).Append('\0');

    private static void AppendField(StringBuilder builder, string name, string? value)
    {
        Append(builder, name);
        Append(builder, value);
    }
}

public sealed record SkillDefaultAssignment(
    [property: JsonPropertyName("role_id")] string RoleId,
    [property: JsonPropertyName("agent_name")] string AgentName,
    [property: JsonPropertyName("skill_name")] string SkillName,
    [property: JsonPropertyName("action")] string Action);

public sealed record SkillDefaultsPreview
{
    [JsonPropertyName("blueprint_id")] public required string BlueprintId { get; init; }
    [JsonPropertyName("blueprint_version")] public required string BlueprintVersion { get; init; }
    [JsonPropertyName("digest")] public required string Digest { get; init; }
    [JsonPropertyName("can_apply")] public required bool CanApply { get; init; }
    [JsonPropertyName("errors")] public IReadOnlyList<string> Errors { get; init; } = [];
    [JsonPropertyName("assignments")] public IReadOnlyList<SkillDefaultAssignment> Assignments { get; init; } = [];
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
    [JsonPropertyName("outcome")] public required string Outcome { get; init; }
    [JsonPropertyName("errors")] public IReadOnlyList<string> Errors { get; init; } = [];
    [JsonPropertyName("preview")] public SkillDefaultsPreview? Preview { get; init; }

    public static SkillDefaultsApplyResult Applied(SkillDefaultsPreview preview) => new() { Outcome = "applied", Preview = preview };
    public static SkillDefaultsApplyResult Stale() => new() { Outcome = "stale", Errors = ["The preview is stale. Preview again before applying."] };
    public static SkillDefaultsApplyResult Invalid(IReadOnlyList<string> errors) => new() { Outcome = "invalid", Errors = errors };
}
