using System.Text.Json;
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
}
