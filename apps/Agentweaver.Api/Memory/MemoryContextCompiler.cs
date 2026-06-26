using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Text;

namespace Agentweaver.Api.Memory;

/// <summary>
/// Deterministic context assembler. Applies strict priority hierarchy:
/// decisions (boundaries) > core_context memories > high-importance learnings > session focus.
/// Memory is scoped to the target agent; cross-team tagged memories cross agent boundaries.
/// </summary>
public sealed class MemoryContextCompiler(MemoryDbContext db, IConfiguration? configuration = null)
{
    private const int DefaultMemoryLimit = 20;
    private const int DefaultMaxTokens = 4000;
    private const int ApproxCharsPerToken = 4;

    /// <summary>
    /// Compiles a structured context block for the given project + agent.
    /// Returns null if no context exists (empty project or no data yet).
    /// </summary>
    public async Task<string?> CompileAsync(
        string projectId, string agentName, CancellationToken ct = default)
    {
        return await CompileAsync(
            projectId,
            agentName,
            maxItems: null,
            maxTokens: null,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Compiles context using caller-provided memory item and token budget overrides. The token
    /// budget is approximate (4 chars/token) and applies to selected memory items.
    /// </summary>
    public async Task<string?> CompileAsync(
        string projectId,
        string agentName,
        int? maxItems,
        int? maxTokens,
        CancellationToken ct = default)
    {
        var memoryLimit = ResolvePositive(maxItems)
            ?? ResolvePositive(configuration?.GetValue<int?>("MemoryContext:MaxItems"))
            ?? ResolvePositive(configuration?.GetValue<int?>("Memory:ContextMaxItems"))
            ?? DefaultMemoryLimit;
        var maxTokenBudget = ResolvePositive(maxTokens)
            ?? ResolvePositive(configuration?.GetValue<int?>("MemoryContext:MaxTokens"))
            ?? ResolvePositive(configuration?.GetValue<int?>("Memory:ContextMaxTokens"))
            ?? DefaultMaxTokens;
        var maxChars = maxTokenBudget * ApproxCharsPerToken;

        // Layer 1: active architectural + scope decisions (team-wide boundaries)
        // Note: SQLite does not support DateTimeOffset in ORDER BY — sort client-side.
        var decisions = (await db.Decisions
            .Where(d => d.ProjectId == projectId
                     && d.Status == "active"
                     && (d.Type == "architectural" || d.Type == "scope"))
            .ToListAsync(ct))
            .OrderBy(d => d.CreatedAt)
            .ToList();

        // Layer 2: agent core_context memories
        var coreMemories = (await db.AgentMemory
            .Where(m => m.ProjectId == projectId
                     && m.AgentName == agentName
                     && m.Type == "core_context")
            .ToListAsync(ct))
            .OrderBy(m => m.CreatedAt)
            .ToList();

        // Layer 3: high-importance learnings/patterns for this agent
        //          + cross-team tagged memories from other agents
        var learnings = (await db.AgentMemory
            .Where(m => m.ProjectId == projectId
                     && m.Importance == "high"
                     && (m.AgentName == agentName || (m.Tags != null && m.Tags.Contains(",cross-team,")))
                     && (m.Type == "learning" || m.Type == "pattern"))
            .ToListAsync(ct))
            .ToList();

        var selectedMemories = SelectMemories(coreMemories, learnings, agentName, memoryLimit, maxChars);

        // Layer 4: current open session
        var session = (await db.SessionContexts
            .Where(s => s.ProjectId == projectId && s.EndedAt == null)
            .ToListAsync(ct))
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefault();

        if (!decisions.Any() && !selectedMemories.Any() && session is null)
            return null;

        var sb = new StringBuilder();

        AppendDecisionsBlock(sb, decisions);

        if (selectedMemories.Count > 0)
        {
            sb.AppendLine("\n## Memory");
            foreach (var m in selectedMemories)
                sb.AppendLine($"- [{m.Label}] {m.Memory.Content}");
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

    private static IReadOnlyList<(AgentMemory Memory, string Label)> SelectMemories(
        IEnumerable<AgentMemory> coreMemories,
        IEnumerable<AgentMemory> learnings,
        string agentName,
        int maxItems,
        int maxChars)
    {
        var candidates = coreMemories
            .Select(m => (Memory: m, Label: "core"))
            .Concat(learnings.Select(m => (
                Memory: m,
                Label: m.AgentName == agentName ? m.Type : $"{m.Type} from {m.AgentName}")))
            .OrderByDescending(m => ImportanceScore(m.Memory.Importance))
            .ThenByDescending(m => m.Memory.CreatedAt)
            .ToList();

        var selected = new List<(AgentMemory Memory, string Label)>();
        var usedChars = 0;
        foreach (var candidate in candidates)
        {
            if (selected.Count >= maxItems)
                break;

            var lineChars = candidate.Label.Length + candidate.Memory.Content.Length + 6;
            if (usedChars + lineChars > maxChars)
                break;

            selected.Add(candidate);
            usedChars += lineChars;
        }

        return selected;
    }

    private static int ImportanceScore(string? importance) => importance?.ToLowerInvariant() switch
    {
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0,
    };

    private static int? ResolvePositive(int? value) => value is > 0 ? value.Value : null;

    /// <summary>
    /// Compiles ONLY the active architectural + scope decisions block (the "## Boundaries and
    /// Decisions" section) for a project. Used for coordinator CHILD worker prompts, which must
    /// receive team-wide non-negotiable boundaries but deliberately NOT the full memory stack
    /// (core_context/learnings/session) — that stack duplicated the charter and bloated child
    /// prompts (Defect C). Returns null if there are no active decisions.
    /// </summary>
    public async Task<string?> CompileDecisionsAsync(string projectId, CancellationToken ct = default)
    {
        // Note: SQLite does not support DateTimeOffset in ORDER BY — sort client-side.
        var decisions = (await db.Decisions
            .Where(d => d.ProjectId == projectId
                     && d.Status == "active"
                     && (d.Type == "architectural" || d.Type == "scope"))
            .ToListAsync(ct))
            .OrderBy(d => d.CreatedAt)
            .ToList();

        if (decisions.Count == 0)
            return null;

        var sb = new StringBuilder();
        AppendDecisionsBlock(sb, decisions);
        return sb.ToString();
    }

    private static void AppendDecisionsBlock(StringBuilder sb, IReadOnlyList<Decision> decisions)
    {
        if (decisions.Count == 0)
            return;

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
}
