using System.Net;
using System.Text.Json;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Api;

public sealed class RunSandboxPersistenceTests : IClassFixture<ReviewWebApplicationFactory>
{
    private readonly ReviewWebApplicationFactory _factory;
    private readonly HttpClient _ownerClient;

    public RunSandboxPersistenceTests(ReviewWebApplicationFactory factory)
    {
        _factory = factory;
        _ownerClient = factory.CreateClient();
        _ownerClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ReviewWebApplicationFactory.OwnerApiKey);
    }

    [Fact]
    public async Task SandboxSelectedEvent_PersistsSandboxInfoToRunStore()
    {
        var store = _factory.Services.GetRequiredService<SqliteRunStore>();
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var workflowFactory = _factory.Services.GetRequiredService<RunWorkflowFactory>();
        var run = await InsertOwnerRunAsync(store);

        streamStore.Create(run.Id.ToString(), run.SubmittingUser);
        var writer = workflowFactory.GetRecordingWriter(run.Id.ToString());

        writer.Should().NotBeNull();
        writer!.TryWrite(new RunEvent(0, "sandbox.selected", new
        {
            backend = "kubernetes-sandbox-claim",
            isRealIsolation = true,
            reason = "selected-for-preview",
        })).Should().BeTrue();

        var updated = await store.GetAsync(run.Id);
        updated.Should().NotBeNull();
        updated!.SandboxBackend.Should().Be("kubernetes-sandbox-claim");
        updated.SandboxClaimName.Should().Be(Agentweaver.Api.Sandbox.SandboxClaimConventions.DeriveAgentHostClaimName(run.Id.ToString()));
        updated.SandboxNamespace.Should().Be("agentweaver");
    }

    [Fact]
    public async Task GetRun_FallsBackToPersistedSandboxInfo_WhenStreamEntryIsEvicted()
    {
        var store = _factory.Services.GetRequiredService<SqliteRunStore>();
        var run = await InsertOwnerRunAsync(store);
        await store.SetSandboxInfoAsync(run.Id, "kubernetes-sandbox-claim", "claim-db", "pod-db", "agentweaver");

        var response = await _ownerClient.GetAsync($"/api/runs/{run.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var sandbox = doc.RootElement.GetProperty("sandbox");
        sandbox.GetProperty("backend").GetString().Should().Be("kubernetes-sandbox-claim");
        sandbox.GetProperty("claim_name").GetString().Should().Be("claim-db");
        sandbox.GetProperty("pod_name").GetString().Should().Be("pod-db");
        sandbox.GetProperty("namespace").GetString().Should().Be("agentweaver");
    }

    [Fact]
    public async Task GetRun_PrefersWarmStreamSandboxInfo_WhenAvailable()
    {
        var store = _factory.Services.GetRequiredService<SqliteRunStore>();
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var run = await InsertOwnerRunAsync(store);
        await store.SetSandboxInfoAsync(run.Id, "process", "claim-db", "pod-db", "agentweaver");

        var entry = streamStore.Create(run.Id.ToString(), run.SubmittingUser);
        entry.RecordNext("sandbox.selected", new
        {
            backend = "kubernetes-sandbox-claim",
            isRealIsolation = true,
            reason = "warm-stream-wins",
        });

        var response = await _ownerClient.GetAsync($"/api/runs/{run.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var sandbox = doc.RootElement.GetProperty("sandbox");
        sandbox.GetProperty("backend").GetString().Should().Be("kubernetes-sandbox-claim");
        sandbox.GetProperty("is_real_isolation").GetBoolean().Should().BeTrue();
        sandbox.GetProperty("claim_name").GetString().Should().Be("claim-db");
    }

    private static async Task<Run> InsertOwnerRunAsync(SqliteRunStore store, RunStatus status = RunStatus.InProgress)
    {
        var run = new Run
        {
            Id = RunId.New(),
            RepositoryPath = "dummy-repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "sandbox persistence test",
            SubmittingUser = ReviewWebApplicationFactory.OwnerUser,
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
        };

        await store.InsertAsync(run);
        return run;
    }
}
