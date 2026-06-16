using Microsoft.EntityFrameworkCore;
using System.Text;

namespace Scaffolder.Api.Memory;

/// <summary>
/// Deterministic context assembler. Applies strict priority hierarchy:
/// decisions (boundaries) > core_context memories > high-importance learnings > session focus.
/// Memory is scoped to the target agent; cross-team tagged memories cross agent boundaries.
/// </summary>
public sealed class MemoryContextCompiler(MemoryDbContext db)
{
    /// <summary>
    /// Compiles a structured context block for the given project + agent.
    /// Returns null if no context exists (empty project or no data yet).
    /// </summary>
    public async Task<string?> CompileAsync(
        string projectId, string agentName, CancellationToken ct = default)
    {
        // Layer 1: active architectural + scope decisions (team-wide boundaries)
        var decisions = await db.Decisions
            .Where(d => d.ProjectId == projectId
                     && d.Status == "active"
                     && (d.Type == "architectural" || d.Type == "scope"))
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);

        // Layer 2: agent core_context memories
        var coreMemories = await db.AgentMemory
            .Where(m => m.ProjectId == projectId
                     && m.AgentName == agentName
                     && m.Type == "core_context")
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        // Layer 3: top-5 high-importance learnings/patterns for this agent
        //          + cross-team tagged memories from other agents
        var learnings = await db.AgentMemory
            .Where(m => m.ProjectId == projectId
                     && m.Importance == "high"
                     && (m.AgentName == agentName || (m.Tags != null && m.Tags.Contains("cross-team")))
                     && (m.Type == "learning" || m.Type == "pattern"))
            .OrderByDescending(m => m.CreatedAt)
            .Take(5)
            .ToListAsync(ct);

        // Layer 4: current open session
        var session = await db.SessionContexts
            .Where(s => s.ProjectId == projectId && s.EndedAt == null)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (!decisions.Any() && !coreMemories.Any() && !learnings.Any() && session is null)
            return null;

        var sb = new StringBuilder();

        if (decisions.Count > 0)
        {
            sb.AppendLine("## Boundaries and Decisions");
            sb.AppendLine("> These are non-negotiable team boundaries. They take precedence over all other context.");
            foreach (var d in decisions)
            {
                sb.AppendLine($"\n### {d.Title}");
                sb.AppendLine($"**Type:** {d.Type} | **Decided by:** {d.AgentName}");
                sb.AppendLine(d.Content);
                if (!string.IsNullOrEmpty(d.Rationale))
                    sb.AppendLine($"> **Rationale:** {d.Rationale}");
            }
        }

        if (coreMemories.Count > 0 || learnings.Count > 0)
        {
            sb.AppendLine("\n## Memory");
            foreach (var m in coreMemories)
                sb.AppendLine($"- [core] {m.Content}");
            foreach (var m in learnings)
            {
                var label = m.AgentName == agentName ? m.Type : $"{m.Type} from {m.AgentName}";
                sb.AppendLine($"- [{label}] {m.Content}");
            }
        }

        if (session is not null)
        {
            sb.AppendLine("\n## Current Session");
            sb.AppendLine($"**Focus:** {session.FocusArea}");
            if (!string.IsNullOrEmpty(session.ActiveIssues))
                sb.AppendLine($"**Active issues:** {session.ActiveIssues}");
            if (!string.IsNullOrEmpty(session.Summary))
                sb.AppendLine($"**Summary:** {session.Summary}");
        }

        return sb.ToString();
    }
}
