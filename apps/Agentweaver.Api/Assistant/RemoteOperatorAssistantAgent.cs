using System.Text.Json;
using System.Threading.Channels;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentweaver.Api.Assistant;

/// <summary>
/// Production <see cref="IOperatorAssistantAgent"/> (narrow AgentHost cutover, #346/#347): dispatches
/// every operator turn to a sandbox AgentHost pod over the SAME warm-pool claim, <c>/configure</c>,
/// and A2A streaming mechanism the Coordinator already uses for subtask children — instead of
/// running the Copilot/MCP loop in-process in the API pod (the retired <see cref="OperatorAssistantAgent"/>,
/// which now runs ONLY inside the AgentHost pod under <c>AgentHostPurpose.OperatorAssistant</c>).
///
/// <para>
/// <see cref="AssistantRunService"/> is completely unaware of this swap: it still calls
/// <see cref="IOperatorAssistantAgent.RunTurnAsync"/> with the same request/sink contract, still owns
/// the conversation's persisted <c>Run</c>/event-stream/history, and its <c>IToolApprovalGate</c>
/// wait is not used on this path — the pod's OWN <c>IToolApprovalGate</c> is the source of truth for
/// gated tool calls, resolved by the operator's existing <c>/api/runs/{id}/tool-approvals</c> /
/// <c>tool-denials</c> endpoints via their established <c>AgentHostApprovalHttpClient</c> fallback
/// (the same fallback a sandboxed coordinator subtask's HITL gate, e.g. <c>web_fetch</c>, already
/// relies on). Only the three <c>tool.approval_*</c> event types are forwarded verbatim from the pod;
/// tool-call/result events are re-derived through the caller's own sink so they carry the correct
/// conversational message id.
/// </para>
///
/// <para>
/// Each turn is fully self-contained (the caller always replays the bounded conversation history), so
/// the pod is claimed fresh and released after every turn rather than held for the conversation's
/// whole lifetime — reusing <see cref="IAgentHostPodLifecycle"/>'s existing launch/release lifecycle
/// exactly as coordinator subtasks do, with no new pod/claim bookkeeping.
/// </para>
/// </summary>
public sealed class RemoteOperatorAssistantAgent(
    ISandboxAgentEndpointResolver endpointResolver,
    IAgentHostTurnTokenRegistry turnTokenRegistry,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    IOptions<RemoteAgentProxyOptions> proxyOptions,
    IConfiguration configuration,
    IRunEventStream eventStream,
    ILogger<RemoteOperatorAssistantAgent> logger,
    IAgentHostPodLifecycle? podLifecycle = null,
    IServiceScopeFactory? scopeFactory = null) : IOperatorAssistantAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OperatorAssistantResponse> RunTurnAsync(
        OperatorAssistantRequest request,
        IOperatorAssistantTurnSink? sink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runId = request.ConversationId;
        if (string.IsNullOrWhiteSpace(request.McpBrokerToken))
            throw new InvalidOperationException(
                "The operator assistant AgentHost requires a per-turn MCP broker token.");
        if (request.RenewMcpBrokerTokenAsync is null)
            throw new InvalidOperationException(
                "The operator assistant AgentHost requires a server-side MCP broker token renewal callback.");

        // IAgentHostPodLifecycle is only registered in-cluster (mirrors every other pod-per-run
        // consumer, e.g. KubernetesPodAgentEndpointResolver's optional podLifecycle) so DI validation
        // still succeeds outside Kubernetes (local dev, unit/integration tests that boot the full
        // host) — those callers never reach this production path because AssistantWebApplicationFactory
        // and friends replace IOperatorAssistantAgent with an explicit fake before any request runs.
        if (podLifecycle is null)
        {
            throw new AgentProviderException(
                ModelSource.GitHubCopilot,
                AgentProviderFailureKind.ProviderUnavailable,
                "agenthost_unavailable",
                $"Run {runId}: the operator assistant requires a Kubernetes AgentHost pod lifecycle, which is not available outside a cluster.",
                isRetryable: false);
        }

        try
        {
            await EnsureAgentHostCapabilityAsync(runId, scopeFactory, ct).ConfigureAwait(false);
            await podLifecycle.LaunchAgentHostPodAsync(
                runId,
                new AgentHostLaunchContext(
                    SharedWorkingDirectory: null,
                    Purpose: AgentHostPurpose.OperatorAssistant,
                    McpBrokerToken: request.McpBrokerToken),
                ct).ConfigureAwait(false);
            await RefreshMcpBrokerTokenAsync(request, podLifecycle, ct).ConfigureAwait(false);
        }
        catch (ModelProviderConnectionRequiredException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ClassifyOrWrap(ex, runId, "AgentHost pod launch failed");
        }

        var proxy = new RemoteAgentProxy(
            endpointResolver,
            httpClientFactory,
            loggerFactory,
            RemoteWorkflowAgentFactory.ResolveRemoteApiBaseUrl(configuration),
            turnTokenRegistry,
            proxyOptions.Value);

        var channel = Channel.CreateUnbounded<RunEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        var invokedTools = new List<string>();

        try
        {
            var envelope = new OperatorAssistantTurnEnvelope(
                request.Message,
                request.AgentDefinition,
                request.GitHubLogin,
                request.RunId,
                request.History);
            var taskJson = JsonSerializer.Serialize(envelope, JsonOptions);

            await proxy.SetupAsync(
                workingDirectory: string.Empty,
                repositoryPath: string.Empty,
                runId: runId,
                modelId: request.ModelId,
                systemPromptContext: null,
                streamWriter: channel.Writer,
                projectId: request.ProjectId,
                agentName: AssistantRunService.OperatorAgentName,
                apiBaseUrl: null,
                apiKey: null,
                ct: ct,
                userId: request.CallerUser).ConfigureAwait(false);

            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var drainTask = DrainAsync(
                runId, channel.Reader, sink, invokedTools, request, podLifecycle, turnCts.Token);

            string text;
            try
            {
                var turnTask = proxy.RunTurnAsync(taskJson, isRevision: false, turnCts.Token);
                var completed = await Task.WhenAny(turnTask, drainTask).ConfigureAwait(false);
                if (completed == drainTask)
                {
                    turnCts.Cancel();
                    await drainTask.ConfigureAwait(false);
                    throw new InvalidOperationException(
                        "The operator assistant AgentHost event stream ended before the model turn completed.");
                }
                text = await turnTask.ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }

            await drainTask.ConfigureAwait(false);

            var trimmed = text.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                trimmed = "I could not produce an operator response. Try rephrasing the request with a project or run context.";

            return new OperatorAssistantResponse(trimmed, invokedTools);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ClassifyOrWrap(ex, runId, "Operator assistant turn failed on the AgentHost pod");
        }
        finally
        {
            await ((IAsyncDisposable)proxy).DisposeAsync().ConfigureAwait(false);
            try
            {
                await podLifecycle.ReleaseAgentHostPodAsync(runId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex, "RemoteOperatorAssistantAgent: failed to release AgentHost pod for run {RunId} (best-effort).", runId);
            }
        }
    }

    private static async Task EnsureAgentHostCapabilityAsync(
        string runId,
        IServiceScopeFactory? scopeFactory,
        CancellationToken ct)
    {
        // Unit seams may deliberately omit the production persistence services. In a production
        // registration Program always supplies the scope factory, so pod creation cannot bypass
        // this run/project/binding fence.
        if (scopeFactory is null)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var runStore = scope.ServiceProvider.GetRequiredService<IRunStore>();
        var run = await runStore.GetAsync(RunId.Parse(runId), ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Operator run '{runId}' was not found.");
        if (run.ModelSource != ModelSource.GitHubCopilot)
            return;

        var lifecycle = scope.ServiceProvider.GetRequiredService<RunGitHubCapabilitySnapshotLifecycle>();

        // Operator/Assistant turns are personal sessions, not project-scoped work: run.ProjectId
        // (when present) is only incidental UI context, so credential resolution must always go
        // through the PLATFORM-level Copilot connection rather than that project's own (possibly
        // broken/missing) binding. A failure always surfaces the platform-settings CTA.
        if (!await lifecycle.PrepareForUnattendedCopilotLaunchAsync(run, ct, platformScoped: true)
                .ConfigureAwait(false))
            throw new ModelProviderConnectionRequiredException();
    }

    /// <summary>
    /// Drains the pod's per-turn <see cref="RunEvent"/> side-channel. Tool call/result events are
    /// re-projected through the caller's own sink (so they persist with the CORRECT conversational
    /// message id, exactly as the retired in-process path did); the three tool-approval event types
    /// are appended to the run's event stream verbatim, because the pod's own approval gate — not
    /// this process's — is what the operator's grant/deny decision ultimately resolves.
    /// </summary>
    private async Task DrainAsync(
        string runId,
        ChannelReader<RunEvent> reader,
        IOperatorAssistantTurnSink? sink,
        List<string> invokedTools,
        OperatorAssistantRequest request,
        IAgentHostPodLifecycle podLifecycle,
        CancellationToken ct)
    {
        await foreach (var runEvent in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (runEvent.Payload is not JsonElement payload)
                continue;

            switch (runEvent.Type)
            {
                case EventTypes.ToolCall:
                    if (TryGetString(payload, "name", out var callName))
                    {
                        invokedTools.Add(callName);
                        var argsJson = TryGetString(payload, "arguments", out var args) ? args : null;
                        if (sink is not null)
                            await sink.OnToolCallAsync(callName, argsJson, ct).ConfigureAwait(false);
                    }
                    break;

                case EventTypes.ToolResult:
                case EventTypes.ToolError:
                    if (TryGetString(payload, "name", out var resultName) && sink is not null)
                        await sink.OnToolResultAsync(
                            resultName, success: runEvent.Type == EventTypes.ToolResult, ct).ConfigureAwait(false);
                    break;

                case EventTypes.ToolApprovalRequired:
                case EventTypes.ToolApprovalPending:
                case EventTypes.ToolApprovalResolved:
                    await eventStream.AppendAsync(runId, runEvent, ct).ConfigureAwait(false);
                    break;

                case EventTypes.McpBrokerTokenRefreshRequired:
                    await RefreshMcpBrokerTokenAsync(request, podLifecycle, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static async Task RefreshMcpBrokerTokenAsync(
        OperatorAssistantRequest request,
        IAgentHostPodLifecycle podLifecycle,
        CancellationToken ct)
    {
        var issuer = request.RenewMcpBrokerTokenAsync
            ?? throw new InvalidOperationException("The MCP broker token renewal callback is unavailable.");
        var token = await issuer(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("MCP broker token renewal returned an empty credential.");
        await podLifecycle.RefreshAgentHostMcpBrokerTokenAsync(request.ConversationId, token, ct)
            .ConfigureAwait(false);
    }

    private static bool TryGetString(JsonElement payload, string propertyName, out string value)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static Exception ClassifyOrWrap(Exception ex, string runId, string fallbackMessage) =>
        AgentProviderException.Classify(ModelSource.GitHubCopilot, ex, runId)
        ?? new AgentProviderException(
            ModelSource.GitHubCopilot,
            AgentProviderFailureKind.ProviderUnavailable,
            "agenthost_unavailable",
            $"Run {runId}: {fallbackMessage}: {ex.Message}",
            isRetryable: true,
            ex);
}
