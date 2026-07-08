using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime;

public sealed record ConsoleFacadeHistoryMessage(string Role, string Text);

public sealed record ConsoleFacadeAgentRequest(
    string ConversationId,
    string Message,
    string CallerUser,
    string? GitHubLogin,
    string? ProjectId,
    string? RunId,
    string? Route,
    string ApiBaseUrl,
    string? AuthorizationHeader,
    string? ModelId,
    string AgentDefinition,
    IReadOnlyList<ConsoleFacadeHistoryMessage> History);

public sealed record ConsoleFacadeToolCall(string Name, string Status, string? Detail);

public sealed record ConsoleFacadeAgentResponse(
    string Message,
    IReadOnlyList<ConsoleFacadeToolCall> ToolCalls);

public interface IConsoleFacadeAgent
{
    Task<ConsoleFacadeAgentResponse> RunTurnAsync(ConsoleFacadeAgentRequest request, CancellationToken ct);
}

/// <summary>
/// MAF-backed browser Console facade. It uses the repo's Agentweaver agent definition as the
/// model-facing operator prompt, but exposes only safe/read-only Agentweaver API tools. Gated and
/// destructive actions remain outside the tool list so a free-form Console turn cannot bypass
/// coordinator outcome, review, merge, or destructive-operation gates.
/// </summary>
public sealed class CopilotConsoleFacadeAgent(
    GitHubCopilotClientFactory factory,
    IGitHubTokenScopeProvider scopeProvider,
    ILogger<CopilotConsoleFacadeAgent> logger) : IConsoleFacadeAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public async Task<ConsoleFacadeAgentResponse> RunTurnAsync(ConsoleFacadeAgentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CallerUser))
            throw new AgentProviderException(
                ModelSource.GitHubCopilot,
                AgentProviderFailureKind.Authorization,
                "github_copilot_auth_required",
                "Console facade cannot start: no authenticated caller identity is available.",
                isRetryable: false);

        var scope = scopeProvider.Resolve(request.CallerUser);
        if (string.Equals(scope.Key, GitHubTokenScope.Installation.Key, StringComparison.Ordinal))
            throw new AgentProviderException(
                ModelSource.GitHubCopilot,
                AgentProviderFailureKind.Authorization,
                "github_copilot_auth_required",
                "Console facade requires the signed-in user's Copilot-entitled token, not the installation token.",
                isRetryable: false);

        var toolCalls = new ConcurrentBag<ConsoleFacadeToolCall>();
        await using var client = await factory.CreateClientAsync(scope, request.ModelId, ct).ConfigureAwait(false);
        try
        {
            await client.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (AgentProviderException.Classify(ModelSource.GitHubCopilot, ex, "console") is { } providerFailure)
        {
            logger.LogWarning(ex, "Console facade provider failure while starting client: {Code}", providerFailure.ErrorCode);
            throw providerFailure;
        }

        var sessionConfig = new SessionConfig
        {
            EnableConfigDiscovery = false,
            Streaming = true,
            SessionId = $"agentweaver-console-{request.ConversationId}",
            EnableSessionStore = false,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            Model = request.ModelId,
            Tools = ConsoleFacadeApiTools.Build(request.ApiBaseUrl, request.AuthorizationHeader, toolCalls)
                .Cast<AIFunctionDeclaration>()
                .ToList(),
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = BuildSystemPrompt(request),
            },
        };

        var agent = client.AsAIAgent(sessionConfig, ownsClient: false, id: null, name: "Agentweaver Console", description: null);
        AgentSession session;
        try
        {
            session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (AgentProviderException.Classify(ModelSource.GitHubCopilot, ex, "console") is { } providerFailure)
        {
            logger.LogWarning(ex, "Console facade provider failure while creating session: {Code}", providerFailure.ErrorCode);
            throw providerFailure;
        }
        var messages = BuildMessages(request);
        var answer = new StringBuilder();
        var streamedMessageIds = new HashSet<string>(StringComparer.Ordinal);
        var anyDeltaForNullId = false;

        try
        {
            await foreach (var chunk in agent.RunStreamingAsync(messages, session, options: null, ct).WithCancellation(ct))
            {
                if (chunk is null) continue;

                var delta = chunk.Text;
                if (!string.IsNullOrEmpty(delta))
                {
                    answer.Append(delta);
                    if (chunk.MessageId is not null)
                        streamedMessageIds.Add(chunk.MessageId);
                    else
                        anyDeltaForNullId = true;
                }

                var final = ExtractFinalMessageContent(chunk);
                if (!string.IsNullOrEmpty(final))
                {
                    var alreadyStreamed = chunk.MessageId is not null
                        ? streamedMessageIds.Contains(chunk.MessageId)
                        : anyDeltaForNullId;
                    if (!alreadyStreamed)
                        answer.Append(final);
                }
            }
        }
        catch (Exception ex) when (AgentProviderException.Classify(ModelSource.GitHubCopilot, ex, "console") is { } providerFailure)
        {
            logger.LogWarning(ex, "Console facade provider failure: {Code}", providerFailure.ErrorCode);
            throw providerFailure;
        }

        var text = answer.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text))
            text = "I could not produce a Console response. Try rephrasing the request with a project or run context.";

        return new ConsoleFacadeAgentResponse(text, toolCalls.Reverse().ToList());
    }

    internal static string BuildSystemPromptForTests(
        string agentDefinition,
        string? projectId = null,
        string? runId = null,
        string? route = null) =>
        BuildSystemPrompt(new ConsoleFacadeAgentRequest(
            ConversationId: "test",
            Message: "test",
            CallerUser: "test",
            GitHubLogin: "test",
            ProjectId: projectId,
            RunId: runId,
            Route: route,
            ApiBaseUrl: "http://localhost",
            AuthorizationHeader: null,
            ModelId: null,
            AgentDefinition: agentDefinition,
            History: []));

    private static string BuildSystemPrompt(ConsoleFacadeAgentRequest request)
    {
        var context = JsonSerializer.Serialize(new
        {
            request.ProjectId,
            request.RunId,
            request.Route,
            request.CallerUser,
            request.GitHubLogin,
        }, JsonOptions);

        return $"""
{request.AgentDefinition}

## Browser Console facade runtime addendum

You are the singleton browser Console facade for Agentweaver. Behave like a chat
operator: infer the right Agentweaver tool from natural language, call safe
read-only tools directly when context is sufficient, and ask exactly one focused
clarifying question only when a project/run/tool target is genuinely ambiguous.

Current Console context:
```json
{context}
```

Hard guardrails:
- The tool set available here is intentionally read-only/status-oriented. If the
  user asks to start budget-consuming work, delete/archive/remove, import memory,
  stop/cancel, confirm an outcome, approve/reject review, merge, or otherwise pass
  a human gate, do NOT claim you executed it. Say the action requires an explicit
  gate/confirmation and name the missing target if needed.
- Preserve truthful steering semantics. For an existing coordinator run, "send a
  note/message/context" means coordinator steering at a safe boundary; it is not a generic chat conversation
  with the coordinator. If the user asks to redirect,
  amend, or stop, say that an explicit steering gate/control is required.
- Never invent project or run IDs. List/discover first when IDs are unknown.
- Keep responses concise and include the relevant IDs you inspected.

Available safe tools include:
project_list, project_get, project_list_runs, backlog_get_board, run_status,
coordinator_work_plan_get, coordinator_children_get, orchestration_topology,
list_blueprints, catalog_list_roles, workflows_list, decision_list,
decision_inbox_list, memory_list.
""";
    }

    private static List<ChatMessage> BuildMessages(ConsoleFacadeAgentRequest request)
    {
        var messages = new List<ChatMessage>();
        foreach (var history in request.History.TakeLast(12))
        {
            var role = string.Equals(history.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User;
            messages.Add(new ChatMessage(role, history.Text));
        }

        messages.Add(new ChatMessage(ChatRole.User, request.Message));
        return messages;
    }

    private static string? ExtractFinalMessageContent(AgentResponseUpdate chunk)
    {
        if (chunk.Contents is null) return null;
        foreach (var content in chunk.Contents)
        {
            if (content.RawRepresentation is AssistantMessageEvent message)
                return message.Data?.Content;
        }
        return null;
    }
}

internal static class ConsoleFacadeApiTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static IEnumerable<AIFunction> Build(
        string apiBaseUrl,
        string? authorizationHeader,
        ConcurrentBag<ConsoleFacadeToolCall> calls)
    {
        var http = CreateHttpClient(apiBaseUrl, authorizationHeader);

        yield return AIFunctionFactory.Create(
            async (CancellationToken ct = default) =>
                await ExecuteAsync(calls, "project_list", () => GetJsonAsync(http, "api/projects", ct)).ConfigureAwait(false),
            "project_list",
            "List Agentweaver projects visible to the current caller. Read-only.");

        yield return AIFunctionFactory.Create(
            async ([Description("Project ID to inspect")] string project_id, CancellationToken ct = default) =>
                await ExecuteAsync(calls, "project_get", () => GetJsonAsync(http, $"api/projects/{Esc(project_id)}", ct)).ConfigureAwait(false),
            "project_get",
            "Get one Agentweaver project's metadata. Read-only.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Project ID whose runs should be listed")] string project_id,
                [Description("Include coordinator child runs when true. Defaults to false.")] bool? include_children = null,
                CancellationToken ct = default) =>
                await ExecuteAsync(
                    calls,
                    "project_list_runs",
                    () => GetJsonAsync(http,
                        $"api/projects/{Esc(project_id)}/runs?include_children={(include_children ?? false).ToString().ToLowerInvariant()}",
                        ct)).ConfigureAwait(false),
            "project_list_runs",
            "List runs for a project. Read-only.");

        yield return AIFunctionFactory.Create(
            async ([Description("Project ID whose backlog board should be read")] string project_id, CancellationToken ct = default) =>
                await ExecuteAsync(calls, "backlog_get_board", () => GetJsonAsync(http, $"api/projects/{Esc(project_id)}/board", ct)).ConfigureAwait(false),
            "backlog_get_board",
            "Get the project's backlog/ready/active/done board. Read-only.");

        yield return AIFunctionFactory.Create(
            async ([Description("Run ID to inspect")] string run_id, CancellationToken ct = default) =>
                await ExecuteAsync(calls, "run_status", () => GetJsonAsync(http, $"api/runs/{Esc(run_id)}", ct)).ConfigureAwait(false),
            "run_status",
            "Get run status and metadata. Read-only.");

        yield return AIFunctionFactory.Create(
            async ([Description("Coordinator run ID")] string run_id, CancellationToken ct = default) =>
                await ExecuteAsync(calls, "coordinator_work_plan_get", () => GetJsonAsync(http, $"api/runs/{Esc(run_id)}/work-plan", ct)).ConfigureAwait(false),
            "coordinator_work_plan_get",
            "Get a coordinator work plan. Read-only.");

        yield return AIFunctionFactory.Create(
            async ([Description("Coordinator run ID")] string run_id, CancellationToken ct = default) =>
                await ExecuteAsync(calls, "coordinator_children_get", () => GetJsonAsync(http, $"api/runs/{Esc(run_id)}/children", ct)).ConfigureAwait(false),
            "coordinator_children_get",
            "List a coordinator run's child runs. Read-only.");

        yield return AIFunctionFactory.Create(
            async ([Description("Coordinator run ID")] string run_id, CancellationToken ct = default) =>
                await ExecuteAsync(calls, "orchestration_topology", async () =>
                {
                    var plan = await GetJsonElementAsync(http, $"api/runs/{Esc(run_id)}/work-plan", ct).ConfigureAwait(false);
                    var children = await GetJsonElementAsync(http, $"api/runs/{Esc(run_id)}/children", ct).ConfigureAwait(false);
                    return JsonSerializer.Serialize(new { run_id, work_plan = plan, children }, JsonOptions);
                }).ConfigureAwait(false),
            "orchestration_topology",
            "Get a combined work-plan and children topology snapshot. Read-only.");

        yield return AIFunctionFactory.Create(
            async (CancellationToken ct = default) =>
                await ExecuteAsync(calls, "list_blueprints", () => GetJsonAsync(http, "api/blueprints", ct)).ConfigureAwait(false),
            "list_blueprints",
            "List predefined Agentweaver blueprints. Read-only.");

        yield return AIFunctionFactory.Create(
            async (CancellationToken ct = default) =>
                await ExecuteAsync(calls, "catalog_list_roles", () => GetJsonAsync(http, "api/catalog/roles", ct)).ConfigureAwait(false),
            "catalog_list_roles",
            "List available agent roles. Read-only.");

        yield return AIFunctionFactory.Create(
            async ([Description("Project ID whose workflows should be listed")] string project_id, CancellationToken ct = default) =>
                await ExecuteAsync(calls, "workflows_list", () => GetJsonAsync(http, $"api/projects/{Esc(project_id)}/workflows", ct)).ConfigureAwait(false),
            "workflows_list",
            "List discovered workflows for a project. Read-only.");

        yield return AIFunctionFactory.Create(
            async ([Description("Project ID whose decisions should be listed")] string project_id, CancellationToken ct = default) =>
                await ExecuteAsync(calls, "decision_list", () => GetJsonAsync(http, $"api/projects/{Esc(project_id)}/decisions", ct)).ConfigureAwait(false),
            "decision_list",
            "List merged project decisions. Read-only.");

        yield return AIFunctionFactory.Create(
            async ([Description("Project ID whose decision inbox should be listed")] string project_id, CancellationToken ct = default) =>
                await ExecuteAsync(calls, "decision_inbox_list", () => GetJsonAsync(http, $"api/projects/{Esc(project_id)}/decisions/inbox?status=pending", ct)).ConfigureAwait(false),
            "decision_inbox_list",
            "List pending decision inbox entries. Read-only.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Project ID whose memory should be listed")] string project_id,
                [Description("Optional agent name. Omit for all project memory.")] string? agent = null,
                CancellationToken ct = default) =>
                await ExecuteAsync(
                    calls,
                    "memory_list",
                    () => string.IsNullOrWhiteSpace(agent)
                        ? GetJsonAsync(http, $"api/projects/{Esc(project_id)}/memory", ct)
                        : GetJsonAsync(http, $"api/projects/{Esc(project_id)}/agents/{Esc(agent!)}/memory", ct)).ConfigureAwait(false),
            "memory_list",
            "List project or agent memory entries. Read-only.");
    }

    private static async Task<string> ExecuteAsync(
        ConcurrentBag<ConsoleFacadeToolCall> calls,
        string name,
        Func<Task<string>> action)
    {
        try
        {
            var result = await action().ConfigureAwait(false);
            calls.Add(new ConsoleFacadeToolCall(name, "completed", Summarize(result)));
            return result;
        }
        catch (Exception ex)
        {
            calls.Add(new ConsoleFacadeToolCall(name, "failed", ex.Message));
            return $"{name} failed: {ex.Message}";
        }
    }

    private static HttpClient CreateHttpClient(string apiBaseUrl, string? authorizationHeader)
    {
        var http = new HttpClient { BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/") };
        if (!string.IsNullOrWhiteSpace(authorizationHeader)
            && AuthenticationHeaderValue.TryParse(authorizationHeader, out var auth))
        {
            http.DefaultRequestHeaders.Authorization = auth;
        }
        return http;
    }

    private static async Task<string> GetJsonAsync(HttpClient http, string path, CancellationToken ct)
    {
        var response = await http.GetAsync(path, ct).ConfigureAwait(false);
        var content = string.Empty;
        try { content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
        return response.IsSuccessStatusCode
            ? content
            : $"GET {path} failed: HTTP {(int)response.StatusCode} — {content}";
    }

    private static async Task<JsonElement> GetJsonElementAsync(HttpClient http, string path, CancellationToken ct)
    {
        var json = await GetJsonAsync(http, path, ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse(JsonSerializer.Serialize(json, JsonOptions)).RootElement.Clone();
        }
    }

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private static string Summarize(string result)
    {
        if (string.IsNullOrWhiteSpace(result)) return "empty response";
        var compact = result.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return compact.Length <= 180 ? compact : compact[..180] + "…";
    }
}
