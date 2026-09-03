using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Assistant;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Assistant;

/// <summary>
/// Regressions for the two live operator-assistant defects fixed together:
///
/// <list type="number">
/// <item>False "too many active assistant conversations" 429s. The per-user bound used to count the
/// per-process <c>_runs</c> dictionary, which <c>RehydrateRunAsync</c> also inserts into — so merely
/// opening or replying to an existing conversation occupied a slot for the whole idle timeout, and
/// with two API replicas and no session affinity the SAME conversation occupied a slot on both. The
/// bound is now derived from durable run status, so it is replica-independent and counts only
/// genuinely active conversations.</item>
/// <item>15-20s of silence per turn. The conversation's AgentHost pod used to be claimed and released
/// on EVERY turn, paying the claim-bind + one-shot <c>/configure</c> cold start again each message.
/// The pod is now HELD between turns and released on pod-idle, on dormancy, and on failure.</item>
/// </list>
/// </summary>
public sealed class AssistantRunConcurrencyAndPodLifecycleTests
{
    [Fact]
    public async Task StartRun_DormantConversationsStillResidentInMemory_DoNotConsumeConcurrencySlots()
    {
        await using var factory = new AssistantWebApplicationFactory { MaxConcurrentRunsPerUser = 2 };
        var client = AuthedClient(factory);

        var first = await StartRunAsync(client);
        var second = await StartRunAsync(client);

        var blocked = await client.PostAsJsonAsync("/api/assistant/runs", new { });
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "two genuinely active conversations exhaust a bound of two");

        // Park both conversations as dormant in the SHARED store, exactly as the idle sweep does,
        // while deliberately leaving them resident in this process's in-memory cache.
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        (await runStore.TryTransitionToIdleAsync(RunId.Parse(first), CancellationToken.None)).Should().BeTrue();
        (await runStore.TryTransitionToIdleAsync(RunId.Parse(second), CancellationToken.None)).Should().BeTrue();

        var afterDormancy = await client.PostAsJsonAsync("/api/assistant/runs", new { });
        afterDormancy.StatusCode.Should().Be(HttpStatusCode.Created,
            "a dormant conversation is not an active execution, so it must not keep holding a slot just " +
            "because this replica still has it cached");
    }

    [Fact]
    public async Task StartRun_TwoReplicasSharingTheStore_DoNotDoubleCountTheSameConversation()
    {
        // Two API replicas: separate processes with their OWN in-memory caches and no session
        // affinity, but one shared database. The bound must be computed from that shared state.
        var sharedDb = Path.Combine(Path.GetTempPath(), $"agentweaver-assistant-shared-{Guid.NewGuid():N}.db");
        await using var replica1 = new AssistantWebApplicationFactory
        {
            MaxConcurrentRunsPerUser = 2,
            SharedDatabasePath = sharedDb,
        };
        await using var replica2 = new AssistantWebApplicationFactory
        {
            MaxConcurrentRunsPerUser = 2,
            SharedDatabasePath = sharedDb,
        };
        var client1 = AuthedClient(replica1);
        var client2 = AuthedClient(replica2);

        var conversationA = await StartRunAsync(client1);

        // Replying on the other replica rehydrates the SAME conversation there, so it is now resident
        // in both processes' caches. It must still count exactly once.
        var reply = await client2.PostAsJsonAsync(
            $"/api/assistant/runs/{conversationA}/messages", new { message = "hello from the other replica" });
        reply.StatusCode.Should().Be(HttpStatusCode.OK);

        var conversationB = await StartRunAsync(client1);

        var blocked = await client2.PostAsJsonAsync("/api/assistant/runs", new { });
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "both replicas must agree that exactly two conversations are active");

        // Park A on replica 1 only. Replica 2 never observes that locally — it still has A cached —
        // but it reads the same durable state, so it must free the slot too.
        var runStore1 = replica1.Services.GetRequiredService<IRunStore>();
        (await runStore1.TryTransitionToIdleAsync(RunId.Parse(conversationA), CancellationToken.None))
            .Should().BeTrue();

        var afterPark = await client2.PostAsJsonAsync("/api/assistant/runs", new { });
        afterPark.StatusCode.Should().Be(HttpStatusCode.Created,
            "the replica that did not park the conversation must still see the freed slot");

        conversationB.Should().NotBe(conversationA);
    }

    [Fact]
    public async Task Turns_HoldTheAgentHostPod_AndReleaseItOnlyAfterThePodIdleTimeout()
    {
        var lifecycle = new RecordingPodLifecycle();
        await using var factory = new AssistantWebApplicationFactory
        {
            UseAgentHost = true,
            PodLifecycle = lifecycle,
            PodIdleTimeout = TimeSpan.FromMinutes(5),
        };
        await SeedByokProviderConfigurationAsync(factory);
        var client = AuthedClient(factory);

        var runId = await StartRunAsync(client, message: "first turn");
        var second = await client.PostAsJsonAsync($"/api/assistant/runs/{runId}/messages", new { message = "second turn" });
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        lifecycle.Releases.Should().BeEmpty(
            "the conversation's pod must be HELD across turns — releasing it per turn is what cost " +
            "15-20s of claim/configure cold start on every message");

        var service = (AssistantRunService)factory.Services.GetRequiredService<IAssistantRunService>();

        service.SweepIdleRuns(DateTimeOffset.UtcNow.AddMinutes(4));
        await Task.Delay(100);
        lifecycle.Releases.Should().BeEmpty("the pod is still within its idle hold window");

        service.SweepIdleRuns(DateTimeOffset.UtcNow.AddMinutes(6));
        await WaitForReleaseAsync(lifecycle, runId);

        service.SweepIdleRuns(DateTimeOffset.UtcNow.AddMinutes(7));
        await Task.Delay(100);
        lifecycle.Releases.Should().ContainSingle("a released pod must not be released again");

        // The conversation itself is untouched — only its pod was given back.
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var run = await runStore.GetAsync(RunId.Parse(runId), CancellationToken.None);
        run!.Status.Should().Be(RunStatus.InProgress);
    }

    [Fact]
    public async Task ParkingAConversationAsDormant_ReleasesItsHeldAgentHostPodExactlyOnce()
    {
        var lifecycle = new RecordingPodLifecycle();
        await using var factory = new AssistantWebApplicationFactory
        {
            UseAgentHost = true,
            PodLifecycle = lifecycle,
            PodIdleTimeout = TimeSpan.FromMinutes(5),
        };
        await SeedByokProviderConfigurationAsync(factory);
        var client = AuthedClient(factory);

        var runId = await StartRunAsync(client, message: "only turn");
        var service = (AssistantRunService)factory.Services.GetRequiredService<IAssistantRunService>();

        // Past the 30-minute conversation idle timeout: the run is parked dormant AND its pod given
        // back, without leaking a second release for the same pod.
        service.SweepIdleRuns(DateTimeOffset.UtcNow.AddMinutes(31));
        await WaitForReleaseAsync(lifecycle, runId);

        var runStore = factory.Services.GetRequiredService<IRunStore>();
        await WaitForStatusAsync(runStore, runId, RunStatus.Idle);
        lifecycle.Releases.Should().ContainSingle();
    }

    private static async Task<string> StartRunAsync(HttpClient client, string? message = null)
    {
        var response = message is null
            ? await client.PostAsJsonAsync("/api/assistant/runs", new { })
            : await client.PostAsJsonAsync("/api/assistant/runs", new { message });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("run_id").GetString()!;
    }

    private static async Task WaitForReleaseAsync(RecordingPodLifecycle lifecycle, string runId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (lifecycle.Releases.Contains(runId))
                return;
            await Task.Delay(25);
        }

        lifecycle.Releases.Should().Contain(runId, "the held AgentHost pod must be given back");
    }

    private static async Task WaitForStatusAsync(IRunStore runStore, string runId, RunStatus expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        Run? run = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            run = await runStore.GetAsync(RunId.Parse(runId), CancellationToken.None);
            if (run?.Status == expected)
                return;
            await Task.Delay(25);
        }

        run!.Status.Should().Be(expected);
    }

    private static HttpClient AuthedClient(AssistantWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AgentweaverWebApplicationFactory.TestApiKey);
        return client;
    }

    /// <summary>Makes the platform resolve to BYOK so the AgentHost capability gate (which is only
    /// armed for GitHub Copilot) stays out of these pod-lifecycle assertions.</summary>
    private static async Task SeedByokProviderConfigurationAsync(AssistantWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ByokProviderConfigurationService>();
        var created = await settings.AddAsync(
            new ByokProviderConfiguration(
                Id: string.Empty,
                Name: "Test Azure provider",
                Type: "azure",
                BaseUrl: "https://byok-resource.openai.azure.com",
                Model: "gpt-4.1",
                ApiKey: "test-byok-key"),
            CancellationToken.None);
        await settings.SetActiveAsync(created.Id, CancellationToken.None);
    }

    private sealed class RecordingPodLifecycle : IAgentHostPodLifecycle
    {
        private readonly List<string> _releases = [];

        public IReadOnlyList<string> Releases
        {
            get { lock (_releases) return _releases.ToList(); }
        }

        public Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult("http://agenthost/a2a/agent");

        public Task<string> LaunchAgentHostPodAsync(
            string runId, string? workingDirectoryOverride, CancellationToken ct = default) =>
            Task.FromResult("http://agenthost/a2a/agent");

        public Task<string> LaunchAgentHostPodAsync(
            string runId, AgentHostLaunchContext context, CancellationToken ct = default) =>
            Task.FromResult("http://agenthost/a2a/agent");

        public Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default)
        {
            lock (_releases) _releases.Add(runId);
            return Task.CompletedTask;
        }
    }
}
