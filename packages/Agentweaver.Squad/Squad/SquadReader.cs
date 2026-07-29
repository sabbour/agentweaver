using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.SandboxFs;
using Agentweaver.Squad.Model;
using Agentweaver.Squad.Naming;

namespace Agentweaver.Squad.Squad;

public sealed record SquadLayoutInfo(
    bool HasCanonical,
    bool HasLegacy,
    bool HasConflict,
    string? MigrationNote);

/// <summary>
/// Reads a project's <c>.squad/</c> directory. Supports the canonical
/// <c>.squad/casting/</c> layout and a legacy flat layout, detecting conflicts.
/// All file access is validated through <see cref="SandboxPathValidator"/>.
/// </summary>
public sealed class SquadReader
{
    private readonly string _workingDirectory;

    public SquadReader(string workingDirectory)
    {
        _workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
    }

    private string? ResolveExisting(string relativePath)
    {
        var full = SandboxPathValidator.ValidateAndResolve(relativePath, _workingDirectory);
        return File.Exists(full) ? full : null;
    }

    public bool SquadDirectoryExists()
    {
        var full = SandboxPathValidator.ValidateAndResolve(SquadPaths.SquadDir, _workingDirectory);
        return Directory.Exists(full);
    }

    public bool TeamExists() => ResolveExisting(SquadPaths.TeamMd) is not null;

    public Team? ReadTeam()
    {
        GuardNoConflict();
        var teamPath = ResolveExisting(SquadPaths.TeamMd);
        if (teamPath is null) return null;

        var registry = ReadRegistry();
        var text = File.ReadAllText(teamPath);
        return TeamMarkdown.Parse(text, registry);
    }

    public IReadOnlyList<CastMember> ReadAlumniMembers()
    {
        GuardNoConflict();

        var alumniRoot = SandboxPathValidator.ValidateAndResolve(".squad/agents/_alumni", _workingDirectory);
        if (!Directory.Exists(alumniRoot)) return [];

        var registry = ReadRegistry();
        var members = new List<CastMember>();

        foreach (var charterPath in Directory.EnumerateFiles(alumniRoot, "charter.md", SearchOption.AllDirectories))
        {
            var charter = File.ReadAllText(charterPath);
            var (name, roleTitle) = ParseCharterIdentity(charter);
            name ??= FindRegistryMemberBySlug(registry, new DirectoryInfo(Path.GetDirectoryName(charterPath)!).Name)?.Name
                ?? new DirectoryInfo(Path.GetDirectoryName(charterPath)!).Name;
            roleTitle ??= "AI Assistant";

            var registryMember = FindRegistryMemberByName(registry, name)
                ?? FindRegistryMemberBySlug(registry, new DirectoryInfo(Path.GetDirectoryName(charterPath)!).Name);
            var relativeCharterPath = Path.GetRelativePath(_workingDirectory, charterPath)
                .Replace(Path.DirectorySeparatorChar, '/');

            members.Add(new CastMember(
                Name: name,
                Role: new Role(
                    Id: roleTitle.ToLowerInvariant().Replace(' ', '-'),
                    Title: roleTitle,
                    Summary: string.Empty,
                    DefaultModel: registryMember?.DefaultModel ?? string.Empty,
                    Capabilities: [],
                    Responsibilities: [],
                    Boundaries: []),
                CharterPath: relativeCharterPath,
                Status: CastMemberStatus.Retired,
                IsNamed: !name.StartsWith("member-", StringComparison.OrdinalIgnoreCase)));
        }

        return members
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    public CastingPolicy ReadPolicy()
    {
        GuardNoConflict();
        var path = ResolveExisting(SquadPaths.CanonicalPolicy) ?? ResolveExisting(SquadPaths.LegacyPolicy);
        if (path is null) return DefaultPolicy();

        var policy = JsonSerializer.Deserialize<CastingPolicy>(File.ReadAllText(path), SquadSerialization.Options);
        return policy ?? DefaultPolicy();
    }

    public CastingRegistry ReadRegistry()
    {
        GuardNoConflict();

        var canonicalEvents = ResolveExisting(SquadPaths.CanonicalRegistryEvents);
        if (canonicalEvents is not null)
            return SquadSerialization.RebuildRegistry(File.ReadAllLines(canonicalEvents));

        var path = ResolveExisting(SquadPaths.CanonicalRegistry) ?? ResolveExisting(SquadPaths.LegacyRegistry);
        if (path is null) return new CastingRegistry(new Dictionary<string, RegistryMember>());

        var registry = JsonSerializer.Deserialize<CastingRegistry>(File.ReadAllText(path), SquadSerialization.Options);
        return registry ?? new CastingRegistry(new Dictionary<string, RegistryMember>());
    }

    public CastHistory ReadHistory()
    {
        GuardNoConflict();

        var canonicalEvents = ResolveExisting(SquadPaths.CanonicalHistoryEvents);
        if (canonicalEvents is not null)
            return SquadSerialization.RebuildHistory(File.ReadAllLines(canonicalEvents));

        var path = ResolveExisting(SquadPaths.CanonicalHistory) ?? ResolveExisting(SquadPaths.LegacyHistory);
        if (path is null) return new CastHistory([], []);

        var history = JsonSerializer.Deserialize<CastHistory>(File.ReadAllText(path), SquadSerialization.Options);
        return history ?? new CastHistory([], []);
    }

    public string? ReadCharter(string memberName)
    {
        var rel = SquadPaths.CharterFor(memberName);
        return ResolveExisting(rel) is { } path ? File.ReadAllText(path) : null;
    }

    public string? ReadHistory(string memberName)
    {
        var rel = SquadPaths.HistoryFor(memberName);
        return ResolveExisting(rel) is { } path ? File.ReadAllText(path) : null;
    }

    public SquadLayoutInfo DetectLayout()
    {
        var hasCanonical =
            ResolveExisting(SquadPaths.CanonicalPolicy) is not null ||
            ResolveExisting(SquadPaths.CanonicalRegistry) is not null ||
            ResolveExisting(SquadPaths.CanonicalRegistryEvents) is not null ||
            ResolveExisting(SquadPaths.CanonicalHistory) is not null ||
            ResolveExisting(SquadPaths.CanonicalHistoryEvents) is not null;

        var hasLegacy =
            ResolveExisting(SquadPaths.LegacyPolicy) is not null ||
            ResolveExisting(SquadPaths.LegacyRegistry) is not null ||
            ResolveExisting(SquadPaths.LegacyHistory) is not null;

        var hasConflict = false;
        if (hasCanonical && hasLegacy)
        {
            hasConflict =
                ArtifactsDiffer(SquadPaths.CanonicalPolicy, SquadPaths.LegacyPolicy) ||
                ArtifactsDiffer(SquadPaths.CanonicalRegistry, SquadPaths.LegacyRegistry) ||
                ArtifactsDiffer(SquadPaths.CanonicalHistory, SquadPaths.LegacyHistory);
        }

        string? note = null;
        if (hasConflict)
            note = "Both canonical (.squad/casting/) and legacy (.squad/casting-*.json) layouts exist with differing content. Resolve the conflict before proceeding.";
        else if (hasLegacy && !hasCanonical)
            note = "Legacy layout detected. Migrate to the canonical .squad/casting/ layout.";

        return new SquadLayoutInfo(hasCanonical, hasLegacy, hasConflict, note);
    }

    private bool ArtifactsDiffer(string canonicalRel, string legacyRel)
    {
        var canonical = ResolveExisting(canonicalRel);
        var legacy = ResolveExisting(legacyRel);
        if (canonical is null || legacy is null) return false;

        var a = NormalizeJson(File.ReadAllText(canonical));
        var b = NormalizeJson(File.ReadAllText(legacy));
        return !string.Equals(a, b, StringComparison.Ordinal);
    }

    private static string NormalizeJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(doc.RootElement);
        }
        catch (JsonException)
        {
            return text.Trim();
        }
    }

    private void GuardNoConflict()
    {
        var layout = DetectLayout();
        if (layout.HasConflict)
            throw new LayoutConflictException(layout.MigrationNote ?? "Conflicting canonical and legacy squad layouts detected.");
    }

    private static CastingPolicy DefaultPolicy()
        => new("1.0.0", UniversePools.Pools.Keys.ToList(), new Dictionary<string, int>());

    private static readonly Regex CharterHeadingPattern =
        new(@"^#\s+(?<name>.+?)\s+[—-]\s+(?<role>.+?)\s*$", RegexOptions.Compiled);

    private static (string? Name, string? RoleTitle) ParseCharterIdentity(string charter)
    {
        foreach (var rawLine in charter.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var match = CharterHeadingPattern.Match(line);
            if (match.Success)
                return (match.Groups["name"].Value.Trim(), match.Groups["role"].Value.Trim());

            break;
        }

        string? roleTitle = null;
        var lines = charter.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!string.Equals(lines[i].Trim(), "## Role", StringComparison.OrdinalIgnoreCase))
                continue;

            for (var j = i + 1; j < lines.Length; j++)
            {
                var roleLine = lines[j].Trim();
                if (roleLine.Length == 0) continue;
                roleTitle = roleLine.EndsWith(" for this project.", StringComparison.OrdinalIgnoreCase)
                    ? roleLine[..^" for this project.".Length].Trim()
                    : roleLine;
                break;
            }

            break;
        }

        return (null, roleTitle);
    }

    private static RegistryMember? FindRegistryMemberByName(CastingRegistry registry, string name)
        => registry.Agents
            .FirstOrDefault(kvp =>
                string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Value.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Value.PersistentName, name, StringComparison.OrdinalIgnoreCase))
            .Value;

    private static RegistryMember? FindRegistryMemberBySlug(CastingRegistry registry, string slug)
        => registry.Agents
            .FirstOrDefault(kvp =>
                MatchesSlug(kvp.Key, slug) ||
                MatchesSlug(kvp.Value.Name, slug) ||
                MatchesSlug(kvp.Value.PersistentName, slug))
            .Value;

    private static bool MatchesSlug(string? value, string slug)
        => !string.IsNullOrWhiteSpace(value)
            && string.Equals(SquadPaths.SlugName(value), slug, StringComparison.OrdinalIgnoreCase);
}
