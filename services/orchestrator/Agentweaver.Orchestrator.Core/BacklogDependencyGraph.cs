using System.Collections.Immutable;

namespace Agentweaver.Orchestrator.Core;

public sealed class BacklogDependencyGraph
{
    private readonly ImmutableDictionary<string, BacklogTaskSnapshot> _tasks;
    private readonly ImmutableHashSet<BacklogDependencyEdge> _dependencies;
    private readonly ImmutableDictionary<string, ImmutableArray<string>> _prerequisitesByTask;

    private BacklogDependencyGraph(
        string projectId,
        long revision,
        ImmutableDictionary<string, BacklogTaskSnapshot> tasks,
        ImmutableHashSet<BacklogDependencyEdge> dependencies,
        ImmutableDictionary<string, ImmutableArray<string>> prerequisitesByTask)
    {
        ProjectId = projectId;
        Revision = revision;
        _tasks = tasks;
        _dependencies = dependencies;
        _prerequisitesByTask = prerequisitesByTask;
    }

    public string ProjectId { get; }
    public long Revision { get; }

    public ImmutableArray<BacklogTaskSnapshot> Tasks =>
        [.. _tasks.Values.OrderBy(task => task.Reference.TaskId, StringComparer.Ordinal)];

    public ImmutableArray<BacklogDependencyEdge> Dependencies =>
        [.. _dependencies.OrderBy(edge => edge.Task.TaskId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Prerequisite.TaskId, StringComparer.Ordinal)];

    public BacklogDependencyGraphSnapshot Snapshot =>
        new(ProjectId, Revision, Tasks, Dependencies);

    public BacklogCoreResult<BacklogTaskEdit> AddTask(
        BacklogTaskReference? taskReference,
        long expectedGraphRevision)
    {
        var issues = ImmutableArray.CreateBuilder<BacklogIssue>();
        if (expectedGraphRevision < 0)
            Add(issues, BacklogIssueCode.InvalidGraphRevision, "expectedGraphRevision",
                "Expected graph revision must be zero or greater.");
        else if (expectedGraphRevision != Revision)
            Add(issues, BacklogIssueCode.StaleGraphRevision, "expectedGraphRevision",
                $"Expected graph revision {expectedGraphRevision} does not match current revision {Revision}.");

        if (!IsValidReference(taskReference))
            Add(issues, BacklogIssueCode.InvalidTaskReference, "task",
                "Task reference must have stable project and task identifiers.");
        else if (!string.Equals(taskReference!.ProjectId, ProjectId, StringComparison.Ordinal))
            Add(issues, BacklogIssueCode.CrossProjectDependency, "task.projectId",
                "A task may be added only to the graph project.");
        else if (_tasks.ContainsKey(taskReference.TaskId))
            Add(issues, BacklogIssueCode.DuplicateTask, "task.taskId",
                $"Task '{taskReference.TaskId}' already exists in the graph.");

        if (issues.Count > 0)
            return BacklogCoreResult<BacklogTaskEdit>.Failure(issues.ToImmutable());
        if (Revision == long.MaxValue)
            return TaskFailure(BacklogIssueCode.GraphRevisionExhausted, "graph.revision",
                "The dependency graph revision cannot be incremented.");

        var task = new BacklogTaskSnapshot(taskReference!, 0, BacklogTaskState.Backlog);
        var tasks = _tasks.Add(taskReference!.TaskId, task);
        var graph = new BacklogDependencyGraph(
            ProjectId, Revision + 1, tasks, _dependencies, _prerequisitesByTask);
        return BacklogCoreResult<BacklogTaskEdit>.Success(
            new BacklogTaskEdit(graph, Changed: true));
    }

    public BacklogCoreResult<BacklogTaskEdit> SetTaskState(
        BacklogTaskReference? taskReference,
        BacklogTaskState state,
        long expectedGraphRevision,
        long expectedTaskRevision)
    {
        if (!TryValidateTaskMutation(taskReference, expectedGraphRevision, expectedTaskRevision,
                out var task, out var issues))
            return BacklogCoreResult<BacklogTaskEdit>.Failure(issues);
        if (!Enum.IsDefined(state))
            return TaskFailure(BacklogIssueCode.InvalidTaskState, "task.state",
                "Task state is not supported.");
        if (state == BacklogTaskState.Claimed)
            return TaskFailure(BacklogIssueCode.TaskNotEditable, "task.state",
                "Claimed state can be set only by an atomic claim.");
        if (task!.State == state)
            return BacklogCoreResult<BacklogTaskEdit>.Success(
                new BacklogTaskEdit(this, Changed: false));
        if (!CanEditDependencies(task))
            return TaskFailure(BacklogIssueCode.TaskNotEditable, "task",
                "Task state can be changed only while the task is unclaimed, unarchived, and not held by an automation invocation.");
        if (task.Revision == long.MaxValue)
            return TaskFailure(BacklogIssueCode.TaskRevisionExhausted, "task.revision",
                "The task revision cannot be incremented.");
        if (Revision == long.MaxValue)
            return TaskFailure(BacklogIssueCode.GraphRevisionExhausted, "graph.revision",
                "The dependency graph revision cannot be incremented.");

        var updatedTask = task with { Revision = task.Revision + 1, State = state };
        var graph = new BacklogDependencyGraph(
            ProjectId,
            Revision + 1,
            _tasks.SetItem(taskReference!.TaskId, updatedTask),
            _dependencies,
            _prerequisitesByTask);
        return BacklogCoreResult<BacklogTaskEdit>.Success(
            new BacklogTaskEdit(graph, Changed: true));
    }

    public BacklogCoreResult<BacklogTaskClaimEdit> ClaimTask(
        BacklogTaskReference? taskReference,
        long expectedGraphRevision,
        long expectedTaskRevision,
        ImmutableArray<BacklogPrerequisiteExecutionSnapshot> prerequisites)
    {
        var readiness = BacklogReadinessEvaluator.Evaluate(
            this, taskReference, expectedGraphRevision, expectedTaskRevision, prerequisites);
        if (!readiness.IsSuccess)
            return BacklogCoreResult<BacklogTaskClaimEdit>.Failure(readiness.Issues);
        if (!readiness.Value!.IsReady)
            return BacklogCoreResult<BacklogTaskClaimEdit>.Failure(readiness.Value.Blockers);
        if (expectedTaskRevision == long.MaxValue)
            return BacklogCoreResult<BacklogTaskClaimEdit>.Failure(
            [
                new BacklogIssue(
                    BacklogIssueCode.TaskRevisionExhausted,
                    "task.revision",
                    "The task revision cannot be incremented.")
            ]);
        if (Revision == long.MaxValue)
            return BacklogCoreResult<BacklogTaskClaimEdit>.Failure(
            [
                new BacklogIssue(
                    BacklogIssueCode.GraphRevisionExhausted,
                    "graph.revision",
                    "The dependency graph revision cannot be incremented.")
            ]);

        var taskId = taskReference!.TaskId;
        var claimedTask = _tasks[taskId] with
        {
            Revision = expectedTaskRevision + 1,
            State = BacklogTaskState.Claimed
        };
        var graph = new BacklogDependencyGraph(
            ProjectId,
            Revision + 1,
            _tasks.SetItem(taskId, claimedTask),
            _dependencies,
            _prerequisitesByTask);
        return BacklogCoreResult<BacklogTaskClaimEdit>.Success(
            new BacklogTaskClaimEdit(graph, readiness.Value));
    }

    public BacklogCoreResult<BacklogTaskEdit> ArchiveTask(
        BacklogTaskReference? taskReference,
        long expectedGraphRevision,
        long expectedTaskRevision)
    {
        if (!TryValidateTaskMutation(taskReference, expectedGraphRevision, expectedTaskRevision,
                out var task, out var issues))
            return BacklogCoreResult<BacklogTaskEdit>.Failure(issues);
        if (task!.IsArchived)
            return BacklogCoreResult<BacklogTaskEdit>.Success(
                new BacklogTaskEdit(this, Changed: false));
        if (!CanEditDependencies(task))
            return TaskFailure(BacklogIssueCode.TaskNotEditable, "task",
                "A task can be archived only while it is unclaimed and not held by an automation invocation.");
        if (task.Revision == long.MaxValue)
            return TaskFailure(BacklogIssueCode.TaskRevisionExhausted, "task.revision",
                "The task revision cannot be incremented.");
        if (Revision == long.MaxValue)
            return TaskFailure(BacklogIssueCode.GraphRevisionExhausted, "graph.revision",
                "The dependency graph revision cannot be incremented.");

        var archivedTask = task with { Revision = task.Revision + 1, IsArchived = true };
        var graph = new BacklogDependencyGraph(
            ProjectId,
            Revision + 1,
            _tasks.SetItem(taskReference!.TaskId, archivedTask),
            _dependencies,
            _prerequisitesByTask);
        return BacklogCoreResult<BacklogTaskEdit>.Success(
            new BacklogTaskEdit(graph, Changed: true));
    }

    public static BacklogCoreResult<BacklogDependencyGraph> Create(
        BacklogDependencyGraphSnapshot? snapshot)
    {
        var issues = ImmutableArray.CreateBuilder<BacklogIssue>();
        if (snapshot is null)
        {
            Add(issues, BacklogIssueCode.InvalidProjectId, "graph",
                "A project-scoped dependency graph snapshot is required.");
            return BacklogCoreResult<BacklogDependencyGraph>.Failure(issues.ToImmutable());
        }

        if (!WorkflowValidationSupport.IsStableId(snapshot.ProjectId))
            Add(issues, BacklogIssueCode.InvalidProjectId, "graph.projectId",
                "Project ID must be a stable, non-empty identifier.");
        if (snapshot.Revision < 0)
            Add(issues, BacklogIssueCode.InvalidGraphRevision, "graph.revision",
                "Graph revision must be zero or greater.");
        if (snapshot.Tasks.IsDefault)
            Add(issues, BacklogIssueCode.InvalidTaskCollection, "graph.tasks",
                "The task collection must be initialized.");
        if (snapshot.Dependencies.IsDefault)
            Add(issues, BacklogIssueCode.InvalidDependencyCollection, "graph.dependencies",
                "The dependency collection must be initialized.");

        var tasks = ImmutableDictionary.CreateBuilder<string, BacklogTaskSnapshot>(
            StringComparer.Ordinal);
        if (!snapshot.Tasks.IsDefault)
        {
            for (var index = 0; index < snapshot.Tasks.Length; index++)
            {
                var task = snapshot.Tasks[index];
                var path = $"graph.tasks[{index}]";
                if (task is null || !IsValidReference(task.Reference))
                {
                    Add(issues, BacklogIssueCode.InvalidTaskReference, path,
                        "Every task must have a stable project and task identifier.");
                    continue;
                }
                if (!string.Equals(task.Reference.ProjectId, snapshot.ProjectId, StringComparison.Ordinal))
                    Add(issues, BacklogIssueCode.CrossProjectDependency, path + ".projectId",
                        "Every task in a dependency graph must belong to the graph project.");
                if (task.Revision < 0)
                    Add(issues, BacklogIssueCode.InvalidTaskRevision, path + ".revision",
                        "Task revision must be zero or greater.");
                if (!Enum.IsDefined(task.State))
                    Add(issues, BacklogIssueCode.InvalidTaskState, path + ".state",
                        "Task state is not supported.");
                if (!tasks.TryAdd(task.Reference.TaskId, task))
                    Add(issues, BacklogIssueCode.DuplicateTask, path + ".taskId",
                        $"Task '{task.Reference.TaskId}' appears more than once.");
            }
        }

        var dependencies = ImmutableHashSet.CreateBuilder<BacklogDependencyEdge>();
        if (!snapshot.Dependencies.IsDefault)
        {
            for (var index = 0; index < snapshot.Dependencies.Length; index++)
            {
                var edge = snapshot.Dependencies[index];
                var path = $"graph.dependencies[{index}]";
                if (edge is null || !IsValidReference(edge.Task) ||
                    !IsValidReference(edge.Prerequisite))
                {
                    Add(issues, BacklogIssueCode.InvalidTaskReference, path,
                        "Every dependency endpoint must have a stable project and task identifier.");
                    continue;
                }
                if (!string.Equals(edge.Task.ProjectId, snapshot.ProjectId, StringComparison.Ordinal) ||
                    !string.Equals(edge.Prerequisite.ProjectId, snapshot.ProjectId, StringComparison.Ordinal))
                {
                    Add(issues, BacklogIssueCode.CrossProjectDependency, path,
                        "A task may depend only on a task in the same project.");
                    continue;
                }
                if (string.Equals(edge.Task.TaskId, edge.Prerequisite.TaskId, StringComparison.Ordinal))
                {
                    Add(issues, BacklogIssueCode.SelfDependency, path,
                        "A task cannot depend on itself.");
                    continue;
                }
                if (!tasks.ContainsKey(edge.Task.TaskId) ||
                    !tasks.ContainsKey(edge.Prerequisite.TaskId))
                {
                    Add(issues, BacklogIssueCode.MissingTask, path,
                        "Both dependency endpoints must exist in the graph snapshot.");
                    continue;
                }
                if (!dependencies.Add(edge))
                    Add(issues, BacklogIssueCode.DuplicateDependency, path,
                        "The dependency edge is duplicated.");
            }
        }

        if (HasCycle(tasks.Keys, dependencies))
            Add(issues, BacklogIssueCode.DependencyCycle, "graph.dependencies",
                "Backlog dependencies must form a directed acyclic graph.");

        if (issues.Count > 0)
            return BacklogCoreResult<BacklogDependencyGraph>.Failure(issues.ToImmutable());

        var immutableDependencies = dependencies.ToImmutable();
        return BacklogCoreResult<BacklogDependencyGraph>.Success(
            new BacklogDependencyGraph(snapshot.ProjectId, snapshot.Revision,
                tasks.ToImmutable(), immutableDependencies, CreatePrerequisiteMap(immutableDependencies)));
    }

    public BacklogCoreResult<BacklogDependencyGraphEdit> AddDependency(
        BacklogTaskReference task,
        BacklogTaskReference prerequisite,
        long expectedGraphRevision)
    {
        if (!TryValidateEdit(task, prerequisite, expectedGraphRevision, out var dependentTask,
                out var prerequisiteTask, out var issues))
            return BacklogCoreResult<BacklogDependencyGraphEdit>.Failure(issues);

        var edge = new BacklogDependencyEdge(task, prerequisite);
        if (_dependencies.Contains(edge))
            return BacklogCoreResult<BacklogDependencyGraphEdit>.Success(
                new BacklogDependencyGraphEdit(this, Changed: false));

        if (!CanEditDependencies(dependentTask!))
            return Failure(BacklogIssueCode.TaskNotEditable, "dependency.task",
                "Dependencies can be changed only while the task is unclaimed, unarchived, and not held by an automation invocation.");
        if (prerequisiteTask!.IsArchived || prerequisiteTask.IsAutomationInvocationPending)
            return Failure(BacklogIssueCode.PrerequisiteUnavailable, "dependency.prerequisite",
                "An archived or provisional task cannot be used as a prerequisite.");
        if (CanReach(prerequisite.TaskId, task.TaskId))
            return Failure(BacklogIssueCode.DependencyCycle, "dependency",
                "The dependency would create a cycle.");
        if (Revision == long.MaxValue)
            return Failure(BacklogIssueCode.GraphRevisionExhausted, "graph.revision",
                "The dependency graph revision cannot be incremented.");

        var dependencies = _dependencies.Add(edge);
        var changed = new BacklogDependencyGraph(
            ProjectId, Revision + 1, _tasks, dependencies, CreatePrerequisiteMap(dependencies));
        return BacklogCoreResult<BacklogDependencyGraphEdit>.Success(
            new BacklogDependencyGraphEdit(changed, Changed: true));
    }

    public BacklogCoreResult<BacklogDependencyGraphEdit> RemoveDependency(
        BacklogTaskReference task,
        BacklogTaskReference prerequisite,
        long expectedGraphRevision)
    {
        if (!TryValidateEdit(task, prerequisite, expectedGraphRevision, out var dependentTask,
                out _, out var issues))
            return BacklogCoreResult<BacklogDependencyGraphEdit>.Failure(issues);

        var edge = new BacklogDependencyEdge(task, prerequisite);
        if (!_dependencies.Contains(edge))
            return BacklogCoreResult<BacklogDependencyGraphEdit>.Success(
                new BacklogDependencyGraphEdit(this, Changed: false));

        if (!CanEditDependencies(dependentTask!))
            return Failure(BacklogIssueCode.TaskNotEditable, "dependency.task",
                "Dependencies can be changed only while the task is unclaimed, unarchived, and not held by an automation invocation.");
        if (Revision == long.MaxValue)
            return Failure(BacklogIssueCode.GraphRevisionExhausted, "graph.revision",
                "The dependency graph revision cannot be incremented.");

        var dependencies = _dependencies.Remove(edge);
        var changed = new BacklogDependencyGraph(
            ProjectId, Revision + 1, _tasks, dependencies, CreatePrerequisiteMap(dependencies));
        return BacklogCoreResult<BacklogDependencyGraphEdit>.Success(
            new BacklogDependencyGraphEdit(changed, Changed: true));
    }

    internal bool TryGetTask(string taskId, out BacklogTaskSnapshot? task) =>
        _tasks.TryGetValue(taskId, out task);

    internal ImmutableArray<BacklogDependencyEdge> DependenciesFor(string taskId) =>
        _prerequisitesByTask.TryGetValue(taskId, out var prerequisites)
            ? [.. prerequisites.Select(prerequisiteId => new BacklogDependencyEdge(
                    new BacklogTaskReference(ProjectId, taskId),
                    new BacklogTaskReference(ProjectId, prerequisiteId)))
                .OrderBy(edge => edge.Prerequisite.TaskId, StringComparer.Ordinal)]
            : [];

    internal static bool IsValidReference(BacklogTaskReference? reference) =>
        reference is not null &&
        WorkflowValidationSupport.IsStableId(reference.ProjectId) &&
        WorkflowValidationSupport.IsStableId(reference.TaskId);

    private bool TryValidateEdit(
        BacklogTaskReference? task,
        BacklogTaskReference? prerequisite,
        long expectedGraphRevision,
        out BacklogTaskSnapshot? dependentTask,
        out BacklogTaskSnapshot? prerequisiteTask,
        out ImmutableArray<BacklogIssue> issues)
    {
        dependentTask = null;
        prerequisiteTask = null;
        var builder = ImmutableArray.CreateBuilder<BacklogIssue>();
        if (expectedGraphRevision < 0)
            Add(builder, BacklogIssueCode.InvalidGraphRevision, "expectedGraphRevision",
                "Expected graph revision must be zero or greater.");
        else if (expectedGraphRevision != Revision)
            Add(builder, BacklogIssueCode.StaleGraphRevision, "expectedGraphRevision",
                $"Expected graph revision {expectedGraphRevision} does not match current revision {Revision}.");

        if (!IsValidReference(task) || !IsValidReference(prerequisite))
            Add(builder, BacklogIssueCode.InvalidTaskReference, "dependency",
                "Both dependency endpoints must have stable project and task identifiers.");
        if (builder.Count == 0)
        {
            if (!string.Equals(task!.ProjectId, ProjectId, StringComparison.Ordinal) ||
                !string.Equals(prerequisite!.ProjectId, ProjectId, StringComparison.Ordinal))
                Add(builder, BacklogIssueCode.CrossProjectDependency, "dependency",
                    "A task may depend only on a task in the same project.");
            else if (string.Equals(task.TaskId, prerequisite.TaskId, StringComparison.Ordinal))
                Add(builder, BacklogIssueCode.SelfDependency, "dependency",
                    "A task cannot depend on itself.");
            else if (!_tasks.TryGetValue(task.TaskId, out dependentTask) ||
                     !_tasks.TryGetValue(prerequisite.TaskId, out prerequisiteTask))
                Add(builder, BacklogIssueCode.MissingTask, "dependency",
                    "Both dependency endpoints must exist in the graph snapshot.");
        }

        issues = builder.ToImmutable();
        return issues.IsEmpty;
    }

    private bool TryValidateTaskMutation(
        BacklogTaskReference? taskReference,
        long expectedGraphRevision,
        long expectedTaskRevision,
        out BacklogTaskSnapshot? task,
        out ImmutableArray<BacklogIssue> issues)
    {
        task = null;
        var builder = ImmutableArray.CreateBuilder<BacklogIssue>();
        if (expectedGraphRevision < 0)
            Add(builder, BacklogIssueCode.InvalidGraphRevision, "expectedGraphRevision",
                "Expected graph revision must be zero or greater.");
        else if (expectedGraphRevision != Revision)
            Add(builder, BacklogIssueCode.StaleGraphRevision, "expectedGraphRevision",
                $"Expected graph revision {expectedGraphRevision} does not match current revision {Revision}.");
        if (expectedTaskRevision < 0)
            Add(builder, BacklogIssueCode.InvalidTaskRevision, "expectedTaskRevision",
                "Expected task revision must be zero or greater.");
        if (!IsValidReference(taskReference))
            Add(builder, BacklogIssueCode.InvalidTaskReference, "task",
                "Task reference must have stable project and task identifiers.");
        else if (!string.Equals(taskReference!.ProjectId, ProjectId, StringComparison.Ordinal))
            Add(builder, BacklogIssueCode.CrossProjectDependency, "task.projectId",
                "Task mutations must target the graph project.");
        else if (!_tasks.TryGetValue(taskReference.TaskId, out task))
            Add(builder, BacklogIssueCode.MissingTask, "task.taskId",
                $"Task '{taskReference.TaskId}' does not exist in the graph.");
        if (task is not null && expectedTaskRevision >= 0 && task.Revision != expectedTaskRevision)
            Add(builder, BacklogIssueCode.StaleTaskRevision, "expectedTaskRevision",
                $"Expected task revision {expectedTaskRevision} does not match current revision {task.Revision}.");

        issues = builder.ToImmutable();
        return issues.IsEmpty;
    }

    private bool CanReach(string start, string target)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
                continue;
            if (string.Equals(current, target, StringComparison.Ordinal))
                return true;
            if (_prerequisitesByTask.TryGetValue(current, out var prerequisites))
            {
                foreach (var prerequisite in prerequisites)
                    pending.Push(prerequisite);
            }
        }
        return false;
    }

    private static ImmutableDictionary<string, ImmutableArray<string>> CreatePrerequisiteMap(
        IEnumerable<BacklogDependencyEdge> dependencies) =>
        dependencies
            .GroupBy(edge => edge.Task.TaskId, StringComparer.Ordinal)
            .ToImmutableDictionary(
                group => group.Key,
                group => group.Select(edge => edge.Prerequisite.TaskId)
                    .Order(StringComparer.Ordinal)
                    .ToImmutableArray(),
                StringComparer.Ordinal);

    private static bool HasCycle(
        IEnumerable<string> taskIds,
        IEnumerable<BacklogDependencyEdge> dependencies)
    {
        var remainingDependencies = taskIds.ToDictionary(
            taskId => taskId, _ => 0, StringComparer.Ordinal);
        var dependentsByPrerequisite = taskIds.ToDictionary(
            taskId => taskId, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in dependencies)
        {
            remainingDependencies[edge.Task.TaskId]++;
            dependentsByPrerequisite[edge.Prerequisite.TaskId].Add(edge.Task.TaskId);
        }

        var ready = new Queue<string>(
            remainingDependencies.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;
        while (ready.TryDequeue(out var taskId))
        {
            visited++;
            foreach (var dependent in dependentsByPrerequisite[taskId])
            {
                if (--remainingDependencies[dependent] == 0)
                    ready.Enqueue(dependent);
            }
        }
        return visited != remainingDependencies.Count;
    }

    private static bool CanEditDependencies(BacklogTaskSnapshot task) =>
        !task.IsArchived &&
        !task.IsAutomationInvocationPending &&
        task.State is BacklogTaskState.Backlog or BacklogTaskState.Ready;

    private static BacklogCoreResult<BacklogDependencyGraphEdit> Failure(
        BacklogIssueCode code,
        string path,
        string message) =>
        BacklogCoreResult<BacklogDependencyGraphEdit>.Failure(
            [new BacklogIssue(code, path, message)]);

    private static BacklogCoreResult<BacklogTaskEdit> TaskFailure(
        BacklogIssueCode code,
        string path,
        string message) =>
        BacklogCoreResult<BacklogTaskEdit>.Failure(
            [new BacklogIssue(code, path, message)]);

    private static void Add(
        ImmutableArray<BacklogIssue>.Builder issues,
        BacklogIssueCode code,
        string path,
        string message) =>
        issues.Add(new BacklogIssue(code, path, message));
}
