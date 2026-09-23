using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests;

public sealed class KubernetesPodAgentEndpointResolverTests
{
    [Fact]
    [Trait("Category", "ProcessEnvironment")]
    public async Task Production_host_registers_generation_bound_dispatch_validator()
    {
        await using var factory = new ReviewWebApplicationFactory();

        factory.Services.GetRequiredService<IAgentHostDispatchBoundaryValidator>()
            .Should().BeOfType<AgentHostDispatchBoundaryValidator>();
    }

    [Fact]
    public async Task Dispatch_boundary_revalidates_project_authorization_before_provider_launch()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = MakeRun();
        await store.InsertAsync(run);
        var services = new ServiceCollection();
        services.AddScoped<IRunStore>(_ => store);
        services.AddScoped<IProjectRoleAuthorizationService, DenyingProjectAuthorization>();
        services.AddSingleton<RunModelInvocationGuard>();
        await using var provider = services.BuildServiceProvider();
        var validator = new AgentHostDispatchBoundaryValidator(
            provider.GetRequiredService<IServiceScopeFactory>());

        var act = () => validator.CaptureAsync(run.Id.ToString(), CancellationToken.None);

        var failure = await act.Should().ThrowAsync<AgentProviderException>();
        failure.Which.ErrorCode.Should().Be("project_authorization_required");
        failure.Which.FailureKind.Should().Be(AgentProviderFailureKind.Authorization);
    }

    [Fact]
    public async Task Concurrent_callers_join_one_recovering_generation_bound_launch()
    {
        var runId = RunId.New().ToString();
        var registry = new PodNameRegistry();
        var boundary = Boundary(runId);
        var validator = new MutableDispatchValidator(boundary);
        var lifecycle = new RecoveringDispatchLifecycle(registry, failFirst: true);
        var resolver = NewDispatchResolver(runId, registry, lifecycle, validator);

        var first = resolver.TryResolveEndpointAsync(runId, CancellationToken.None);
        var second = resolver.TryResolveEndpointAsync(runId, CancellationToken.None);
        var endpoints = await Task.WhenAll(first, second);

        endpoints.Should().OnlyContain(endpoint => endpoint == new Uri("http://10.0.0.50:8088/a2a/agent"));
        lifecycle.LaunchCalls.Should().Be(2, "one failed launch receives one bounded fresh launch");
        lifecycle.Contexts.Select(context => context.DispatchId).Distinct().Should().ContainSingle();
        lifecycle.Contexts.Should().OnlyContain(context =>
            context.LifecycleGeneration == boundary.LifecycleGeneration
            && context.DispatchProjectId == boundary.ProjectId
            && context.DispatchUserId == boundary.UserId
            && context.DispatchAgentName == boundary.AgentName
            && context.ProviderSnapshotKey == boundary.ProviderKey);
    }

    [Fact]
    public async Task Cancelled_waiter_does_not_cancel_shared_launch()
    {
        var runId = RunId.New().ToString();
        var registry = new PodNameRegistry();
        var lifecycle = new RecoveringDispatchLifecycle(registry, pauseLaunch: true);
        var resolver = NewDispatchResolver(runId, registry, lifecycle, new MutableDispatchValidator(Boundary(runId)));
        using var cancelled = new CancellationTokenSource();

        var first = resolver.TryResolveEndpointAsync(runId, cancelled.Token);
        await lifecycle.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var cancelledWait = async () => await first;
        await cancelledWait.Should().ThrowAsync<OperationCanceledException>();

        var second = resolver.TryResolveEndpointAsync(runId, CancellationToken.None);
        lifecycle.CompleteLaunch();
        (await second).Should().Be("http://10.0.0.50:8088/a2a/agent");
        lifecycle.LaunchCalls.Should().Be(1);
    }

    [Fact]
    public async Task Delivery_revalidation_rejects_changed_authorization_boundary_without_relaunch()
    {
        var runId = RunId.New().ToString();
        var registry = new PodNameRegistry();
        var validator = new MutableDispatchValidator(Boundary(runId));
        var lifecycle = new RecoveringDispatchLifecycle(registry);
        var resolver = NewDispatchResolver(runId, registry, lifecycle, validator);
        await resolver.TryResolveEndpointAsync(runId, CancellationToken.None);

        validator.Current = validator.Current with { UserId = "different-user" };
        var act = () => resolver.ValidateDeliveryAsync(runId, CancellationToken.None);

        var failure = await act.Should().ThrowAsync<WorkflowAgentInfrastructureException>();
        failure.Which.Reason.Should().Be("agenthost_dispatch_stale");
        lifecycle.LaunchCalls.Should().Be(1, "delivery validation must never replay a possibly accepted turn");
    }

    [Fact]
    public async Task Released_successful_claim_is_launched_again_instead_of_reusing_completed_cache()
    {
        var runId = RunId.New().ToString();
        var registry = new PodNameRegistry();
        var lifecycle = new RecoveringDispatchLifecycle(registry);
        var resolver = NewDispatchResolver(runId, registry, lifecycle, new MutableDispatchValidator(Boundary(runId)));

        await resolver.TryResolveEndpointAsync(runId, CancellationToken.None);
        await lifecycle.ReleaseAgentHostPodAsync(runId);
        await resolver.TryResolveEndpointAsync(runId, CancellationToken.None);

        lifecycle.LaunchCalls.Should().Be(2);
    }

    [Fact]
    public async Task Exhaustion_writes_one_redacted_retryable_terminal_outcome()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = MakeRun();
        await store.InsertAsync(run);
        var registry = new PodNameRegistry();
        var lifecycle = new RecoveringDispatchLifecycle(
            registry,
            alwaysFail: true,
            pauseLaunch: true,
            failureMessage: "Authorization: Bearer secret-do-not-persist");
        var resolver = NewDispatchResolver(
            run.Id.ToString(),
            registry,
            lifecycle,
            new MutableDispatchValidator(Boundary(run.Id.ToString(), run.LifecycleGeneration)),
            store);

        var first = CaptureFailureAsync(resolver, run.Id.ToString());
        var second = CaptureFailureAsync(resolver, run.Id.ToString());
        await lifecycle.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lifecycle.CompleteLaunch();
        var failures = await Task.WhenAll(first, second);

        failures.Should().OnlyContain(failure =>
            failure.Reason == "agent_host_unavailable" && failure.IsRetryable == true);
        lifecycle.LaunchCalls.Should().Be(2);
        var outcome = (await store.GetUnprojectedTerminalOutcomesAsync()).Should().ContainSingle().Subject;
        outcome.LifecycleGeneration.Should().Be(run.LifecycleGeneration);
        outcome.Outcome.Payload.GetProperty("errorCode").GetString().Should().Be("agent_host_unavailable");
        outcome.Outcome.Payload.GetProperty("retryable").GetBoolean().Should().BeTrue();
        outcome.Outcome.Payload.GetRawText().Should().NotContain("secret-do-not-persist");
        (await store.GetAsync(run.Id))!.Status.Should().Be(RunStatus.Failed);
    }

    [Fact]
    public async Task Endpoint_read_failure_relaunches_once_then_records_canonical_exhaustion()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = MakeRun();
        await store.InsertAsync(run);
        var registry = new PodNameRegistry();
        var lifecycle = new RecoveringDispatchLifecycle(registry);
        var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://localhost:8080" },
            new ThrowingPodHandler(new HttpRequestException("secret-do-not-persist")));
        var resolver = new KubernetesPodAgentEndpointResolver(
            client,
            registry,
            "agentweaver",
            new SandboxAgentOptions { RequireMtls = false },
            NullLogger<KubernetesPodAgentEndpointResolver>.Instance,
            lifecycle,
            store,
            new StaticLaunchContextResolver(),
            new MutableDispatchValidator(Boundary(run.Id.ToString(), run.LifecycleGeneration)));

        var failure = await CaptureFailureAsync(resolver, run.Id.ToString());

        failure.Reason.Should().Be("agent_host_unavailable");
        failure.IsRetryable.Should().BeTrue();
        lifecycle.LaunchCalls.Should().Be(2, "one failed endpoint read receives exactly one fresh launch");
        lifecycle.Contexts.Select(context => context.DispatchId).Distinct().Should().HaveCount(2);
        lifecycle.Contexts.Should().OnlyContain(context =>
            context.LifecycleGeneration == run.LifecycleGeneration);
        registry.TryGet(run.Id.ToString()).Should().BeNull();
        var outcome = (await store.GetUnprojectedTerminalOutcomesAsync()).Should().ContainSingle().Subject;
        outcome.LifecycleGeneration.Should().Be(run.LifecycleGeneration);
        outcome.Outcome.Payload.GetProperty("errorCode").GetString().Should().Be("agent_host_unavailable");
        outcome.Outcome.Payload.GetProperty("retryable").GetBoolean().Should().BeTrue();
        outcome.Outcome.Payload.GetRawText().Should().NotContain("secret-do-not-persist");
        (await store.GetAsync(run.Id))!.Status.Should().Be(RunStatus.Failed);
    }

    [Fact]
    public async Task Assistant_exhaustion_remains_resumable_and_does_not_write_terminal_outcome()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = MakeRun() with { AgentName = "Operator" };
        await store.InsertAsync(run);
        var registry = new PodNameRegistry();
        var lifecycle = new RecoveringDispatchLifecycle(registry, alwaysFail: true);
        var boundary = Boundary(run.Id.ToString(), run.LifecycleGeneration) with
        {
            AgentName = "Operator",
            IsResumableAssistant = true,
        };
        var resolver = NewDispatchResolver(
            run.Id.ToString(),
            registry,
            lifecycle,
            new MutableDispatchValidator(boundary),
            store);

        var failure = await CaptureFailureAsync(resolver, run.Id.ToString());

        failure.Reason.Should().Be("agent_host_unavailable");
        (await store.GetUnprojectedTerminalOutcomesAsync()).Should().BeEmpty();
        (await store.GetAsync(run.Id))!.Status.Should().Be(RunStatus.InProgress);
    }

    [Fact]
    public async Task ReapedNonTerminalPod_SignalsRetryableRedispatch()
    {
        const string runId = "run-reaped-pod";
        var registry = new PodNameRegistry();
        registry.Register(runId, "agenthost-reaped");
        var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://localhost:8080" },
            new NotFoundPodHandler());
        var resolver = new KubernetesPodAgentEndpointResolver(
            client,
            registry,
            "agentweaver",
            new SandboxAgentOptions { RequireMtls = false },
            NullLogger<KubernetesPodAgentEndpointResolver>.Instance);

        var act = async () => await resolver.TryResolveEndpointAsync(runId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<WorkflowAgentInfrastructureException>();
        exception.Which.Reason.Should().Be("agent_host_unavailable");
        exception.Which.IsRetryable.Should().BeTrue();
        registry.TryGet(runId).Should().BeNull("the stale pod mapping must not be reused on redispatch");
    }

    [Fact]
    public async Task ReapedCoordinatorPod_RedispatchesOnceAndResolvesReplacementForAssemblyRai()
    {
        const string runId = "run-assembly-rai";
        var registry = new PodNameRegistry();
        registry.Register(runId, "agenthost-reaped");
        var lifecycle = new RegisteringPodLifecycle(registry, "agenthost-replacement");
        var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://localhost:8080" },
            new ReapedThenReplacementPodHandler("agenthost-reaped", "agenthost-replacement", "10.0.0.42"));
        var resolver = new KubernetesPodAgentEndpointResolver(
            client,
            registry,
            "agentweaver",
            new SandboxAgentOptions { RequireMtls = false },
            NullLogger<KubernetesPodAgentEndpointResolver>.Instance,
            lifecycle);

        var endpoint = await resolver.TryResolveEndpointAsync(runId, CancellationToken.None);

        endpoint.Should().Be("http://10.0.0.42:8088/a2a/agent");
        lifecycle.LaunchCalls.Should().Be(1,
            "a reaped coordinator pod used by assembly RAI is recovered inline exactly once");
        registry.TryGet(runId).Should().Be("agenthost-replacement");
    }

    [Fact]
    public async Task ReapedReplacementPod_StopsAfterSingleRecoveryAttempt()
    {
        const string runId = "run-persistent-reap";
        var registry = new PodNameRegistry();
        registry.Register(runId, "agenthost-reaped");
        var lifecycle = new RegisteringPodLifecycle(registry, "agenthost-replacement");
        var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://localhost:8080" },
            new NotFoundPodHandler());
        var resolver = new KubernetesPodAgentEndpointResolver(
            client,
            registry,
            "agentweaver",
            new SandboxAgentOptions { RequireMtls = false },
            NullLogger<KubernetesPodAgentEndpointResolver>.Instance,
            lifecycle);

        var act = () => resolver.TryResolveEndpointAsync(runId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<WorkflowAgentInfrastructureException>();
        exception.Which.Reason.Should().Be("agent_host_unavailable");
        exception.Which.IsRetryable.Should().BeTrue(
            "the terminal dispatch outcome is retryable as a fresh run generation");
        lifecycle.LaunchCalls.Should().Be(1);
    }

    [Fact]
    public async Task Provider_failure_is_rethrown_unchanged_and_failed_launch_cache_is_cleared()
    {
        const string runId = "run-stale-project-credential";
        var projectId = ProjectId.New();
        var registry = new PodNameRegistry();
        var failure = new ModelProviderConnectionRequiredException(projectId);
        var lifecycle = new RecoveringProviderFailurePodLifecycle(
            registry,
            "agenthost-authorized",
            failure);
        var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://localhost:8080" },
            new ReadyPodHandler("agenthost-authorized", "10.0.0.43"));
        var resolver = new KubernetesPodAgentEndpointResolver(
            client,
            registry,
            "agentweaver",
            new SandboxAgentOptions { RequireMtls = false },
            NullLogger<KubernetesPodAgentEndpointResolver>.Instance,
            lifecycle);

        var first = () => resolver.TryResolveEndpointAsync(runId, CancellationToken.None);

        var exception = await first.Should().ThrowAsync<ModelProviderConnectionRequiredException>();
        exception.Which.Should().BeSameAs(failure);
        exception.Which.ErrorCode.Should().Be(ModelProviderConnectionRequirement.RequirementCode);
        exception.Which.FailureKind.Should().Be(AgentProviderFailureKind.Authorization);
        exception.Which.IsRetryable.Should().BeFalse();

        var endpoint = await resolver.TryResolveEndpointAsync(runId, CancellationToken.None);

        endpoint.Should().Be("http://10.0.0.43:8088/a2a/agent");
        lifecycle.LaunchCalls.Should().Be(2,
            "the typed provider failure must be removed from the launch cache before it propagates");
    }

    [Fact]
    public async Task Failed_launch_waiters_do_not_evict_replacement_launch()
    {
        const string runId = "run-concurrent-stale-launch";
        var projectId = ProjectId.New();
        var registry = new PodNameRegistry();
        var failure = new ModelProviderConnectionRequiredException(projectId);
        var lifecycle = new ConcurrentProviderFailurePodLifecycle(
            registry,
            "agenthost-replacement",
            failure);
        var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://localhost:8080" },
            new ReadyPodHandler("agenthost-replacement", "10.0.0.44"));
        var resolver = new KubernetesPodAgentEndpointResolver(
            client,
            registry,
            "agentweaver",
            new SandboxAgentOptions { RequireMtls = false },
            NullLogger<KubernetesPodAgentEndpointResolver>.Instance,
            lifecycle);

        var firstWaiter = resolver.TryResolveEndpointAsync(runId, CancellationToken.None);
        var secondWaiter = resolver.TryResolveEndpointAsync(runId, CancellationToken.None);
        lifecycle.LaunchCalls.Should().Be(1);

        var launches = GetLaunches(resolver);
        launches.TryGetValue(runId, out var failedLaunch).Should().BeTrue();
        var cachedFailedLaunch = failedLaunch
            ?? throw new InvalidOperationException("Failed launch was not cached.");
        launches.TryRemove(
                new KeyValuePair<string, Lazy<Task<string>>>(runId, cachedFailedLaunch))
            .Should().BeTrue("this stages the interleaving after one failed waiter removes its launch");

        var replacement = resolver.TryResolveEndpointAsync(runId, CancellationToken.None);
        lifecycle.LaunchCalls.Should().Be(2);

        lifecycle.FailFirstLaunch();
        var firstWaiterFailure = async () => await firstWaiter;
        var secondWaiterFailure = async () => await secondWaiter;
        await firstWaiterFailure.Should().ThrowAsync<ModelProviderConnectionRequiredException>();
        await secondWaiterFailure.Should().ThrowAsync<ModelProviderConnectionRequiredException>();

        var replacementWaiter = resolver.TryResolveEndpointAsync(runId, CancellationToken.None);
        lifecycle.LaunchCalls.Should().Be(2,
            "stale waiters must not remove the replacement launch from the cache");

        lifecycle.CompleteReplacement(runId);
        var endpoints = await Task.WhenAll(replacement, replacementWaiter);
        endpoints.Should().OnlyContain(endpoint => endpoint == new Uri("http://10.0.0.44:8088/a2a/agent"));
    }

    private static ConcurrentDictionary<string, Lazy<Task<string>>> GetLaunches(
        KubernetesPodAgentEndpointResolver resolver)
    {
        var field = typeof(KubernetesPodAgentEndpointResolver).GetField(
            "_launches",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return (ConcurrentDictionary<string, Lazy<Task<string>>>)
            (field?.GetValue(resolver) ?? throw new InvalidOperationException("Launch cache field not found."));
    }

    private static KubernetesPodAgentEndpointResolver NewDispatchResolver(
        string runId,
        PodNameRegistry registry,
        IAgentHostPodLifecycle lifecycle,
        IAgentHostDispatchBoundaryValidator validator,
        IRunStore? runStore = null)
    {
        var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://localhost:8080" },
            new ReadyPodHandler("agenthost-dispatch", "10.0.0.50"));
        return new KubernetesPodAgentEndpointResolver(
            client,
            registry,
            "agentweaver",
            new SandboxAgentOptions { RequireMtls = false },
            NullLogger<KubernetesPodAgentEndpointResolver>.Instance,
            lifecycle,
            runStore,
            new StaticLaunchContextResolver(),
            validator);
    }

    private static AgentHostDispatchBoundary Boundary(string runId, int generation = 3) =>
        new(runId, runId, generation, "project-1", "user-1", "link", "copilot:binding:v7", false);

    private static Run MakeRun() => new()
    {
        Id = RunId.New(),
        RepositoryPath = ".",
        OriginatingBranch = "dev",
        ModelSource = ModelSource.GitHubCopilot,
        Task = "dispatch recovery",
        SubmittingUser = "user-1",
        Status = RunStatus.InProgress,
        StartedAt = DateTimeOffset.UtcNow,
        ProjectId = ProjectId.New(),
        AgentName = "link",
    };

    private static async Task<WorkflowAgentInfrastructureException> CaptureFailureAsync(
        KubernetesPodAgentEndpointResolver resolver,
        string runId)
    {
        try
        {
            await resolver.TryResolveEndpointAsync(runId, CancellationToken.None);
            throw new InvalidOperationException("Expected AgentHost dispatch failure.");
        }
        catch (WorkflowAgentInfrastructureException ex)
        {
            return ex;
        }
    }

    private sealed class MutableDispatchValidator(AgentHostDispatchBoundary current)
        : IAgentHostDispatchBoundaryValidator
    {
        public AgentHostDispatchBoundary Current { get; set; } = current;

        public Task<AgentHostDispatchBoundary> CaptureAsync(string runId, CancellationToken ct) =>
            Task.FromResult(Current);
    }

    private sealed class DenyingProjectAuthorization : IProjectRoleAuthorizationService
    {
        public bool IsPlatformAdmin(CallerContext caller) => false;

        public Task<ProjectRole?> GetEffectiveRoleAsync(
            CallerContext caller,
            ProjectId projectId,
            CancellationToken ct = default) =>
            Task.FromResult<ProjectRole?>(null);

        public Task<bool> HasRoleAsync(
            CallerContext caller,
            ProjectId projectId,
            ProjectRole minimumRole,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyDictionary<ProjectId, ProjectRole>> ListExplicitRolesAsync(
            CallerContext caller,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<ProjectId, ProjectRole>>(
                new Dictionary<ProjectId, ProjectRole>());
    }

    private sealed class StaticLaunchContextResolver : IRunAgentHostContextResolver
    {
        public Task<AgentHostLaunchContext> ResolveAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult(new AgentHostLaunchContext("/workspace"));
    }

    private sealed class RecoveringDispatchLifecycle(
        IPodNameRegistry registry,
        bool failFirst = false,
        bool alwaysFail = false,
        bool pauseLaunch = false,
        string failureMessage = "readiness failed") : IAgentHostPodLifecycle
    {
        private readonly TaskCompletionSource _continue =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _launchCalls;

        public int LaunchCalls => Volatile.Read(ref _launchCalls);
        public List<AgentHostLaunchContext> Contexts { get; } = [];
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> LaunchAgentHostPodAsync(
            string runId,
            string? workingDirectoryOverride,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async Task<string> LaunchAgentHostPodAsync(
            string runId,
            AgentHostLaunchContext context,
            CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _launchCalls);
            lock (Contexts)
                Contexts.Add(context);
            Started.TrySetResult();
            await Task.Yield();
            if (pauseLaunch)
                await _continue.Task.ConfigureAwait(false);
            if (alwaysFail || (failFirst && call == 1))
                throw new InvalidOperationException(failureMessage);

            registry.Register(runId, "agenthost-dispatch");
            return "http://10.0.0.50:8088/a2a/agent";
        }

        public Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default)
        {
            registry.Unregister(runId);
            return Task.CompletedTask;
        }

        public void CompleteLaunch() => _continue.TrySetResult();
    }

    private sealed class RegisteringPodLifecycle(IPodNameRegistry registry, string replacementPod)
        : IAgentHostPodLifecycle
    {
        public int LaunchCalls { get; private set; }

        public Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default)
        {
            LaunchCalls++;
            registry.Register(runId, replacementPod);
            return Task.FromResult("http://replacement");
        }

        public Task<string> LaunchAgentHostPodAsync(
            string runId,
            string? workingDirectoryOverride,
            CancellationToken ct = default) =>
            LaunchAgentHostPodAsync(runId, ct);

        public Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecoveringProviderFailurePodLifecycle(
        IPodNameRegistry registry,
        string podName,
        AgentProviderException failure) : IAgentHostPodLifecycle
    {
        public int LaunchCalls { get; private set; }

        public Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default)
        {
            LaunchCalls++;
            if (LaunchCalls == 1)
                return Task.FromException<string>(failure);

            registry.Register(runId, podName);
            return Task.FromResult("http://authorized");
        }

        public Task<string> LaunchAgentHostPodAsync(
            string runId,
            string? workingDirectoryOverride,
            CancellationToken ct = default) =>
            LaunchAgentHostPodAsync(runId, ct);

        public Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class ConcurrentProviderFailurePodLifecycle(
        IPodNameRegistry registry,
        string podName,
        AgentProviderException failure) : IAgentHostPodLifecycle
    {
        private readonly TaskCompletionSource<string> _failedLaunch =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _replacementLaunch =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _launchCalls;

        public int LaunchCalls => Volatile.Read(ref _launchCalls);

        public Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _launchCalls);
            return call == 1 ? _failedLaunch.Task : _replacementLaunch.Task;
        }

        public Task<string> LaunchAgentHostPodAsync(
            string runId,
            string? workingDirectoryOverride,
            CancellationToken ct = default) =>
            LaunchAgentHostPodAsync(runId, ct);

        public Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public void FailFirstLaunch() => _failedLaunch.SetException(failure);

        public void CompleteReplacement(string runId)
        {
            registry.Register(runId, podName);
            _replacementLaunch.SetResult("http://replacement");
        }
    }

    private sealed class ReapedThenReplacementPodHandler(
        string reapedPod,
        string replacementPod,
        string replacementIp) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith($"/pods/{reapedPod}", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"kind":"Status","code":404}"""),
                    RequestMessage = request,
                });
            }

            if (request.RequestUri?.AbsolutePath.EndsWith($"/pods/{replacementPod}", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        kind = "Pod",
                        metadata = new { name = replacementPod },
                        status = new { podIP = replacementIp },
                    })),
                    RequestMessage = request,
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
            });
        }
    }

    private sealed class NotFoundPodHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"kind":"Status","code":404}"""),
                RequestMessage = request,
            });
    }

    private sealed class ThrowingPodHandler(Exception failure) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(failure);
    }

    private sealed class ReadyPodHandler(string podName, string podIp) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    kind = "Pod",
                    metadata = new { name = podName },
                    status = new { podIP = podIp },
                })),
                RequestMessage = request,
            });
    }
}
