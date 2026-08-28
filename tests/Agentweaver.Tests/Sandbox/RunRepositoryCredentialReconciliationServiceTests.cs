using System.Net.Http;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Sandbox;

public sealed class RunRepositoryCredentialReconciliationServiceTests
{
    [Fact]
    public async Task ReconcileOnce_RevokesReplicaACredential_WhenReplicaBReleasesClaim()
    {
        await using var database = await TestSqliteDb.CreateAsync();
        var runStore = new SqliteRunStore(database.Db);
        var run = await InsertRunAsync(runStore, RunStatus.AwaitingReview);
        var claims = new ReplicaBClaimStore();
        claims.Add(run.SandboxClaimName!, run.Id.ToString());
        using var services = CreateServices(claims);

        var minter = new StubCredentialMinter(
            new RepositoryCredential("replica-a-release-token", DateTimeOffset.UtcNow.AddMinutes(5)));
        var registry = new RunRepositoryCredentialRegistry(minter);
        (await registry.MintAsync(run.Id.ToString())).Should().Be("replica-a-release-token");
        var replicaA = CreateReconciler(registry, runStore, services);

        // Replica B performs the normal pod release after the run reaches its review state.
        claims.DeleteClaimFromReplicaB(run.SandboxClaimName!);

        await replicaA.ReconcileOnceAsync();

        minter.RevokedTokens.Should().ContainSingle().Which.Should().Be("replica-a-release-token",
            "replica A must observe replica B's authoritative claim deletion on its next sweep");
    }

    [Fact]
    public async Task ReconcileOnce_RetriesRevocationAfterReplicaBDeletesRunAndClaim()
    {
        var now = new DateTimeOffset(2026, 8, 27, 20, 0, 0, TimeSpan.Zero);
        await using var database = await TestSqliteDb.CreateAsync();
        var runStore = new SqliteRunStore(database.Db);
        var run = await InsertRunAsync(runStore, RunStatus.InProgress);
        var claims = new ReplicaBClaimStore();
        claims.Add(run.SandboxClaimName!, run.Id.ToString());
        using var services = CreateServices(claims);

        var minter = new StubCredentialMinter(
            new RepositoryCredential("replica-a-retry-token", now.AddMinutes(5)));
        minter.RevokeFailures.Enqueue(new HttpRequestException("temporary revoke failure"));
        var clock = new MutableTimeProvider(now);
        var registry = new RunRepositoryCredentialRegistry(minter, clock);
        (await registry.MintAsync(run.Id.ToString())).Should().Be("replica-a-retry-token");
        var replicaA = CreateReconciler(registry, runStore, services);

        // The orphan-cleanup request landed on replica B, which removed both shared records.
        await runStore.DeleteAsync(run.Id);
        claims.DeleteClaimFromReplicaB(run.SandboxClaimName!);

        await replicaA.ReconcileOnceAsync();
        minter.RevokedTokens.Should().ContainSingle(
            "the first failed revoke is retained locally even after replica B removed the claim");

        clock.Advance(RunRepositoryCredentialRegistry.InitialRevocationRetryDelay);
        await replicaA.ReconcileOnceAsync();

        minter.RevokedTokens.Should().HaveCount(2,
            "the retained credential is retried through its expiry even though its run and claim are gone");
        minter.RevokedTokens.Should().OnlyContain(token => token == "replica-a-retry-token");
    }

    [Fact]
    public async Task ReconcileOnce_DoesNotRevokeCredentialForActiveRunWithLiveClaim()
    {
        await using var database = await TestSqliteDb.CreateAsync();
        var runStore = new SqliteRunStore(database.Db);
        var run = await InsertRunAsync(runStore, RunStatus.InProgress);
        var claims = new ReplicaBClaimStore();
        claims.Add(run.SandboxClaimName!, run.Id.ToString());
        using var services = CreateServices(claims);

        var minter = new StubCredentialMinter(
            new RepositoryCredential("active-run-token", DateTimeOffset.UtcNow.AddMinutes(5)));
        var registry = new RunRepositoryCredentialRegistry(minter);
        (await registry.MintAsync(run.Id.ToString())).Should().Be("active-run-token");

        await CreateReconciler(registry, runStore, services).ReconcileOnceAsync();

        minter.RevokedTokens.Should().BeEmpty(
            "a non-terminal run whose authoritative AgentHost claim is still present remains live");
    }

    /// <summary>
    /// Regression for PR #968's rejected cross-replica repair: the tests above insert a run with
    /// <c>SandboxClaimName</c> set BY THE TEST, which hid the real production gap — the
    /// <c>kata-exec-sidecar</c> AgentHost lifecycle (<see cref="KubernetesSandboxExecutor.LaunchAgentHostPodAsync"/>)
    /// never persisted the claim name it created, so this reconciliation never had anything to check.
    /// This test drives that REAL lifecycle end-to-end — no manual <c>SandboxClaimName</c> injection —
    /// against a fake cluster, then simulates a second replica deleting the same claim, and proves the
    /// first replica revokes its locally held repository credential.
    /// </summary>
    [Fact]
    public async Task ReconcileOnce_RevokesReplicaACredential_WhenReplicaBReleasesTheRealClaimLaunchAgentHostPodAsyncPersisted()
    {
        var id = RunId.New();
        var runId = id.ToString();
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        await using var database = await TestSqliteDb.CreateAsync();
        var runStore = new SqliteRunStore(database.Db);

        // Deliberately NO SandboxBackend/SandboxClaimName/SandboxNamespace here — proving those are
        // written by LaunchAgentHostPodAsync itself, not by test setup.
        var run = new Run
        {
            Id = id,
            RepositoryPath = "real-claim-lifecycle-repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "real AgentHost claim lifecycle",
            SubmittingUser = "replica-a-user",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await runStore.InsertAsync(run);

        // Fakes the cluster's view of the AgentHost claim/pod once the agent-sandbox controller has
        // bound it — the same shape KubernetesSandboxExecutor's real WaitForBound polling expects.
        var kube = new FakeKubeHandler();
        kube.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-real-claim-pod"}}}""");
        kube.OnAny(
            @"^/api/v1/namespaces/agentweaver/pods/agent-real-claim-pod$",
            """{"kind":"Pod","metadata":{"name":"agent-real-claim-pod"},"status":{"podIP":"10.0.5.9"}}""");

        var k8sClient = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://localhost:8080" }, kube);
        var options = new KubernetesSandboxOptions
        {
            Namespace = "agentweaver",
            WarmPoolRef = "agentweaver-sandbox",
            AgentHostWarmPoolRef = "agentweaver-agent-host",
            TimeoutSeconds = 600,
            RequireMtls = false,
            AgentHostPort = 8088,
            AgentHostA2APath = "/a2a/agent",
            WorkspaceMountPath = "/workspace",
        };

        // RunStoreSubmittingUserResolver is the REAL production resolver (reads Run.SubmittingUser
        // back from the same store), not a hand-rolled stub — keeping this launch as close to
        // production as the fake cluster allows. runStore is wired in so LaunchAgentHostPodAsync can
        // exercise its own persistence write.
        var executor = new KubernetesSandboxExecutor(
            k8sClient,
            options,
            NullLogger<KubernetesSandboxExecutor>.Instance,
            submittingUserResolver: new RunStoreSubmittingUserResolver(runStore),
            runStore: runStore);

        var endpoint = await executor.LaunchAgentHostPodAsync(runId);
        endpoint.Should().Contain("10.0.5.9");

        // Proves the fix: production code — not this test — wrote the real claim identity.
        var persistedRun = await runStore.GetAsync(id);
        persistedRun.Should().NotBeNull();
        persistedRun!.SandboxClaimName.Should().Be(claimName);
        persistedRun.SandboxBackend.Should().Be("kata-exec-sidecar");

        // Replica A mints and locally holds a repository credential for this run (decoupled from the
        // executor's own minting path, mirroring how the other tests in this file isolate the
        // registry/reconciliation behavior under test).
        var minter = new StubCredentialMinter(
            new RepositoryCredential("replica-a-real-claim-token", DateTimeOffset.UtcNow.AddMinutes(5)));
        var registry = new RunRepositoryCredentialRegistry(minter);
        (await registry.MintAsync(runId)).Should().Be("replica-a-real-claim-token");

        // Replica B's cluster view initially matches the claim LaunchAgentHostPodAsync created...
        var claims = new ReplicaBClaimStore();
        claims.Add(claimName, runId);
        using var services = CreateServices(claims);
        var replicaA = CreateReconciler(registry, runStore, services);

        // ...until replica B performs the normal pod release and deletes it.
        claims.DeleteClaimFromReplicaB(claimName);

        await replicaA.ReconcileOnceAsync();

        minter.RevokedTokens.Should().ContainSingle().Which.Should().Be("replica-a-real-claim-token",
            "replica A must revoke its local token once replica B deletes the REAL AgentHost claim " +
            "that LaunchAgentHostPodAsync itself created and persisted, with no manual " +
            "SandboxClaimName injection standing in for production behavior");
    }

    [Fact]
    public void ApiRegistersRepositoryCredentialReconciliationAsHostedService()
    {
        using var factory = new AgentweaverWebApplicationFactory();

        factory.Services.GetServices<IHostedService>()
            .Should().Contain(service => service is RunRepositoryCredentialReconciliationService);
    }

    private static ServiceProvider CreateServices(IAgentHostReaper reaper) =>
        new ServiceCollection()
            .AddSingleton(reaper)
            .BuildServiceProvider();

    private static RunRepositoryCredentialReconciliationService CreateReconciler(
        RunRepositoryCredentialRegistry registry,
        IRunStore runStore,
        IServiceProvider services) =>
        new(
            registry,
            new RunRepositoryCredentialLiveness(
                runStore,
                services,
                NullLogger<RunRepositoryCredentialLiveness>.Instance),
            NullLogger<RunRepositoryCredentialReconciliationService>.Instance);

    private static async Task<Run> InsertRunAsync(SqliteRunStore runStore, RunStatus status)
    {
        var id = RunId.New();
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(id.ToString());
        var run = new Run
        {
            Id = id,
            RepositoryPath = "credential-reconciliation-repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "credential reconciliation",
            SubmittingUser = "replica-test-user",
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
            SandboxBackend = "kubernetes-sandbox-claim",
            SandboxClaimName = claimName,
            SandboxNamespace = "agentweaver",
        };
        await runStore.InsertAsync(run);
        return run;
    }

    private sealed class ReplicaBClaimStore : IAgentHostReaper
    {
        private readonly Dictionary<string, AgentHostClaimInfo> _claims = new(StringComparer.Ordinal);

        public void Add(string claimName, string runId) =>
            _claims[claimName] = new AgentHostClaimInfo(
                claimName,
                RunId: runId,
                PodName: "agenthost-pod",
                Ready: true,
                CreatedAt: DateTimeOffset.UtcNow,
                Orphaned: false,
                AnnotatedRunId: runId);

        public void DeleteClaimFromReplicaB(string claimName) => _claims.Remove(claimName);

        public Task<int> SweepOrphanedPodsAsync(CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<AgentHostClaimInfo>> GetClaimInventoryAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AgentHostClaimInfo>>(_claims.Values.ToArray());
    }

    private sealed class StubCredentialMinter(RepositoryCredential credential)
        : IRunRepositoryCredentialMinter
    {
        public Queue<Exception> RevokeFailures { get; } = new();
        public List<string> RevokedTokens { get; } = [];

        public Task<RepositoryCredential?> MintAsync(string runId, CancellationToken ct) =>
            Task.FromResult<RepositoryCredential?>(credential);

        public Task RevokeAsync(string accessToken, CancellationToken ct)
        {
            RevokedTokens.Add(accessToken);
            if (RevokeFailures.TryDequeue(out var failure))
                throw failure;
            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
