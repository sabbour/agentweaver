extern alias agenthost;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using agenthost::Agentweaver.AgentHost;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Agentweaver.Tests.AgentHost;

/// <summary>
/// spec-018 P1.5 — REAL end-to-end A2A round-trip proof for pod-per-run execution.
///
/// <para>
/// Unlike <see cref="A2ATurnBridgeAgentTests"/> (which drives the bridge in-process with a fake
/// runner), this test exercises the <b>entire worker→pod seam with no fakes on it</b>:
/// </para>
/// <list type="bullet">
///   <item>The REAL <c>A2ATurnBridgeAgent</c> is hosted on a REAL Kestrel HTTP listener on
///     loopback via the same <c>AddAIAgent</c> + <c>AddA2AServer</c> + <c>MapA2AHttpJson</c>
///     wiring the pod's <c>Program.cs</c> uses (RequireMtls=false / plain http).</item>
///   <item>The REAL worker-side <see cref="RemoteAgentProxy"/> connects over a REAL
///     <c>A2AClient</c>/<c>HttpClient</c> pointed at the Kestrel endpoint.</item>
///   <item>The only deterministic stand-in is the leaf <see cref="IPodTurnRunner"/> (a
///     CopilotAIAgent-shaped turn that emits a couple of <see cref="RunEvent"/>s + final text);
///     the bridge, the A2A server, the HTTP transport, and the client are all real product code.</item>
/// </list>
///
/// <para>
/// The proof asserts on the <b>worker side</b> (the stream-writer channel the proxy forwards to):
/// the RunEvents emitted pod-side actually arrive decoded at the worker, the final assistant text
/// arrives, and <c>IsRevision=true</c> is genuinely observed pod-side on the revision turn — i.e.
/// the <c>AgentSetupParams</c> DataPart survives the real A2A transport (the original revisions bug).
/// </para>
/// </summary>
public sealed class A2ARoundTripIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public A2ARoundTripIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RealA2ARoundTrip_ForwardsRunEvents_FinalText_AndIsRevision_OverPlainHttp()
    {
        var port = GetFreeTcpPort();
        var runner = new DeterministicTurnRunner();

        // ── Pod side: boot the REAL A2ATurnBridgeAgent on a REAL Kestrel http listener ──
        // Mirrors apps/Agentweaver.AgentHost/Program.cs (PoC plain-http path) exactly, except the
        // leaf turn runner is the deterministic stand-in instead of CopilotPodTurnRunner.
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://localhost:{port}");

        var agentHostedBuilder = builder.AddAIAgent(
            A2ATurnBridgeAgent.AgentName,
            (sp, _) => new A2ATurnBridgeAgent(
                new MinimalInnerAgent(),
                runner,
                NullLogger<A2ATurnBridgeAgent>.Instance),
            ServiceLifetime.Singleton);

#pragma warning disable MEAI001 // preview A2A hosting API (matches AgentHost Program.cs)
        agentHostedBuilder.AddA2AServer(options =>
        {
            options.AgentRunMode = AgentRunMode.DisallowBackground;
        });
#pragma warning restore MEAI001

        await using var app = builder.Build();
        app.MapA2AHttpJson(agentHostedBuilder, "/a2a/agent");
        await app.StartAsync();

        try
        {
            // ── Worker side: REAL RemoteAgentProxy over a REAL A2AClient/HttpClient ──
            using var clientServices = new ServiceCollection().AddHttpClient().BuildServiceProvider();
            var httpFactory = clientServices.GetRequiredService<IHttpClientFactory>();
            var resolver = new FixedEndpointResolver(new Uri($"http://localhost:{port}/a2a/agent"));

            await using var proxy = new RemoteAgentProxy(resolver, httpFactory, NullLoggerFactory.Instance);

            // The side-channel the proxy forwards decoded RunEvents onto (worker-side assertion target).
            var workerEvents = Channel.CreateUnbounded<RunEvent>();

            await proxy.SetupAsync(
                workingDirectory: "/workspace",
                repositoryPath: "/workspace",
                runId: "run-roundtrip-1",
                modelId: null,
                systemPromptContext: null,
                streamWriter: workerEvents.Writer,
                projectId: null,
                agentName: null,
                apiBaseUrl: null,
                apiKey: null,
                ct: TestCt,
                userId: null);

            // Turn (a): a fresh turn.
            var textA = await proxy.RunTurnAsync("first task", isRevision: false, TestCt);

            // Turn (b): a revision turn.
            var textB = await proxy.RunTurnAsync("second task", isRevision: true, TestCt);

            workerEvents.Writer.Complete();

            var received = new List<RunEvent>();
            await foreach (var evt in workerEvents.Reader.ReadAllAsync(TestCt))
                received.Add(evt);

            // ── Observed trace (printed for the PASS/FAIL report) ──
            _output.WriteLine($"[worker] turn-a final text  : '{textA}'");
            _output.WriteLine($"[worker] turn-b final text  : '{textB}'");
            _output.WriteLine($"[worker] RunEvents received : {received.Count}");
            foreach (var e in received)
                _output.WriteLine($"    seq={e.Sequence} type={e.Type} payload={JsonSerializer.Serialize(e.Payload)}");
            _output.WriteLine($"[pod   ] runner turn calls   : {string.Join(", ", runner.Calls.Select(c => $"(task='{c.Task}', isRevision={c.IsRevision})"))}");

            // ── Assertions: final assistant text round-trips on both turns ──
            textA.Should().Be("fresh:first task", "the bridge must surface the runner's final text over A2A");
            textB.Should().Be("revised:second task");

            // ── Assertions: RunEvents emitted pod-side arrive decoded at the worker ──
            // Two turns × two events each, all forwarded through the real A2A DataPart codec.
            received.Should().HaveCount(4, "each turn emits agent.task + agent.message.delta");
            received.Select(r => r.Type).Should().Contain("agent.task")
                .And.Contain("agent.message.delta");

            // The anonymous-typed payload must survive serialization (reflection codec path).
            var delta = received.First(r => r.Type == "agent.message.delta");
            JsonSerializer.Serialize(delta.Payload).Should().Contain("Hello from pod");

            // ── Assertions: IsRevision is genuinely observed POD-SIDE (the revisions bug) ──
            runner.Calls.Should().HaveCount(2);
            runner.Calls[0].Should().Be(("first task", false), "turn (a) is a fresh turn");
            runner.Calls[1].Should().Be(("second task", true),
                "turn (b)'s IsRevision=true must survive the AgentSetupParams DataPart across real A2A");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static CancellationToken TestCt =>
        new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Deterministic, CopilotAIAgent-shaped <see cref="IPodTurnRunner"/>: records the per-turn
    /// args, emits two RunEvents matching the real event shapes (<c>agent.task</c>,
    /// <c>agent.message.delta</c> with anonymous-typed payloads), and returns the final text.
    /// This is the ONLY stand-in — everything on the A2A seam under test is real.
    /// </summary>
    private sealed class DeterministicTurnRunner : IPodTurnRunner
    {
        private ChannelWriter<RunEvent>? _writer;
        public List<(string Task, bool IsRevision)> Calls { get; } = new();

        public void SetTurnStreamWriter(ChannelWriter<RunEvent>? streamWriter) => _writer = streamWriter;

        public async Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken cancellationToken)
        {
            Calls.Add((task, isRevision));
            var writer = _writer ?? throw new InvalidOperationException("Stream writer not attached.");

            await writer.WriteAsync(new RunEvent(1, "agent.task", new { task }), cancellationToken)
                .ConfigureAwait(false);
            await writer.WriteAsync(
                new RunEvent(2, "agent.message.delta", new { delta = "Hello from pod", messageId = "m1" }),
                cancellationToken).ConfigureAwait(false);

            return isRevision ? $"revised:{task}" : $"fresh:{task}";
        }
    }

    /// <summary>Fixed endpoint resolver pointing the proxy at the loopback Kestrel listener.</summary>
    private sealed class FixedEndpointResolver : ISandboxAgentEndpointResolver
    {
        private readonly Uri _uri;
        public FixedEndpointResolver(Uri uri) => _uri = uri;
        public Task<Uri?> TryResolveEndpointAsync(string runId, CancellationToken ct) =>
            Task.FromResult<Uri?>(_uri);
    }

    /// <summary>
    /// Minimal REAL inner <see cref="AIAgent"/> that backs <c>DelegatingAIAgent</c>'s session
    /// plumbing (create / serialize / deserialize). The bridge overrides the streaming/run
    /// entrypoints to call the runner, so this inner's Run methods are never the turn executor —
    /// it only provides the MAF session lifecycle the A2A server expects.
    /// </summary>
    private sealed class MinimalInnerAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            new(new MinimalSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession? session, JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            new(JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            new(new MinimalSession());

        private sealed class MinimalSession : AgentSession;
    }
}
