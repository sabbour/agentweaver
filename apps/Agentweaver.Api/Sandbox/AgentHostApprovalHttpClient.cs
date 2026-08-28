using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Api.Sandbox;

public sealed record AgentHostApprovalOutcome(
    bool Resolved,
    string State,
    bool Unreachable,
    int? StatusCode,
    bool Applied = false,
    string? ToolName = null,
    string? Url = null);

public interface IAgentHostApprovalHttpClient
{
    Task<AgentHostApprovalOutcome> GrantAsync(
        string childRunId,
        string requestId,
        string scope,
        string? bearer,
        CancellationToken ct);

    Task<AgentHostApprovalOutcome> DenyAsync(
        string childRunId,
        string requestId,
        string? bearer,
        CancellationToken ct);
}

public sealed class AgentHostApprovalHttpClient(
    IHttpClientFactory httpClientFactory,
    IAgentHostOriginResolver originResolver,
    ILogger<AgentHostApprovalHttpClient> logger) : IAgentHostApprovalHttpClient
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    public Task<AgentHostApprovalOutcome> GrantAsync(
        string childRunId,
        string requestId,
        string scope,
        string? bearer,
        CancellationToken ct) =>
        SendAsync(childRunId, requestId, scope, bearer, "/tool-approvals", ct);

    public Task<AgentHostApprovalOutcome> DenyAsync(
        string childRunId,
        string requestId,
        string? bearer,
        CancellationToken ct) =>
        SendAsync(childRunId, requestId, scope: null, bearer, "/tool-denials", ct);

    private async Task<AgentHostApprovalOutcome> SendAsync(
        string childRunId,
        string requestId,
        string? scope,
        string? bearer,
        string path,
        CancellationToken ct)
    {
        var origin = await originResolver.TryResolveOriginAsync(childRunId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(origin))
            return new(false, "unreachable", true, null);

        using var request = new HttpRequestMessage(HttpMethod.Post, origin.TrimEnd('/') + path)
        {
            Content = scope is null
                ? JsonContent.Create(new { runId = childRunId, requestId })
                : JsonContent.Create(new { runId = childRunId, requestId, scope }),
        };
        if (!string.IsNullOrEmpty(bearer))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CallTimeout);

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient("a2a-sandbox-pod")
                .SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, "unreachable", true, null);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "AgentHost approval call failed for run {RunId}", childRunId);
            return new(false, "unreachable", true, null);
        }

        using (response)
        {
            ApprovalResponse? body = null;
            try
            {
                body = await response.Content.ReadFromJsonAsync<ApprovalResponse>(timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "AgentHost approval response was invalid for run {RunId} (HTTP {StatusCode})",
                    childRunId,
                    (int)response.StatusCode);
            }

            var state = body?.State ?? "error";
            return new(
                body?.Resolved ?? false,
                state,
                Unreachable: (int)response.StatusCode >= 500,
                StatusCode: (int)response.StatusCode,
                Applied: body?.Applied ?? false,
                ToolName: body?.ToolName,
                Url: body?.Url);
        }
    }

    private sealed record ApprovalResponse
    {
        [JsonPropertyName("resolved")] public bool Resolved { get; init; }
        [JsonPropertyName("applied")] public bool Applied { get; init; }
        [JsonPropertyName("state")] public string? State { get; init; }
        [JsonPropertyName("toolName")] public string? ToolName { get; init; }
        [JsonPropertyName("url")] public string? Url { get; init; }
    }
}
