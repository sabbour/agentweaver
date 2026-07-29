using Agentweaver.Api.Diagnostics;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests;

public sealed class ClusterDiagnosticsServiceTests
{
    [Theory]
    [InlineData(150, 200, 140, 200, "healthy", "pods")]
    [InlineData(199, 200, 190, 200, "warning", "pods")]
    [InlineData(200, 200, 180, 200, "critical", "pods")]
    public async Task GetClusterDiagnosticsAsync_UsesObjectQuotaHeadroomThresholds(
        int podUsed,
        int podLimit,
        int sandboxClaimUsed,
        int sandboxClaimLimit,
        string expectedStatus,
        string expectedLimitingResource)
    {
        var service = NewClusterService(
            BuildConfiguration(),
            ClientFor(QuotaHandler(podUsed, podLimit, sandboxClaimUsed, sandboxClaimLimit)));

        var dto = await service.GetClusterDiagnosticsAsync();

        var quota = dto.Checks.Single(c => c.Name == "agent_pod_quota");
        quota.Status.Should().Be(expectedStatus);
        quota.Unit.Should().Be(expectedLimitingResource);
        quota.Message.Should().Contain($"pods {podUsed}/{podLimit}");
        quota.Message.Should().Contain($"sandboxclaims {sandboxClaimUsed}/{sandboxClaimLimit}");
    }

    [Fact]
    public async Task GetClusterDiagnosticsAsync_OmitsInstallationTokenCheck()
    {
        var service = NewClusterService(
            BuildConfiguration(),
            ClientFor(QuotaHandler(150, 200, 150, 200)));

        var dto = await service.GetClusterDiagnosticsAsync();

        dto.Checks.Select(c => c.Name).Should().NotContain("github_installation_token");
    }

    [Fact]
    public async Task GetClusterDiagnosticsAsync_HandlesRealQuotaShapeWithoutCpuKeys()
    {
        var service = NewClusterService(
            BuildConfiguration(),
            ClientFor(QuotaHandler(190, 200, 185, 200)));

        var dto = await service.GetClusterDiagnosticsAsync();

        dto.Checks.Single(c => c.Name == "agent_pod_quota").Status.Should().Be("healthy");
    }

    [Fact]
    public async Task GetSystemDiagnosticsAsync_UsesObjectQuotaHeadroom()
    {
        await using var db = await TestSqliteDatabase.CreateAsync();
        var config = BuildConfiguration(db.Path);
        var service = NewSystemService(
            config,
            db.Db,
            ClientFor(QuotaHandler(199, 200, 180, 200)));

        var dto = await service.GetSystemDiagnosticsAsync();

        dto.AgentPodQuota.Should().NotBeNull();
        dto.AgentPodQuota!.Status.Should().Be("warning");
        dto.AgentPodQuota.Unit.Should().Be("pods");
        dto.AgentPodQuota.Used.Should().Be(199);
        dto.AgentPodQuota.Limit.Should().Be(200);
    }

    private static DiagnosticsService NewClusterService(IConfiguration configuration, IKubernetes client) =>
        new(
            db: null!,
            projectStore: new EmptyProjectStore(),
            workspaceProvider: null!,
            heartbeatStore: new HeartbeatStatusStore(configuration),
            workflowRegistry: null!,
            configuration: configuration,
            scopeFactory: null!,
            k8s: client);

    private static DiagnosticsService NewSystemService(
        IConfiguration configuration,
        SqliteDb db,
        IKubernetes client) =>
        new(
            db: db,
            projectStore: new EmptyProjectStore(),
            workspaceProvider: null!,
            heartbeatStore: new HeartbeatStatusStore(configuration),
            workflowRegistry: null!,
            configuration: configuration,
            scopeFactory: null!,
            k8s: client);

    private static IConfiguration BuildConfiguration(string? dbPath = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = dbPath,
                ["Sandbox:Kubernetes:Namespace"] = "agentweaver",
            })
            .Build();

    private static IKubernetes ClientFor(FakeKubeHandler handler) =>
        new Kubernetes(new KubernetesClientConfiguration { Host = "http://localhost:8080" }, handler);

    private static FakeKubeHandler QuotaHandler(
        int podUsed,
        int podLimit,
        int sandboxClaimUsed,
        int sandboxClaimLimit)
    {
        var handler = new FakeKubeHandler();
        handler.OnGet(
            "/api/v1/namespaces/agentweaver/resourcequotas/agentweaver-quota",
            $$"""
              {
                "apiVersion": "v1",
                "kind": "ResourceQuota",
                "metadata": { "name": "agentweaver-quota", "namespace": "agentweaver" },
                "status": {
                  "hard": {
                    "pods": "{{podLimit}}",
                    "count/sandboxclaims.extensions.agents.x-k8s.io": "{{sandboxClaimLimit}}"
                  },
                  "used": {
                    "pods": "{{podUsed}}",
                    "count/sandboxclaims.extensions.agents.x-k8s.io": "{{sandboxClaimUsed}}"
                  }
                }
              }
              """);
        return handler;
    }

    private sealed class EmptyProjectStore : IProjectStore
    {
        public Task InsertAsync(Project project, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default) => Task.FromResult<Project?>(null);
        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Project>>(Array.Empty<Project>());
        public Task UpdateNameAsync(ProjectId id, string name, DateTimeOffset updatedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateProviderSettingsAsync(ProjectId id, ProjectProviderSettings settings, DateTimeOffset updatedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOriginAsync(ProjectId id, ProjectOrigin origin, DateTimeOffset updatedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryBeginDeleteAsync(ProjectId id, CancellationToken ct = default) => Task.FromResult(false);
        public Task DeleteAsync(ProjectId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdatePickupSettingsAsync(ProjectId id, int maxReadyPerHeartbeat, bool autopilot, bool autoApproveTools, DateTimeOffset updatedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateDefaultWorkflowAsync(ProjectId id, string? workflowId, DateTimeOffset updatedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateActiveReviewPolicyAsync(ProjectId id, string? policyName, DateTimeOffset updatedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateSandboxProfileAsync(ProjectId id, string? sandboxProfile, DateTimeOffset updatedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateSourceBlueprintAsync(ProjectId id, string? blueprintId, string? blueprintType, DateTimeOffset updatedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAllowedWorkflowIdsAsync(ProjectId id, IReadOnlyList<string>? allowedWorkflowIds, DateTimeOffset updatedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IProjectTeamMutationLease?> TryBeginTeamMutationAsync(ProjectId id, long expectedRevision, CancellationToken ct = default) =>
            Task.FromResult<IProjectTeamMutationLease?>(null);

        public Task UpdateGenerationModelSettingsAsync(
            ProjectId id,
            string? blueprintGenerationModel,
            string? workflowGenerationModel,
            string? outcomeSpecGenerationModel,
            DateTimeOffset updatedAt,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestSqliteDatabase(string path, SqliteDb db) : IAsyncDisposable
    {
        public string Path { get; } = path;
        public SqliteDb Db { get; } = db;

        public static async Task<TestSqliteDatabase> CreateAsync()
        {
            var path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                $"cluster-diagnostics-{Guid.NewGuid():N}.db");
            var config = BuildConfiguration(path);
            var db = new SqliteDb(config);
            await db.EnsureCreatedAsync().ConfigureAwait(false);
            return new TestSqliteDatabase(path, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Delay(50).ConfigureAwait(false);
            foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" })
            {
                try { File.Delete(file); }
                catch { }
            }
        }
    }
}
