using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;

namespace Agentweaver.AgentHost;

internal sealed class AgentHostModelInvocationGuard(
    AgentHostRuntimeState runtimeState,
    IHttpClientFactory httpClientFactory) : IModelInvocationGuard
{
    public async Task ValidateAsync(string runId, CancellationToken ct)
    {
        var access = runtimeState.ToolApprovalApiAccess;
        if (!runtimeState.IsConfigured
            || !string.Equals(runId, runtimeState.RunId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(runtimeState.ModelProviderKey)
            || string.IsNullOrWhiteSpace(runtimeState.TurnBearerToken)
            || access is null
            || !Uri.TryCreate(access.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw Changed();

        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(baseUri, $"/api/runs/{Uri.EscapeDataString(runId)}/model-provider/validate"))
        {
            Content = JsonContent.Create(new { expected_provider_key = runtimeState.ModelProviderKey }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runtimeState.TurnBearerToken);
        request.Headers.TryAddWithoutValidation(RunAuthorshipHeaders.RunId, runId);
        request.Headers.TryAddWithoutValidation(RunAuthorshipHeaders.RunToken, runtimeState.TurnBearerToken);
        using var response = await httpClientFactory.CreateClient("agentweaver-api")
            .SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw Changed();
        if (response.StatusCode != HttpStatusCode.NoContent)
            throw new AgentProviderException(
                Source, AgentProviderFailureKind.ProviderUnavailable,
                "model_provider_validation_unavailable",
                "Model provider validation is unavailable. No model call was started.", isRetryable: true);
    }

    private ModelSource Source => runtimeState.ByokProviderConfiguration is null
        ? ModelSource.GitHubCopilot : ModelSource.Byok;

    private AgentProviderException Changed() => new(
        Source, AgentProviderFailureKind.Configuration, "model_provider_changed",
        "The accepted model provider changed before invocation.", isRetryable: true);
}
