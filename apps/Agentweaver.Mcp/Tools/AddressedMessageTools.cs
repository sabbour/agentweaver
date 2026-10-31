using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

[McpServerToolType]
public sealed class AddressedMessageTools(AgentweaverApiClient api)
{
    private static string Route(string project) =>
        $"api/projects/{Uri.EscapeDataString(project)}/agent-messages";

    [McpServerTool(Name = "agent_message_send"), Description("Persist an addressed message to one teammate's active run; acknowledgment only confirms receipt.")]
    public async Task<string> SendAsync(
        [Description("Project ID")] string project_id,
        [Description("Active teammate name")] string recipient,
        [Description("Recipient's active run ID")] string target_run_id,
        [Description("Message content")] string content,
        [Description("Retry-stable idempotency key")] string idempotency_key,
        [Description("Acknowledged message ID to reply to")] string? reply_to_id = null,
        [Description("Optional backlog_task, work_plan, or finding")] string? reference_kind = null,
        [Description("Optional task, plan, or finding ID")] string? reference_id = null,
        CancellationToken ct = default) =>
        JsonSerializer.Serialize(await api.PostAsync<object>(Route(project_id),
            new { recipient, target_run_id, content, idempotency_key, reply_to_id, reference_kind, reference_id }, ct));

    [McpServerTool(Name = "agent_message_list"), Description("List addressed messages and delivery diagnostics visible to this caller.")]
    public async Task<string> ListAsync(
        [Description("Project ID")] string project_id,
        [Description("Optional run ID filter for operator callers")] string? run_id = null,
        CancellationToken ct = default) =>
        JsonSerializer.Serialize(await api.GetAsync<object>(
            Route(project_id) + (run_id is null ? "" : $"?run_id={Uri.EscapeDataString(run_id)}"), ct));

    [McpServerTool(Name = "agent_message_get"), Description("Get one addressed message's state and correlation.")]
    public async Task<string> GetAsync(
        [Description("Project ID")] string project_id,
        [Description("Message ID")] string message_id,
        CancellationToken ct = default) =>
        JsonSerializer.Serialize(await api.GetAsync<object>(
            $"{Route(project_id)}/{Uri.EscapeDataString(message_id)}", ct));

    [McpServerTool(Name = "agent_message_retry"), Description("Retry your expired or undeliverable message against an active recipient run with a new idempotency key.")]
    public async Task<string> RetryAsync(
        [Description("Project ID")] string project_id,
        [Description("Failed message ID")] string message_id,
        [Description("New retry-stable idempotency key")] string idempotency_key,
        [Description("Optional replacement active recipient run ID")] string? target_run_id = null,
        CancellationToken ct = default) =>
        JsonSerializer.Serialize(await api.PostAsync<object>(
            $"{Route(project_id)}/{Uri.EscapeDataString(message_id)}/retry",
            new { idempotency_key, target_run_id }, ct));

    [McpServerTool(Name = "agent_message_claim"), Description("At a recipient turn boundary, lease the oldest pending addressed message.")]
    public async Task<string> ClaimAsync(
        [Description("Project ID")] string project_id,
        [Description("Unique claim-attempt owner")] string owner,
        CancellationToken ct = default) =>
        JsonSerializer.Serialize(await api.PostAsync<object>(
            $"{Route(project_id)}/claim", new { owner }, ct));

    [McpServerTool(Name = "agent_message_deliver"), Description("Record delivery only after presenting the claimed message at a safe turn boundary.")]
    public async Task<string> DeliverAsync(
        [Description("Project ID")] string project_id,
        [Description("Message ID")] string message_id,
        [Description("Claim owner")] string owner,
        [Description("Claim fence returned by claim")] long fence,
        CancellationToken ct = default) =>
        JsonSerializer.Serialize(await api.PostAsync<object>(
            $"{Route(project_id)}/{Uri.EscapeDataString(message_id)}/deliver", new { owner, fence }, ct));

    [McpServerTool(Name = "agent_message_acknowledge"), Description("Acknowledge a delivered message as received; does not change task or decision state.")]
    public async Task<string> AcknowledgeAsync(
        [Description("Project ID")] string project_id,
        [Description("Message ID")] string message_id,
        CancellationToken ct = default) =>
        JsonSerializer.Serialize(await api.PostAsync<object>(
            $"{Route(project_id)}/{Uri.EscapeDataString(message_id)}/acknowledge", null, ct));
}
