using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Security;
using Agentweaver.AgentRuntime;
using Agentweaver.Domain;

namespace Agentweaver.Api.ConsoleFacade;

public interface IConsoleTurnService
{
    Task<ConsoleTurnResponse> HandleAsync(
        ConsoleTurnRequest request,
        CallerContext caller,
        string? authorizationHeader,
        CancellationToken ct);
}

public sealed class ConsoleTurnHttpException(int statusCode, string error, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Error { get; } = error;
}

public sealed class ConsoleConversationStore
{
    private readonly Dictionary<string, List<ConsoleFacadeHistoryMessage>> _history = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<ConsoleFacadeHistoryMessage> Snapshot(string conversationId)
    {
        lock (_lock)
            return _history.TryGetValue(conversationId, out var messages)
                ? messages.ToList()
                : [];
    }

    public void Append(string conversationId, string role, string text)
    {
        lock (_lock)
        {
            if (!_history.TryGetValue(conversationId, out var messages))
            {
                messages = [];
                _history[conversationId] = messages;
            }

            messages.Add(new ConsoleFacadeHistoryMessage(role, text));
            if (messages.Count > 24)
                messages.RemoveRange(0, messages.Count - 24);
        }
    }
}

/// <summary>
/// Backend seam for the global Browser Console facade. The production LLM/Morpheus
/// router can replace this service behind <see cref="IConsoleTurnService"/>; until
/// that runtime path exists, this deterministic fallback only executes safe,
/// existing API-equivalent actions and returns explicit gate/clarification states.
/// </summary>
public sealed class ConsoleTurnService(
    IProjectStore projectStore,
    IRunStore runStore,
    CoordinatorSteeringService steering,
    IConsoleFacadeAgent facadeAgent,
    ConsoleConversationStore conversationStore,
    IConfiguration configuration) : IConsoleTurnService
{
    public async Task<ConsoleTurnResponse> HandleAsync(
        ConsoleTurnRequest request,
        CallerContext caller,
        string? authorizationHeader,
        CancellationToken ct)
    {
        var text = FirstNonEmpty(request.Text, request.Message)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new ConsoleTurnHttpException(StatusCodes.Status400BadRequest, "text_required", "text or message is required.");

        var conversationId = string.IsNullOrWhiteSpace(request.ConversationId)
            ? Guid.NewGuid().ToString("N")
            : request.ConversationId;

        var context = request.Context ?? new ConsoleTurnContext();
        var contextProjectId = FirstNonEmpty(context.ProjectId, request.ProjectId);
        var contextRunId = FirstNonEmpty(context.RunId, request.RunId);
        var contextRoute = FirstNonEmpty(context.Route, request.Route);
        var project = await ResolveProjectAsync(contextProjectId, caller, ct).ConfigureAwait(false);
        var run = await ResolveRunAsync(contextRunId, caller, ct).ConfigureAwait(false);

        if (project is not null && run?.ProjectId is not null && run.ProjectId.Value != project.Id)
        {
            throw new ConsoleTurnHttpException(
                StatusCodes.Status409Conflict,
                "context_mismatch",
                "The supplied run does not belong to the supplied project.");
        }

        project ??= run?.ProjectId is ProjectId runProjectId
            ? await ResolveProjectAsync(runProjectId.ToString(), caller, ct).ConfigureAwait(false)
            : null;

        var lower = text.ToLowerInvariant();

        if (LooksLikeGateRequest(lower))
            return GateRequired(conversationId, project, run, contextRoute, lower);

        if (LooksDestructive(lower))
            return DestructiveGate(conversationId, project, run, contextRoute, text);

        if (LooksLikeProjectList(lower))
            return await ListProjectsAsync(conversationId, caller, contextRoute, ct).ConfigureAwait(false);

        if (LooksLikeStart(lower))
            return StartRequiresConfirmation(conversationId, text, project, contextRoute);

        if (LooksLikeReadOnlyStatus(lower))
            return await RunFacadeAgentAsync(conversationId, text, project, run, caller, authorizationHeader, contextRoute, ct)
                .ConfigureAwait(false);

        if (run is not null)
            return await SendToCoordinatorAsync(conversationId, text, project, run, caller, contextRoute, ct)
                .ConfigureAwait(false);

        if (project is null)
        {
            return Clarification(
                conversationId,
                "Select a project or bind a run before I take action.",
                contextRoute,
                suggested: ["/projects", "/use <project>", "/monitor <runId>"]);
        }

        return Clarification(
            conversationId,
            "Do you want me to start a new coordinator orchestration for this project, or should I bind to an existing run?",
            contextRoute,
            project.Id.ToString(),
            suggested: ["/orchestrate <goal>", "/runs", "/monitor <runId>"]);
    }

    private async Task<ConsoleTurnResponse> RunFacadeAgentAsync(
        string conversationId,
        string text,
        Agentweaver.Domain.Project? project,
        Run? run,
        CallerContext caller,
        string? authorizationHeader,
        string? route,
        CancellationToken ct)
    {
        var projectId = project?.Id.ToString() ?? run?.ProjectId?.ToString();
        var runId = run?.Id.ToString();
        var history = conversationStore.Snapshot(conversationId);
        conversationStore.Append(conversationId, "user", text);

        var response = await facadeAgent.RunTurnAsync(new ConsoleFacadeAgentRequest(
            ConversationId: conversationId,
            Message: text,
            CallerUser: caller.GitHubLogin ?? caller.User,
            GitHubLogin: caller.GitHubLogin,
            ProjectId: projectId,
            RunId: runId,
            Route: route,
            ApiBaseUrl: RunWorkflowFactory.ResolveApiBaseUrl(configuration),
            AuthorizationHeader: authorizationHeader,
            ModelId: project?.ProviderSettings.GitHubCopilotModel,
            AgentDefinition: LoadAgentDefinition(),
            History: history), ct).ConfigureAwait(false);

        conversationStore.Append(conversationId, "assistant", response.Message);

        return Response(
            conversationId,
            "answer",
            response.Message,
            projectId,
            runId,
            route,
            tools: response.ToolCalls
                .Select(t => new ConsoleToolSummary { Label = t.Name, Status = t.Status, Detail = t.Detail })
                .ToList(),
            links: projectId is not null && runId is not null ? [RunLink(projectId, runId)] : [],
            actions: response.ToolCalls
                .Select(t => new ConsoleActionSummary { Action = t.Name, Status = t.Status, Detail = t.Detail })
                .ToList());
    }

    private async Task<Agentweaver.Domain.Project?> ResolveProjectAsync(
        string? rawProjectId,
        CallerContext caller,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawProjectId))
            return null;

        if (!ProjectId.TryParse(rawProjectId, out var projectId))
            throw new ConsoleTurnHttpException(StatusCodes.Status400BadRequest, "invalid_project_id", "Invalid project id.");

        var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null)
            throw new ConsoleTurnHttpException(StatusCodes.Status404NotFound, "project_not_found", "The project was not found.");

        if (!caller.Owns(project.Owner))
            throw new ConsoleTurnHttpException(StatusCodes.Status403Forbidden, "forbidden", "The authenticated caller does not own this project.");

        return project;
    }

    private async Task<Run?> ResolveRunAsync(string? rawRunId, CallerContext caller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawRunId))
            return null;

        if (!RunId.TryParse(rawRunId, out var runId))
            throw new ConsoleTurnHttpException(StatusCodes.Status400BadRequest, "invalid_run_id", "Invalid run id.");

        var run = await runStore.GetAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
            throw new ConsoleTurnHttpException(StatusCodes.Status404NotFound, "run_not_found", "The run was not found.");

        if (!caller.Owns(run.SubmittingUser))
            throw new ConsoleTurnHttpException(StatusCodes.Status403Forbidden, "forbidden", "The authenticated caller does not own this run.");

        return run;
    }

    private async Task<ConsoleTurnResponse> ListProjectsAsync(
        string conversationId,
        CallerContext caller,
        string? route,
        CancellationToken ct)
    {
        var projects = (await projectStore.ListAsync(ct).ConfigureAwait(false))
            .Where(p => caller.Owns(p.Owner))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var message = projects.Count == 0
            ? "No projects are available for your account."
            : $"Found {projects.Count} project(s).";

        return Response(
            conversationId,
            "answer",
            message,
            projectId: null,
            runId: null,
            route: route,
            tools:
            [
                new ConsoleToolSummary { Label = "project_list", Status = "completed", Detail = $"{projects.Count} project(s)" },
            ],
            links: projects.Select(ProjectLink).ToList(),
            actions:
            [
                new ConsoleActionSummary { Action = "project_list", Status = "completed", TargetType = "project", Detail = $"{projects.Count} project(s)" },
            ]);
    }

    private ConsoleTurnResponse StartRequiresConfirmation(
        string conversationId,
        string goal,
        Agentweaver.Domain.Project? project,
        string? route)
    {
        var projectId = project?.Id.ToString();
        var message = project is null
            ? "Starting an orchestration requires explicit confirmation and a project target. Select a project first."
            : "Starting an orchestration requires explicit confirmation. I have not created a run.";
        return Response(
            conversationId,
            "gate_required",
            message,
            projectId,
            runId: null,
            route,
            links: project is null ? [] : [ProjectLink(project)],
            gate: new ConsoleGate
            {
                Kind = "start_orchestration",
                Title = "Start orchestration?",
                Description = goal,
                ProjectId = projectId,
                RunId = null,
            },
            actions:
            [
                new ConsoleActionSummary { Action = "coordinator_start", Status = "requires_confirmation", TargetType = "project", TargetId = projectId, Label = "Start orchestration" },
            ],
            pendingGate: "start_orchestration",
            suggested: project is null ? ["/projects", "/use <project>"] : ["/orchestrate <goal>", "Use the start-orchestration confirmation UI"]);
    }

    private async Task<ConsoleTurnResponse> SendToCoordinatorAsync(
        string conversationId,
        string instruction,
        Agentweaver.Domain.Project? project,
        Run run,
        CallerContext caller,
        string? route,
        CancellationToken ct)
    {
        var runId = run.Id.ToString();
        var directive = await steering.SteerAsync(
            runId,
            SteeringKind.Send,
            targetChildRunId: null,
            instruction,
            caller.User,
            ct).ConfigureAwait(false);

        var projectId = project?.Id.ToString() ?? run.ProjectId?.ToString();
        return Response(
            conversationId,
            "answer",
            "Sent to the coordinator as a steering message. Watch the run stream for the response.",
            projectId,
            runId,
            route,
            tools:
            [
                new ConsoleToolSummary { Label = "coordinator_steer", Status = directive.Status, Detail = "kind=send" },
            ],
            links: projectId is null ? [] : [RunLink(projectId, runId)],
            actions:
            [
                new ConsoleActionSummary
                {
                    Action = "coordinator_steer",
                    Status = directive.Status,
                    TargetType = "run",
                    TargetId = runId,
                    Label = "Sent steering message",
                },
            ]);
    }

    private static ConsoleTurnResponse GateRequired(
        string conversationId,
        Agentweaver.Domain.Project? project,
        Run? run,
        string? route,
        string lower)
    {
        var projectId = project?.Id.ToString() ?? run?.ProjectId?.ToString();
        var runId = run?.Id.ToString();
        var gateKind = lower.Contains("assembly", StringComparison.Ordinal)
            ? "assembly_review"
            : lower.Contains("merge", StringComparison.Ordinal) || lower.Contains("review", StringComparison.Ordinal)
                ? "review_merge"
                : "outcome_spec_confirmation";

        var message = runId is null
            ? "That is a gated action. Bind the relevant run first so the gate can be shown explicitly."
            : "That action requires an explicit gate decision. I am returning the gate state instead of bypassing it.";

        return Response(
            conversationId,
            "gate_required",
            message,
            projectId,
            runId,
            route,
            links: projectId is not null && runId is not null ? [RunLink(projectId, runId)] : [],
            gate: new ConsoleGate
            {
                Kind = gateKind,
                Title = "Explicit approval required",
                Description = "Use the gated run view or explicit console gate controls to proceed.",
                ProjectId = projectId,
                RunId = runId,
            },
            actions:
            [
                new ConsoleActionSummary { Action = gateKind, Status = "required", TargetType = "run", TargetId = runId },
            ],
            pendingGate: gateKind,
            suggested: runId is null ? ["/runs", "/monitor <runId>"] : ["/confirm", "/revise <feedback>", "/approve-assembly"]);
    }

    private static ConsoleTurnResponse DestructiveGate(
        string conversationId,
        Agentweaver.Domain.Project? project,
        Run? run,
        string? route,
        string text)
    {
        var projectId = project?.Id.ToString() ?? run?.ProjectId?.ToString();
        var runId = run?.Id.ToString();
        return Response(
            conversationId,
            "gate_required",
            "This looks destructive or interrupting, so I will not execute it from a free-form turn. Use the explicit gated control.",
            projectId,
            runId,
            route,
            links: projectId is not null && runId is not null ? [RunLink(projectId, runId)] : [],
            gate: new ConsoleGate
            {
                Kind = "destructive_action",
                Title = "Explicit confirmation required",
                Description = text,
                ProjectId = projectId,
                RunId = runId,
            },
            actions:
            [
                new ConsoleActionSummary { Action = "destructive_action", Status = "required", TargetType = runId is null ? "project" : "run", TargetId = runId ?? projectId },
            ],
            pendingGate: "destructive_action",
            suggested: ["/stop", "Open the gated run view"]);
    }

    private static ConsoleTurnResponse Clarification(
        string conversationId,
        string prompt,
        string? route,
        string? projectId = null,
        IReadOnlyList<string>? suggested = null)
    {
        return Response(
            conversationId,
            "clarification",
            prompt,
            projectId,
            runId: null,
            route,
            clarifications:
            [
                new ConsoleClarification
                {
                    Id = "missing_context",
                    Prompt = prompt,
                    Required = true,
                    Options = suggested,
                },
            ],
            suggested: suggested);
    }

    private static ConsoleTurnResponse Response(
        string conversationId,
        string kind,
        string message,
        string? projectId,
        string? runId,
        string? route,
        IReadOnlyList<ConsoleToolSummary>? tools = null,
        IReadOnlyList<ConsoleLink>? links = null,
        ConsoleGate? gate = null,
        IReadOnlyList<ConsoleActionSummary>? actions = null,
        IReadOnlyList<ConsoleClarification>? clarifications = null,
        string? pendingGate = null,
        IReadOnlyList<string>? suggested = null)
    {
        return new ConsoleTurnResponse
        {
            ConversationId = conversationId,
            Kind = kind,
            Status = ToConsoleStatus(kind),
            Message = message,
            Action = gate?.Kind ?? actions?.FirstOrDefault()?.Action,
            ProjectId = projectId,
            RunId = runId,
            Tools = tools,
            ToolCalls = tools?.Select(t => new ConsoleToolCall
            {
                Name = t.Label,
                Status = t.Status,
                Summary = t.Detail,
            }).ToList(),
            Links = links,
            Gate = gate,
            MessageChunks = [new ConsoleMessageChunk { Text = message }],
            Events = [new ConsoleTurnEvent { Type = "message", Text = message, Status = kind }],
            ActionSummaries = actions,
            Clarifications = clarifications,
            ActionableState = new ConsoleActionableState
            {
                ProjectId = projectId,
                RunId = runId,
                Route = route,
                PendingGate = pendingGate,
                SuggestedCommands = suggested,
            },
        };
    }

    private static string ToConsoleStatus(string kind) => kind switch
    {
        "clarification" => "needs_clarification",
        "gate_required" => "needs_confirmation",
        "error" => "blocked",
        _ => "completed",
    };

    private static ConsoleLink ProjectLink(Agentweaver.Domain.Project project) => new()
    {
        Label = project.Name,
        To = $"/projects/{project.Id}",
    };

    private static ConsoleLink RunLink(string projectId, string runId) => new()
    {
        Label = "Open orchestration",
        To = $"/projects/{projectId}/orchestrations/{runId}",
    };

    private static bool LooksLikeProjectList(string lower) =>
        (lower.Contains("project", StringComparison.Ordinal) || lower.Contains("projects", StringComparison.Ordinal)) &&
        (lower.Contains("list", StringComparison.Ordinal) ||
         lower.Contains("show", StringComparison.Ordinal) ||
         lower.Contains("what", StringComparison.Ordinal) ||
         lower.Contains("available", StringComparison.Ordinal));

    private static bool LooksLikeStart(string lower) =>
        lower.Contains("orchestrate", StringComparison.Ordinal) ||
        lower.Contains("start ", StringComparison.Ordinal) ||
        lower.StartsWith("start", StringComparison.Ordinal) ||
        lower.Contains("kick off", StringComparison.Ordinal) ||
        lower.Contains("run ", StringComparison.Ordinal);

    private static bool LooksLikeGateRequest(string lower) =>
        lower.Contains("confirm", StringComparison.Ordinal) ||
        lower.Contains("approve", StringComparison.Ordinal) ||
        lower.Contains("merge", StringComparison.Ordinal) ||
        lower.Contains("review", StringComparison.Ordinal);

    private static bool LooksDestructive(string lower) =>
        lower.Contains("delete", StringComparison.Ordinal) ||
        lower.Contains("remove", StringComparison.Ordinal) ||
        lower.Contains("archive", StringComparison.Ordinal) ||
        lower.Contains("cancel", StringComparison.Ordinal) ||
        lower.Contains("stop", StringComparison.Ordinal);

    private static bool LooksLikeReadOnlyStatus(string lower) =>
        lower.Contains("status", StringComparison.Ordinal) ||
        lower.Contains("show", StringComparison.Ordinal) ||
        lower.Contains("list", StringComparison.Ordinal) ||
        lower.Contains("what", StringComparison.Ordinal) ||
        lower.Contains("which", StringComparison.Ordinal) ||
        lower.Contains("who", StringComparison.Ordinal) ||
        lower.Contains("progress", StringComparison.Ordinal) ||
        lower.Contains("backlog", StringComparison.Ordinal) ||
        lower.Contains("board", StringComparison.Ordinal) ||
        lower.Contains("runs", StringComparison.Ordinal) ||
        lower.Contains("work plan", StringComparison.Ordinal) ||
        lower.Contains("children", StringComparison.Ordinal) ||
        lower.Contains("topology", StringComparison.Ordinal) ||
        lower.Contains("decision", StringComparison.Ordinal) ||
        lower.Contains("memory", StringComparison.Ordinal) ||
        lower.Contains("workflow", StringComparison.Ordinal) ||
        lower.Contains("blueprint", StringComparison.Ordinal) ||
        lower.Contains("role", StringComparison.Ordinal);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private string LoadAgentDefinition()
    {
        var configured = configuration["Console:AgentDefinitionPath"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return File.ReadAllText(configured);

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, ".github", "agents", "agentweaver.agent.md");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            current = current.Parent;
        }

        return """
---
description: Drives the Agentweaver multi-agent orchestration platform end-to-end via its MCP tools.
---

# Agentweaver Driver

You translate natural-language Agentweaver requests into safe tool calls, discover IDs before acting,
and preserve coordinator gates and steering semantics.
""";
    }
}
