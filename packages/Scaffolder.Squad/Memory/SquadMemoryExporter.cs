using System.Text;
using Scaffolder.SandboxFs;

namespace Scaffolder.Squad.Memory;

/// <summary>
/// Exports DB-sourced memory to .squad/ filesystem files and .agentweaver/context/ artifacts.
/// Receives plain data — no EF dependency. Called from the API layer with materialized data.
/// </summary>
public sealed class SquadMemoryExporter
{
    private readonly string _workingDirectory;

    public SquadMemoryExporter(string workingDirectory)
    {
        _workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
    }

    private string Resolve(string relativePath)
        => SandboxPathValidator.ValidateAndResolve(relativePath, _workingDirectory);

    private void WriteAllText(string relativePath, string content)
    {
        var full = Resolve(relativePath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content, Encoding.UTF8);
    }

    public Task ExportAsync(
        IReadOnlyList<DecisionExportDto> decisions,
        IReadOnlyList<InboxExportDto> inboxEntries,
        IReadOnlyList<MemoryExportDto> memories,
        SessionExportDto? currentSession,
        CancellationToken ct = default)
    {
        ExportSquadDecisions(decisions, inboxEntries);
        ExportAgentHistories(memories);
        ExportNowMd(currentSession);
        ExportBoundariesMd(decisions);
        ExportPatternsMd(memories);
        return Task.CompletedTask;
    }

    private void ExportSquadDecisions(
        IReadOnlyList<DecisionExportDto> decisions,
        IReadOnlyList<InboxExportDto> inboxEntries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Team Decisions");
        sb.AppendLine();
        foreach (var d in decisions)
        {
            sb.AppendLine($"## {d.Title}");
            sb.AppendLine($"**Type:** {d.Type} | **By:** {d.AgentName} | **Status:** {d.Status}");
            sb.AppendLine();
            sb.AppendLine(d.Content);
            if (!string.IsNullOrEmpty(d.Rationale))
                sb.AppendLine($"\n> **Rationale:** {d.Rationale}");
            sb.AppendLine("\n---\n");
        }
        WriteAllText(".squad/decisions.md", sb.ToString());

        foreach (var e in inboxEntries)
        {
            var content = $"---\nagent: {e.AgentName}\nslug: {e.Slug}\ntype: {e.Type}\ntitle: {e.Title}\n---\n\n{e.Content}";
            if (!string.IsNullOrEmpty(e.Rationale))
                content += $"\n\n**Rationale:** {e.Rationale}";
            WriteAllText($".squad/decisions/inbox/{e.Slug}.md", content);
        }
    }

    private void ExportAgentHistories(IReadOnlyList<MemoryExportDto> memories)
    {
        foreach (var group in memories.GroupBy(m => m.AgentName))
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {group.Key} History");
            sb.AppendLine();
            foreach (var m in group.Where(m => m.Type != "core_context").OrderBy(m => m.CreatedAt))
            {
                sb.AppendLine($"### [{m.Type}] {m.CreatedAt:yyyy-MM-dd}");
                sb.AppendLine(m.Content);
                sb.AppendLine();
            }
            if (sb.Length > 0)
                WriteAllText($".squad/agents/{group.Key.ToLowerInvariant()}/history.md", sb.ToString());
        }
    }

    private void ExportNowMd(SessionExportDto? session)
    {
        if (session is null) return;
        var content = $"---\nupdated_at: {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\nfocus_area: {session.FocusArea}\nactive_issues: [{session.ActiveIssues ?? ""}]\n---\n\n" +
                      $"# What We're Focused On\n\n{session.FocusArea}\n";
        if (!string.IsNullOrEmpty(session.Summary))
            content += $"\n## Summary\n\n{session.Summary}\n";
        WriteAllText(".squad/identity/now.md", content);
    }

    private void ExportBoundariesMd(IReadOnlyList<DecisionExportDto> decisions)
    {
        var architectural = decisions.Where(d => d.Type == "architectural" || d.Type == "scope").ToList();
        if (architectural.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("# Project Boundaries");
        sb.AppendLine();
        sb.AppendLine("> These boundaries have been explicitly decided by the team. They take precedence over individual agent preferences.");
        sb.AppendLine();
        foreach (var d in architectural)
        {
            sb.AppendLine($"## {d.Title}");
            sb.AppendLine($"*Decided by {d.AgentName} on {d.CreatedAt:yyyy-MM-dd}*");
            sb.AppendLine();
            sb.AppendLine(d.Content);
            if (!string.IsNullOrEmpty(d.Rationale))
                sb.AppendLine($"\n> **Why:** {d.Rationale}");
            sb.AppendLine("\n---\n");
        }
        WriteAllText(".squad/identity/boundaries.md", sb.ToString());
    }

    private void ExportPatternsMd(IReadOnlyList<MemoryExportDto> memories)
    {
        var patterns = memories.Where(m => m.Type == "pattern").ToList();
        if (patterns.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("# Shared Patterns");
        sb.AppendLine();
        foreach (var p in patterns)
        {
            sb.AppendLine($"### [{p.AgentName}] {p.CreatedAt:yyyy-MM-dd}");
            sb.AppendLine(p.Content);
            sb.AppendLine();
        }
        WriteAllText(".squad/identity/patterns.md", sb.ToString());
    }
}
