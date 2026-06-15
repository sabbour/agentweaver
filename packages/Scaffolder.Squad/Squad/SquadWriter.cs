using System.Text;
using System.Text.Json;
using Scaffolder.SandboxFs;
using Scaffolder.Squad.Model;

namespace Scaffolder.Squad.Squad;

/// <summary>
/// Writes a project's <c>.squad/</c> directory using the canonical
/// <c>.squad/casting/</c> layout. All file access is validated through
/// <see cref="SandboxPathValidator"/>.
/// </summary>
public sealed class SquadWriter
{
    private readonly string _workingDirectory;

    public SquadWriter(string workingDirectory)
    {
        _workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
    }

    private string Resolve(string relativePath)
        => SandboxPathValidator.ValidateAndResolve(relativePath, _workingDirectory);

    private static void EnsureDirectory(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    private void WriteAllText(string relativePath, string content)
    {
        var full = Resolve(relativePath);
        EnsureDirectory(full);
        File.WriteAllText(full, content);
    }

    private void AppendLine(string relativePath, string line)
    {
        var full = Resolve(relativePath);
        EnsureDirectory(full);
        File.AppendAllText(full, line + "\n");
    }

    public void WriteTeam(Team team, string owner, DateTimeOffset createdAt, string? description = null)
        => WriteAllText(SquadPaths.TeamMd, TeamMarkdown.Render(team, owner, createdAt, description));

    public bool ConfigExists()
        => File.Exists(Resolve(SquadPaths.ConfigJson));

    public void WriteConfig(string content)
        => WriteAllText(SquadPaths.ConfigJson, content);

    public void EnsureIdentityFiles(string projectName, DateTimeOffset createdAt)
    {
        var nowPath = Resolve(SquadPaths.IdentityNowMd);
        EnsureDirectory(nowPath);
        if (!File.Exists(nowPath))
        {
            var nowContent =
                $"---\nupdated_at: {createdAt:yyyy-MM-ddTHH:mm:ssZ}\nfocus_area: Initial setup\nactive_issues: []\n---\n\n" +
                $"# What We're Focused On\n\nGetting started on {projectName}. Updated by coordinator at session start.\n";
            File.WriteAllText(nowPath, nowContent);
        }

        var wisdomPath = Resolve(SquadPaths.IdentityWisdomMd);
        if (!File.Exists(wisdomPath))
        {
            var wisdomContent =
                $"---\nlast_updated: {createdAt:yyyy-MM-ddTHH:mm:ssZ}\n---\n\n" +
                "# Team Wisdom\n\nReusable patterns and heuristics learned through work. " +
                "NOT transcripts — each entry is a distilled, actionable insight.\n\n" +
                "## Patterns\n\n" +
                "<!-- Append entries below. Format: **Pattern:** description. **Context:** when it applies. -->\n";
            File.WriteAllText(wisdomPath, wisdomContent);
        }
    }

    public void EnsureFirstRunMarker(DateTimeOffset createdAt)
    {
        var path = Resolve(SquadPaths.FirstRunMarker);
        EnsureDirectory(path);
        if (!File.Exists(path))
            File.WriteAllText(path, createdAt.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\n");
    }

    public bool RaiPolicyExists()
        => File.Exists(Resolve(SquadPaths.RaiPolicyMd));

    public void WriteRaiPolicy(string content)
        => WriteAllText(SquadPaths.RaiPolicyMd, content);

    public void EnsureRaiAuditTrail()
    {
        var full = Resolve(SquadPaths.RaiAuditTrailMd);
        EnsureDirectory(full);
        if (!File.Exists(full))
            File.WriteAllText(full, "# RAI Audit Trail\n\nAppend-only record of all RAI review findings.\n\n");
    }

    public bool CharterExists(string memberName)
        => File.Exists(Resolve(SquadPaths.CharterFor(memberName)));

    public void WriteCharter(string memberName, string charterMarkdown)
        => WriteAllText(SquadPaths.CharterFor(memberName), charterMarkdown);

    public bool HistoryExists(string memberName)
        => File.Exists(Resolve(SquadPaths.HistoryFor(memberName)));

    public void WriteAgentHistory(string memberName, string content)
        => WriteAllText(SquadPaths.HistoryFor(memberName), content);

    public void WriteMafAgent(string agentName, string content)
        => WriteAllText(SquadPaths.MafAgentFor(agentName), content);

    public bool MafAgentExists(string agentName)
        => File.Exists(Resolve(SquadPaths.MafAgentFor(agentName)));

    public async Task WriteAgentWeaverCoordinatorAsync()
    {
        var full = Resolve(SquadPaths.AgentWeaverCoordinatorMd);
        EnsureDirectory(full);

        var assembly = typeof(SquadWriter).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("agentweaver-squad-coordinator.md", StringComparison.OrdinalIgnoreCase));

        await using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();

        await File.WriteAllTextAsync(full, content);
    }

    public void AppendRegistryEvent(object eventRecord)
        => AppendLine(SquadPaths.CanonicalRegistryEvents, SquadSerialization.SerializeLine(eventRecord));

    public void AppendHistoryEvent(CastSnapshot snapshot)
        => AppendLine(SquadPaths.CanonicalHistoryEvents, SquadSerialization.SerializeLine(snapshot));

    /// <summary>
    /// Rebuilds <c>registry.json</c> and <c>history.json</c> from their append-only sidecars.
    /// </summary>
    public void RegenerateCanonicalJson()
    {
        var registryEvents = Resolve(SquadPaths.CanonicalRegistryEvents);
        if (File.Exists(registryEvents))
        {
            var registry = SquadSerialization.RebuildRegistry(File.ReadAllLines(registryEvents));
            WriteAllText(SquadPaths.CanonicalRegistry, JsonSerializer.Serialize(registry, SquadSerialization.Options));
        }

        var historyEvents = Resolve(SquadPaths.CanonicalHistoryEvents);
        if (File.Exists(historyEvents))
        {
            var history = SquadSerialization.RebuildHistory(File.ReadAllLines(historyEvents));
            WriteAllText(SquadPaths.CanonicalHistory, JsonSerializer.Serialize(history, SquadSerialization.Options));
        }
    }

    public void EnsureGitIgnoreEntries()
    {
        var required = new[]
        {
            ".squad/orchestration-log/",
            ".squad/log/",
            ".squad/decisions/inbox/",
            ".squad/sessions/",
            ".squad/.scratch/",
            ".squad/.cache/",
            ".squad-workstream",
        };

        var fullPath = Resolve(".gitignore");
        EnsureDirectory(fullPath);

        var existing = File.Exists(fullPath) ? File.ReadAllText(fullPath) : string.Empty;
        var toAdd = required.Where(line => !existing.Contains(line)).ToList();
        if (toAdd.Count == 0) return;

        var header = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : string.Empty;
        header += "# Squad: runtime state (not committed)\n";
        File.AppendAllText(fullPath, header + string.Join("\n", toAdd) + "\n");
    }

    public void EnsureGitAttributes()
    {
        var required = new[]
        {
            ".squad/decisions.md merge=union",
            ".squad/agents/*/history.md merge=union",
            ".squad/log/** merge=union",
            ".squad/orchestration-log/** merge=union",
            ".squad/rai/audit-trail.md merge=union",
            ".squad/casting/registry.events.jsonl merge=union",
            ".squad/casting/history.events.jsonl merge=union",
        };

        var fullPath = Resolve(SquadPaths.GitAttributes);
        EnsureDirectory(fullPath);

        var existing = File.Exists(fullPath)
            ? File.ReadAllText(fullPath)
            : string.Empty;

        var toAdd = required.Where(line => !existing.Contains(line)).ToList();
        if (toAdd.Count == 0) return;

        var separator = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : string.Empty;
        File.AppendAllText(fullPath, separator + string.Join("\n", toAdd) + "\n");
    }

    public void WriteRouting(string content)
        => WriteAllText(SquadPaths.RoutingMd, content);

    public void WriteDecisions(string content)
        => WriteAllText(SquadPaths.DecisionsMd, content);

    public bool DecisionsExist()
        => File.Exists(Resolve(SquadPaths.DecisionsMd));

    public void EnsureSquadDirectories()
    {
        foreach (var dir in new[]
        {
            ".squad/log",
            ".squad/orchestration-log",
            ".squad/skills",
            ".squad/rai",
            ".squad/decisions/inbox",
        })
        {
            var full = Resolve(dir);
            Directory.CreateDirectory(full);
            var gitkeep = Path.Combine(full, ".gitkeep");
            if (!File.Exists(gitkeep)) File.WriteAllText(gitkeep, string.Empty);
        }
    }

    /// <summary>
    /// Moves a member's charter to the alumni archive at
    /// <c>.squad/agents/_alumni/{name}/charter.md</c>.
    /// </summary>
    public void ArchiveMemberCharter(string memberName)
    {
        var source = Resolve(SquadPaths.CharterFor(memberName));
        if (!File.Exists(source)) return;

        var dest = Resolve(SquadPaths.AlumniCharterFor(memberName));
        EnsureDirectory(dest);
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(source, dest);
    }
}
