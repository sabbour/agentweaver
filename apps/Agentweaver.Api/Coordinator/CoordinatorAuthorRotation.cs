using Agentweaver.Squad.Squad;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// UNIFIED AUTONOMOUS STEERING (Req-2, Reviewer Rejection Lockout) — the domain context of a subtask
/// whose current author has just been rejected and must be rotated to a DIFFERENT eligible agent.
/// </summary>
public sealed record RotationSubtaskContext(
    int SubtaskId,
    string CurrentAuthor,
    string Title,
    string Scope,
    string Phase);

/// <summary>The rotated author selected for a subtask's next revision: name + model + optional charter.</summary>
public sealed record RotationChoice(string AgentName, string SelectedModelId, string? AgentCharter);

/// <summary>
/// UNIFIED AUTONOMOUS STEERING (Req-2, change #5) — selects a DIFFERENT eligible agent to own a
/// subtask's revision after its current author was locked out by a context-complete reviewer rejection
/// (Strict Lockout, squad.agent.md §"Reviewer Rejection Lockout Semantics"). Returns <c>null</c> when
/// NO domain-eligible agent remains outside the locked-out set — a DEADLOCK that the coordinator routes
/// to human-review escalation (protocol step 7), never a rotation to an unrelated agent.
/// </summary>
public interface IAssemblyAuthorRotationSelector
{
    RotationChoice? SelectRotationAuthor(
        string repositoryPath,
        RotationSubtaskContext subtask,
        IReadOnlySet<string> lockedOut);
}

/// <summary>
/// Default <see cref="IAssemblyAuthorRotationSelector"/> — reads the SAME project team roster as the
/// orchestrator assignment path (<see cref="SquadReader.ReadTeam"/>), filters to dispatchable members
/// that are NOT locked out (and not the current author), and REQUIRES a positive domain-capability
/// score (change #5) so a single-eligible-agent domain correctly DEADLOCKS after the first rejection
/// instead of rotating to an unrelated agent. The best positive-scoring candidate wins.
/// </summary>
public sealed class SquadAuthorRotationSelector : IAssemblyAuthorRotationSelector
{
    public static readonly SquadAuthorRotationSelector Instance = new();

    public RotationChoice? SelectRotationAuthor(
        string repositoryPath,
        RotationSubtaskContext subtask,
        IReadOnlySet<string> lockedOut)
    {
        Agentweaver.Squad.Model.Team? team;
        try
        {
            team = new SquadReader(repositoryPath).ReadTeam();
        }
        catch
        {
            // A roster that cannot be read is treated as a deadlock: escalate to human review rather
            // than silently rotating to an unknown/unsafe author.
            return null;
        }
        if (team is null) return null;

        var domain = Tokenize($"{subtask.Title} {subtask.Scope}");

        RotationChoice? best = null;
        var bestScore = 0; // strictly-positive threshold — 0 is NOT eligible (change #5).
        foreach (var m in team.Members)
        {
            if (!CoordinatorRosterGuard.IsDispatchableMember(m)) continue;
            var name = m.Name;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (IsExcluded(name, subtask.CurrentAuthor, lockedOut)) continue;

            var role = m.Role;
            var haystack = Tokenize(string.Join(' ', new[] { role?.Id, role?.Title }
                .Concat(role?.Capabilities ?? [])
                .Concat(role?.Responsibilities ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s))!));

            var score = domain.Count(haystack.Contains) * 10;
            if (subtask.Phase == "validation"
                && (Contains(role?.Title, "review") || Contains(role?.Title, "qa") || Contains(role?.Title, "quality")))
                score += 15;
            if (subtask.Phase == "planning"
                && (Contains(role?.Title, "lead") || Contains(role?.Title, "architect")))
                score += 15;

            if (score > bestScore)
            {
                bestScore = score;
                best = new RotationChoice(name, role?.DefaultModel ?? string.Empty, AgentCharter: null);
            }
        }

        return best;
    }

    private static bool IsExcluded(string name, string currentAuthor, IReadOnlySet<string> lockedOut)
    {
        if (string.Equals(name, currentAuthor, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var l in lockedOut)
            if (string.Equals(name, l, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool Contains(string? field, string token) =>
        field is not null && field.ToLowerInvariant().Contains(token);

    private static HashSet<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return text.ToLowerInvariant()
            .Split([' ', '-', '_', ',', '.', '/', '\\', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .ToHashSet();
    }
}
