using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Agentweaver.AgentRuntime;
using Agentweaver.Domain;

namespace Agentweaver.AgentHost;

/// <summary>
/// Keeps pending requests local to the pod while asking the API for scope policies. The API
/// validates the run-bound capability, so a pod can only read policies that apply to its own run.
/// </summary>
internal sealed class AgentHostDurableToolApprovalGate(
    AgentHostRuntimeState runtimeState,
    IAgentHostToolApprovalPolicyClient policyClient) : IToolApprovalGate
{
    private readonly InMemoryToolApprovalGate _local =
        new(new AgentHostToolApprovalOwnerResolver(runtimeState));

    public Task<bool> WaitForApprovalAsync(
        string runId,
        string requestId,
        string toolName,
        string? url,
        TimeSpan timeout,
        CancellationToken ct) =>
        _local.WaitForApprovalAsync(runId, requestId, toolName, url, timeout, ct);

    public Task<bool> GrantAsync(string runId, string requestId, ApprovalScope scope) =>
        _local.GrantAsync(runId, requestId, scope);

    public bool Deny(string runId, string requestId) =>
        _local.Deny(runId, requestId);

    public bool IsAutoApproved(string runId, string toolName, string? url)
    {
        if (_local.IsAutoApproved(runId, toolName, url))
            return true;

        if (!string.Equals(runId, runtimeState.RunId, StringComparison.Ordinal))
            return false;

        try
        {
            return policyClient.IsAutoApprovedAsync(runId, toolName, url, CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            return false;
        }
    }

    public ToolApprovalRequestState GetRequestState(string runId, string requestId) =>
        _local.GetRequestState(runId, requestId);

    public ToolApprovalRequestContext? GetRequestContext(string runId, string requestId) =>
        _local.GetRequestContext(runId, requestId);

    public bool IsKnownRequest(string runId, string requestId) =>
        _local.IsKnownRequest(runId, requestId);

    public bool HasArmedApproval(string runId) =>
        _local.HasArmedApproval(runId);

    public void Clear(string runId) =>
        _local.Clear(runId);

    public void RegisterParentRun(string childRunId, string parentRunId) =>
        _local.RegisterParentRun(childRunId, parentRunId);
}

internal interface IAgentHostToolApprovalPolicyClient
{
    Task<bool> IsAutoApprovedAsync(
        string runId,
        string toolName,
        string? url,
        CancellationToken ct);
}

internal sealed class AgentHostToolApprovalPolicyClient(
    AgentHostRuntimeState runtimeState,
    IHttpClientFactory httpClientFactory,
    ILogger<AgentHostToolApprovalPolicyClient> logger) : IAgentHostToolApprovalPolicyClient
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    public async Task<bool> IsAutoApprovedAsync(
        string runId,
        string toolName,
        string? url,
        CancellationToken ct)
    {
        var access = runtimeState.ToolApprovalApiAccess;
        if (!runtimeState.IsConfigured
            || !string.Equals(runId, runtimeState.RunId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(toolName)
            || string.IsNullOrWhiteSpace(runtimeState.TurnBearerToken)
            || access is null
            || string.IsNullOrWhiteSpace(access.BearerToken)
            || !Uri.TryCreate(access.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var path = $"/api/runs/{Uri.EscapeDataString(runId)}/tool-approval-policies/{Uri.EscapeDataString(toolName)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.BearerToken);
        request.Headers.TryAddWithoutValidation(RunAuthorshipHeaders.RunId, runId);
        request.Headers.TryAddWithoutValidation(RunAuthorshipHeaders.RunToken, runtimeState.TurnBearerToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CallTimeout);
        try
        {
            using var response = await httpClientFactory.CreateClient("agentweaver-api")
                .SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;

            var body = await response.Content
                .ReadFromJsonAsync<ToolApprovalPolicyResponse>(timeoutCts.Token)
                .ConfigureAwait(false);
            return body?.AutoApproved == true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning(
                "AgentHost could not read durable tool-approval policy for run {RunId}; keeping the tool gated.",
                runId);
            return false;
        }
    }

    private sealed record ToolApprovalPolicyResponse
    {
        [JsonPropertyName("auto_approved")]
        public bool AutoApproved { get; init; }
    }
}
