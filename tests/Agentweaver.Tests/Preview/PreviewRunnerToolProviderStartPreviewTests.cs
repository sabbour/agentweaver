extern alias agenthost;

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Agentweaver.AgentTools;
using Agentweaver.SandboxExec;
using Agentweaver.SandboxFs;
using AgentHostOptions = agenthost::Agentweaver.AgentHost.AgentHostOptions;
using IPreviewRunner = agenthost::Agentweaver.AgentHost.IPreviewRunner;
using PreviewHealthResult = agenthost::Agentweaver.AgentHost.PreviewHealthResult;
using PreviewPortObservation = agenthost::Agentweaver.AgentHost.PreviewPortObservation;
using PreviewProcessStartResult = agenthost::Agentweaver.AgentHost.PreviewProcessStartResult;
using PreviewRunnerToolProvider = agenthost::Agentweaver.AgentHost.PreviewRunnerToolProvider;
using PreviewStopResult = agenthost::Agentweaver.AgentHost.PreviewStopResult;

namespace Agentweaver.Tests.Preview;

/// <summary>
/// Regression coverage for GitHub issue #334: <c>observe_bound_port</c>'s response text instructs
/// the agent to call <c>start_preview(port=...)</c> next, but until this fix no tool by that name
/// was ever registered for sandboxed subtask agents — only <see cref="PreviewRunnerToolProvider"/>'s
/// other three preview tools were (start_preview_process, observe_bound_port, health_check,
/// stop_preview_process). <c>start_preview</c> previously lived only in
/// <c>AgentweaverApiTools.Build</c>, gated on both <c>projectId</c>/<c>agentName</c> being
/// non-empty — a gate subtask sandboxes intentionally don't satisfy (see #268). These tests prove
/// the full lifecycle (start_preview_process -&gt; observe_bound_port -&gt; start_preview) now
/// produces a durable preview URL without ever dead-ending on a nonexistent tool call.
/// </summary>
public sealed class PreviewRunnerToolProviderStartPreviewTests
{
    private const string RunId = "run-fury-334";

    private static PreviewRunnerToolProvider NewProvider(
        IPreviewRunner runner, string apiBaseUrl = "http://localhost", string? apiKey = null) =>
        new(runner, Options.Create(new AgentHostOptions { ApiBaseUrl = apiBaseUrl, ApiKey = apiKey }));

    private static SandboxToolContext NewContext(string runId = RunId)
    {
        var workspacePath = System.IO.Path.Combine(
            Directory.GetCurrentDirectory(), ".agentweaver-test-workspaces", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);
        return new SandboxToolContext(
            AgentId: "fury",
            WorkingDirectory: workspacePath,
            SandboxRoot: workspacePath,
            Executor: SandboxExecutorFactory.CreatePassthrough(),
            FileTools: new SandboxedFileTools(workspacePath),
            SearchTools: new SandboxedSearchTools(workspacePath),
            Redactor: SandboxOutputRedactor.Default,
            Options: new SandboxToolOptions(ShellEnabled: false),
            Logger: NullLogger.Instance,
            RunId: runId);
    }

    [Fact]
    public void BuildTools_IncludesStartPreview_AlongsideTheOtherThreePreviewLifecycleTools()
    {
        var provider = NewProvider(new FakePreviewRunner());
        var context = NewContext();

        var names = provider.BuildTools(context).Select(t => t.Name).ToList();

        names.Should().Contain("start_preview_process");
        names.Should().Contain("observe_bound_port");
        names.Should().Contain("health_check");
        names.Should().Contain("stop_preview_process");
        names.Should().Contain("start_preview",
            because: "observe_bound_port's own response text tells the model to call start_preview " +
                      "next; it must always be registered alongside the other preview tools (#334)");
    }

    [Fact]
    public void BuildTools_OmitsStartPreview_WhenRunIdIsAbsent()
    {
        var provider = NewProvider(new FakePreviewRunner());
        var context = NewContext(runId: "");

        var names = provider.BuildTools(context).Select(t => t.Name).ToList();

        names.Should().NotContain("start_preview",
            because: "start_preview is run-scoped and must not be offered without a bound runId");
        names.Should().Contain("observe_bound_port", because: "the other preview tools are unconditional");
    }

    [Fact]
    public async Task FullLifecycle_ObserveThenStartPreview_ProducesDurablePreviewUrl_WithoutDeadEnding()
    {
        using var fakeApi = new FakeSandboxPreviewApi(RunId, expectedTargetPort: 6800,
            responseBody: """{"session_id":"sess-1","target_port":6800,"preview_url":"https://preview.example.com/p/tok"}""");

        var runner = new FakePreviewRunner
        {
            ObserveResult = new PreviewPortObservation(
                SessionId: "sess-1", Port: 6800, Evidence: "proc-fd", Healthy: true,
                HealthEvidence: "HTTP 200", AppPort: 6800, Reason: null),
        };
        var provider = NewProvider(runner, apiBaseUrl: fakeApi.BaseUrl);
        var context = NewContext();
        var tools = provider.BuildTools(context).ToDictionary(t => t.Name);

        // Step 1: start_preview_process.
        var startResult = await tools["start_preview_process"].InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["command"] = "npm run dev" }));
        startResult!.ToString().Should().Contain("preview_process_started");

        // Step 2: observe_bound_port — its own text must still tell the model to call start_preview.
        var observeResult = (await tools["observe_bound_port"].InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["session_id"] = "sess-1" })))!.ToString();
        observeResult.Should().Contain("Call start_preview(port=6800) next.");

        // Step 3: start_preview — this is the tool the agent used to fail to find (#334). It must
        // exist, and calling it must actually finalize a durable, externally-reachable preview URL
        // rather than dead-ending.
        tools.Should().ContainKey("start_preview");
        var previewResult = (await tools["start_preview"].InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["port"] = 6800 })))!.ToString();

        previewResult.Should().Contain("https://preview.example.com/p/tok",
            because: "the agent must receive a real, shareable preview URL — not a dead end");
        fakeApi.LastRequestPath.Should().Be($"/api/runs/{RunId}/sandbox/preview");
        fakeApi.LastRequestBody.Should().Contain("\"target_port\":6800");
    }

    private sealed class FakePreviewRunner : IPreviewRunner
    {
        public PreviewPortObservation ObserveResult { get; set; } = new(
            SessionId: "sess-1", Port: 3000, Evidence: "e", Healthy: true, HealthEvidence: "h");

        public Task<PreviewProcessStartResult> StartPreviewProcessAsync(
            string command, string cwd, string? runId, string? workPlanId, string? treeHash,
            CancellationToken ct = default) =>
            Task.FromResult(new PreviewProcessStartResult("sess-1", 4242, DateTimeOffset.UtcNow, cwd));

        public Task<PreviewPortObservation> ObserveBoundPortAsync(
            string sessionId, TimeSpan? timeout = null, string healthPath = "/",
            CancellationToken ct = default) =>
            Task.FromResult(ObserveResult);

        public Task<PreviewHealthResult> HealthCheckAsync(
            string sessionId, int port, string path = "/", CancellationToken ct = default) =>
            Task.FromResult(new PreviewHealthResult(sessionId, port, path, true, 200, "ok"));

        public Task<PreviewStopResult> StopPreviewProcessAsync(
            string sessionId, string reason, CancellationToken ct = default) =>
            Task.FromResult(new PreviewStopResult(sessionId, true, reason));
    }

    /// <summary>Minimal loopback HTTP server standing in for the real sandbox preview endpoint,
    /// since <see cref="PreviewRunnerToolProvider"/> builds its own real <see cref="HttpClient"/>
    /// from <c>AgentHostOptions.ApiBaseUrl</c> with no test-only override hook.</summary>
    private sealed class FakeSandboxPreviewApi : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly string _expectedPath;
        private readonly int _expectedTargetPort;
        private readonly string _responseBody;
        private readonly Task _acceptLoop;

        public string BaseUrl { get; }
        public string? LastRequestPath { get; private set; }
        public string LastRequestBody { get; private set; } = string.Empty;

        public FakeSandboxPreviewApi(string runId, int expectedTargetPort, string responseBody)
        {
            _expectedPath = $"/api/runs/{runId}/sandbox/preview";
            _expectedTargetPort = expectedTargetPort;
            _responseBody = responseBody;

            var port = GetFreeTcpPort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                LastRequestPath = ctx.Request.Url?.AbsolutePath;
                using var reader = new StreamReader(ctx.Request.InputStream);
                LastRequestBody = await reader.ReadToEndAsync();

                var body = LastRequestPath == _expectedPath && LastRequestBody.Contains($"\"target_port\":{_expectedTargetPort}")
                    ? _responseBody
                    : """{"error":"unexpected request"}""";
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.OutputStream.Close();
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }

        private static int GetFreeTcpPort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { }
        }
    }
}
