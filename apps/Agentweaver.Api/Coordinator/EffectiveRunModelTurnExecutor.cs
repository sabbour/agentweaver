using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.GitHub.Copilot;

namespace Agentweaver.Api.Coordinator;

/// <summary>Runs constrained classifiers through the fingerprinted effective-provider boundary.</summary>
public sealed class EffectiveRunModelTurnExecutor(
    GitHubCopilotClientFactory clientFactory,
    RunModelInvocationGuard invocationGuard,
    IByokProviderConfigurationProvider byokProviderConfiguration)
{
    internal static bool IsProviderChange(Exception exception) =>
        exception is AiExecutionPlanException
        || exception is AgentProviderException { ErrorCode: "model_provider_changed" };

    public async Task<string?> RunAsync(
        string runId, string? projectId, string? modelId, string charter, string prompt,
        bool supportsByok, CancellationToken ct)
    {
        var boundary = await invocationGuard.PrepareAsync(runId, ct, supportsByok).ConfigureAwait(false);
        var byok = await ResolveByokConfigurationAsync(boundary, ct).ConfigureAwait(false);
        await using var client = byok is null
            ? await clientFactory.CreateClientAsync(runId, modelId, ct).ConfigureAwait(false)
            : clientFactory.CreateByokClient();
        await client.StartAsync(ct).ConfigureAwait(false);
        AIAgent? agent = null;
        try
        {
            agent = client.AsAIAgent(new SessionConfig
            {
                SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Append, Content = charter },
                Tools = [],
                AvailableTools = [],
                OnPermissionRequest = CopilotWorkflowSelectionModel.RejectAllToolPermissionHandler,
                Model = byok?.Model ?? modelId,
                Provider = byok is null ? null : new ProviderConfig
                {
                    Type = byok.Type,
                    BaseUrl = byok.BaseUrl,
                    ApiKey = byok.ApiKey,
                    WireApi = byok.WireApi ?? "responses",
                    Headers = ByokProviderConfigMapper.ToHeaderDictionary(byok.Headers),
                    Azure = ByokProviderConfigMapper.ToAzureOptions(byok),
                },
                EnableConfigDiscovery = false,
                Streaming = true,
                EnableSessionStore = false,
                InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            }, ownsClient: false, id: null, name: null, description: null);
            var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            var validatedBoundary = await invocationGuard.PrepareAsync(runId, ct, supportsByok).ConfigureAwait(false);
            _ = await ResolveByokConfigurationAsync(validatedBoundary, ct).ConfigureAwait(false);
            return await CopilotWorkflowSelectionModel.CaptureResponseTextAsync(
                agent.RunStreamingAsync(prompt, session, options: null, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            if (agent is IAsyncDisposable disposable)
                await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<ByokProviderConfiguration?> ResolveByokConfigurationAsync(
        ResolvedRunModelProviderBoundary boundary,
        CancellationToken ct)
    {
        if (boundary.Provider is not EffectiveModelProviderResult.Byok)
            return null;

        var configuration = await byokProviderConfiguration.GetAsync(ct).ConfigureAwait(false);
        if (configuration is null
            || !string.Equals(
                configuration.ExecutionFingerprint(),
                boundary.ByokProviderFingerprint,
                StringComparison.Ordinal))
        {
            throw new AgentProviderException(
                ModelSource.Byok,
                AgentProviderFailureKind.Configuration,
                "model_provider_changed",
                "The effective BYOK provider changed before model invocation.",
                isRetryable: true);
        }

        return configuration;
    }
}
