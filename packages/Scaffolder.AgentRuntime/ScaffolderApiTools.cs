using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.AI;

namespace Scaffolder.AgentRuntime;

/// <summary>
/// Factory that produces <see cref="AIFunction"/> definitions for Scaffolder API operations.
/// These are injected into the agent's <c>SessionConfig.Tools</c> list so the model can invoke
/// them as structured tool calls rather than via bash/curl instructions in the task prompt.
/// </summary>
internal static class ScaffolderApiTools
{
    /// <summary>
    /// Builds the Scaffolder API <see cref="AIFunction"/> instances for the given project/agent
    /// context. Each function captures <paramref name="projectId"/>, <paramref name="agentName"/>,
    /// and the HTTP client in a closure so callers require no runtime arguments beyond the
    /// parameters declared in the tool schema.
    /// </summary>
    /// <param name="projectId">The project ID used in API path construction.</param>
    /// <param name="agentName">The agent name used as the submitter identity in API requests.</param>
    /// <param name="apiBaseUrl">The Scaffolder API base URL (e.g. <c>http://localhost:5000</c>).</param>
    /// <param name="apiKey">Bearer token for API authentication; may be null for unauthenticated local dev.</param>
    internal static IEnumerable<AIFunction> Build(
        string projectId, string agentName, string apiBaseUrl, string? apiKey)
    {
        var http = CreateHttpClient(apiBaseUrl, apiKey);

        yield return AIFunctionFactory.Create(
            async (
                [Description("Unique kebab-case slug for idempotency, e.g. 'prefer-async-over-sync'")] string slug,
                [Description("Decision type: architectural | scope | process | pattern | technical")] string type,
                [Description("Short, descriptive title for the decision")] string title,
                [Description("Full decision content — what was decided and why")] string content,
                [Description("Optional rationale explaining the trade-offs considered")] string? rationale = null,
                CancellationToken ct = default) =>
            {
                var response = await http.PostAsJsonAsync(
                    $"api/projects/{projectId}/decisions/inbox",
                    new { agent_name = agentName, slug, type, title, content, rationale },
                    ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return $"Decision submitted to inbox: {title}";
            },
            "submit_decision",
            "Submit an architectural, scope, or process decision to the project decision inbox for team review. " +
            "Use this whenever you make a significant design choice that the team should know about.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Memory type: learning | pattern | core_context | update")] string type,
                [Description("Importance level: low | medium | high")] string importance,
                [Description("The memory content to record")] string content,
                [Description("Comma-separated tags for future retrieval (optional)")] string? tags = null,
                CancellationToken ct = default) =>
            {
                var response = await http.PostAsJsonAsync(
                    $"api/projects/{projectId}/agents/{agentName}/memory",
                    new { type, importance, content, tags },
                    ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return "Memory recorded.";
            },
            "record_memory",
            "Record a learning, pattern, or insight into agent memory for future reference. " +
            "Use this to persist important discoveries, gotchas, or patterns encountered during the task.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Progress note or summary text to append to the current session log")] string summary,
                CancellationToken ct = default) =>
            {
                var response = await http.PutAsJsonAsync(
                    $"api/projects/{projectId}/sessions/current",
                    new { summary },
                    ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return "Session updated.";
            },
            "update_session",
            "Append a progress note or completion summary to the current project session log. " +
            "Use this to record what was accomplished or to flag work-in-progress state.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Unique kebab-case slug for idempotency, e.g. 'boundary-conflict-auth-scope'")] string slug,
                [Description("Entry type: learning | pattern | update | architectural | scope | process | technical")] string type,
                [Description("Short, descriptive title")] string title,
                [Description("Full entry content")] string content,
                CancellationToken ct = default) =>
            {
                var response = await http.PostAsJsonAsync(
                    $"api/projects/{projectId}/inbox",
                    new { agent_name = agentName, slug, type, title, content },
                    ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return $"Inbox entry submitted: {title}";
            },
            "submit_inbox_entry",
            "Submit a general inbox entry such as a learning, update, or boundary conflict flag. " +
            "For boundary conflicts use type 'process' and title 'Boundary conflict: [short description]'.");
    }

    private static HttpClient CreateHttpClient(string apiBaseUrl, string? apiKey)
    {
        var http = new HttpClient { BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + '/') };
        if (!string.IsNullOrEmpty(apiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return http;
    }
}
