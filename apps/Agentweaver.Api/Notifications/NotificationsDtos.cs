using System.Text.Json.Serialization;

namespace Agentweaver.Api.Notifications;

/// <summary>
/// A single pending item the signed-in user needs to act on (Human Review or Tool Approval),
/// surfaced by the global notification center (#247). Both Human Review and Tool Approval (#321)
/// are covered (see NotificationsService remarks).
/// </summary>
public sealed record NotificationDto
{
    /// <summary>Stable id for client-side de-duplication across polls (e.g. "review:{runId}").</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>"human_review" or "tool_approval".</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }

    [JsonPropertyName("project_name")]
    public string? ProjectName { get; init; }

    [JsonPropertyName("agent_name")]
    public string? AgentName { get; init; }

    /// <summary>Short human-readable label (truncated run task) shown in the toast/bell list.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("created_utc")]
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>App-relative path the CTA should navigate to (deep-links straight to the run).</summary>
    [JsonPropertyName("cta_path")]
    public required string CtaPath { get; init; }
}

/// <summary>Response body for GET /api/notifications.</summary>
public sealed record NotificationsResponseDto
{
    [JsonPropertyName("generated_utc")]
    public required DateTimeOffset GeneratedUtc { get; init; }

    [JsonPropertyName("notifications")]
    public required IReadOnlyList<NotificationDto> Notifications { get; init; }
}
