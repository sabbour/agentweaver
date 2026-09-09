using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Auth;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.GitHub.Copilot;

namespace Agentweaver.Api.Coordinator;

/// <summary>Runs the existing Copilot-only classifiers through the fingerprinted run boundary.</summary>
public sealed class EffectiveRunModelTurnExecutor(
    GitHubCopilotClientFactory clientFactory,
    RunModelInvocationGuard invocationGuard)
{
    internal static bool IsProviderChange(Exception exception) =>
        exception is AiExecutionPlanException
        || exception is AgentProviderException { ErrorCode: "model_provider_changed" };

    public async Task<string?> RunAsync(
        string runId, string? projectId, string? modelId, string charter, string prompt,
        CancellationToken ct)
    {
        await invocationGuard.PrepareAsync(runId, ct, supportsByok: false).ConfigureAwait(false);
        await using var client = await clientFactory.CreateClientAsync(runId, modelId, ct).ConfigureAwait(false);
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
                Model = modelId,
                EnableConfigDiscovery = false,
                Streaming = true,
                EnableSessionStore = false,
                InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            }, ownsClient: false, id: null, name: null, description: null);
            var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            await invocationGuard.PrepareAsync(runId, ct, supportsByok: false).ConfigureAwait(false);
            return await CopilotWorkflowSelectionModel.CaptureResponseTextAsync(
                agent.RunStreamingAsync(prompt, session, options: null, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            if (agent is IAsyncDisposable disposable)
                await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }
}
