using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Assistant;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
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

    [Fact]
    public async Task PlatformProviderChangedBetweenTurns_ReleasesTheHeldPod_SoTheNextTurnRebuildsAgainstIt()
    {
        // The point of per-turn re-resolution is that a platform provider change takes effect on the
        // NEXT message. Holding the pod across turns silently defeated that: CopilotAIAgent.SetupAsync
        // — which decides BYOK vs Copilot and builds the SDK client — runs only at the pod's one-shot
        // /configure, and the per-turn refresh never rebuilds the client. So repointing the run row
        // changed the bookkeeping while the held pod kept serving every turn from the OLD provider.
        // Giving the pod back is what makes the switch real.
        var lifecycle = new RecordingPodLifecycle();
        await using var factory = new AssistantWebApplicationFactory
        {
            UseAgentHost = true,
            PodLifecycle = lifecycle,
        };
        await SeedByokProviderConfigurationAsync(factory);
        var client = AuthedClient(factory);

        var runId = await StartRunAsync(client, message: "opening turn on BYOK");
        lifecycle.Releases.Should().BeEmpty("the opening turn has nothing to invalidate");

        await SeedPlatformDefaultCopilotBindingAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var byok = scope.ServiceProvider.GetRequiredService<ByokProviderConfigurationService>();
            await byok.SetActiveAsync(null, CancellationToken.None);
        }

        var turn = await client.PostAsJsonAsync($"/api/assistant/runs/{runId}/messages", new { message = "next turn" });
        turn.StatusCode.Should().Be(HttpStatusCode.OK);

        lifecycle.Releases.Should().Contain(runId,
            "the held pod was configured for BYOK and cannot re-resolve its provider in place, so it " +
            "must be given back for the turn that follows a provider change");

        var runStore = factory.Services.GetRequiredService<IRunStore>();
        (await runStore.GetAsync(RunId.Parse(runId), CancellationToken.None))!
            .ModelSource.Should().Be(ModelSource.GitHubCopilot);
    }

    [Fact]
    public async Task ActiveByokProviderSwappedBetweenTurns_StillReleasesTheHeldPod_ThoughModelSourceIsUnchanged()
    {
        // Comparing only the two-value ModelSource enum cannot see this at all: swapping the active
        // BYOK configuration (a different endpoint, key and model) leaves ModelSource == Byok, so the
        // conversation kept being served by the provider that is no longer configured. The comparison
        // is on provider IDENTITY, which does change here.
        var lifecycle = new RecordingPodLifecycle();
        await using var factory = new AssistantWebApplicationFactory
        {
            UseAgentHost = true,
            PodLifecycle = lifecycle,
        };
        await SeedByokProviderConfigurationAsync(factory, name: "Provider A");
        var client = AuthedClient(factory);

        var runId = await StartRunAsync(client, message: "opening turn on provider A");
        lifecycle.Releases.Should().BeEmpty();

        await SeedByokProviderConfigurationAsync(factory, name: "Provider B");

        var turn = await client.PostAsJsonAsync($"/api/assistant/runs/{runId}/messages", new { message = "next turn" });
        turn.StatusCode.Should().Be(HttpStatusCode.OK);

        lifecycle.Releases.Should().Contain(runId,
            "a same-kind provider swap is invisible to ModelSource but still changes which provider " +
            "actually serves the conversation");

        var runStore = factory.Services.GetRequiredService<IRunStore>();
        (await runStore.GetAsync(RunId.Parse(runId), CancellationToken.None))!
            .ModelSource.Should().Be(ModelSource.Byok, "the provider KIND did not change");
    }

    [Fact]
    public async Task ProviderUnchangedBetweenTurns_KeepsTheHeldPod()
    {
        // The control for the two tests above: invalidation must be driven by a real change, or the
        // pod hold — worth 15-20s of cold start per message — would be pointless.
        var lifecycle = new RecordingPodLifecycle();
        await using var factory = new AssistantWebApplicationFactory
        {
            UseAgentHost = true,
            PodLifecycle = lifecycle,
        };
        await SeedByokProviderConfigurationAsync(factory);
        var client = AuthedClient(factory);

        var runId = await StartRunAsync(client, message: "first turn");
        for (var i = 0; i < 3; i++)
        {
            var turn = await client.PostAsJsonAsync(
                $"/api/assistant/runs/{runId}/messages", new { message = $"turn {i}" });
            turn.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        lifecycle.Releases.Should().BeEmpty("nothing about the effective provider changed");
    }

    [Fact]
    public async Task HeldPodHolderToken_IsStableAcrossTurns_AndDistinctPerReplica()
    {
        // The fencing token that makes a release a compare-and-swap: one owner per conversation per
        // replica, stable for as long as that owner holds the pod, and necessarily different on the
        // replica that cold-starts its own pod for the same conversation.
        var sharedDb = Path.Combine(Path.GetTempPath(), $"agentweaver-assistant-shared-{Guid.NewGuid():N}.db");
        await using var replica1 = new AssistantWebApplicationFactory { SharedDatabasePath = sharedDb };
        await using var replica2 = new AssistantWebApplicationFactory { SharedDatabasePath = sharedDb };
        // Both hosts are started before any run exists: the startup recovery sweep fails whatever it
        // finds InProgress, which is unrelated to what this test is about.
        var client1 = AuthedClient(replica1);
        var client2 = AuthedClient(replica2);

        var runId = await StartRunAsync(client1, message: "first turn");
        (await client1.PostAsJsonAsync($"/api/assistant/runs/{runId}/messages", new { message = "second turn" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var tokensOnReplica1 = replica1.Agent.Requests.Select(r => r.PodHolderToken).ToList();
        tokensOnReplica1.Should().HaveCount(2);
        tokensOnReplica1.Should().AllSatisfy(t => t.Should().NotBeNullOrWhiteSpace());
        tokensOnReplica1.Distinct().Should().ContainSingle(
            "the same owner holds the pod for the whole conversation on this replica");

        (await client2.PostAsJsonAsync($"/api/assistant/runs/{runId}/messages", new { message = "third turn, elsewhere" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        replica2.Agent.Requests.Should().ContainSingle();
        replica2.Agent.Requests.Single().PodHolderToken.Should().NotBe(tokensOnReplica1[0],
            "the other replica cold-starts its OWN pod, so a stale release from replica 1 must no " +
            "longer match the claim and must therefore be refused");
    }

    [Fact]
    public async Task CancelledTurn_ClearsTheLocalPodHold_SoNoRedundantReleaseIsIssued()
    {
        // RemoteOperatorAssistantAgent gives the real pod back on its cancellation path too, but it
        // rethrows OperationCanceledException as-is rather than wrapping it, so the service's
        // provider-failure handler never saw it and the local hold flag stayed stuck at held.
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

        factory.Agent.ThrowOnNextTurn = new OperationCanceledException();
        try
        {
            await client.PostAsJsonAsync($"/api/assistant/runs/{runId}/messages", new { message = "cancelled turn" });
        }
        catch (Exception)
        {
            // The transport outcome of a cancelled turn is not what this test is about.
        }

        var service = (AssistantRunService)factory.Services.GetRequiredService<IAssistantRunService>();
        service.SweepIdleRuns(DateTimeOffset.UtcNow.AddMinutes(6));
        await Task.Delay(200);

        lifecycle.Releases.Should().BeEmpty(
            "the cancelled turn already gave the pod back, so the sweep must not issue a second " +
            "release for a pod that no longer exists");
    }

    [Fact]
    public async Task StartRun_DurablyStaleInProgressRun_DoesNotStrandAConcurrencySlotForever()
    {
        // A durable InProgress row is only ever parked by the owning API pod's in-memory sweep. If
        // that pod restarts first the row stays InProgress forever — the AgentHost reaper reclaims
        // the pod but never touches run status — so every restart permanently burned one of the
        // user's slots until they were all gone.
        var sharedDb = Path.Combine(Path.GetTempPath(), $"agentweaver-assistant-shared-{Guid.NewGuid():N}.db");
        await using var owner = new AssistantWebApplicationFactory
        {
            MaxConcurrentRunsPerUser = 1,
            SharedDatabasePath = sharedDb,
        };
        // A replica that never had the conversation resident is exactly what the run's owner looks
        // like once it is gone: the durable row is still InProgress, but no in-memory sweep anywhere
        // will ever park it. Both of these are started BEFORE the run exists, because the startup
        // recovery sweep fails whatever it finds InProgress and would mask the behaviour under test.
        await using var otherReplica = new AssistantWebApplicationFactory
        {
            MaxConcurrentRunsPerUser = 1,
            SharedDatabasePath = sharedDb,
            StaleActiveRunThreshold = TimeSpan.FromMinutes(90),
        };
        await using var afterLongOutage = new AssistantWebApplicationFactory
        {
            MaxConcurrentRunsPerUser = 1,
            SharedDatabasePath = sharedDb,
            StaleActiveRunThreshold = TimeSpan.FromMilliseconds(1),
        };
        var ownerClient = AuthedClient(owner);
        var otherClient = AuthedClient(otherReplica);
        var outageClient = AuthedClient(afterLongOutage);

        var strandedRunId = await StartRunAsync(ownerClient, message: "stranded by a restart");

        var blocked = await otherClient.PostAsJsonAsync("/api/assistant/runs", new { });
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "a recently-active conversation must keep its slot — staleness, not mere non-residency, " +
            "is what frees one");

        await Task.Delay(20);

        var allowed = await outageClient.PostAsJsonAsync("/api/assistant/runs", new { });
        allowed.StatusCode.Should().Be(HttpStatusCode.Created,
            "a run that has been durably silent past the staleness threshold is stranded, not active");

        var runStore = afterLongOutage.Services.GetRequiredService<IRunStore>();
        (await runStore.GetAsync(RunId.Parse(strandedRunId), CancellationToken.None))!
            .Status.Should().Be(RunStatus.Idle,
                "the repair is CAS-parked durably so every replica sees the slot freed, rather than " +
                "being re-derived on every start");
    }

    private static async Task<string> StartRunAsync(HttpClient client, string? message = null)    {
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
    /// armed for GitHub Copilot) stays out of these pod-lifecycle assertions. Each call adds a
    /// DISTINCT configuration and makes it the active one, so callers can also swap providers.</summary>
    private static async Task SeedByokProviderConfigurationAsync(
        AssistantWebApplicationFactory factory, string name = "Test Azure provider")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ByokProviderConfigurationService>();
        var created = await settings.AddAsync(
            new ByokProviderConfiguration(
                Id: string.Empty,
                Name: name,
                Type: "azure",
                BaseUrl: "https://byok-resource.openai.azure.com",
                Model: "gpt-4.1",
                ApiKey: "test-byok-key"),
            CancellationToken.None);
        await settings.SetActiveAsync(created.Id, CancellationToken.None);
    }

    private static async Task SeedPlatformDefaultCopilotBindingAsync(AssistantWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        db.PlatformDefaultCopilotBindings.Add(new PlatformDefaultCopilotBindingRecord
        {
            Id = PlatformDefaultCopilotBindingRecord.SingletonId,
            EntraObjectId = "platform-admin",
            CredentialReference = "copilot-app-platform-default-version",
            CredentialVersion = "platform-version",
            GrantDigest = "platform-digest",
            Status = GitHubBindingStatus.Active,
            BoundAt = DateTimeOffset.UtcNow,
        });
        await secrets.SetSecretAsync(
            "copilot-app-platform-default-version",
            """{"status":"signed-in","accessToken":"platform-token","expiresAt":"2099-01-01T00:00:00Z","githubLogin":"platform-bot"}""");
        await db.SaveChangesAsync();
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
