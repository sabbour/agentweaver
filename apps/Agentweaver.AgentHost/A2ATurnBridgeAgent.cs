using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agentweaver.AgentHost;

/// <summary>
/// Pod-side A2A bridge (spec-018 P1.5) that adapts the standard MAF <c>AIAgent</c> streaming
/// entrypoint — which the <c>MapA2AHttpJson</c> server invokes as
/// <c>RunStreamingAsync(messages, session, options, ct)</c> — onto
/// <see cref="CopilotAIAgent.RunTurnAsync(string, bool, CancellationToken)"/>.
///
/// <para>
/// This closes two gaps that a direct <see cref="CopilotAIAgent"/> registration leaves open:
/// </para>
/// <list type="number">
///   <item>
///     <b>Bridge IN (p15-revisions):</b> the incoming A2A message carries an
///     <see cref="AgentSetupParams"/> <c>DataPart</c> (packed by
///     <see cref="RemoteAgentProxy"/>). The bridge decodes it and forwards
///     <see cref="AgentSetupParams.IsRevision"/> into <c>RunTurnAsync</c>, so the pod uses the
///     session-resume branch on revisions instead of treating every turn as fresh.
///   </item>
///   <item>
///     <b>Bridge OUT (p15-event-fwd):</b> the bridge installs a per-turn
///     <see cref="Channel{T}"/> as the runner's stream writer, then drains it and re-emits each
///     <see cref="RunEvent"/> as an A2A <c>DataContent</c>
///     (<see cref="RunEventDataPartCodec.MediaType"/>) interleaved with the assistant text. The
///     worker (<see cref="RemoteAgentProxy"/>) decodes these back onto its
///     <c>ChannelWriter&lt;RunEvent&gt;</c> → <c>RunStreamStore</c> → SSE.
///   </item>
/// </list>
///
/// <para>
/// <b>Run-scoped vs per-turn config:</b> <c>AgentHostStartupService</c> calls <c>SetupAsync</c>
/// once at pod startup for the run-scoped provisioning (Copilot client, governance, working dir)
/// using only the static, image-baked pod context. The bridge then applies the <b>per-turn</b>
/// <see cref="AgentSetupParams"/> on every turn (spec-018 / #336): it forwards
/// <see cref="AgentSetupParams.IsRevision"/> into <c>RunTurnAsync</c> and, via
/// <see cref="IPodTurnRunner.ApplyPerTurnContext"/>, layers the per-run system prompt context
/// (charter/memory/assigned skills) and project/agent identity onto the pod's agent — without
/// re-provisioning the Copilot client. It also swaps the per-turn stream writer and extends
/// <see cref="DelegatingAIAgent"/> so MAF session create/serialize/deserialize delegate to the
/// inner <see cref="CopilotAIAgent"/>.
/// </para>
///
/// <para>
/// <b>PoC transport note:</b> RunEvents are forwarded <i>in-band</i> as A2A DataParts — the
/// simplest path and the one the worker decoder already supports, with no new infrastructure.
/// For higher fan-out/scale, RunEvents could instead be published to an external bus (Azure
/// Event Hub / Service Bus / Redis pub-sub) with the worker subscribing out-of-band; that is a
/// deliberate future option and out of scope for this PoC.
/// </para>
/// </summary>
internal sealed class A2ATurnBridgeAgent : DelegatingAIAgent
{
    /// <summary>
    /// The MAF agent name this bridge is registered under (<c>AddAIAgent</c> /
    /// <c>MapA2AHttpJson</c>). MAF validates that the factory-produced agent's
    /// <see cref="AIAgent.Name"/> matches the registered key, so the bridge must report this
    /// name rather than delegating <see cref="Name"/> to the inner <see cref="CopilotAIAgent"/>
    /// (whose name is unset). Used by both <c>Program.cs</c> and the round-trip integration test.
    /// </summary>
    public const string AgentName = "agentweaver-pod";

    private readonly IPodTurnRunner _runner;
    private readonly PodLocalWorkspaceManager? _workspaceManager;
    private readonly AgentHostRuntimeState? _runtimeState;
    private readonly ILogger<A2ATurnBridgeAgent> _logger;
    private readonly TimeSpan _turnDrainTimeout;
    private readonly TimeSpan _forcedCleanupTimeout;

    internal static readonly TimeSpan DefaultTurnDrainTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DefaultForcedCleanupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reports the registered MAF agent name. <see cref="DelegatingAIAgent"/> otherwise forwards
    /// <see cref="Name"/> to the inner agent, whose name is empty — which makes
    /// <c>MapA2AHttpJson</c>'s factory-name validation throw
    /// (<c>returned an agent with name '', but the expected name is 'agentweaver-pod'</c>).
    /// </summary>
    public override string Name => AgentName;

    /// <summary>Production constructor: drives the pod's singleton <see cref="CopilotAIAgent"/>.</summary>
    public A2ATurnBridgeAgent(CopilotAIAgent inner, ILogger<A2ATurnBridgeAgent> logger)
        : this(inner, new CopilotPodTurnRunner(inner), workspaceManager: null, runtimeState: null, logger)
    {
    }

    /// <summary>Production constructor with writable-workspace finalization.</summary>
    public A2ATurnBridgeAgent(
        CopilotAIAgent inner,
        PodLocalWorkspaceManager workspaceManager,
        AgentHostRuntimeState runtimeState,
        ILogger<A2ATurnBridgeAgent> logger)
        : this(inner, new CopilotPodTurnRunner(inner), workspaceManager, runtimeState, logger)
    {
    }

    /// <summary>Test seam: the <paramref name="inner"/> backs MAF session plumbing; the
    /// <paramref name="runner"/> executes the turn.</summary>
    internal A2ATurnBridgeAgent(AIAgent inner, IPodTurnRunner runner, ILogger<A2ATurnBridgeAgent> logger)
        : this(inner, runner, workspaceManager: null, runtimeState: null, logger)
    {
    }

    internal A2ATurnBridgeAgent(
        AIAgent inner,
        IPodTurnRunner runner,
        PodLocalWorkspaceManager? workspaceManager,
        AgentHostRuntimeState? runtimeState,
        ILogger<A2ATurnBridgeAgent> logger,
        TimeSpan? turnDrainTimeout = null,
        TimeSpan? forcedCleanupTimeout = null)
        : base(inner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _workspaceManager = workspaceManager;
        _runtimeState = runtimeState;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _turnDrainTimeout = turnDrainTimeout ?? DefaultTurnDrainTimeout;
        _forcedCleanupTimeout = forcedCleanupTimeout ?? DefaultForcedCleanupTimeout;
    }

    /// <inheritdoc />
    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken) =>
        StreamTurnAsync(messages, cancellationToken);

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        await foreach (var update in StreamTurnAsync(messages, cancellationToken).ConfigureAwait(false))
        {
            if (update.Text is { Length: > 0 } t)
                text.Append(t);
        }

        return new AgentResponse(new ChatMessage(ChatRole.Assistant, text.ToString()));
    }

    /// <summary>
    /// Core bridge loop: decode the inbound turn, run it via <see cref="IPodTurnRunner"/>, and
    /// stream each emitted <see cref="RunEvent"/> as a <c>DataContent</c> update followed by the
    /// final assistant text. Exposed <see langword="internal"/> for unit testing.
    /// </summary>
    internal async IAsyncEnumerable<AgentResponseUpdate> StreamTurnAsync(
        IEnumerable<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (task, setup) = ExtractTurnWithSetup(messages);
        var isRevision = setup?.IsRevision ?? false;

        // spec-018 / #336: apply the per-turn agent context the worker packed into AgentSetupParams
        // (assembled system prompt with charter/memory/assigned skills, plus project/agent identity)
        // BEFORE running the turn. A warm pod's startup SetupAsync only applied the static
        // pod-environment context, so without this the per-run skills/memory never reach the agent's
        // assembled prompt in pod-per-run mode.
        ApplyPerTurnSetup(setup);

        _logger.LogDebug(
            "A2ATurnBridgeAgent: turn start — isRevision={IsRevision}, taskLength={Length}",
            isRevision, task.Length);

        // Per-turn RunEvent side-channel. The runner emits to this writer; we drain it and forward
        // each event back over A2A as a DataPart, interleaved with the assistant text.
        var channel = Channel.CreateUnbounded<RunEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runner.SetTurnStreamWriter(channel.Writer);

        var turnTask = Task.Run(async () =>
        {
            try
            {
                return await _runner.RunTurnAsync(task, isRevision, turnCts.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        try
        {
            // Track whether the pod turn already surfaced a structured terminal reason. The worker
            // (RemoteAgentProxy) only surfaces a failure when the A2A stream ends abnormally (the
            // SDK throws NotSupportedException "Received: None" on the truncated stream — #267); it
            // recovers the reason from the last RunFailed event it observed. If the turn aborts
            // WITHOUT emitting one, the worker is left with a bare, context-free "Received: None"
            // that it can only classify as a2a_protocol_event_unsupported. We guarantee a structured
            // terminal below so pod-side failures always carry a real errorCode.
            var sawTerminalFailure = false;

            await foreach (var runEvent in channel.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (string.Equals(runEvent.Type, EventTypes.RunFailed, StringComparison.Ordinal))
                {
                    sawTerminalFailure = true;
                }

                yield return new AgentResponseUpdate(
                    ChatRole.Assistant,
                    new List<AIContent> { RunEventDataPartCodec.EncodeRunEvent(runEvent) });
            }

            // Surface the accumulated assistant text and propagate any turn exception.
            string responseText;
            ExceptionDispatchInfo? turnFailure = null;
            try
            {
                responseText = await turnTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                turnFailure = ExceptionDispatchInfo.Capture(ex);
                responseText = string.Empty;
            }

            if (turnFailure is not null)
            {
                // #267 root-cause guard: the pod turn threw without a structured RunFailed reaching
                // the worker. Emit a synthetic structured terminal so the worker recovers a real
                // errorCode instead of the misleading, context-free a2a_protocol_event_unsupported.
                // The exception is still propagated (rethrown) so the stream terminates abnormally,
                // exactly as before — only the diagnosis carried to the worker is improved.
                if (!sawTerminalFailure)
                {
                    _logger.LogWarning(
                        turnFailure.SourceException,
                        "A2ATurnBridgeAgent: turn aborted without a structured RunFailed; emitting " +
                        "synthetic agent_turn_internal_error terminal so the worker avoids a bare " +
                        "'Received: None' classification (#267).");

                    yield return new AgentResponseUpdate(
                        ChatRole.Assistant,
                        new List<AIContent>
                        {
                            RunEventDataPartCodec.EncodeRunEvent(new RunEvent(
                                0,
                                EventTypes.RunFailed,
                                new
                                {
                                    message = turnFailure.SourceException.Message,
                                    errorCode = "agent_turn_internal_error",
                                    retryable = true,
                                })),
                        });
                }

                turnFailure.Throw();
            }

            if (!string.IsNullOrEmpty(responseText))
            {
                yield return new AgentResponseUpdate(ChatRole.Assistant, responseText);
            }

            if (_runtimeState?.WorkspaceMode == ExecutionWorkspaceMode.LocalWritable)
            {
                PreparedWriteback? writeback = null;
                AgentHostConfigurationException? publicationFailure = null;
                try
                {
                    writeback = await (_workspaceManager
                        ?? throw new InvalidOperationException(
                            "A writable AgentHost turn requires PodLocalWorkspaceManager."))
                        .PrepareWritebackAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (AgentHostConfigurationException ex)
                {
                    publicationFailure = ex;
                }

                if (publicationFailure is not null)
                {
                    yield return new AgentResponseUpdate(
                        ChatRole.Assistant,
                        new List<AIContent>
                        {
                            RunEventDataPartCodec.EncodeRunEvent(new RunEvent(
                                0,
                                EventTypes.RunFailed,
                                new
                                {
                                    message = publicationFailure.Message,
                                    errorCode = publicationFailure.Reason,
                                    retryable = false,
                                })),
                        });
                    throw new InvalidOperationException(
                        "Pod-local write-back publication failed.",
                        publicationFailure);
                }

                yield return new AgentResponseUpdate(
                    ChatRole.Assistant,
                    new List<AIContent>
                    {
                        PreparedWritebackDataPartCodec.Encode(
                            writeback ?? throw new InvalidOperationException(
                                "Writable workspace finalization returned no publication descriptor.")),
                    });
            }
        }
        finally
        {
            if (!turnTask.IsCompleted)
            {
                turnCts.Cancel();
                var drained = await WaitForCompletionAsync(turnTask, _turnDrainTimeout).ConfigureAwait(false);
                if (!drained)
                {
                    try
                    {
                        await _runner.ForceStopTurnAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "A2A bridge force-stop failed while draining a cancelled turn.");
                    }

                    drained = await WaitForCompletionAsync(turnTask, _forcedCleanupTimeout).ConfigureAwait(false);
                    if (!drained)
                    {
                        _logger.LogWarning(
                            "detached_turn — A2A stream ended while its turn remained active after cancellation and force-stop.");
                    }
                }
            }
            _runner.SetTurnStreamWriter(null);
        }
    }

    private static async Task<bool> WaitForCompletionAsync(Task turnTask, TimeSpan timeout) =>
        ReferenceEquals(await Task.WhenAny(turnTask, Task.Delay(timeout)).ConfigureAwait(false), turnTask);

    /// <summary>
    /// Extracts the task text and per-turn <c>isRevision</c> flag from the inbound A2A message
    /// contents. Retained for callers/tests that only need those two values; delegates to
    /// <see cref="ExtractTurnWithSetup"/>.
    /// </summary>
    internal static (string Task, bool IsRevision) ExtractTurn(IEnumerable<ChatMessage> messages)
    {
        var (task, setup) = ExtractTurnWithSetup(messages);
        return (task, setup?.IsRevision ?? false);
    }

    /// <summary>
    /// Extracts the task text and the full per-turn <see cref="AgentSetupParams"/> from the inbound
    /// A2A message contents: the <see cref="AgentSetupParams"/> <c>DataPart</c> (packed by
    /// <see cref="RemoteAgentProxy"/>) yields the per-turn setup — including the assembled
    /// <see cref="AgentSetupParams.SystemPromptContext"/> and identity — and the
    /// <see cref="TextContent"/> parts yield the task. Exposed <see langword="internal"/> for unit
    /// testing the decode path.
    /// </summary>
    internal static (string Task, AgentSetupParams? Setup) ExtractTurnWithSetup(IEnumerable<ChatMessage> messages)
    {
        var taskText = new StringBuilder();
        AgentSetupParams? setup = null;

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case DataContent data when AgentSetupParams.TryDecode(data) is { } decoded:
                        setup = decoded;
                        break;
                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        taskText.Append(text.Text);
                        break;
                }
            }
        }

        return (taskText.ToString(), setup);
    }

    /// <summary>
    /// Applies the decoded per-turn <see cref="AgentSetupParams"/> to the pod's agent via
    /// <see cref="IPodTurnRunner.ApplyPerTurnContext"/>. The per-turn system prompt context (the
    /// per-run charter/memory/skills assembled by the API) is layered ON TOP OF the static
    /// pod-environment context recorded at startup (<see cref="AgentHostRuntimeState.PodBaseSystemPromptContext"/>)
    /// so environment guidance (e.g. the sandbox tool manifest) is preserved (spec-018 / #336).
    /// </summary>
    private void ApplyPerTurnSetup(AgentSetupParams? setup)
    {
        if (setup is null)
            return;

        var merged = MergeSystemPromptContext(
            _runtimeState?.PodBaseSystemPromptContext,
            setup.SystemPromptContext);

        var applied = _runner.ApplyPerTurnContext(merged, setup.ProjectId, setup.AgentName);
        if (applied)
        {
            _logger.LogInformation(
                "A2ATurnBridgeAgent: applied per-turn context — projectId={ProjectId}, agentName={AgentName}, " +
                "skillsIncluded={SkillsIncluded}, systemPromptChars={Chars}",
                setup.ProjectId, setup.AgentName,
                Agentweaver.Domain.Skills.SkillPromptMarkers.ContainsSkillContext(setup.SystemPromptContext),
                merged?.Length ?? 0);
        }
    }

    /// <summary>
    /// Joins the static pod-environment context with the per-turn per-run context. Either side may
    /// be null/blank; when both are present they are separated by a blank line, mirroring how the
    /// startup path assembles its prompt sections. Exposed <see langword="internal"/> for testing.
    /// </summary>
    internal static string? MergeSystemPromptContext(string? baseContext, string? perTurnContext)
    {
        var hasBase = !string.IsNullOrWhiteSpace(baseContext);
        var hasTurn = !string.IsNullOrWhiteSpace(perTurnContext);
        if (hasBase && hasTurn)
            return baseContext + "\n\n" + perTurnContext;
        if (hasBase)
            return baseContext;
        return hasTurn ? perTurnContext : null;
    }
}
