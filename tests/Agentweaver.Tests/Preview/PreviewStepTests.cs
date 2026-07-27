using System.IO;
using System.Text.Json;
using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Coordinator.Preview;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Preview;

/// <summary>
/// State-machine coverage for the deterministic <see cref="PreviewStep"/> (spec-006 §11). The step is
/// verdict-independent and is the SINGLE emitter of the terminal preview outcome; a preview failure
/// NEVER blocks review. Also covers the credential durable lifecycle (delete-after-release +
/// relaunch remint) at the secret-store seam.
/// </summary>
public sealed class PreviewStepTests : IDisposable
{
    private const string RunId = "run-preview-step";
    private const int WorkPlanId = 7;
    private const string TreeHash = "tree-1";

    private readonly string _worktree;

    public PreviewStepTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "aw-step-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_worktree);
        // A resolvable Vite app (forces --host 0.0.0.0).
        File.WriteAllText(Path.Combine(_worktree, "package.json"), """{ "scripts": { "dev": "vite" } }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best-effort */ }
    }

    // ── Happy path ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolvableApp_HealthyPort_Approved_EmitsSinglePreviewReady()
    {
        var h = new Harness(_worktree);

        await h.Step.RunAsync(Request(), CancellationToken.None);

        h.PreviewRunner.LastObserveTimeoutSeconds.Should().Be(105);
        h.TerminalKinds().Should().ContainSingle().Which.Should().Be(EventTypes.SandboxPreviewReady);
        var ready = h.Single(EventTypes.SandboxPreviewReady);
        // BLOCKER B: token and preview-runner session id are distinct and both present.
        Str(ready, "session_id").Should().Be("gw-token");
        Str(ready, "preview_runner_session_id").Should().Be("proc-sess-1");
        Str(ready, "preview_url").Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Preview_resolves_from_source_tree_but_executes_in_equivalent_local_checkout_directory()
    {
        File.Delete(Path.Combine(_worktree, "package.json"));
        var sourceFrontend = Path.Combine(_worktree, "frontend");
        Directory.CreateDirectory(sourceFrontend);
        File.WriteAllText(
            Path.Combine(sourceFrontend, "package.json"),
            """{ "scripts": { "dev": "vite" } }""");
        var executionRoot = Path.Combine(
            AppContext.BaseDirectory,
            ".preview-execution-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(executionRoot);

        try
        {
            var podRegistry = new PodNameRegistry();
            podRegistry.RegisterEffectiveWorkingDirectory(RunId, executionRoot);
            var h = new Harness(_worktree, podRegistry: podRegistry);
            await h.Step.RunAsync(Request(), CancellationToken.None);

            h.PreviewRunner.LastCommand.Should().Be("npm run dev -- --host 0.0.0.0");
            h.PreviewRunner.LastCwd.Should().Be(Path.Combine(executionRoot, "frontend"));
        }
        finally
        {
            try { Directory.Delete(executionRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Preview_without_reported_effective_workspace_falls_back_to_shared_source_directory()
    {
        File.Delete(Path.Combine(_worktree, "package.json"));
        var sourceFrontend = Path.Combine(_worktree, "frontend");
        Directory.CreateDirectory(sourceFrontend);
        File.WriteAllText(
            Path.Combine(sourceFrontend, "package.json"),
            """{ "scripts": { "dev": "vite" } }""");

        var h = new Harness(_worktree, podRegistry: new PodNameRegistry());

        await h.Step.RunAsync(Request(), CancellationToken.None);

        h.PreviewRunner.LastCwd.Should().Be(sourceFrontend);
    }

    // ── Infra off → skipped, never failed ───────────────────────────────────────────────

    [Fact]
    public async Task InfraOff_PodPerRunDisabled_EmitsSkipped_NotFailed()
    {
        var h = new Harness(_worktree, podPerRun: false);

        await h.Step.RunAsync(Request(), CancellationToken.None);

        h.Types().Should().Contain(EventTypes.SandboxPreviewSkippedNotApplicable);
        h.Types().Should().NotContain(EventTypes.SandboxPreviewFailed);
    }

    [Fact]
    public async Task InfraOff_PreviewServiceDisabled_EmitsSkipped_NotFailed()
    {
        var h = new Harness(_worktree);
        h.PreviewService.EnabledValue = false;

        await h.Step.RunAsync(Request(), CancellationToken.None);

        h.Types().Should().Contain(EventTypes.SandboxPreviewSkippedNotApplicable);
        h.Types().Should().NotContain(EventTypes.SandboxPreviewFailed);
    }

    // ── Unresolved command ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UnresolvableApp_EmitsCommandUnresolved()
    {
        Directory.Delete(_worktree, recursive: true);
        Directory.CreateDirectory(_worktree); // empty → unresolved
        var h = new Harness(_worktree);

        await h.Step.RunAsync(Request(), CancellationToken.None);

        Str(h.Single(EventTypes.SandboxPreviewFailed), "reason").Should().Be("preview_command_unresolved");
        h.PreviewRunner.StartCalls.Should().Be(0);
    }

    // ── LLM command fallback (issue #541) ───────────────────────────────────────────────

    [Fact]
    public async Task HeuristicResolves_ModelFallbackNotConsulted()
    {
        // _worktree already has a resolvable Vite package.json → the heuristic wins and the model
        // fallback must NOT be consulted (fast/free/deterministic first pass stays unchanged).
        var model = new FakePreviewCommandModel
        {
            Override = _ => new PreviewCommandProposal(true, "should-not-be-used", "."),
        };
        var h = new Harness(_worktree, commandModel: model);

        await h.Step.RunAsync(Request(), CancellationToken.None);

        model.CallCount.Should().Be(0);
        h.TerminalKinds().Should().ContainSingle().Which.Should().Be(EventTypes.SandboxPreviewReady);
        Str(h.Single(EventTypes.SandboxPreviewStartRequested), "command_source").Should().Be("package.json:dev");
        h.PreviewRunner.LastCommand.Should().Be("npm run dev -- --host 0.0.0.0");
    }

    [Fact]
    public async Task StaticHtmlOnly_ResolvesViaLlmFallback_EmitsReady_WithLlmSource()
    {
        // A plain static site with no build tooling → heuristics return Unresolved, the LLM fallback
        // proposes a static server, and preview succeeds through the SAME start/observe/approval path.
        Directory.Delete(_worktree, recursive: true);
        Directory.CreateDirectory(_worktree);
        File.WriteAllText(Path.Combine(_worktree, "index.html"), "<html><body>hi</body></html>");
        File.WriteAllText(Path.Combine(_worktree, "styles.css"), "body{color:red}");

        var model = new FakePreviewCommandModel
        {
            Override = _ => new PreviewCommandProposal(true, "npx --yes serve -l tcp://0.0.0.0:0 .", "."),
        };
        var h = new Harness(_worktree, commandModel: model);

        await h.Step.RunAsync(Request(), CancellationToken.None);

        model.CallCount.Should().Be(1);
        h.TerminalKinds().Should().ContainSingle().Which.Should().Be(EventTypes.SandboxPreviewReady);
        Str(h.Single(EventTypes.SandboxPreviewStartRequested), "command_source").Should().Be("llm");
        h.PreviewRunner.LastCommand.Should().Be("npx --yes serve -l tcp://0.0.0.0:0 .");
        h.PreviewRunner.LastCwd.Should().Be(_worktree);
    }

    [Fact]
    public async Task LlmDeclines_StillEmitsCommandUnresolved()
    {
        Directory.Delete(_worktree, recursive: true);
        Directory.CreateDirectory(_worktree);
        File.WriteAllText(Path.Combine(_worktree, "notes.txt"), "just some prose, nothing to run");

        var model = new FakePreviewCommandModel
        {
            Override = _ => new PreviewCommandProposal(false, null, null),
        };
        var h = new Harness(_worktree, commandModel: model);

        await h.Step.RunAsync(Request(), CancellationToken.None);

        model.CallCount.Should().Be(1);
        Str(h.Single(EventTypes.SandboxPreviewFailed), "reason").Should().Be("preview_command_unresolved");
        h.PreviewRunner.StartCalls.Should().Be(0);
    }

    [Fact]
    public async Task LlmUnavailable_ReturnsNull_StillEmitsCommandUnresolved()
    {
        Directory.Delete(_worktree, recursive: true);
        Directory.CreateDirectory(_worktree);
        File.WriteAllText(Path.Combine(_worktree, "index.html"), "<html></html>");

        var model = new FakePreviewCommandModel { Override = _ => null };
        var h = new Harness(_worktree, commandModel: model);

        await h.Step.RunAsync(Request(), CancellationToken.None);

        model.CallCount.Should().Be(1);
        Str(h.Single(EventTypes.SandboxPreviewFailed), "reason").Should().Be("preview_command_unresolved");
        h.PreviewRunner.StartCalls.Should().Be(0);
    }

    [Fact]
    public async Task LlmProposesEscapingCwd_TreatedAsUnresolved()
    {
        // A model-proposed cwd that escapes the worktree must NOT be honored — it is treated as
        // unresolved rather than steering execution outside the checkout.
        Directory.Delete(_worktree, recursive: true);
        Directory.CreateDirectory(_worktree);
        File.WriteAllText(Path.Combine(_worktree, "index.html"), "<html></html>");

        var model = new FakePreviewCommandModel
        {
            Override = _ => new PreviewCommandProposal(true, "python3 -m http.server --bind 0.0.0.0 0", "../../etc"),
        };
        var h = new Harness(_worktree, commandModel: model);

        await h.Step.RunAsync(Request(), CancellationToken.None);

        Str(h.Single(EventTypes.SandboxPreviewFailed), "reason").Should().Be("preview_command_unresolved");
        h.PreviewRunner.StartCalls.Should().Be(0);
    }

    [Fact]
    public async Task LlmThrows_TreatedAsUnresolved_NeverBlocks()
    {
        Directory.Delete(_worktree, recursive: true);
        Directory.CreateDirectory(_worktree);
        File.WriteAllText(Path.Combine(_worktree, "index.html"), "<html></html>");

        var model = new FakePreviewCommandModel { Exception = new InvalidOperationException("model boom") };
        var h = new Harness(_worktree, commandModel: model);

        await h.Step.RunAsync(Request(), CancellationToken.None);

        Str(h.Single(EventTypes.SandboxPreviewFailed), "reason").Should().Be("preview_command_unresolved");
        h.PreviewRunner.StartCalls.Should().Be(0);
    }

    [Fact]
    public async Task NoModelWired_PreservesUnresolvedBehavior()
    {
        Directory.Delete(_worktree, recursive: true);
        Directory.CreateDirectory(_worktree);
        File.WriteAllText(Path.Combine(_worktree, "index.html"), "<html></html>");

        // No command model → identical to the pre-#541 heuristic-only behavior.
        var h = new Harness(_worktree, commandModel: null);

        await h.Step.RunAsync(Request(), CancellationToken.None);

        Str(h.Single(EventTypes.SandboxPreviewFailed), "reason").Should().Be("preview_command_unresolved");
        h.PreviewRunner.StartCalls.Should().Be(0);
    }

    [Fact]
    public async Task ProcessStartThrows_EmitsProcessExited()
    {
        var h = new Harness(_worktree);
        h.PreviewRunner.StartBehavior = () => throw new PreviewRunnerHttpException("preview_runner_error", "boom");

        await h.Step.RunAsync(Request(), CancellationToken.None);

        Str(h.Single(EventTypes.SandboxPreviewFailed), "reason").Should().Be("process_exited");
    }

    [Fact]
    public async Task OriginLookupTimeout_PreservesTypedReason()
    {
        var h = new Harness(_worktree);
        h.PreviewRunner.StartBehavior =
            () => throw new PreviewRunnerHttpException("preview_origin_lookup_timeout", "timed out");

        await h.Step.RunAsync(Request(), CancellationToken.None);

        Str(h.Single(EventTypes.SandboxPreviewFailed), "reason")
            .Should().Be("preview_origin_lookup_timeout");
    }

    [Fact]
    public async Task UnrelatedCancellation_EmitsSingleInternalTimeout_AndCompletes()
    {
        var h = new Harness(_worktree);
        h.PreviewRunner.StartBehavior = () => throw new TaskCanceledException("internal timeout");

        await h.Step.RunAsync(Request(), CancellationToken.None);

        var failures = h.All(EventTypes.SandboxPreviewFailed);
        failures.Should().ContainSingle();
        Str(failures[0], "reason").Should().Be("preview_internal_timeout");
    }

    [Fact]
    public async Task CallerCancellation_Rethrows_WithoutTerminalFailure()
    {
        var h = new Harness(_worktree);
        h.PreviewRunner.StartBehavior = () => throw new TaskCanceledException("caller canceled");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => h.Step.RunAsync(Request(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        h.TerminalKinds().Should().BeEmpty();
    }

    [Fact]
    public async Task Unauthorized_EmitsUnauthorized_AndProceeds()
    {
        var h = new Harness(_worktree);
        h.PreviewRunner.StartBehavior =
            () => throw new PreviewRunnerHttpException("preview_runner_unauthorized", "401");

        await h.Step.RunAsync(Request(), CancellationToken.None);

        Str(h.Single(EventTypes.SandboxPreviewFailed), "reason").Should().Be("preview_runner_unauthorized");
        h.TerminalKinds().Should().ContainSingle(); // proceeds, single terminal
    }

    [Fact]
    public async Task ObservePortThrows_EmitsPortNotFound()
    {
        var h = new Harness(_worktree);
        h.PreviewRunner.ObserveBehavior = () => throw new PreviewRunnerHttpException("preview_runner_error", "no port");

        await h.Step.RunAsync(Request(), CancellationToken.None);

        Str(h.Single(EventTypes.SandboxPreviewFailed), "reason").Should().Be("port_not_found");
    }

    [Fact]
    public async Task ObserveTimeout_AfterStart_StopsOnce_AndEmitsSingleFailure()
    {
        var h = new Harness(_worktree);
        h.PreviewRunner.ObserveBehavior =
            () => throw new PreviewRunnerHttpException("preview_runner_timeout", "timed out");

        await h.Step.RunAsync(Request(), CancellationToken.None);

        h.PreviewRunner.StopCalls.Should().Be(1);
        h.All(EventTypes.SandboxPreviewFailed).Should().ContainSingle();
        Str(h.Single(EventTypes.SandboxPreviewFailed), "reason").Should().Be("port_not_found");
    }

    [Fact]
    public async Task UnhealthyPort_EmitsHealthCheckFailed()
    {
        var h = new Harness(_worktree);
        h.PreviewRunner.PortResult = new PreviewRunnerPortResult("proc-sess-1", 3000, Healthy: false, "no");

        await h.Step.RunAsync(Request(), CancellationToken.None);

        Str(h.Single(EventTypes.SandboxPreviewFailed), "reason").Should().Be("health_check_failed");
    }

    [Fact]
    public async Task ForwarderUnreachable_EmitsDistinctBoundUnreachable()
    {
        // spec-006 preview-forwarder item D: observe verifies reachability THROUGH the forwarder;
        // a public-port health miss surfaces the distinct actionable reason (never health_check_failed).
        var h = new Harness(_worktree);
        h.PreviewRunner.PortResult = new PreviewRunnerPortResult(
            "proc-sess-1", 45678, Healthy: false, "bound_unreachable: forwarder port 45678 unreachable",
            AppPort: 3000, Reason: "bound_unreachable");

        await h.Step.RunAsync(Request(), CancellationToken.None);

        var failure = h.Single(EventTypes.SandboxPreviewFailed);
        Str(failure, "reason").Should().Be("bound_unreachable");
        h.TerminalKinds().Should().ContainSingle(); // proceeds, single terminal
    }

    [Fact]
    public async Task NoPublicPortAvailable_EmitsDistinctReason_AndStopsProcess()
    {
        // spec-006 preview-forwarder BLOCKER #1: range exhaustion surfaces a distinct actionable reason,
        // and the started process must be stopped (not leaked) since registration never succeeds.
        var h = new Harness(_worktree);
        h.PreviewRunner.PortResult = new PreviewRunnerPortResult(
            "proc-sess-1", 0, Healthy: false, "public_port_exhausted:[3000,9000]",
            AppPort: 5173, Reason: "no_public_port_available");

        await h.Step.RunAsync(Request(), CancellationToken.None);

        Str(h.Single(EventTypes.SandboxPreviewFailed), "reason").Should().Be("no_public_port_available");
        h.PreviewRunner.StopCalls.Should().Be(1); // process released, not leaked
    }

    [Fact]
    public async Task PostStartFailure_StopsProcess_ButSuccessDoesNot()
    {
        // A registration failure after a successful start must release the process + forwarder.
        var fail = new Harness(_worktree);
        fail.PreviewService.StartBehavior = () => throw new InvalidOperationException("gateway boom");
        await fail.Step.RunAsync(Request(), CancellationToken.None);
        fail.PreviewRunner.StopCalls.Should().Be(1);

        // The happy path keeps the process alive to serve the preview (no stop).
        var ok = new Harness(_worktree);
        await ok.Step.RunAsync(Request(), CancellationToken.None);
        ok.All(EventTypes.SandboxPreviewReady).Should().ContainSingle();
        ok.PreviewRunner.StopCalls.Should().Be(0);
    }

    // ── Registration failures (single-owner emission) ──────────────────────────────────

    [Fact]
    public async Task RegistrationFailure_EmitsSingleRegistrationFailed()
    {
        var h = new Harness(_worktree);
        h.PreviewService.StartBehavior = () => throw new InvalidOperationException("gateway boom");

        await h.Step.RunAsync(Request(), CancellationToken.None);

        var failures = h.All(EventTypes.SandboxPreviewFailed);
        failures.Should().ContainSingle();
        Str(failures[0], "reason").Should().Be("registration_failed");
    }

    [Fact]
    public async Task PortOutOfRange_EmitsSinglePortNotAllowed()
    {
        var h = new Harness(_worktree);
        h.PreviewRunner.PortResult = new PreviewRunnerPortResult("proc-sess-1", 100, Healthy: true, "ok"); // < AllowedPortMin

        await h.Step.RunAsync(Request(), CancellationToken.None);

        var failures = h.All(EventTypes.SandboxPreviewFailed);
        failures.Should().ContainSingle();
        Str(failures[0], "reason").Should().Be("port_not_allowed");
    }

    // ── Idempotency ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExistingReadyForTree_SecondRun_EmitsNothing()
    {
        var h = new Harness(_worktree);
        h.Streams.Get(RunId)!.RecordNext(EventTypes.SandboxPreviewReady, new
        {
            run_id = RunId,
            work_plan_id = WorkPlanId,
            tree_hash = TreeHash,
        });
        var before = h.Streams.Get(RunId)!.GetSnapshotSince(0).Events.Count;

        await h.Step.RunAsync(Request(), CancellationToken.None);

        h.Streams.Get(RunId)!.GetSnapshotSince(0).Events.Count.Should().Be(before);
        h.PreviewRunner.StartCalls.Should().Be(0);
    }

    // ── Approval denied ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApprovalDenied_EmitsApprovalDenied_NoRegistration()
    {
        var h = new Harness(_worktree, autoApprove: false);

        var task = h.Step.RunAsync(Request(), CancellationToken.None);
        var requestId = await h.WaitForApprovalRequestAsync();
        h.ApprovalGate.Deny(RunId, requestId);
        await task;

        Str(h.Single(EventTypes.SandboxPreviewFailed), "reason").Should().Be("approval_denied");
        h.PreviewService.StartCalls.Should().Be(0);
    }

    // ── Credential durable lifecycle ────────────────────────────────────────────────────

    [Fact]
    public async Task CredentialLifecycle_DeleteAfterRelease_RelaunchRemints()
    {
        var store = new InMemorySecretStore();
        var key = PreviewRunnerCredential.SecretKey(RunId);

        // Mint (launch).
        var first = PreviewRunnerCredential.Mint();
        await store.SetSecretAsync(key, first);
        (await store.GetSecretAsync(key)).Found.Should().BeTrue();

        // Delete (release) — must land, and be a no-op-safe delete.
        await store.DeleteSecretAsync(key);
        (await store.GetSecretAsync(key)).Found.Should().BeFalse();

        // Relaunch mints a FRESH value; the old one no longer authenticates.
        var second = PreviewRunnerCredential.Mint();
        second.Should().NotBe(first);
        await store.SetSecretAsync(key, second);
        (await store.GetSecretAsync(key)).Value.Should().Be(second);
    }

    [Fact]
    public void SecretKey_IsDeterministicForRun_AndSanitized()
    {
        var a = PreviewRunnerCredential.SecretKey("Run/With:Weird_Chars-1");
        var b = PreviewRunnerCredential.SecretKey("Run/With:Weird_Chars-1");
        a.Should().Be(b); // mint and delete derive the same key
        a.Should().NotContain("/").And.NotContain(":");
    }

    private PreviewStepRequest Request() =>
        new(RunId, WorkPlanId, TreeHash, WorktreePath: _worktree, SubmittingUser: "owner");

    private static string Str(object payload, string prop)
    {
        var node = JsonSerializer.SerializeToNode(payload)!.AsObject();
        return node.TryGetPropertyValue(prop, out var v) && v is not null ? v.ToString() : "";
    }

    // ── Harness + fakes ─────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public readonly RunStreamStore Streams = new();
        public readonly FakePreviewRunnerClient PreviewRunner = new();
        public readonly FakePreviewService PreviewService = new();
        public readonly InMemoryToolApprovalGate ApprovalGate = new();
        public readonly PreviewStep Step;

        public Harness(
            string worktree,
            bool podPerRun = true,
            bool autoApprove = true,
            IPodNameRegistry? podRegistry = null,
            IPreviewCommandModel? commandModel = null)
        {
            Streams.Create(RunId, "owner");
            var runtime = new SandboxRuntimeOptions
            {
                AgentExecutionMode = podPerRun ? "pod-per-run" : "in-api",
            };
            var gate = new AgentPreviewGate(
                ApprovalGate, new InMemoryRunOptionsStore(), Streams, autoApprove,
                NullLogger<AgentPreviewGate>.Instance, TimeSpan.FromSeconds(5));

            Step = new PreviewStep(
                PreviewService,
                gate,
                PreviewRunner,
                new PreviewCommandResolver(),
                new FakeTurnTokens("turn-token"),
                Streams,
                runtime,
                NullLogger<PreviewStep>.Instance,
                secretStore: null,
                podRegistry: podRegistry,
                commandModel: commandModel);
        }

        public IReadOnlyList<string> Types() =>
            Streams.Get(RunId)!.GetSnapshotSince(0).Events.Select(e => e.Type).ToList();

        public IReadOnlyList<string> TerminalKinds() =>
            Streams.Get(RunId)!.GetSnapshotSince(0).Events
                .Where(e => e.Type is EventTypes.SandboxPreviewReady
                    or EventTypes.SandboxPreviewFailed
                    or EventTypes.SandboxPreviewSkippedNotApplicable)
                .Select(e => e.Type).ToList();

        public object Single(string type) =>
            Streams.Get(RunId)!.GetSnapshotSince(0).Events.Single(e => e.Type == type).Payload;

        public IReadOnlyList<object> All(string type) =>
            Streams.Get(RunId)!.GetSnapshotSince(0).Events.Where(e => e.Type == type)
                .Select(e => e.Payload).ToList();

        public async Task<string> WaitForApprovalRequestAsync()
        {
            for (var i = 0; i < 200; i++)
            {
                var card = Streams.Get(RunId)!.GetSnapshotSince(0).Events
                    .FirstOrDefault(e => e.Type == EventTypes.ToolApprovalRequired);
                if (card is not null)
                {
                    var node = JsonSerializer.SerializeToNode(card.Payload)!.AsObject();
                    return node["requestId"]!.ToString();
                }
                await Task.Delay(10);
            }
            throw new InvalidOperationException("approval request not emitted");
        }
    }

    private sealed class FakeTurnTokens(string token) : IAgentHostTurnTokenRegistry
    {
        public void RegisterTurnToken(string runId, string token) { }
        public string? TryGetTurnToken(string runId) => token;
        public void UnregisterTurnToken(string runId) { }
    }

    private sealed class FakePreviewRunnerClient : IPreviewRunnerHttpClient
    {
        public int StartCalls;
        public int StopCalls;
        public string? LastStopReason;
        public string? LastCommand;
        public string? LastCwd;
        public int? LastObserveTimeoutSeconds;
        public Func<PreviewRunnerStartResult>? StartBehavior;
        public Func<PreviewRunnerPortResult>? ObserveBehavior;
        public PreviewRunnerPortResult PortResult = new("proc-sess-1", 3000, Healthy: true, "ok");

        public Task<PreviewRunnerStartResult> StartProcessAsync(
            string runId, string? bearer, string command, string cwd, int? workPlanId, string? treeHash, CancellationToken ct)
        {
            StartCalls++;
            LastCommand = command;
            LastCwd = cwd;
            if (StartBehavior is not null) return Task.FromResult(StartBehavior());
            return Task.FromResult(new PreviewRunnerStartResult("proc-sess-1", 123, cwd));
        }

        public Task<PreviewRunnerPortResult> ObserveBoundPortAsync(
            string runId, string? bearer, string sessionId, int timeoutSeconds, string healthPath, CancellationToken ct)
        {
            LastObserveTimeoutSeconds = timeoutSeconds;
            if (ObserveBehavior is not null) return Task.FromResult(ObserveBehavior());
            return Task.FromResult(PortResult);
        }

        public Task<PreviewRunnerHealthResult> HealthCheckAsync(
            string runId, string? bearer, string sessionId, int port, string path, CancellationToken ct) =>
            Task.FromResult(new PreviewRunnerHealthResult(sessionId, port, true, 200));

        public Task StopProcessAsync(string runId, string? bearer, string sessionId, string reason, CancellationToken ct)
        {
            StopCalls++;
            LastStopReason = reason;
            return Task.CompletedTask;
        }

        public Task<PreviewRunnerHealthResult> HealthCheckByOriginAsync(
            string origin, string? bearer, string sessionId, int port, string path, CancellationToken ct) =>
            Task.FromResult(new PreviewRunnerHealthResult(sessionId, port, true, 200));
    }

    private sealed class FakePreviewService : ISandboxPreviewService
    {
        public bool EnabledValue = true;
        public int StartCalls;
        public Func<PreviewSession>? StartBehavior;

        public bool Enabled => EnabledValue;
        public int AllowedPortMin => 3000;
        public int AllowedPortMax => 9000;

        public Task<PreviewSession> StartPreviewAsync(
            string runId, int targetPort, string ownerUserId, CancellationToken ct = default,
            string? previewRunnerSessionId = null)
        {
            StartCalls++;
            if (StartBehavior is not null) return Task.FromResult(StartBehavior());
            return Task.FromResult(new PreviewSession(
                "gw-token", runId, "pod-1", targetPort, "https://preview.example.test", DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<PreviewSession>> ListForRunAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PreviewSession>>([]);

        public Task<bool> HasActivePreviewAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task RenewBackingClaimTtlAsync(string runId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SetBackingPodSafeToEvictAsync(string runId, bool safeToEvict, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task KeepAliveAsync(string token, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> VerifyTokenForRunAsync(string token, string runId, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task StopPreviewAsync(string token, CancellationToken ct = default) => Task.CompletedTask;

        public Task<int> ReapAsync(CancellationToken ct = default) => Task.FromResult(0);
    }
}
