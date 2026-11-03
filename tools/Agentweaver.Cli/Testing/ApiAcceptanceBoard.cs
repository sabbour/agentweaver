using System.Text.Json.Nodes;

namespace Agentweaver.Cli;

internal sealed record BoardExecutionResult(string Status, string? Failure, JsonObject Fixture);

internal sealed class BoardFixtureState(string invocationId)
{
    public string InvocationId { get; } = invocationId;
    public string ProjectName { get; } = $"board-{invocationId.ToLowerInvariant()}";
    public string? ActorId { get; set; }
    public string? TenantId { get; set; }
    public string? Issuer { get; set; }
    public long? MembershipRevision { get; set; }
    public string? ProjectId { get; set; }
    public long? ProjectRevision { get; set; }
    public long? ProjectAcknowledgedRevision { get; set; }
    public string? OwnerAssignmentId { get; set; }
    public long? OwnerAssignmentRevision { get; set; }
    public List<string> AttemptedTaskIds { get; } = [];
    public Dictionary<string, long> OwnedTaskRevisions { get; } = new(StringComparer.Ordinal);
    public HashSet<string> RetainedArchivedTaskIds { get; } = new(StringComparer.Ordinal);
    public bool ProjectAcknowledged { get; set; }
    public bool OwnerAcknowledged { get; set; }
    public bool MutationInFlight { get; set; }
    public bool MutationAcknowledged { get; set; }
    public bool Indeterminate { get; set; }
    public JsonObject? Cleanup { get; set; }
}

internal static class ApiAcceptanceBoard
{
    private const string AdminScope = "projects.admin";
    private const string OrchestratorScope = "projects.orchestrator";

    public static async Task<BoardExecutionResult> RunAsync(
        AcceptanceManifest manifest,
        string invocationId,
        IGatewayClient adminApi,
        IGatewayClientFactory clients,
        ICliAuthentication authentication,
        InteractionPolicy interaction,
        List<JsonObject> steps,
        JsonObject observations,
        CancellationToken ct)
    {
        BoardFixtureState state = new(invocationId);
        string status = "blocked";
        string? failure = null;
        try
        {
            if (!HasOnlyScope(adminApi.Identity, AdminScope))
                throw new AcceptanceBlockedException("Board setup requires an unbound projects.admin identity.");

            JsonObject adminContext = await ValidateContextAsync(adminApi, manifest, null, null, ct).ConfigureAwait(false);
            state.ActorId = ApiAcceptanceSupport.GetString(adminContext, "actorId");
            state.TenantId = ApiAcceptanceSupport.GetString(adminContext, "tenantId");
            state.Issuer = ApiAcceptanceSupport.GetString(adminContext, "issuer");
            state.MembershipRevision = ReadLong(adminContext, "membershipRevision");
            string actorId = state.ActorId!;
            if (ApiAcceptanceSupport.GetString(manifest.Environment, "actorId") is { } plannedActor
                && plannedActor != actorId)
                throw new AcceptanceBlockedException("The supplied Board actorId does not match the live authorization context.");
            observations["actorId"] = actorId;
            observations["tenantId"] = ApiAcceptanceSupport.GetString(adminContext, "tenantId");
            await RequireCapabilitiesAsync(adminApi, manifest.Journey, ct).ConfigureAwait(false);
            await CreateOwnedProjectAsync(adminApi, state, ct).ConfigureAwait(false);

            if (manifest.Journey == "board-archived-denial")
            {
                await RunArchivedTaskDenialAsync(manifest, adminApi, state, steps, observations, ct).ConfigureAwait(false);
            }
            else
            {
                CliIdentity orchestratorIdentity = new(adminApi.Identity.Target,
                    ["openid", "api.read", "offline_access", OrchestratorScope]);
                IGatewayClient orchestratorApi = clients.Create(orchestratorIdentity);
                try
                {
                    _ = await authentication.GetBearerAsync(orchestratorIdentity, interaction, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    ApiAcceptanceSupport.AddStep(steps, "owner-denial", "blocked",
                        ("reason", JsonValue.Create("The same actor's unbound projects.orchestrator-only credential is unavailable.")));
                    throw new AcceptanceBlockedException("The Board owner-denial credential is unavailable; the acknowledged fixture will be cleaned up.");
                }

                await RunDeleteJourneyAsync(manifest, adminApi, orchestratorApi, state, steps, observations, ct)
                    .ConfigureAwait(false);
            }
            status = "passed";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = "failed";
            failure = "The Board acceptance run was cancelled.";
            throw;
        }
        catch (AcceptanceBlockedException error)
        {
            failure = ApiAcceptanceSupport.RedactText(error.Message);
            status = "blocked";
        }
        catch (Exception error)
        {
            failure = ApiAcceptanceSupport.RedactText(error.Message);
            status = "failed";
        }
        finally
        {
            if (state.MutationInFlight || state.MutationAcknowledged)
                state.Indeterminate = true;
            try
            {
                state.Cleanup = await CleanupAsync(adminApi, state, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                state.Indeterminate = true;
                state.Cleanup ??= new JsonObject
                {
                    ["state"] = "indeterminate",
                    ["reason"] = ApiAcceptanceSupport.RedactText(error.Message),
                };
            }

            observations["boardFixture"] = Summarize(state);
            if (state.Indeterminate || ApiAcceptanceSupport.GetString(state.Cleanup ?? [], "state") == "indeterminate")
            {
                status = "failed";
                failure = string.IsNullOrWhiteSpace(failure)
                    ? "Board fixture cleanup or a mutation receipt is indeterminate; reconcile this invocation before taking further action."
                    : $"{failure}; Board fixture cleanup or a mutation receipt is indeterminate; reconcile this invocation before taking further action.";
            }
        }

        JsonObject fixture = Summarize(state);
        fixture["coverage"] = manifest.Journey == "board-archived-denial"
            ? new JsonObject
            {
                ["featureStatus"] = status,
                ["fullP1Status"] = "not-evaluated",
                ["blocked"] = new JsonArray("automation-held", "claimed", "distinct-actor-denial", "revoked-role-revalidation"),
            }
            : new JsonObject
            {
                ["featureStatus"] = status,
                ["fullP1Status"] = "not-evaluated",
                ["deferred"] = new JsonArray("automation-held", "claimed", "archived-task-denial", "distinct-actor-denial", "revoked-role-revalidation", "UI-cancel"),
            };
        observations["boardFixture"] = fixture.DeepClone();
        return new(status, failure, fixture);
    }

    private static async Task RunArchivedTaskDenialAsync(
        AcceptanceManifest manifest,
        IGatewayClient adminApi,
        BoardFixtureState state,
        List<JsonObject> steps,
        JsonObject observations,
        CancellationToken ct)
    {
        string projectId = state.ProjectId!;
        await RequireOwnerAuthorityAsync(adminApi, manifest, state, "writeProjects", ct).ConfigureAwait(false);
        BoardGraph graph = await ReadGraphAsync(adminApi, projectId, state, ct).ConfigureAwait(false);
        RequireGraph(graph, projectId, []);

        string taskId = ApiAcceptanceSupport.StableUuid(state.InvocationId, "board-archived-task");
        graph = await CaptureAsync(adminApi, state, graph,
            new(taskId, new("Archived task guard", "Invocation-owned archived-task deletion guard.")), ct)
            .ConfigureAwait(false);
        BoardTask captured = FindTask(graph, taskId);
        GatewayResponse archivedResponse = await MutateAsync(state,
            () => BoardGateway.ArchiveTaskAsync(adminApi, projectId, taskId,
                new(graph.Revision, captured.Revision), ct)).ConfigureAwait(false);
        BoardGraph archived = ParseGraph(RequireSuccess(archivedResponse, "archiveProjectBacklogTask"), projectId);
        BoardTask archivedTask = FindTask(archived, taskId);
        if (archived.Revision != graph.Revision + 1
            || archived.Tasks.Count != 1
            || archived.Dependencies.Count != 0
            || archivedTask.Revision != captured.Revision + 1
            || !archivedTask.IsArchived
            || archivedTask.IsAutomationInvocationPending
            || archivedTask.State != captured.State)
            throw Indeterminate("archiveProjectBacklogTask did not return the exact invocation-owned archived task.");
        state.OwnedTaskRevisions[taskId] = archivedTask.Revision;
        state.RetainedArchivedTaskIds.Add(taskId);
        state.MutationAcknowledged = false;

        BoardGraph archivedReadback = await ReadGraphAsync(adminApi, projectId, state, ct).ConfigureAwait(false);
        RequireUnchanged(archived, archivedReadback);
        AddStep(steps, "archive-owned-task", 200, null);

        GatewayResponse rejectedDelete = await DeleteAsync(adminApi, state, projectId, taskId,
            new(archivedReadback.Revision, archivedTask.Revision), ct).ConfigureAwait(false);
        RequireIssue(rejectedDelete, 409, "taskNotEditable", "deleteProjectBacklogTask");
        BoardGraph afterRejectedDelete = await ReadGraphAsync(adminApi, projectId, state, ct).ConfigureAwait(false);
        RequireUnchanged(archivedReadback, afterRejectedDelete);
        AddStep(steps, "delete-archived-task-denied", 409, "taskNotEditable");

        observations["archivedTaskGuard"] = new JsonObject
        {
            ["projectId"] = projectId,
            ["taskId"] = taskId,
            ["archiveGraphRevision"] = archived.Revision,
            ["archiveTaskRevision"] = archivedTask.Revision,
            ["deleteStatus"] = rejectedDelete.Status,
            ["deleteIssueCode"] = "taskNotEditable",
            ["persistedGraphUnchanged"] = true,
        };
        AddBlockedStep(steps, "automation-held-denial",
            "No safe no-paid invocation-held fixture and independent cleanup path is available.");
        AddBlockedStep(steps, "claimed-task-denial",
            "No safe no-paid claim fixture and independent cleanup path is available.");
        AddBlockedStep(steps, "distinct-actor-denial",
            "A distinct authenticated actor is unavailable; same-actor authority cannot prove a 403.");
        AddBlockedStep(steps, "revoked-role-revalidation",
            "The available actor is TenantAdmin; a separate authenticated non-admin fixture actor is required to prove revoked-role denial.");
    }

    private static async Task CreateOwnedProjectAsync(
        IGatewayClient api,
        BoardFixtureState state,
        CancellationToken ct)
    {
        state.MutationInFlight = true;
        GatewayResponse response;
        try
        {
            response = await BoardGateway.CreateProjectAsync(api, new(state.ProjectName), ct).ConfigureAwait(false);
        }
        catch
        {
            state.Indeterminate = true;
            throw;
        }
        state.MutationInFlight = false;
        state.MutationAcknowledged = response.Status is >= 200 and < 300;
        if (response.Status != 201)
        {
            if (response.Status >= 500) state.Indeterminate = true;
            throw BoardFailure("createProject", response, [201]);
        }

        JsonObject project = RequireBody(response, "createProject");
        string projectId = ApiAcceptanceSupport.GetString(project, "projectId")
            ?? throw Indeterminate("Projects Config acknowledged project creation without a project ID.");
        long revision = ReadPositive(project, "revision", "project revision");
        long configurationRevision = ReadPositive(project, "configurationRevision", "project configuration revision");
        if (!IsProjectId(projectId)
            || ApiAcceptanceSupport.GetString(project, "name") != state.ProjectName
            || ApiAcceptanceSupport.GetString(project, "state") != "active")
            throw Indeterminate("Projects Config returned a mismatched owned project receipt.");

        state.ProjectId = projectId;
        state.ProjectRevision = revision;
        state.ProjectAcknowledgedRevision = revision;
        state.ProjectAcknowledged = true;
        state.MutationAcknowledged = false;

        JsonObject current = await ValidateContextAsync(api, null, state.ActorId, null, ct).ConfigureAwait(false);
        RequireSameIdentity(state, current);
        await ReadOwnedProjectAsync(api, state, ct).ConfigureAwait(false);

        state.Indeterminate = true;
        response = await BoardGateway.ListAssignmentsAsync(api, projectId, ct).ConfigureAwait(false);
        if (response.Status != 200)
        {
            throw BoardFailure("listProjectRoleAssignments", response, [200]);
        }

        JsonObject assignmentList = RequireBody(response, "listProjectRoleAssignments");
        JsonObject[] ownerAssignments = (assignmentList["assignments"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(item => ApiAcceptanceSupport.GetString(item, "principalId") == state.ActorId
                && ApiAcceptanceSupport.GetString(item, "role") == "owner")
            .ToArray();
        if (ApiAcceptanceSupport.GetString(assignmentList, "projectId") != projectId
            || ownerAssignments.Length != 1
            || ApiAcceptanceSupport.GetString(ownerAssignments[0], "createdBy") != state.ActorId)
        {
            throw Indeterminate("Projects Config did not return the exact creator Owner assignment receipt.");
        }

        JsonObject assignment = ownerAssignments[0];
        string assignmentId = ApiAcceptanceSupport.GetString(assignment, "assignmentId") ?? "";
        long assignmentRevision = ReadPositive(assignment, "revision", "owner assignment revision");
        if (!ApiAcceptanceSupport.IsUuid(assignmentId)
            || assignmentRevision != 1)
        {
            throw Indeterminate("Projects Config returned a mismatched owner assignment receipt.");
        }

        state.OwnerAssignmentId = assignmentId;
        state.OwnerAssignmentRevision = assignmentRevision;
        state.OwnerAcknowledged = true;
        state.Indeterminate = false;
        JsonObject authority = await ValidateContextAsync(api, null, state.ActorId, null, ct).ConfigureAwait(false);
        RequireSameIdentity(state, authority);
        RequireProjectAuthority(authority, projectId, "writeProjects");
        await ReadOwnedProjectAsync(api, state, ct).ConfigureAwait(false);
    }

    private static async Task RunDeleteJourneyAsync(
        AcceptanceManifest manifest,
        IGatewayClient adminApi,
        IGatewayClient orchestratorApi,
        BoardFixtureState state,
        List<JsonObject> steps,
        JsonObject observations,
        CancellationToken ct)
    {
        string projectId = state.ProjectId!;
        await RequireOwnerAuthorityAsync(adminApi, manifest, state, "writeProjects", ct).ConfigureAwait(false);
        BoardGraph graph = await ReadGraphAsync(adminApi, projectId, state, ct).ConfigureAwait(false);
        RequireGraph(graph, projectId, []);

        string taskA = ApiAcceptanceSupport.StableUuid(state.InvocationId, "board-task-a");
        string taskB = ApiAcceptanceSupport.StableUuid(state.InvocationId, "board-task-b");
        graph = await CaptureAsync(adminApi, state, graph,
            new(taskA, new("Board task A", "Controlled disposable board fixture task.")), ct).ConfigureAwait(false);
        graph = await CaptureAsync(adminApi, state, graph,
            new(taskB, new("Board task B", "Controlled disposable board prerequisite.")), ct).ConfigureAwait(false);
        BoardGraph beforeDependency = graph;
        graph = await AddDependencyAsync(adminApi, state, graph, taskA, taskB, ct).ConfigureAwait(false);
        RequireEdge(graph, projectId, taskA, taskB);

        GatewayResponse staleGraph = await DeleteAsync(adminApi, state, projectId, taskA,
            new(beforeDependency.Revision, FindTask(graph, taskA).Revision), ct).ConfigureAwait(false);
        RequireIssue(staleGraph, 409, "staleGraphRevision", "deleteProjectBacklogTask");
        BoardGraph afterStaleGraph = await ReadGraphAsync(adminApi, projectId, state, ct).ConfigureAwait(false);
        RequireUnchanged(graph, afterStaleGraph);
        AddStep(steps, "stale-graph-revision", staleGraph.Status, "staleGraphRevision");

        BoardGraph edited = await EditAsync(adminApi, state, graph, taskA, ct).ConfigureAwait(false);
        GatewayResponse staleTask = await DeleteAsync(adminApi, state, projectId, taskA,
            new(edited.Revision, FindTask(graph, taskA).Revision), ct).ConfigureAwait(false);
        RequireIssue(staleTask, 409, "staleTaskRevision", "deleteProjectBacklogTask");
        BoardGraph afterStaleTask = await ReadGraphAsync(adminApi, projectId, state, ct).ConfigureAwait(false);
        RequireUnchanged(edited, afterStaleTask);
        AddStep(steps, "stale-task-revision", staleTask.Status, "staleTaskRevision");

        BoardTask taskBNow = FindTask(edited, taskB);
        GatewayResponse dependent = await DeleteAsync(adminApi, state, projectId, taskB,
            new(edited.Revision, taskBNow.Revision), ct).ConfigureAwait(false);
        RequireIssue(dependent, 409, "taskHasDependents", "deleteProjectBacklogTask");
        BoardGraph afterDependent = await ReadGraphAsync(adminApi, projectId, state, ct).ConfigureAwait(false);
        RequireUnchanged(edited, afterDependent);
        AddStep(steps, "dependent-task-delete", dependent.Status, "taskHasDependents");

        JsonObject orchestratorContext = await ValidateContextAsync(orchestratorApi, manifest, state.ActorId, null, ct)
            .ConfigureAwait(false);
        RequireSameIdentity(state, await ValidateContextAsync(adminApi, manifest, state.ActorId, null, ct).ConfigureAwait(false));
        RequireSameIdentity(state, orchestratorContext);
        GatewayResponse ownerDenied = await DeleteAsync(orchestratorApi, state, projectId, taskA,
            new(edited.Revision, FindTask(edited, taskA).Revision), ct).ConfigureAwait(false);
        RequireOwnerDenial(ownerDenied);
        BoardGraph afterOwnerDenial = await ReadGraphAsync(adminApi, projectId, state, ct).ConfigureAwait(false);
        RequireUnchanged(edited, afterOwnerDenial);
        AddStep(steps, "owner-denial", ownerDenied.Status, "backlog_project_permission_denied");

        await RequireOwnerAuthorityAsync(adminApi, manifest, state, "writeProjects", ct).ConfigureAwait(false);
        BoardGraph beforeDeleteA = await ReadGraphAsync(adminApi, projectId, state, ct).ConfigureAwait(false);
        GatewayResponse deletedA = await DeleteAsync(adminApi, state, projectId, taskA,
            new(beforeDeleteA.Revision, FindTask(beforeDeleteA, taskA).Revision), ct).ConfigureAwait(false);
        BoardGraph returnedAfterA = ParseGraph(RequireSuccess(deletedA, "deleteProjectBacklogTask"), projectId);
        BoardGraph persistedAfterA = await ReadGraphAsync(adminApi, projectId, state, ct).ConfigureAwait(false);
        RequireUnchanged(returnedAfterA, persistedAfterA);
        RequireDeletedA(returnedAfterA, projectId, taskA, taskB, beforeDeleteA);
        state.MutationAcknowledged = false;
        AddStep(steps, "delete-task-with-dependent-edge", 200, null);

        BoardTask remainingB = FindTask(persistedAfterA, taskB);
        GatewayResponse deletedB = await DeleteAsync(adminApi, state, projectId, taskB,
            new(persistedAfterA.Revision, remainingB.Revision), ct).ConfigureAwait(false);
        BoardGraph returnedAfterB = ParseGraph(RequireSuccess(deletedB, "deleteProjectBacklogTask"), projectId);
        RequireGraph(returnedAfterB, projectId, []);
        BoardGraph persistedAfterB = await ReadGraphAsync(adminApi, projectId, state, ct).ConfigureAwait(false);
        RequireUnchanged(returnedAfterB, persistedAfterB);
        state.MutationAcknowledged = false;
        AddStep(steps, "delete-empty-board", 200, null);

        observations["board"] = new JsonObject
        {
            ["projectId"] = projectId,
            ["tasks"] = new JsonArray(JsonValue.Create(taskA), JsonValue.Create(taskB)),
            ["finalGraphRevision"] = persistedAfterB.Revision,
            ["finalTaskCount"] = 0,
            ["finalDependencyCount"] = 0,
        };
    }

    private static async Task<BoardGraph> CaptureAsync(
        IGatewayClient api, BoardFixtureState state, BoardGraph previous, BoardCaptureBacklogTask request, CancellationToken ct)
    {
        await RequireOwnerAuthorityAsync(api, null, state, "writeProjects", ct).ConfigureAwait(false);
        state.AttemptedTaskIds.Add(request.TaskId);
        GatewayResponse response = await MutateAsync(state,
            () => BoardGateway.CaptureTaskAsync(api, state.ProjectId!, request with { ExpectedGraphRevision = previous.Revision }, ct))
            .ConfigureAwait(false);
        BoardGraph graph = ParseGraph(RequireSuccess(response, "captureBacklogTask"), state.ProjectId!);
        if (graph.Revision != previous.Revision + 1
            || graph.Tasks.Count != previous.Tasks.Count + 1
            || graph.Dependencies.Count != previous.Dependencies.Count)
            throw Indeterminate("captureBacklogTask returned an unexpected graph revision or task count.");
        BoardTask task = FindTask(graph, request.TaskId);
        if (task.Title != request.Content.Title || task.Description != request.Content.Description
            || task.State != "backlog" || task.IsArchived || task.IsAutomationInvocationPending)
            throw Indeterminate("captureBacklogTask did not return the exact controlled owned task.");
        state.OwnedTaskRevisions[request.TaskId] = task.Revision;
        state.MutationAcknowledged = false;
        return graph;
    }

    private static async Task<BoardGraph> AddDependencyAsync(
        IGatewayClient api, BoardFixtureState state, BoardGraph previous, string taskId, string prerequisiteId, CancellationToken ct)
    {
        await RequireOwnerAuthorityAsync(api, null, state, "writeProjects", ct).ConfigureAwait(false);
        GatewayResponse response = await MutateAsync(state,
            () => BoardGateway.AddDependencyAsync(api, state.ProjectId!, taskId, prerequisiteId,
                new(previous.Revision), ct)).ConfigureAwait(false);
        BoardGraph graph = ParseGraph(RequireSuccess(response, "addProjectBacklogDependency"), state.ProjectId!);
        if (graph.Revision != previous.Revision + 1 || graph.Tasks.Count != previous.Tasks.Count
            || graph.Dependencies.Count != previous.Dependencies.Count + 1)
            throw Indeterminate("addProjectBacklogDependency returned an unexpected graph revision.");
        state.MutationAcknowledged = false;
        return graph;
    }

    private static async Task<BoardGraph> EditAsync(
        IGatewayClient api, BoardFixtureState state, BoardGraph previous, string taskId, CancellationToken ct)
    {
        await RequireOwnerAuthorityAsync(api, null, state, "writeProjects", ct).ConfigureAwait(false);
        BoardTask before = FindTask(previous, taskId);
        GatewayResponse response = await MutateAsync(state,
            () => BoardGateway.EditTaskContentAsync(api, state.ProjectId!, taskId,
                new(new("Board task A edited", "Controlled content edit."), previous.Revision, before.Revision), ct))
            .ConfigureAwait(false);
        BoardGraph graph = ParseGraph(RequireSuccess(response, "editBacklogTaskContent"), state.ProjectId!);
        BoardTask edited = FindTask(graph, taskId);
        if (graph.Revision != previous.Revision + 1 || graph.Tasks.Count != previous.Tasks.Count
            || edited.Revision != before.Revision + 1 || edited.Title != "Board task A edited")
            throw Indeterminate("editBacklogTaskContent returned an unexpected graph or task revision.");
        state.OwnedTaskRevisions[taskId] = edited.Revision;
        state.MutationAcknowledged = false;
        return graph;
    }

    private static async Task<GatewayResponse> DeleteAsync(
        IGatewayClient api, BoardFixtureState state, string projectId, string taskId, BoardTaskRevision request, CancellationToken ct) =>
        await MutateAsync(state, () => BoardGateway.DeleteTaskAsync(api, projectId, taskId, request, ct)).ConfigureAwait(false);

    private static async Task<GatewayResponse> MutateAsync(BoardFixtureState state, Func<Task<GatewayResponse>> action)
    {
        state.MutationInFlight = true;
        try
        {
            GatewayResponse response = await action().ConfigureAwait(false);
            state.MutationInFlight = false;
            if (response.Status >= 500) state.Indeterminate = true;
            state.MutationAcknowledged = response.Status is >= 200 and < 300;
            return response;
        }
        catch
        {
            state.Indeterminate = true;
            throw;
        }
    }

    private static async Task<BoardGraph> ReadGraphAsync(IGatewayClient api, string projectId, CancellationToken ct)
    {
        GatewayResponse response = await BoardGateway.ReadBacklogAsync(api, projectId, ct).ConfigureAwait(false);
        return ParseGraph(RequireSuccess(response, "readProjectBacklog"), projectId);
    }

    private static async Task<BoardGraph> ReadGraphAsync(IGatewayClient api, string projectId, BoardFixtureState state, CancellationToken ct)
    {
        JsonObject context = await ValidateContextAsync(api, null, state.ActorId, null, ct).ConfigureAwait(false);
        RequireSameIdentity(state, context);
        RequireProjectAuthority(context, projectId, "readProjects");
        return await ReadGraphAsync(api, projectId, ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<BoardTask> DependentFirst(BoardGraph graph)
    {
        List<BoardTask> remaining = graph.Tasks.ToList();
        List<BoardTask> ordered = [];
        while (remaining.Count > 0)
        {
            BoardTask? next = remaining.FirstOrDefault(task =>
                !graph.Dependencies.Any(edge => edge.PrerequisiteId == task.TaskId
                    && remaining.Any(dependent => dependent.TaskId == edge.TaskId)));
            if (next is null)
                throw new InvalidOperationException("The owned backlog dependency graph is cyclic during cleanup.");
            ordered.Add(next);
            remaining.Remove(next);
        }
        return ordered;
    }

    private static async Task<JsonObject> CleanupAsync(IGatewayClient api, BoardFixtureState state, CancellationToken ct)
    {
        if (!state.ProjectAcknowledged || state.ProjectId is null)
            return new JsonObject { ["state"] = state.Indeterminate ? "indeterminate" : "not-created" };
        if (state.Indeterminate || state.MutationInFlight)
            return new JsonObject
            {
                ["state"] = "indeterminate",
                ["projectId"] = state.ProjectId,
                ["reason"] = "A fixture mutation has no verified result; no cleanup write was retried.",
            };

        string projectId = state.ProjectId;
        if (state.OwnerAcknowledged)
        {
            BoardGraph graph = await ReadGraphAsync(api, projectId, state, ct).ConfigureAwait(false);
            foreach (BoardTask candidate in DependentFirst(graph))
            {
                if (!state.OwnedTaskRevisions.ContainsKey(candidate.TaskId))
                {
                    state.Indeterminate = true;
                    return new JsonObject
                    {
                        ["state"] = "indeterminate",
                        ["projectId"] = projectId,
                        ["reason"] = "The project contains a task outside the invocation-owned fixture ledger.",
                    };
                }
                graph = await ReadGraphAsync(api, projectId, state, ct).ConfigureAwait(false);
                BoardTask task = FindTask(graph, candidate.TaskId);
                if (state.RetainedArchivedTaskIds.Contains(task.TaskId))
                {
                    if (!task.IsArchived)
                        throw Indeterminate("The retained invocation-owned task is no longer archived.");
                    continue;
                }
                GatewayResponse deleted = await MutateAsync(state, () => BoardGateway.DeleteTaskAsync(api, projectId, task.TaskId,
                    new(graph.Revision, task.Revision), ct)).ConfigureAwait(false);
                if (deleted.Status != 200)
                    throw BoardFailure("deleteProjectBacklogTask cleanup", deleted, [200]);
                BoardGraph readback = await ReadGraphAsync(api, projectId, state, ct).ConfigureAwait(false);
                if (readback.Tasks.Any(item => item.TaskId == task.TaskId))
                    throw Indeterminate("Cleanup did not verify deletion of the invocation-owned task.");
                state.MutationAcknowledged = false;
                graph = readback;
            }
        }

        JsonObject latestProject = await ReadOwnedProjectAsync(api, state, ct).ConfigureAwait(false);
        long projectRevision = ReadPositive(latestProject, "revision", "current owned project revision");
        JsonObject currentContext = await ValidateContextAsync(api, null, state.ActorId, null, ct).ConfigureAwait(false);
        RequireSameIdentity(state, currentContext);
        if (state.OwnerAcknowledged) RequireProjectAuthority(currentContext, projectId, "writeProjects");
        GatewayResponse archived = await MutateAsync(state, () => BoardGateway.ArchiveProjectAsync(api, projectId,
            new(state.ProjectName, projectRevision), ct)).ConfigureAwait(false);
        if (archived.Status != 200) throw BoardFailure("updateProject cleanup", archived, [200]);
        JsonObject project = RequireBody(archived, "updateProject");
        long archivedRevision = ReadPositive(project, "revision", "archived project revision");
        if (ApiAcceptanceSupport.GetString(project, "projectId") != projectId
            || ApiAcceptanceSupport.GetString(project, "name") != state.ProjectName
            || ApiAcceptanceSupport.GetString(project, "state") != "archived"
            || archivedRevision != projectRevision + 1)
            throw Indeterminate("Projects Config did not return the exact archived project receipt.");
        state.ProjectRevision = archivedRevision;
        state.MutationAcknowledged = false;

        JsonObject result = new()
        {
            ["state"] = "cleaned",
            ["projectId"] = projectId,
            ["projectState"] = "archived",
            ["projectRevision"] = archivedRevision,
            ["taskDeletion"] = state.RetainedArchivedTaskIds.Count == 0 ? "verified" : "not-attempted-archived-retained",
            ["retainedArchivedTaskIds"] = new JsonArray(state.RetainedArchivedTaskIds
                .Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
        };
        if (state.OwnerAcknowledged && state.OwnerAssignmentId is not null && state.OwnerAssignmentRevision is long assignmentRevision)
        {
            GatewayResponse revoked = await MutateAsync(state, () => BoardGateway.RevokeOwnerAsync(api, projectId,
                state.OwnerAssignmentId, assignmentRevision, ct)).ConfigureAwait(false);
            if (revoked.Status != 200) throw BoardFailure("revokeProjectRoleAssignment cleanup", revoked, [200]);
            JsonObject receipt = RequireBody(revoked, "revokeProjectRoleAssignment");
            if (ApiAcceptanceSupport.GetString(receipt, "assignmentId") != state.OwnerAssignmentId
                || ApiAcceptanceSupport.GetString(receipt, "projectId") != projectId
                || ApiAcceptanceSupport.GetString(receipt, "principalId") != state.ActorId
                || ApiAcceptanceSupport.GetString(receipt, "role") != "owner"
                || ApiAcceptanceSupport.GetString(receipt, "state") != "revoked"
                || ReadPositive(receipt, "revision", "revoked owner assignment revision") != assignmentRevision + 1
                || ApiAcceptanceSupport.GetString(receipt, "revokedBy") != state.ActorId)
                throw Indeterminate("Projects Config did not return the exact revoked owner assignment receipt.");
            result["ownerAssignmentId"] = state.OwnerAssignmentId;
            result["ownerAssignmentRevision"] = assignmentRevision + 1;
            state.MutationAcknowledged = false;
        }
        if (state.RetainedArchivedTaskIds.Count != 0)
        {
            GatewayResponse projectResponse = await BoardGateway.ReadProjectAsync(api, projectId, ct).ConfigureAwait(false);
            if (projectResponse.Status != 200)
                throw BoardFailure("getProject cleanup readback", projectResponse, [200]);
            JsonObject projectReadback = RequireBody(projectResponse, "getProject cleanup readback");
            if (ApiAcceptanceSupport.GetString(projectReadback, "projectId") != projectId
                || ApiAcceptanceSupport.GetString(projectReadback, "name") != state.ProjectName
                || ApiAcceptanceSupport.GetString(projectReadback, "state") != "archived"
                || ReadLong(projectReadback, "revision") != archivedRevision
                || ReadLong(projectReadback, "configurationRevision") is not > 0)
                throw Indeterminate("The independent Projects readback did not confirm the archived Board fixture.");

            GatewayResponse assignmentsResponse = await BoardGateway.ListAssignmentsAsync(api, projectId, ct).ConfigureAwait(false);
            if (assignmentsResponse.Status != 200)
                throw BoardFailure("listProjectRoleAssignments cleanup readback", assignmentsResponse, [200]);
            JsonObject assignments = RequireBody(assignmentsResponse, "listProjectRoleAssignments cleanup readback");
            if (ApiAcceptanceSupport.GetString(assignments, "projectId") != projectId
                || assignments["assignments"] is not JsonArray activeAssignments
                || activeAssignments.Count != 0)
                throw Indeterminate("The independent Projects readback did not confirm all Board fixture assignments were revoked.");
            result["projectReadback"] = "archived";
            result["assignmentsReadback"] = "empty";
        }
        return result;
    }

    private static async Task RequireOwnerAuthorityAsync(
        IGatewayClient api, AcceptanceManifest? manifest, BoardFixtureState state, string permission, CancellationToken ct)
    {
        JsonObject context = await ValidateContextAsync(api, manifest, state.ActorId, null, ct).ConfigureAwait(false);
        RequireSameIdentity(state, context);
        RequireProjectAuthority(context, state.ProjectId!, permission);
    }

    private static async Task<JsonObject> ReadOwnedProjectAsync(
        IGatewayClient api, BoardFixtureState state, CancellationToken ct)
    {
        GatewayResponse response = await BoardGateway.ReadProjectAsync(api, state.ProjectId!, ct).ConfigureAwait(false);
        JsonObject project = response.Status == 200
            ? RequireBody(response, "getProject")
            : throw BoardFailure("getProject", response, [200]);
        if (ApiAcceptanceSupport.GetString(project, "projectId") != state.ProjectId
            || ApiAcceptanceSupport.GetString(project, "name") != state.ProjectName
            || ApiAcceptanceSupport.GetString(project, "state") != "active")
            throw Indeterminate("Projects Config did not return the exact active invocation-owned project.");
        state.ProjectRevision = ReadPositive(project, "revision", "current owned project revision");
        _ = ReadPositive(project, "configurationRevision", "current owned project configuration revision");
        return project;
    }

    private static async Task<JsonObject> ValidateContextAsync(
        IGatewayClient api, AcceptanceManifest? manifest, string? expectedActor, string? projectId, CancellationToken ct)
    {
        JsonObject context = await ApiAcceptanceJourney.ValidateAuthContextAsync(api, ct).ConfigureAwait(false);
        string actorId = ApiAcceptanceSupport.GetString(context, "actorId") ?? "";
        string tenantId = ApiAcceptanceSupport.GetString(context, "tenantId") ?? "";
        string issuer = ApiAcceptanceSupport.GetString(context, "issuer") ?? "";
        if (context["membershipRevision"] is not JsonValue membership || !membership.TryGetValue<long>(out long membershipRevision)
            || membershipRevision <= 0
            || string.IsNullOrWhiteSpace(actorId)
            || string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(issuer)
            || ApiAcceptanceSupport.GetString(context, "boundProjectId") is not null
            || ApiAcceptanceSupport.GetString(context, "boundRunId") is not null
            || manifest is not null
                && (issuer != ApiAcceptanceSupport.GetString(manifest.Environment, "issuer")
                    || tenantId != ApiAcceptanceSupport.GetString(manifest.Environment, "tenantId"))
            || expectedActor is not null && actorId != expectedActor
            || api.Identity.Target.TenantId != tenantId
            || api.Identity.Target.Issuer.AbsoluteUri != issuer
            || !HasOnlyScope(api.Identity, api.Identity.Scopes.Contains(AdminScope, StringComparer.Ordinal) ? AdminScope : OrchestratorScope))
            throw new AcceptanceBlockedException("The live Board authorization context is not the expected unbound actor, tenant, issuer, membership, or scope.");
        return context;
    }

    private static void RequireSameIdentity(BoardFixtureState expected, JsonObject actual)
    {
        if (ApiAcceptanceSupport.GetString(actual, "issuer") != expected.Issuer
            || ApiAcceptanceSupport.GetString(actual, "actorId") != expected.ActorId
            || ApiAcceptanceSupport.GetString(actual, "tenantId") != expected.TenantId
            || ReadLong(actual, "membershipRevision") != expected.MembershipRevision
            || expected.MembershipRevision is not > 0)
            throw new AcceptanceBlockedException("The Board authorization identity changed during the fixture journey.");
    }

    private static void RequireProjectAuthority(JsonObject context, string projectId, string permission)
    {
        JsonArray authority = context["effectiveAuthority"] as JsonArray ?? [];
        bool found = authority.OfType<JsonObject>().Any(item =>
            ApiAcceptanceSupport.GetString(item, "resourceType") == "project"
            && ApiAcceptanceSupport.GetString(item, "resourceId") == projectId
            && (item["permissions"] as JsonArray ?? []).OfType<JsonObject>().Any(grant =>
                ApiAcceptanceSupport.GetString(grant, "permission") == permission
                && ReadLong(grant, "roleRevision") is > 0));
        if (!found)
            throw new AcceptanceBlockedException($"Current project authority does not include {permission} for the invocation-owned Board.");
    }

    private static bool HasOnlyScope(CliIdentity identity, string scope) =>
        identity.ProjectId is null && identity.RunId is null
        && identity.Scopes.Contains(scope, StringComparer.Ordinal)
        && !identity.Scopes.Contains(scope == AdminScope ? OrchestratorScope : AdminScope, StringComparer.Ordinal);

    private static bool IsProjectId(string? value) =>
        ApiAcceptanceSupport.IsUuid(value) || Guid.TryParseExact(value, "N", out _);

    private static async Task RequireCapabilitiesAsync(IGatewayClient api, string journey, CancellationToken ct)
    {
        IReadOnlySet<string> operations = ApiAcceptanceJourney.GetOperationIds(await api.DiscoverAsync(ct).ConfigureAwait(false));
        List<string> required =
        [
            "getAuthorizationContext", "createProject", "getProject", "assignProjectRole", "updateProject", "revokeProjectRoleAssignment",
            "readProjectBacklog", "captureBacklogTask", "deleteProjectBacklogTask",
        ];
        if (journey == "board-archived-denial")
            required.AddRange(["archiveProjectBacklogTask", "listProjectRoleAssignments"]);
        else
            required.AddRange(["editBacklogTaskContent", "addProjectBacklogDependency"]);
        string[] missing = required.Where(operation => !operations.Contains(operation)).ToArray();
        if (missing.Length != 0)
            throw new AcceptanceBlockedException($"Required Board Gateway operations are missing: {string.Join(", ", missing)}.");
    }

    private static BoardGraph ParseGraph(JsonObject json, string projectId)
    {
        long revision = ReadLong(json, "revision") ?? throw new InvalidOperationException("The backlog graph revision is missing.");
        JsonArray tasks = json["tasks"] as JsonArray ?? throw new InvalidOperationException("The backlog task list is missing.");
        JsonArray dependencies = json["dependencies"] as JsonArray ?? throw new InvalidOperationException("The backlog dependency list is missing.");
        List<BoardTask> parsedTasks = [];
        foreach (JsonObject item in tasks.OfType<JsonObject>())
        {
            JsonObject reference = item["reference"] as JsonObject ?? [];
            string taskProjectId = ApiAcceptanceSupport.GetString(reference, "projectId") ?? "";
            string taskId = ApiAcceptanceSupport.GetString(reference, "taskId") ?? "";
            if (taskProjectId != projectId || !ApiAcceptanceSupport.IsUuid(taskId)
                || ReadLong(item, "revision") is not >= 0
                || ApiAcceptanceSupport.GetString(item, "state") is not ("backlog" or "ready" or "claimed")
                || item["isArchived"] is not JsonValue archived || !archived.TryGetValue<bool>(out bool isArchived)
                || item["isAutomationInvocationPending"] is not JsonValue pending || !pending.TryGetValue<bool>(out bool isPending)
                || ReadLong(item, "position") is not long position || position < 0)
                throw new InvalidOperationException("The backlog graph contains an invalid task snapshot.");
            parsedTasks.Add(new(taskId, ReadLong(item, "revision")!.Value,
                ApiAcceptanceSupport.GetString(item, "state")!, isArchived, isPending,
                ApiAcceptanceSupport.GetString(item, "title") ?? "",
                ApiAcceptanceSupport.GetString(item, "description"),
                position, item.DeepClone() as JsonObject ?? []));
        }
        if (parsedTasks.Count != tasks.Count)
            throw new InvalidOperationException("The backlog graph contains a malformed task.");
        List<BoardEdge> parsedEdges = [];
        foreach (JsonObject item in dependencies.OfType<JsonObject>())
        {
            JsonObject task = item["task"] as JsonObject ?? [];
            JsonObject prerequisite = item["prerequisite"] as JsonObject ?? [];
            string taskId = ApiAcceptanceSupport.GetString(task, "taskId") ?? "";
            string prerequisiteId = ApiAcceptanceSupport.GetString(prerequisite, "taskId") ?? "";
            if (ApiAcceptanceSupport.GetString(task, "projectId") != projectId
                || ApiAcceptanceSupport.GetString(prerequisite, "projectId") != projectId
                || !parsedTasks.Any(row => row.TaskId == taskId)
                || !parsedTasks.Any(row => row.TaskId == prerequisiteId))
                throw new InvalidOperationException("The backlog graph contains an invalid dependency.");
            parsedEdges.Add(new(taskId, prerequisiteId));
        }
        if (parsedEdges.Count != dependencies.Count)
            throw new InvalidOperationException("The backlog graph contains a malformed dependency.");
        if (ApiAcceptanceSupport.GetString(json, "projectId") != projectId)
            throw new InvalidOperationException("The backlog graph belongs to a different project.");
        return new(projectId, revision, parsedTasks, parsedEdges, json.DeepClone() as JsonObject ?? []);
    }

    private static void RequireGraph(BoardGraph graph, string projectId, IReadOnlyCollection<string> taskIds)
    {
        if (graph.ProjectId != projectId
            || graph.Tasks.Count != taskIds.Count
            || taskIds.Any(id => graph.Tasks.Count(task => task.TaskId == id) != 1)
            || graph.Dependencies.Count != 0)
            throw new InvalidOperationException("The authoritative backlog graph does not match the expected task set.");
    }

    private static BoardTask FindTask(BoardGraph graph, string taskId) =>
        graph.Tasks.SingleOrDefault(task => task.TaskId == taskId)
        ?? throw new InvalidOperationException("The authoritative backlog graph omitted an invocation-owned task.");

    private static void RequireEdge(BoardGraph graph, string projectId, string taskId, string prerequisiteId)
    {
        if (graph.Dependencies.Count != 1 || graph.Dependencies[0] != new BoardEdge(taskId, prerequisiteId)
            || graph.Tasks.Any(task => task.Raw["reference"]?["projectId"]?.GetValue<string>() != projectId))
            throw new InvalidOperationException("The authoritative backlog graph omitted the exact prerequisite edge.");
    }

    private static void RequireUnchanged(BoardGraph expected, BoardGraph actual)
    {
        if (!JsonNode.DeepEquals(expected.Raw, actual.Raw))
            throw new InvalidOperationException("A rejected backlog mutation changed the authoritative graph, revisions, or queue positions.");
    }

    private static void RequireDeletedA(BoardGraph graph, string projectId, string taskA, string taskB, BoardGraph before)
    {
        if (graph.Revision != before.Revision + 1
            || graph.Tasks.Count != 1
            || graph.Tasks[0].TaskId != taskB
            || graph.Dependencies.Count != 0
            || graph.Tasks[0].Position != 0
            || FindTask(before, taskB).State != graph.Tasks[0].State
            || graph.Tasks[0].Revision != FindTask(before, taskB).Revision + 1
            || graph.Tasks.Any(task => task.TaskId == taskA || task.Raw["reference"]?["projectId"]?.GetValue<string>() != projectId))
            throw new InvalidOperationException("Deleting task A did not return the expected compacted prerequisite graph.");
    }

    private static void RequireIssue(GatewayResponse response, int status, string code, string operation)
    {
        JsonObject body = response.Body as JsonObject ?? [];
        JsonArray issues = body["issues"] as JsonArray ?? [];
        if (response.Status != status || !issues.OfType<JsonObject>().Any(issue =>
                ApiAcceptanceSupport.GetString(issue, "code") == code))
            throw new InvalidOperationException($"The live {operation} operation returned HTTP {response.Status} without the required owner issue '{code}'.");
    }

    private static void RequireOwnerDenial(GatewayResponse response)
    {
        JsonObject body = response.Body as JsonObject ?? [];
        string? error = ApiAcceptanceSupport.GetString(body, "error");
        if (response.Status != 403 || error != "backlog_project_permission_denied")
            throw new InvalidOperationException($"The projects.orchestrator-only delete returned HTTP {response.Status} without backlog_project_permission_denied.");
    }

    private static JsonObject RequireSuccess(GatewayResponse response, string operation)
    {
        if (response.Status != 200)
            throw BoardFailure(operation, response, [200]);
        return RequireBody(response, operation);
    }

    private static JsonObject RequireBody(GatewayResponse response, string operation) =>
        response.Body as JsonObject
        ?? throw Indeterminate($"The {operation} operation returned no JSON object.");

    private static InvalidOperationException BoardFailure(string operation, GatewayResponse response, int[] expected)
    {
        string? code = ApiAcceptanceSupport.GetString(response.Body as JsonObject ?? [], "code");
        return new($"The Board {operation} operation returned HTTP {response.Status}{(code is null ? "" : $" ({ApiAcceptanceSupport.RedactText(code)})")}; expected {string.Join(" or ", expected)}.");
    }

    private static InvalidOperationException Indeterminate(string message) => new(message);

    private static long ReadPositive(JsonObject value, string name, string description)
    {
        long? result = ReadLong(value, name);
        return result is > 0 ? result.Value : throw Indeterminate($"The {description} is invalid.");
    }

    private static long? ReadLong(JsonObject value, string name) =>
        ApiAcceptanceSupport.TryLong(value[name], out long result) ? result : null;

    private static JsonObject Summarize(BoardFixtureState state) => new()
    {
        ["invocationId"] = state.InvocationId,
        ["projectId"] = state.ProjectId,
        ["projectName"] = state.ProjectName,
        ["projectRevision"] = state.ProjectRevision,
        ["projectAcknowledgedRevision"] = state.ProjectAcknowledgedRevision,
        ["ownerAssignmentId"] = state.OwnerAssignmentId,
        ["ownerAssignmentRevision"] = state.OwnerAssignmentRevision,
        ["tenantId"] = state.TenantId,
        ["actorId"] = state.ActorId,
        ["ownedTaskIds"] = new JsonArray(state.OwnedTaskRevisions.Keys.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
        ["retainedArchivedTaskIds"] = new JsonArray(state.RetainedArchivedTaskIds
            .Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
        ["taskCaptureAttempts"] = new JsonArray(state.AttemptedTaskIds.Select(id => (JsonNode?)new JsonObject
        {
            ["taskId"] = id,
            ["outcome"] = state.OwnedTaskRevisions.ContainsKey(id) ? "acknowledged" : "unknown",
        }).ToArray()),
        ["cleanup"] = state.Cleanup?.DeepClone(),
    };

    private static void AddStep(List<JsonObject> steps, string name, int status, string? issueCode) =>
        ApiAcceptanceSupport.AddStep(steps, name, "passed",
            ("httpStatus", JsonValue.Create(status)),
            ("ownerIssueCode", issueCode is null ? null : JsonValue.Create(issueCode)));

    private static void AddBlockedStep(List<JsonObject> steps, string name, string reason) =>
        ApiAcceptanceSupport.AddStep(steps, name, "blocked", ("reason", JsonValue.Create(reason)));

    private sealed record BoardGraph(string ProjectId, long Revision, IReadOnlyList<BoardTask> Tasks, IReadOnlyList<BoardEdge> Dependencies, JsonObject Raw);
    private sealed record BoardTask(string TaskId, long Revision, string State, bool IsArchived, bool IsAutomationInvocationPending, string Title, string? Description, long Position, JsonObject Raw);
    private sealed record BoardEdge(string TaskId, string PrerequisiteId);
    private sealed record BoardTaskContent(string Title, string? Description);
    private sealed record BoardCaptureBacklogTask(string TaskId, BoardTaskContent Content, long ExpectedGraphRevision = 0);
    private sealed record BoardTaskRevision(long ExpectedGraphRevision, long ExpectedTaskRevision);
    private sealed record BoardGraphRevision(long ExpectedGraphRevision);
    private sealed record BoardEditTaskContent(BoardTaskContent Content, long ExpectedGraphRevision, long ExpectedTaskRevision);
    private sealed record BoardCreateProject(string Name);
    private sealed record BoardArchiveProject(string Name, long ExpectedRevision);

    private static class BoardGateway
    {
        private static JsonObject ToJson(BoardTaskContent content) => new()
        {
            ["title"] = content.Title,
            ["description"] = content.Description,
            ["lifecycle"] = "open",
        };

        public static Task<GatewayResponse> CreateProjectAsync(IGatewayClient api, BoardCreateProject request, CancellationToken ct) =>
            api.InvokeAsync("createProject", new(Body: new JsonObject { ["name"] = request.Name }), ct);

        public static Task<GatewayResponse> ReadProjectAsync(IGatewayClient api, string projectId, CancellationToken ct) =>
            api.InvokeAsync("getProject", new(ApiAcceptanceJourney.Path(("projectId", projectId))), ct);

        public static Task<GatewayResponse> ReadBacklogAsync(IGatewayClient api, string projectId, CancellationToken ct) =>
            api.InvokeAsync("readProjectBacklog", new(ApiAcceptanceJourney.Path(("projectId", projectId))), ct);

        public static Task<GatewayResponse> ListAssignmentsAsync(IGatewayClient api, string projectId, CancellationToken ct) =>
            api.InvokeAsync("listProjectRoleAssignments", new(ApiAcceptanceJourney.Path(("projectId", projectId))), ct);

        public static Task<GatewayResponse> CaptureTaskAsync(IGatewayClient api, string projectId, BoardCaptureBacklogTask request, CancellationToken ct) =>
            api.InvokeAsync("captureBacklogTask", new(
                ApiAcceptanceJourney.Path(("projectId", projectId)),
                Body: new JsonObject
                {
                    ["taskId"] = request.TaskId,
                    ["content"] = ToJson(request.Content),
                    ["expectedGraphRevision"] = request.ExpectedGraphRevision,
                }), ct);

        public static Task<GatewayResponse> ArchiveTaskAsync(IGatewayClient api, string projectId, string taskId, BoardTaskRevision request, CancellationToken ct) =>
            api.InvokeAsync("archiveProjectBacklogTask", new(
                ApiAcceptanceJourney.Path(("projectId", projectId), ("taskId", taskId)),
                Body: new JsonObject
                {
                    ["expectedGraphRevision"] = request.ExpectedGraphRevision,
                    ["expectedTaskRevision"] = request.ExpectedTaskRevision,
                }), ct);

        public static Task<GatewayResponse> EditTaskContentAsync(IGatewayClient api, string projectId, string taskId, BoardEditTaskContent request, CancellationToken ct) =>
            api.InvokeAsync("editBacklogTaskContent", new(
                ApiAcceptanceJourney.Path(("projectId", projectId), ("taskId", taskId)),
                Body: new JsonObject
                {
                    ["content"] = ToJson(request.Content),
                    ["expectedGraphRevision"] = request.ExpectedGraphRevision,
                    ["expectedTaskRevision"] = request.ExpectedTaskRevision,
                }), ct);

        public static Task<GatewayResponse> AddDependencyAsync(IGatewayClient api, string projectId, string taskId, string prerequisiteId, BoardGraphRevision request, CancellationToken ct) =>
            api.InvokeAsync("addProjectBacklogDependency", new(
                ApiAcceptanceJourney.Path(("projectId", projectId), ("taskId", taskId), ("prerequisiteTaskId", prerequisiteId)),
                Body: new JsonObject { ["expectedGraphRevision"] = request.ExpectedGraphRevision }), ct);

        public static Task<GatewayResponse> DeleteTaskAsync(IGatewayClient api, string projectId, string taskId, BoardTaskRevision request, CancellationToken ct) =>
            api.InvokeAsync("deleteProjectBacklogTask", new(
                ApiAcceptanceJourney.Path(("projectId", projectId), ("taskId", taskId)),
                Body: new JsonObject
                {
                    ["expectedGraphRevision"] = request.ExpectedGraphRevision,
                    ["expectedTaskRevision"] = request.ExpectedTaskRevision,
                }), ct);

        public static Task<GatewayResponse> ArchiveProjectAsync(IGatewayClient api, string projectId, BoardArchiveProject request, CancellationToken ct) =>
            api.InvokeAsync("updateProject", new(
                ApiAcceptanceJourney.Path(("projectId", projectId)),
                Body: new JsonObject
                {
                    ["name"] = request.Name,
                    ["expectedRevision"] = request.ExpectedRevision,
                    ["state"] = "archived",
                }), ct);

        public static Task<GatewayResponse> RevokeOwnerAsync(IGatewayClient api, string projectId, string assignmentId, long revision, CancellationToken ct) =>
            api.InvokeAsync("revokeProjectRoleAssignment", new(
                ApiAcceptanceJourney.Path(("projectId", projectId), ("assignmentId", assignmentId)),
                Query: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["expectedRevision"] = revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }), ct);
    }
}
