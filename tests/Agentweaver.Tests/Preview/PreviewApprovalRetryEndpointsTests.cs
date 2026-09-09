using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using k8s;

namespace Agentweaver.Tests.Preview;

public sealed class PreviewApprovalRetryEndpointsTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PreviewApprovalRetryEndpointsTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task RetryExpiredApproval_CreatesFreshPendingAttempt()
    {
        var (runId, requestId) = await CreateRetryableRunAsync();

        var response = await _client.PostAsync(
            $"/api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var retryRequestId = body.GetProperty("request_id").GetString();
        retryRequestId.Should().NotBeNullOrWhiteSpace().And.NotBe(requestId);
        body.GetProperty("retry_of_request_id").GetString().Should().Be(requestId);

        var streams = _factory.Services.GetRequiredService<RunStreamStore>();
        var pending = streams.Get(runId)!.GetSnapshotSince(0).Events
            .Last(e => e.Type == EventTypes.SandboxPreviewPending);
        ReadString(pending.Payload, "request_id").Should().Be(retryRequestId);
        ReadString(pending.Payload, "retry_of_request_id").Should().Be(requestId);

        _factory.Services.GetRequiredService<IToolApprovalGate>()
            .Deny(runId, retryRequestId!)
            .Should().BeTrue();
    }

    [Fact]
    public async Task RetryExpiredApproval_RejectsNonOwner()
    {
        var (runId, requestId) = await CreateRetryableRunAsync(owner: "another-user");

        var response = await _client.PostAsync(
            $"/api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RetryApproval_RejectsRequestThatHasNotExpired()
    {
        var (runId, requestId) = await CreateRunAsync(RunStatus.InProgress);
        var gate = _factory.Services.GetRequiredService<IToolApprovalGate>();
        _ = gate.WaitForApprovalAsync(
            runId,
            requestId,
            "start_preview",
            "sandbox-preview:5173",
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        EmitRetryableFailure(runId, requestId);

        var response = await _client.PostAsync(
            $"/api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        gate.Deny(runId, requestId).Should().BeTrue();
    }

    [Fact]
    public async Task RetryExpiredApproval_RejectsTerminalRun()
    {
        var (runId, requestId) = await CreateRetryableRunAsync(status: RunStatus.Completed);

        var response = await _client.PostAsync(
            $"/api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RetryExpiredApproval_RejectsSupersededPreviewState()
    {
        var (runId, requestId) = await CreateRetryableRunAsync();
        _factory.Services.GetRequiredService<RunStreamStore>().Get(runId)!.RecordNext(
            EventTypes.SandboxPreviewReady,
            new { run_id = runId, target_port = 5173 });

        var response = await _client.PostAsync(
            $"/api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RetryExpiredApproval_ConcurrentDuplicatesCreateOneAttempt()
    {
        var (runId, requestId) = await CreateRetryableRunAsync();
        var path = $"/api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry";

        var responses = await Task.WhenAll(
            _client.PostAsync(path, content: null),
            _client.PostAsync(path, content: null));

        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);

        var accepted = responses.Single(r => r.StatusCode == HttpStatusCode.Accepted);
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        _factory.Services.GetRequiredService<IToolApprovalGate>()
            .Deny(runId, body.GetProperty("request_id").GetString()!)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task AgentTimeout_RetryRetainsSessionAndRechecksHealthAfterApproval(bool healthy, bool unreachable)
    {
        var runner = new RetainedRunnerClient(healthy, unreachable);
        var preview = new RetainedPreviewService(runner);
        var secrets = new InMemorySecretStore();
        var approvalTimeout = TimeSpan.FromMilliseconds(25);
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IPreviewRunnerHttpClient>(runner);
            services.AddSingleton<ISandboxPreviewService>(preview);
            services.AddSingleton<ISecretStore>(secrets);
            services.AddSingleton<IAgentHostTurnTokenRegistry>(new EmptyTurnTokens());
            services.AddTransient(sp => new AgentPreviewGate(
                sp.GetRequiredService<IToolApprovalGate>(),
                sp.GetRequiredService<IRunOptionsStore>(),
                sp.GetRequiredService<RunStreamStore>(),
                autoApproveConfigured: false,
                NullLogger<AgentPreviewGate>.Instance,
                approvalTimeout));
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var (runId, _) = await CreateRunAsync(RunStatus.InProgress, services: factory.Services);
        await secrets.SetSecretAsync(PreviewRunnerCredential.SecretKey(runId), "retained-test-credential");
        var streams = factory.Services.GetRequiredService<RunStreamStore>();
        var gate = factory.Services.GetRequiredService<IToolApprovalGate>();

        var timeoutResponse = await client.PostAsJsonAsync($"/api/runs/{runId}/sandbox/preview", new
        {
            target_port = 5173,
            preview_runner_session_id = "retained-process",
        });

        timeoutResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var expired = streams.Get(runId)!.GetSnapshotSince(0).Events
            .Single(e => e.Type == EventTypes.SandboxPreviewFailed);
        ReadString(expired.Payload, "preview_runner_session_id").Should().Be("retained-process");
        ReadString(expired.Payload, "reason").Should().Be("approval_timed_out");
        runner.HealthCalls.Should().Be(0);
        runner.StopCalls.Should().Be(0);
        preview.StartCalls.Should().Be(0);

        approvalTimeout = TimeSpan.FromSeconds(10);
        var requestId = ReadString(expired.Payload, "approval_request_id");
        var retryResponse = await client.PostAsync(
            $"/api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry", content: null);
        retryResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var retryId = (await retryResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("request_id").GetString()!;
        runner.HealthCalls.Should().Be(0, "health must be fresh after the delayed approval, not before it");
        (await gate.GrantAsync(runId, retryId, ApprovalScope.Once)).Should().BeTrue();

        for (var i = 0; i < 500; i++)
        {
            if (streams.Get(runId)!.GetSnapshotSince(0).Events.Count(e =>
                e.Type is EventTypes.SandboxPreviewReady or EventTypes.SandboxPreviewFailed) == 2)
                break;
            await Task.Delay(10);
        }

        var outcomes = streams.Get(runId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type is EventTypes.SandboxPreviewReady or EventTypes.SandboxPreviewFailed).ToList();
        outcomes.Should().HaveCount(2);
        runner.HealthCalls.Should().Be(1);
        runner.HealthCancellationToken.CanBeCanceled.Should().BeTrue();
        runner.LastSessionId.Should().Be("retained-process");
        runner.LastPort.Should().Be(5173);
        runner.LastBearer.Should().Be("retained-test-credential", "retry uses the retained per-run credential, not operator auth");
        ReadString(outcomes[1].Payload, "preview_runner_session_id").Should().Be("retained-process");
        if (healthy)
        {
            outcomes[1].Type.Should().Be(EventTypes.SandboxPreviewReady);
            preview.StartCalls.Should().Be(1);
            preview.SessionId.Should().Be("retained-process");
            runner.StopCalls.Should().Be(0);
        }
        else
        {
            outcomes[1].Type.Should().Be(EventTypes.SandboxPreviewFailed);
            ReadString(outcomes[1].Payload, "reason").Should().Be("preview_session_exited");
            preview.StartCalls.Should().Be(0);
            runner.StopCalls.Should().Be(1);
        }
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public async Task Publication_RunEndsDuringHttpsOrPersistenceWait_CannotPublish(
        bool initialApproval, bool completeLocalStream, bool pauseAtPersistence)
    {
        PausingPreviewEventStream? persistence = null;
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var publication = new HttpClient(new PreviewPublicationHandler(async (_, ct) =>
        {
            entered.SetResult(ct);
            // Deliberately return 200 even after cancellation, reproducing a late external response.
            if (!pauseAtPersistence)
                await resume.Task;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var kube = new FakeKubeHandler();
        const string routes = "/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/httproutes";
        kube.OnGet(routes, """{"kind":"HTTPRouteList","items":[]}""");
        using var kubernetes = new Kubernetes(new KubernetesClientConfiguration { Host = "http://localhost:8080" }, kube);
        var preview = new SandboxPreviewService(kubernetes, new SandboxPreviewOptions
        {
            Enabled = true,
            ZoneSuffix = "preview.example.test",
        }, NullLogger<SandboxPreviewService>.Instance, publicationClient: publication);
        var runner = new RetainedRunnerClient(healthy: true, unreachable: false);
        var secrets = new InMemorySecretStore();
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IPreviewRunnerHttpClient>(runner);
            services.AddSingleton<ISandboxPreviewService>(preview);
            services.AddSingleton<ISecretStore>(secrets);
            services.AddSingleton<IAgentHostTurnTokenRegistry>(new EmptyTurnTokens());
            if (pauseAtPersistence)
                services.AddSingleton<IRunEventStream>(sp => persistence = new PausingPreviewEventStream(
                    new SqliteRunEventStream(sp.GetRequiredService<IConfiguration>())));
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var (runId, requestId) = initialApproval
            ? await CreateRunAsync(RunStatus.InProgress, services: factory.Services)
            : await CreateRetryableRunAsync(
                services: factory.Services, previewRunnerSessionId: "retained-process");
        await secrets.SetSecretAsync(PreviewRunnerCredential.SecretKey(runId), "retained-test-credential");
        var streams = factory.Services.GetRequiredService<RunStreamStore>();
        var claim = SandboxClaimConventions.DeriveAgentHostClaimName(runId);
        kube.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claim}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"preview-pod"}}}""");

        using var requestLifetime = new CancellationTokenSource();
        Task<HttpResponseMessage>? initialRequest = null;
        string approvalId;
        if (initialApproval)
        {
            initialRequest = client.PostAsJsonAsync($"/api/runs/{runId}/sandbox/preview", new
            {
                target_port = 5173,
                preview_runner_session_id = "retained-process",
            }, requestLifetime.Token);
            approvalId = await WaitForApprovalAsync(streams, runId);
        }
        else
        {
            var response = await client.PostAsync(
                $"/api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry", null, requestLifetime.Token);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            approvalId = (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("request_id").GetString()!;
            requestLifetime.Cancel();
        }
        (await factory.Services.GetRequiredService<IToolApprovalGate>()
            .GrantAsync(runId, approvalId, ApprovalScope.Once)).Should().BeTrue();
        var publicationCt = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (persistence is not null)
            await persistence.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var route = kube.Requests.Single(r => r.Method == "POST" && r.Path == routes);
        using var routeDocument = JsonDocument.Parse(route.Body!);
        var routeName = routeDocument.RootElement.GetProperty("metadata").GetProperty("name").GetString();
        kube.OnGet($"{routes}/{routeName}", route.Body!);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        (await runStore.TrySetTerminalStatusAsync(
            RunId.Parse(runId), RunStatus.Failed, DateTimeOffset.UtcNow, "abandoned")).Should().BeTrue();
        if (completeLocalStream)
            streams.Complete(runId);
        var publicationCancelled = publicationCt.IsCancellationRequested;
        resume.SetResult();
        persistence?.Resume.TrySetResult();

        if (initialRequest is not null)
            (await initialRequest.WaitAsync(TimeSpan.FromSeconds(5))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var expectedFailures = initialApproval ? 1 : 2;
        for (var i = 0; i < 500; i++)
        {
            if (streams.Get(runId)!.GetSnapshotSince(0).Events.Count(e =>
                e.Type is EventTypes.SandboxPreviewReady or EventTypes.SandboxPreviewFailed) == expectedFailures)
                break;
            await Task.Delay(10);
        }

        var events = streams.Get(runId)!.GetSnapshotSince(0).Events;
        events.Should().NotContain(e =>
            e.Type == EventTypes.SandboxPreviewReady || e.Type == EventTypes.CoordinatorPreviewReady);
        events.Count(e => e.Type == EventTypes.SandboxPreviewFailed).Should().Be(expectedFailures);
        if (!pauseAtPersistence)
            publicationCancelled.Should().Be(completeLocalStream);
        runner.HealthCancellationToken.IsCancellationRequested.Should().Be(completeLocalStream);
        runner.LastBearer.Should().Be(initialApproval
            ? ProjectsWebApplicationFactory.TestApiKey : "retained-test-credential");
        await runner.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runner.HealthCalls.Should().Be(1);
        runner.StopCalls.Should().Be(1);
        runner.StopCancellationToken.IsCancellationRequested.Should().BeFalse();
        var deleted = kube.Requests.Where(r => r.Method == "DELETE").ToList();
        deleted.Should().HaveCount(2);
        deleted[0].Path.Should().Be($"{routes}/{routeName}");
        deleted[1].Path.Should().Be($"/api/v1/namespaces/agentweaver/services/{routeName}");
        kube.Requests.Should().Contain(r =>
            r.Method == "PATCH" && r.Path.EndsWith("/pods/preview-pod")
            && r.Body!.Contains("safe-to-evict") && r.Body.Contains("true"));
        using var retention = JsonDocument.Parse(kube.Requests.Last(r =>
            r.Method == "PATCH" && r.Path.EndsWith($"/sandboxclaims/{claim}")).Body!);
        retention.RootElement.GetProperty("spec").GetProperty("lifecycle")
            .GetProperty("ttlSecondsAfterFinished").GetInt32().Should().Be(600);
        (await factory.Services.GetRequiredService<IRunEventStream>().GetPersistedEventsAsync(runId))
            .Should().NotContain(e =>
                e.Type == EventTypes.SandboxPreviewReady || e.Type == EventTypes.CoordinatorPreviewReady);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Publication_RunEndsDuringPostApprovalHealth_DoesNotRegister(
        bool initialApproval, bool completeLocalStream)
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new RetainedRunnerClient(healthy: true, unreachable: false)
        {
            HealthBehavior = async ct =>
            {
                entered.SetResult(ct);
                await resume.Task;
                return new PreviewRunnerHealthResult("retained-process", 5173, true, 200);
            },
        };
        var preview = new RetainedPreviewService(runner);
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IPreviewRunnerHttpClient>(runner);
            services.AddSingleton<ISandboxPreviewService>(preview);
            services.AddSingleton<ISecretStore>(new InMemorySecretStore());
            services.AddSingleton<IAgentHostTurnTokenRegistry>(new EmptyTurnTokens());
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var (runId, requestId) = initialApproval
            ? await CreateRunAsync(RunStatus.InProgress, services: factory.Services)
            : await CreateRetryableRunAsync(
                services: factory.Services, previewRunnerSessionId: "retained-process");
        var streams = factory.Services.GetRequiredService<RunStreamStore>();
        Task<HttpResponseMessage>? initialRequest = null;
        string approvalId;
        if (initialApproval)
        {
            initialRequest = client.PostAsJsonAsync($"/api/runs/{runId}/sandbox/preview", new
            {
                target_port = 5173,
                preview_runner_session_id = "retained-process",
            });
            approvalId = await WaitForApprovalAsync(streams, runId);
        }
        else
        {
            var response = await client.PostAsync(
                $"/api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry", null);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            approvalId = (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("request_id").GetString()!;
        }
        (await factory.Services.GetRequiredService<IToolApprovalGate>()
            .GrantAsync(runId, approvalId, ApprovalScope.Once)).Should().BeTrue();
        var healthCt = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        (await factory.Services.GetRequiredService<IRunStore>().TrySetTerminalStatusAsync(
            RunId.Parse(runId), RunStatus.Failed, DateTimeOffset.UtcNow, "abandoned")).Should().BeTrue();
        if (completeLocalStream)
            streams.Complete(runId);
        var healthCancelled = healthCt.IsCancellationRequested;
        resume.SetResult();

        if (initialRequest is not null)
            (await initialRequest.WaitAsync(TimeSpan.FromSeconds(5))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await runner.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        healthCancelled.Should().Be(completeLocalStream);
        preview.StartCalls.Should().Be(0, "even late healthy results must not create a terminal run's publication");
        runner.StopCalls.Should().Be(1);
        runner.StopCancellationToken.IsCancellationRequested.Should().BeFalse();
        streams.Get(runId)!.GetSnapshotSince(0).Events.Should().NotContain(e =>
            e.Type == EventTypes.SandboxPreviewReady || e.Type == EventTypes.CoordinatorPreviewReady);
    }

    [Fact]
    public async Task OperatorPreview_AfterRunCompletion_RemainsAllowed()
    {
        var runner = new RetainedRunnerClient(healthy: true, unreachable: false);
        var preview = new RetainedPreviewService(runner, requireHealthCheck: false);
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<ISandboxPreviewService>(preview)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var (runId, _) = await CreateRunAsync(RunStatus.Completed, services: factory.Services);
        var streams = factory.Services.GetRequiredService<RunStreamStore>();
        streams.Complete(runId);

        var response = await client.PostAsJsonAsync($"/api/runs/{runId}/sandbox/port-forward", new { targetPort = 5173 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        preview.StartCalls.Should().Be(1);
        streams.Get(runId)!.GetSnapshotSince(0).Events.Count(e =>
            e.Type is EventTypes.SandboxPreviewReady or EventTypes.CoordinatorPreviewReady).Should().Be(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AgentPreview_ApprovedActiveRun_Publishes(bool hasProcessSession)
    {
        var runner = new RetainedRunnerClient(healthy: true, unreachable: false);
        var preview = new RetainedPreviewService(runner, requireHealthCheck: hasProcessSession);
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IPreviewRunnerHttpClient>(runner);
            services.AddSingleton<ISandboxPreviewService>(preview);
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var (runId, _) = await CreateRunAsync(RunStatus.InProgress, services: factory.Services);
        var streams = factory.Services.GetRequiredService<RunStreamStore>();
        var request = client.PostAsJsonAsync($"/api/runs/{runId}/sandbox/preview", new
        {
            target_port = 5173,
            preview_runner_session_id = hasProcessSession ? "retained-process" : null,
        });
        var approvalId = await WaitForApprovalAsync(streams, runId);
        (await factory.Services.GetRequiredService<IToolApprovalGate>()
            .GrantAsync(runId, approvalId, ApprovalScope.Once)).Should().BeTrue();

        (await request.WaitAsync(TimeSpan.FromSeconds(5))).StatusCode.Should().Be(HttpStatusCode.OK);
        preview.StartCalls.Should().Be(1);
        runner.HealthCalls.Should().Be(hasProcessSession ? 1 : 0);
        runner.StopCalls.Should().Be(0);
        streams.Get(runId)!.GetSnapshotSince(0).Events.Count(e =>
            e.Type is EventTypes.SandboxPreviewReady or EventTypes.CoordinatorPreviewReady).Should().Be(2);
    }

    private static async Task<string> WaitForApprovalAsync(RunStreamStore streams, string runId)
    {
        for (var i = 0; i < 500; i++)
        {
            var pending = streams.Get(runId)!.GetSnapshotSince(0).Events
                .LastOrDefault(e => e.Type == EventTypes.SandboxPreviewPending);
            if (pending is not null)
                return ReadString(pending.Payload, "request_id");
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Preview approval was not requested.");
    }

    private async Task<(string RunId, string RequestId)> CreateRetryableRunAsync(
        string owner = ProjectsWebApplicationFactory.TestUser,
        RunStatus status = RunStatus.InProgress,
        IServiceProvider? services = null,
        string? previewRunnerSessionId = null)
    {
        services ??= _factory.Services;
        var (runId, requestId) = await CreateRunAsync(status, owner, services);
        var gate = services.GetRequiredService<IToolApprovalGate>();
        await gate.WaitForApprovalAsync(
            runId,
            requestId,
            "start_preview",
            "sandbox-preview:5173",
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);
        gate.GetRequestState(runId, requestId).Should().Be(ToolApprovalRequestState.Expired);
        EmitRetryableFailure(runId, requestId, services, previewRunnerSessionId);
        return (runId, requestId);
    }

    private async Task<(string RunId, string RequestId)> CreateRunAsync(
        RunStatus status,
        string owner = ProjectsWebApplicationFactory.TestUser,
        IServiceProvider? services = null)
    {
        services ??= _factory.Services;
        var runId = RunId.New();
        await services.GetRequiredService<SqliteRunStore>().InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = _factory.NewWorkingDirectory(),
            OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "preview retry endpoint test",
            SubmittingUser = owner,
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
        });

        var id = runId.ToString();
        services.GetRequiredService<RunStreamStore>().Create(id, owner);
        return (id, Guid.NewGuid().ToString("n"));
    }

    private void EmitRetryableFailure(
        string runId, string requestId, IServiceProvider? services = null, string? previewRunnerSessionId = null) =>
        (services ?? _factory.Services).GetRequiredService<RunStreamStore>().Get(runId)!.RecordNext(
            EventTypes.SandboxPreviewFailed,
            new
            {
                run_id = runId,
                target_port = 5173,
                reason = "approval_timed_out",
                approval_request_id = requestId,
                retry_available = true,
                preview_runner_session_id = previewRunnerSessionId,
            });

    private static string ReadString(object payload, string property) =>
        payload.GetType().GetProperty(property)!.GetValue(payload)!.ToString()!;

    private sealed class EmptyTurnTokens : IAgentHostTurnTokenRegistry
    {
        public void RegisterTurnToken(string runId, string token) { }
        public string? TryGetTurnToken(string runId) => null;
        public void UnregisterTurnToken(string runId) { }
    }

    private sealed class RetainedRunnerClient(bool healthy, bool unreachable) : IPreviewRunnerHttpClient
    {
        public int HealthCalls;
        public int StopCalls;
        public string? LastSessionId;
        public string? LastBearer;
        public int LastPort;
        public CancellationToken HealthCancellationToken;
        public CancellationToken StopCancellationToken;
        public Func<CancellationToken, Task<PreviewRunnerHealthResult>>? HealthBehavior;
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PreviewRunnerHealthResult> HealthCheckAsync(
            string runId, string? bearer, string sessionId, int port, string path, CancellationToken ct)
        {
            HealthCalls++;
            LastSessionId = sessionId;
            LastBearer = bearer;
            LastPort = port;
            HealthCancellationToken = ct;
            if (HealthBehavior is not null) return HealthBehavior(ct);
            if (unreachable) throw new PreviewRunnerHttpException("agenthost_unreachable", "unreachable");
            return Task.FromResult(new PreviewRunnerHealthResult(sessionId, port, healthy, healthy ? 200 : 503));
        }

        public Task StopProcessAsync(string runId, string? bearer, string sessionId, string reason, CancellationToken ct)
        {
            StopCalls++;
            StopCancellationToken = ct;
            sessionId.Should().Be("retained-process");
            Stopped.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<PreviewRunnerStartResult> StartProcessAsync(
            string runId, string? bearer, string command, string cwd, int? workPlanId, string? treeHash, CancellationToken ct) =>
            throw new InvalidOperationException("Retry must not start another process.");

        public Task<PreviewRunnerPortResult> ObserveBoundPortAsync(
            string runId, string? bearer, string sessionId, int timeoutSeconds, string healthPath, CancellationToken ct) =>
            throw new InvalidOperationException("Retry must reuse the retained session and port.");

        public Task<PreviewRunnerHealthResult> HealthCheckByOriginAsync(
            string origin, string? bearer, string sessionId, int port, string path, CancellationToken ct) =>
            throw new InvalidOperationException("Registration must check health by run identity, not keepalive.");
    }

    private sealed class RetainedPreviewService(RetainedRunnerClient runner, bool requireHealthCheck = true) : ISandboxPreviewService
    {
        public int StartCalls;
        public string? SessionId;
        public bool Enabled => true;
        public int AllowedPortMin => 3000;
        public int AllowedPortMax => 9000;

        public Task<PreviewSession> StartPreviewAsync(
            string runId, int targetPort, string ownerUserId, CancellationToken ct = default,
            string? previewRunnerSessionId = null)
        {
            if (requireHealthCheck)
                runner.HealthCalls.Should().Be(1, "fresh process health must precede registration");
            StartCalls++;
            SessionId = previewRunnerSessionId;
            return Task.FromResult(new PreviewSession(
                "gateway-token", runId, "pod", targetPort, "https://preview.example.test", DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<PreviewSession>> ListForRunAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PreviewSession>>([]);
        public Task KeepAliveAsync(string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PreviewLifecycleState> ReconcilePreviewLifecycleAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult(PreviewLifecycleState.Previewable);
        public Task<bool> VerifyTokenForRunAsync(string token, string runId, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task StopPreviewAsync(string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> ReapAsync(CancellationToken ct = default) => Task.FromResult(0);
    }
}
