extern alias agenthost;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using agenthost::Agentweaver.AgentHost;
using Agentweaver.AgentRuntime;
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
    private const string RemoteApiBaseUrl = "http://agentweaver-api.agentweaver.svc.cluster.local:8080";

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

            await using var proxy = new RemoteAgentProxy(
                resolver, httpFactory, NullLoggerFactory.Instance, RemoteApiBaseUrl);

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
                apiBaseUrl: "http://localhost:8080",
                apiKey: "test-api-key",
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
            // Two turns × three events each (agent.task, agent.message.delta, agent.turn.end),
            // all forwarded through the real A2A DataPart codec.
            received.Should().HaveCount(6, "each turn emits agent.task + agent.message.delta + agent.turn.end");
            received.Select(r => r.Type).Should().Contain("agent.task")
                .And.Contain("agent.message.delta")
                .And.Contain(EventTypes.AgentTurnEnd);

            // The anonymous-typed payload must survive serialization (reflection codec path).
            var delta = received.First(r => r.Type == "agent.message.delta");
            JsonSerializer.Serialize(delta.Payload).Should().Contain("Hello from pod");

            // ── Assertions: IsRevision is genuinely observed POD-SIDE (the revisions bug) ──
            runner.Calls.Should().HaveCount(2);
            runner.Calls[0].Should().Be(("first task", false), "turn (a) is a fresh turn");
            runner.Calls[1].Should().Be(("second task", true),
                "turn (b)'s IsRevision=true must survive the AgentSetupParams DataPart across real A2A");
            runner.Contexts.Should().HaveCount(2);
            runner.Contexts.Should().OnlyContain(c => c.ApiBaseUrl == RemoteApiBaseUrl,
                "the remote URL must replace the caller's loopback URL at the A2A boundary");
            runner.Contexts.Should().OnlyContain(c => c.ApiKey == "test-api-key",
                "the mandatory API bearer credential must survive A2A serialization");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task RemoteAgentProxy_Resiliency_PreservesStructuredRunFailedAcrossA2AFault()
    {
        var port = GetFreeTcpPort();
        var runner = new StructuredFailingTurnRunner();
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
#pragma warning disable MEAI001
        agentHostedBuilder.AddA2AServer(options => options.AgentRunMode = AgentRunMode.DisallowBackground);
#pragma warning restore MEAI001

        await using var app = builder.Build();
        app.MapA2AHttpJson(agentHostedBuilder, "/a2a/agent");
        await app.StartAsync();

        try
        {
            using var clientServices = new ServiceCollection().AddHttpClient().BuildServiceProvider();
            var httpFactory = clientServices.GetRequiredService<IHttpClientFactory>();
            var resolver = new FixedEndpointResolver(new Uri($"http://localhost:{port}/a2a/agent"));
            await using var proxy = new RemoteAgentProxy(
                resolver,
                httpFactory,
                NullLoggerFactory.Instance,
                RemoteApiBaseUrl);
            var workerEvents = Channel.CreateUnbounded<RunEvent>();
            await proxy.SetupAsync(
                "/workspace",
                "/workspace",
                "run-structured-failure-254",
                modelId: null,
                systemPromptContext: null,
                workerEvents.Writer,
                projectId: null,
                agentName: null,
                apiBaseUrl: null,
                apiKey: null,
                TestCt,
                userId: null);

            var act = () => proxy.RunTurnAsync("long shell", isRevision: false, TestCt);

            var ex = await act.Should().ThrowAsync<WorkflowAgentInfrastructureException>();
            ex.Which.Reason.Should().Be("shell_execution_timeout");
            ex.Which.Message.Should().Contain("hard deadline");
            ex.Which.Message.Should().NotContain("Internal error");
            ex.Which.IsRetryable.Should().BeTrue();
            var forwarded = await workerEvents.Reader.ReadAsync(TestCt);
            forwarded.Type.Should().Be(EventTypes.RunFailed);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task RemoteAgentProxy_CleanStreamWithoutTurnEnd_FailsRetryably_InsteadOfPhantomSuccess()
    {
        // #242 regression: the pod streams progress (deltas) and then its A2A stream ends CLEANLY
        // (no fault) WITHOUT ever emitting the definitive `agent.turn.end` completion marker — the
        // signature of a pod-teardown / transport truncation mid-turn. The worker must NOT return a
        // phantom success (which would let the child workflow complete with no terminal
        // WorkflowOutputEvent and trip the coordinator's false-positive stall detector). It must
        // instead fail RETRYABLY so the graph terminalizes visibly and the coordinator can redispatch.
        var port = GetFreeTcpPort();
        var runner = new TruncatedTurnRunner();
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
#pragma warning disable MEAI001
        agentHostedBuilder.AddA2AServer(options => options.AgentRunMode = AgentRunMode.DisallowBackground);
#pragma warning restore MEAI001

        await using var app = builder.Build();
        app.MapA2AHttpJson(agentHostedBuilder, "/a2a/agent");
        await app.StartAsync();

        try
        {
            using var clientServices = new ServiceCollection().AddHttpClient().BuildServiceProvider();
            var httpFactory = clientServices.GetRequiredService<IHttpClientFactory>();
            var resolver = new FixedEndpointResolver(new Uri($"http://localhost:{port}/a2a/agent"));
            await using var proxy = new RemoteAgentProxy(
                resolver, httpFactory, NullLoggerFactory.Instance, RemoteApiBaseUrl);
            var workerEvents = Channel.CreateUnbounded<RunEvent>();
            await proxy.SetupAsync(
                "/workspace",
                "/workspace",
                "run-truncated-242",
                modelId: null,
                systemPromptContext: null,
                workerEvents.Writer,
                projectId: null,
                agentName: null,
                apiBaseUrl: null,
                apiKey: null,
                TestCt,
                userId: null);

            var act = () => proxy.RunTurnAsync("do work", isRevision: false, TestCt);

            var ex = await act.Should().ThrowAsync<WorkflowAgentInfrastructureException>();
            ex.Which.Reason.Should().Be("agent_host_turn_incomplete",
                "a clean stream that never delivered agent.turn.end must not be treated as success");
            ex.Which.IsRetryable.Should().BeTrue(
                "a truncated turn is redispatchable — the coordinator should redispatch, not falsely stall");

            // The pod's real progress was still forwarded before the truncation — no data loss.
            workerEvents.Writer.Complete();
            var received = new List<RunEvent>();
            await foreach (var evt in workerEvents.Reader.ReadAllAsync(TestCt))
                received.Add(evt);
            received.Select(r => r.Type).Should().Contain("agent.message.delta")
                .And.NotContain(EventTypes.AgentTurnEnd);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task RealA2ARoundTrip_OperatorAssistantPurpose_ExecutesMcpToolCallAndApprovalFlow_ThroughRemotePath()
    {
        // Narrow AgentHost cutover (#346/#347): proves the REAL A2ATurnBridgeAgent + RoutingPodTurnRunner
        // + OperatorPodTurnRunner select the operator assistant path (not CopilotAIAgent) end to end
        // over a REAL A2A HTTP transport, and that:
        //  - the pod's OWN IToolApprovalGate — not the worker's — is what a tool-approval grant resolves;
        //  - a gated tool call is blocked until granted (fails closed while pending);
        //  - the OAuth token and run id delivered via the existing /configure contract
        //    (AgentHostRunConfiguration.GitHubAccessToken / RunId) are what reaches the assistant request;
        //  - the turn completes with the definitive agent.turn.end marker (no phantom-incomplete failure).
        var port = GetFreeTcpPort();
        var runtimeState = new AgentHostRuntimeState();
        runtimeState.TryConfigure(new AgentHostRunConfiguration(
            RunId: "run-operator-roundtrip-1",
            UserId: "user-1",
            TurnBearerToken: "turn-token",
            KvUserSecretName: null,
            GitHubAccessToken: "gh-oauth-token-abc",
            PreviewRunnerCredential: null,
            SharedWorkingDirectory: null,
            Purpose: AgentHostPurpose.OperatorAssistant,
            ProjectId: "proj-1",
            AgentName: "Operator")).Should().BeTrue();

        var approvalGate = new InMemoryToolApprovalGate();
        var fakeAssistant = new GatedFakeOperatorAssistantAgent();
        var operatorRunner = new OperatorPodTurnRunner(
            fakeAssistant, runtimeState, approvalGate, NullLogger<OperatorPodTurnRunner>.Instance);
        var routingRunner = new RoutingPodTurnRunner(
            copilotRunner: new DeterministicTurnRunner(), operatorRunner, runtimeState);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        var agentHostedBuilder = builder.AddAIAgent(
            A2ATurnBridgeAgent.AgentName,
            (sp, _) => new A2ATurnBridgeAgent(
                new MinimalInnerAgent(),
                routingRunner,
                NullLogger<A2ATurnBridgeAgent>.Instance),
            ServiceLifetime.Singleton);
#pragma warning disable MEAI001
        agentHostedBuilder.AddA2AServer(options => options.AgentRunMode = AgentRunMode.DisallowBackground);
#pragma warning restore MEAI001

        await using var app = builder.Build();
        app.MapA2AHttpJson(agentHostedBuilder, "/a2a/agent");
        await app.StartAsync();

        try
        {
            using var clientServices = new ServiceCollection().AddHttpClient().BuildServiceProvider();
            var httpFactory = clientServices.GetRequiredService<IHttpClientFactory>();
            var resolver = new FixedEndpointResolver(new Uri($"http://localhost:{port}/a2a/agent"));
            await using var proxy = new RemoteAgentProxy(
                resolver, httpFactory, NullLoggerFactory.Instance, RemoteApiBaseUrl);
            var workerEvents = Channel.CreateUnbounded<RunEvent>();
            await proxy.SetupAsync(
                workingDirectory: "",
                repositoryPath: "",
                runId: "run-operator-roundtrip-1",
                modelId: null,
                systemPromptContext: null,
                workerEvents.Writer,
                projectId: "proj-1",
                agentName: "Operator",
                apiBaseUrl: null,
                apiKey: null,
                TestCt,
                userId: "user-1");

            var envelope = new OperatorAssistantTurnEnvelope(
                Message: "please run the tool",
                AgentDefinition: "You are the operator.",
                GitHubLogin: "octocat",
                ContextRunId: null,
                History: Array.Empty<ConsoleFacadeHistoryMessage>());
            var taskJson = JsonSerializer.Serialize(envelope);

            // Grant the approval concurrently with the turn — mirrors the real
            // /api/runs/{id}/tool-approvals -> pod /tool-approvals fallback path (a live grant while
            // the tool call is genuinely pending, not a pre-armed one).
            var grantTask = Task.Run(async () =>
            {
                while (!approvalGate.HasArmedApproval("run-operator-roundtrip-1"))
                    await Task.Delay(15, TestCt);
                await approvalGate.GrantAsync("run-operator-roundtrip-1", fakeAssistant.LastRequestId!, ApprovalScope.Once);
            });

            var text = await proxy.RunTurnAsync(taskJson, isRevision: false, TestCt);
            await grantTask;

            workerEvents.Writer.Complete();
            var received = new List<RunEvent>();
            await foreach (var evt in workerEvents.Reader.ReadAllAsync(TestCt))
                received.Add(evt);

            // The request the OperatorAssistantAgent-shaped fake actually received came entirely
            // through the existing /configure contract (AgentHostRuntimeState), not a new channel.
            fakeAssistant.LastRequest.Should().NotBeNull();
            fakeAssistant.LastRequest!.ConversationId.Should().Be("run-operator-roundtrip-1");
            fakeAssistant.LastRequest.CallerUser.Should().Be("user-1");
            fakeAssistant.LastRequest.CallerBearerToken.Should().Be("gh-oauth-token-abc",
                "the OAuth token must arrive via the SAME GitHubAccessToken field the existing /configure contract already carries");
            fakeAssistant.LastRequest.ProjectId.Should().Be("proj-1");
            fakeAssistant.LastRequest.Message.Should().Be("please run the tool");
            fakeAssistant.LastRequest.AgentDefinition.Should().Be("You are the operator.");

            // The tool call was genuinely gated: it only "ran" (per the fake) after the grant.
            fakeAssistant.ToolRanAfterApproval.Should().BeTrue();
            text.Should().Be("done: please run the tool");

            received.Select(r => r.Type).Should().Contain(EventTypes.ToolCall)
                .And.Contain(EventTypes.ToolApprovalRequired)
                .And.Contain(EventTypes.ToolApprovalResolved)
                .And.Contain(EventTypes.ToolResult)
                .And.Contain(EventTypes.AgentTurnEnd,
                    "the operator turn must also emit the definitive completion marker so the worker never reports a phantom-incomplete failure");

            var resolved = received.First(r => r.Type == EventTypes.ToolApprovalResolved);
            JsonSerializer.Serialize(resolved.Payload).Should().Contain("\"approved\":true");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task OperatorPodTurnRunner_WithoutStreamWriter_FailsClosed_InsteadOfRunningToolsUngated()
    {
        // If the bridge ever ran a turn without attaching a stream writer, a null sink would silently
        // disable approval gating in OperatorAssistantAgent (its BuildToolDeclarations only gates when
        // given a non-null sink). The runner must refuse the turn instead of degrading to "ungated".
        var runtimeState = new AgentHostRuntimeState();
        runtimeState.TryConfigure(new AgentHostRunConfiguration(
            RunId: "run-no-writer",
            UserId: "user-1",
            TurnBearerToken: "turn-token",
            KvUserSecretName: null,
            GitHubAccessToken: "gh-oauth-token",
            PreviewRunnerCredential: null,
            SharedWorkingDirectory: null,
            Purpose: AgentHostPurpose.OperatorAssistant));

        var runner = new OperatorPodTurnRunner(
            new GatedFakeOperatorAssistantAgent(),
            runtimeState,
            new InMemoryToolApprovalGate(),
            NullLogger<OperatorPodTurnRunner>.Instance);

        var envelope = new OperatorAssistantTurnEnvelope("hi", "def", null, null, Array.Empty<ConsoleFacadeHistoryMessage>());

        // SetTurnStreamWriter is never called — mirrors a wiring defect, not a legitimate no-stream case.
        var act = () => runner.RunTurnAsync(JsonSerializer.Serialize(envelope), isRevision: false, TestCt);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*without a sink*");
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
        public List<(string? ApiBaseUrl, string? ApiKey)> Contexts { get; } = new();

        public void SetTurnStreamWriter(ChannelWriter<RunEvent>? streamWriter) => _writer = streamWriter;

        public bool ApplyPerTurnContext(
            string? systemPromptContext,
            string? projectId,
            string? agentName,
            string? apiBaseUrl = null,
            string? apiKey = null)
        {
            Contexts.Add((apiBaseUrl, apiKey));
            return true;
        }

        public async Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken cancellationToken)
        {
            Calls.Add((task, isRevision));
            var writer = _writer ?? throw new InvalidOperationException("Stream writer not attached.");

            await writer.WriteAsync(new RunEvent(1, "agent.task", new { task }), cancellationToken)
                .ConfigureAwait(false);
            await writer.WriteAsync(
                new RunEvent(2, "agent.message.delta", new { delta = "Hello from pod", messageId = "m1" }),
                cancellationToken).ConfigureAwait(false);
            // Definitive per-turn completion marker every real pod runner emits (CopilotAIAgent) —
            // the worker requires it to distinguish a finished turn from a truncated stream (#242).
            await writer.WriteAsync(
                new RunEvent(3, EventTypes.AgentTurnEnd, new { turnId = "0" }),
                cancellationToken).ConfigureAwait(false);

            return isRevision ? $"revised:{task}" : $"fresh:{task}";
        }
    }

    /// <summary>
    /// A pod runner that streams progress (a delta) and then completes its turn WITHOUT ever
    /// emitting the definitive `agent.turn.end` completion marker — simulating a pod-teardown /
    /// transport truncation mid-turn where the A2A stream ends cleanly but the turn never finished.
    /// </summary>
    private sealed class TruncatedTurnRunner : IPodTurnRunner
    {
        private ChannelWriter<RunEvent>? _writer;

        public void SetTurnStreamWriter(ChannelWriter<RunEvent>? streamWriter) => _writer = streamWriter;

        public async Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken cancellationToken)
        {
            var writer = _writer ?? throw new InvalidOperationException("Stream writer not attached.");
            await writer.WriteAsync(
                new RunEvent(1, "agent.message.delta", new { delta = "partial progress", messageId = "m1" }),
                cancellationToken).ConfigureAwait(false);

            // NOTE: intentionally NO agent.turn.end — the turn is cut off. The stream still closes
            // cleanly (the runner returns normally), so only the missing marker distinguishes this
            // from a real success.
            return "partial";
        }
    }

    private sealed class StructuredFailingTurnRunner : IPodTurnRunner
    {
        private ChannelWriter<RunEvent>? _writer;

        public void SetTurnStreamWriter(ChannelWriter<RunEvent>? streamWriter) => _writer = streamWriter;

        public async Task<string> RunTurnAsync(
            string task,
            bool isRevision,
            CancellationToken cancellationToken)
        {
            await (_writer ?? throw new InvalidOperationException("Stream writer not attached."))
                .WriteAsync(new RunEvent(1, EventTypes.RunFailed, new
                {
                    message = "Shell execution exceeded its hard deadline and was terminated.",
                    errorCode = "shell_execution_timeout",
                    retryable = true,
                }), cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException("Internal error");
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

    /// <summary>
    /// Fake <see cref="IOperatorAssistantAgent"/> that models a single gated MCP tool call: it emits
    /// a tool-call event, then asks the sink to gate an approval-required tool exactly as the real
    /// <c>OperatorAssistantAgent</c> does for a consequential MCP tool, and only reports the tool as
    /// having actually run once the operator's decision resolves the gate — proving
    /// <see cref="OperatorPodTurnRunner"/> wires the pod's own <see cref="IToolApprovalGate"/> into the
    /// turn without needing a live MCP server or Copilot client.
    /// </summary>
    private sealed class GatedFakeOperatorAssistantAgent : IOperatorAssistantAgent
    {
        private const string ToolName = "run_something";

        public OperatorAssistantRequest? LastRequest { get; private set; }
        public string? LastRequestId { get; private set; }
        public bool ToolRanAfterApproval { get; private set; }

        public async Task<OperatorAssistantResponse> RunTurnAsync(
            OperatorAssistantRequest request,
            IOperatorAssistantTurnSink? sink,
            CancellationToken ct)
        {
            LastRequest = request;
            LastRequestId = Guid.NewGuid().ToString("n");

            if (sink is not null)
                await sink.OnToolCallAsync(ToolName, argumentsJson: null, ct).ConfigureAwait(false);

            var approved = sink is null
                || await sink.OnApprovalRequiredAsync(LastRequestId, ToolName, argumentsJson: null, ct)
                    .ConfigureAwait(false);

            ToolRanAfterApproval = approved;

            if (sink is not null)
                await sink.OnToolResultAsync(ToolName, success: approved, ct).ConfigureAwait(false);

            return new OperatorAssistantResponse($"done: {request.Message}", new[] { ToolName });
        }
    }
}
