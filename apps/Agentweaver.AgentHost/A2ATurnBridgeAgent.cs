using System.Runtime.CompilerServices;
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
/// <b>Run-scoped vs per-turn config:</b> <c>AgentHostStartupService</c> still calls
/// <c>SetupAsync</c> once at pod startup for the run-scoped configuration (Copilot client,
/// governance, working dir). The bridge only swaps the per-turn stream writer and passes the
/// per-turn <c>isRevision</c> flag; it does not re-run <c>SetupAsync</c>. It also extends
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
    private readonly ILogger<A2ATurnBridgeAgent> _logger;

    /// <summary>
    /// Reports the registered MAF agent name. <see cref="DelegatingAIAgent"/> otherwise forwards
    /// <see cref="Name"/> to the inner agent, whose name is empty — which makes
    /// <c>MapA2AHttpJson</c>'s factory-name validation throw
    /// (<c>returned an agent with name '', but the expected name is 'agentweaver-pod'</c>).
    /// </summary>
    public override string Name => AgentName;

    /// <summary>Production constructor: drives the pod's singleton <see cref="CopilotAIAgent"/>.</summary>
    public A2ATurnBridgeAgent(CopilotAIAgent inner, ILogger<A2ATurnBridgeAgent> logger)
        : this(inner, new CopilotPodTurnRunner(inner), logger)
    {
    }

    /// <summary>Test seam: the <paramref name="inner"/> backs MAF session plumbing; the
    /// <paramref name="runner"/> executes the turn.</summary>
    internal A2ATurnBridgeAgent(AIAgent inner, IPodTurnRunner runner, ILogger<A2ATurnBridgeAgent> logger)
        : base(inner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        var (task, isRevision) = ExtractTurn(messages);

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

        _runner.SetTurnStreamWriter(channel.Writer);

        var turnTask = Task.Run(async () =>
        {
            try
            {
                return await _runner.RunTurnAsync(task, isRevision, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        try
        {
            await foreach (var runEvent in channel.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return new AgentResponseUpdate(
                    ChatRole.Assistant,
                    new List<AIContent> { RunEventDataPartCodec.EncodeRunEvent(runEvent) });
            }

            // Surface the accumulated assistant text and propagate any turn exception.
            var responseText = await turnTask.ConfigureAwait(false);
            if (!string.IsNullOrEmpty(responseText))
            {
                yield return new AgentResponseUpdate(ChatRole.Assistant, responseText);
            }
        }
        finally
        {
            _runner.SetTurnStreamWriter(null);
        }
    }

    /// <summary>
    /// Extracts the task text and per-turn <c>isRevision</c> flag from the inbound A2A message
    /// contents: the <see cref="AgentSetupParams"/> <c>DataPart</c> yields <c>isRevision</c>,
    /// the <see cref="TextContent"/> parts yield the task. Exposed <see langword="internal"/> for
    /// unit testing the decode path.
    /// </summary>
    internal static (string Task, bool IsRevision) ExtractTurn(IEnumerable<ChatMessage> messages)
    {
        var taskText = new StringBuilder();
        var isRevision = false;

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case DataContent data when AgentSetupParams.TryDecode(data) is { } setup:
                        isRevision = setup.IsRevision;
                        break;
                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        taskText.Append(text.Text);
                        break;
                }
            }
        }

        return (taskText.ToString(), isRevision);
    }
}
