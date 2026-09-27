using Agentweaver.Domain;

namespace Agentweaver.Api.Backlog;

internal static class BacklogDependencyGraph
{
    public static (BacklogDependencyEditResult Result, HashSet<BacklogTaskId> Before, HashSet<BacklogTaskId> After)
        Project(long revision, BacklogDependencyEdit edit,
            IReadOnlyDictionary<BacklogTaskId, bool> tasks,
            IReadOnlyCollection<BacklogTaskDependency> dependencies)
    {
        if (!tasks.TryGetValue(edit.TaskId, out var editable))
            throw new BacklogDependencyEditException("task_not_found");
        if (!editable)
            throw new BacklogDependencyEditException("task_claimed_or_archived");

        var before = dependencies.Where(d => d.TaskId == edit.TaskId)
            .Select(d => d.DependsOnTaskId).ToHashSet();
        var after = edit.Replace is null ? before.ToHashSet() : edit.Replace.ToHashSet();
        after.ExceptWith(edit.Remove);
        after.UnionWith(edit.Add);
        if (after.Contains(edit.TaskId))
            throw new BacklogDependencyEditException("self_dependency");
        if (after.Any(id => !tasks.ContainsKey(id)))
            throw new BacklogDependencyEditException("prerequisite_not_in_project");
        var graph = dependencies.Where(d => d.TaskId != edit.TaskId)
            .GroupBy(d => d.TaskId)
            .ToDictionary(g => g.Key, g => g.Select(d => d.DependsOnTaskId).ToHashSet());
        graph[edit.TaskId] = after;
        foreach (var prerequisite in after)
        {
            var visited = new HashSet<BacklogTaskId>();
            var pending = new Stack<BacklogTaskId>();
            pending.Push(prerequisite);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current == edit.TaskId)
                    throw new BacklogDependencyEditException("dependency_cycle");
                if (visited.Add(current) && graph.TryGetValue(current, out var next))
                    foreach (var id in next)
                        pending.Push(id);
            }
        }

        var affected = new HashSet<BacklogTaskId> { edit.TaskId };
        var outgoing = new Dictionary<BacklogTaskId, List<BacklogTaskId>>();
        foreach (var edge in dependencies)
        {
            if (!outgoing.TryGetValue(edge.DependsOnTaskId, out var dependents))
                outgoing[edge.DependsOnTaskId] = dependents = [];
            dependents.Add(edge.TaskId);
        }
        var frontier = new Queue<BacklogTaskId>();
        frontier.Enqueue(edit.TaskId);
        while (frontier.Count > 0)
            if (outgoing.TryGetValue(frontier.Dequeue(), out var dependents))
                foreach (var dependent in dependents)
                    if (affected.Add(dependent))
                        frontier.Enqueue(dependent);

        var changed = !before.SetEquals(after);
        return (new BacklogDependencyEditResult(
            revision + (changed ? 1 : 0),
            after.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray(),
            affected.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray(),
            changed), before, after);
    }
}
