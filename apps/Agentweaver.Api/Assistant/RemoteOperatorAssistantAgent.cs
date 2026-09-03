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
/// The pod is HELD for the conversation, not for a single turn. Claiming a warm pod and running its
/// one-shot <c>/configure</c> (which itself runs <c>CopilotAIAgent.SetupAsync</c> and starts a
/// Copilot/BYOK client from scratch) costs ~8s, and the surrounding claim-bind + readiness gate adds
/// several more — so releasing the pod in a per-turn <c>finally</c>, as this agent originally did,
/// paid that entire cold start again on EVERY message (measured live at 15-20s of silence per turn).
/// The pod is therefore released only when the turn FAILS; a successful turn leaves the claim in
/// place so the next message re-binds the very same, already-configured pod. Because <c>/configure</c>
/// is one-shot and cannot re-deliver the caller's platform bearer, the current token is instead
/// delivered on every turn through <see cref="RemoteAgentProxy.CallerBearerToken"/> (the per-turn
/// <c>AgentSetupParams</c> the pod already applies before each turn), so a held pod never serves MCP
/// calls with a stale credential.
/// </para>
///
/// <para>
/// Holding cannot leak pods: <c>AssistantRunService</c> releases the pod once the conversation has
/// been quiet for <c>Assistant:PodIdleTimeout</c> and again when the conversation is parked as
/// dormant, and <c>AgentHostReaperService</c> reaps any claim whose run is no longer active as the
/// cross-replica backstop.
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
                    CallerBearerToken: request.CallerBearerToken,
                    HolderToken: request.PodHolderToken),
                ct).ConfigureAwait(false);
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
            proxyOptions.Value)
        {
            // Refresh the held pod's caller credential for THIS turn (see the class remarks): the
            // one-shot /configure that originally delivered it cannot be replayed.
            CallerBearerToken = request.CallerBearerToken,
        };

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

            var drainTask = DrainAsync(runId, channel.Reader, sink, invokedTools, ct);

            string text;
            try
            {
                text = await proxy.RunTurnAsync(taskJson, isRevision: false, ct).ConfigureAwait(false);
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
            // A failed turn may have left the pod half-configured or wedged, so it must NOT be
            // carried into the next message — release it here (the success path deliberately does
            // not, so the conversation keeps its warm, already-configured pod).
            await TryReleaseAgentHostPodAsync(podLifecycle, runId).ConfigureAwait(false);
            throw ClassifyOrWrap(ex, runId, "Operator assistant turn failed on the AgentHost pod");
        }
        catch (OperationCanceledException)
        {
            await TryReleaseAgentHostPodAsync(podLifecycle, runId).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await ((IAsyncDisposable)proxy).DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Best-effort pod release used on the failure paths only; a release failure must never
    /// mask the original turn error. The idle/dormancy sweeps in <see cref="AssistantRunService"/> and
    /// the <c>AgentHostReaperService</c> are the backstops for anything missed here.</summary>
    private async Task TryReleaseAgentHostPodAsync(IAgentHostPodLifecycle podLifecycle, string runId)
    {
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
        // broken/missing) binding. A failure always surfaces the platform-settings CTA. This is the
        // SAME scope AssistantRunService selects the session's provider at (its
        // ResolveAssistantModelSourceAsync resolves at platform scope too, and re-resolves each
        // turn), so selection and validation cannot disagree — and because run.ModelSource is read
        // fresh from the store above, a mid-conversation provider switch is honoured here too.
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
            }
        }
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
