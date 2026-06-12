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

    public void WriteTeam(Team team, string owner, DateTimeOffset createdAt)
        => WriteAllText(SquadPaths.TeamMd, TeamMarkdown.Render(team, owner, createdAt));

    public bool CharterExists(string memberName)
        => File.Exists(Resolve(SquadPaths.CharterFor(memberName)));

    public void WriteCharter(string memberName, string charterMarkdown)
        => WriteAllText(SquadPaths.CharterFor(memberName), charterMarkdown);

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

    public void EnsureGitAttributes()
    {
        const string content =
            "casting/registry.events.jsonl merge=union\n" +
            "casting/history.events.jsonl merge=union\n";
        WriteAllText(SquadPaths.GitAttributes, content);
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
