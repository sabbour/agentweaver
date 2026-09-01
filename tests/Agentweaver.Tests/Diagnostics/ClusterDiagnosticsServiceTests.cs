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
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sandbox:Kubernetes:Namespace"] = "agentweaver",
                })
                .Build(),
            ClientFor(QuotaHandler(150, 200, 150, 200)));

        var dto = await service.GetClusterDiagnosticsAsync();

        dto.Checks.Select(c => c.Name).Should().NotContain("github_installation_token");
    }

    [Fact]
    public async Task GetClusterDiagnosticsAsync_UsesCurrentApiKeyInsteadOfRetiredOAuthSigningKey()
    {
        var service = NewClusterService(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sandbox:Kubernetes:Namespace"] = "agentweaver",
                    ["Auth:ApiKey"] = "current-csi-secret",
                })
                .Build(),
            ClientFor(QuotaHandler(150, 200, 150, 200)));

        var keyVault = (await service.GetClusterDiagnosticsAsync()).Checks.Single(c => c.Name == "key_vault");

        keyVault.Status.Should().Be("healthy");
        keyVault.Message.Should().Contain("mcp-api-key").And.NotContain("mcp-oauth-signing-key");
    }

    [Fact]
    public async Task GetClusterDiagnosticsAsync_ReportsMissingCurrentApiKeyAsCritical()
    {
        var service = NewClusterService(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sandbox:Kubernetes:Namespace"] = "agentweaver",
                })
                .Build(),
            ClientFor(QuotaHandler(150, 200, 150, 200)));

        var keyVault = (await service.GetClusterDiagnosticsAsync()).Checks.Single(c => c.Name == "key_vault");

        keyVault.Status.Should().Be("critical");
        keyVault.Message.Should().Contain("mcp-api-key");
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
    public async Task GetClusterDiagnosticsAsync_ReadsWarmPoolFromV1Beta1ClaimSpec()
    {
        var service = NewClusterService(
            BuildConfiguration(),
            ClientFor(ClaimsHandler()));

        var dto = await service.GetClusterDiagnosticsAsync();

        dto.SandboxClaims.Should().ContainSingle();
        dto.SandboxClaims[0].WarmPool.Should().Be("agentweaver-agent-host");
    }

    [Fact]
    public async Task GetClusterDiagnosticsAsync_ExposesWarmPoolInstances_WithClaimOwnership()
    {
        await using var db = await TestSqliteDatabase.CreateAsync();
        var runStore = new SqliteRunStore(db.Db);
        var projectId = ProjectId.New();
        const string runId = "01234567-89ab-cdef-0123-456789abcdef";
        await runStore.InsertAsync(new Run
        {
            Id = RunId.Parse(runId),
            RepositoryPath = "C:\\repo",
            OriginatingBranch = "feat/test",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "diagnostics",
            SubmittingUser = "octocat",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = projectId,
        });

        var service = NewClusterService(
            BuildConfiguration(db.Path),
            ClientFor(WarmPoolTopologyHandler()),
            db.Db);

        var dto = await service.GetClusterDiagnosticsAsync();

        dto.SandboxClaims.Should().ContainSingle();
        dto.SandboxClaims[0].RunId.Should().Be(runId);

        var pool = dto.WarmPools.Should().ContainSingle().Subject;
        pool.Instances.Should().ContainEquivalentOf(new WarmPoolInstanceDto
        {
            Name = "agentweaver-sandbox-available",
            Status = "available",
            Claimed = false,
        }, options => options.Excluding(x => x.AgeSeconds));

        pool.Instances.Should().ContainEquivalentOf(new WarmPoolInstanceDto
        {
            Name = "agentweaver-sandbox-claimed",
            Status = "claimed",
            Claimed = true,
            ClaimName = "agent-0123456789ab",
            RunId = runId,
            ProjectId = projectId.ToString(),
        }, options => options.Excluding(x => x.AgeSeconds));
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

    private static DiagnosticsService NewClusterService(
        IConfiguration configuration,
        IKubernetes client,
        SqliteDb? db = null) =>
        new(
            db: db!,
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
                ["Auth:ApiKey"] = "current-csi-secret",
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

    private static FakeKubeHandler ClaimsHandler()
    {
        var handler = QuotaHandler(150, 200, 150, 200);
        handler.OnGet(
            "/apis/extensions.agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxclaims",
            """
            {
              "apiVersion": "extensions.agents.x-k8s.io/v1beta1",
              "kind": "SandboxClaimList",
              "items": [
                {
                  "metadata": {
                    "name": "agent-0123456789ab",
                    "namespace": "agentweaver",
                    "creationTimestamp": "2026-07-28T12:00:00Z",
                    "annotations": {
                      "agentweaver.io/run-id": "01234567-89ab-cdef-0123-456789abcdef"
                    }
                  },
                  "spec": {
                    "warmPoolRef": {
                      "name": "agentweaver-agent-host"
                    }
                  },
                  "status": {
                    "sandbox": {
                      "name": "agentweaver-sandbox-claimed"
                    },
                    "conditions": [
                      {
                        "type": "Ready",
                        "status": "True"
                      }
                    ]
                  }
                }
              ]
            }
            """);
        return handler;
    }

    private static FakeKubeHandler WarmPoolTopologyHandler()
    {
        var handler = ClaimsHandler();
        handler.OnGet(
            "/apis/extensions.agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxwarmpools",
            """
            {
              "apiVersion": "extensions.agents.x-k8s.io/v1beta1",
              "kind": "SandboxWarmPoolList",
              "items": [
                {
                  "metadata": {
                    "name": "agentweaver-agent-host",
                    "namespace": "agentweaver",
                    "creationTimestamp": "2026-07-28T12:00:00Z"
                  },
                  "spec": {
                    "replicas": 2
                  },
                  "status": {
                    "readyReplicas": 2,
                    "availableReplicas": 1
                  }
                }
              ]
            }
            """);
        handler.OnGet(
            "/api/v1/namespaces/agentweaver/pods",
            """
            {
              "apiVersion": "v1",
              "kind": "PodList",
              "items": [
                {
                  "metadata": {
                    "name": "agentweaver-sandbox-claimed",
                    "namespace": "agentweaver",
                    "creationTimestamp": "2026-07-28T12:00:00Z"
                  },
                  "status": {
                    "phase": "Running",
                    "conditions": [
                      { "type": "Ready", "status": "True" }
                    ]
                  }
                },
                {
                  "metadata": {
                    "name": "agentweaver-sandbox-available",
                    "namespace": "agentweaver",
                    "creationTimestamp": "2026-07-28T12:05:00Z"
                  },
                  "status": {
                    "phase": "Running",
                    "conditions": [
                      { "type": "Ready", "status": "True" }
                    ]
                  }
                }
              ]
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
