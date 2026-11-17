using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Environment;

namespace Agentweaver.Environment.Tests;

public sealed class CiliumPlatformEgressReservationTests
{
    [Fact]
    public async Task DelayedOlderRequestCannotReplaceANewerPolicy()
    {
        var api = new FakeKubernetesApi { DelayOlderPut = true };
        using var httpClient = new HttpClient(api) { BaseAddress = new Uri("https://kubernetes.test/") };
        var store = new KubernetesCiliumPolicyResourceStore(httpClient);
        var oldPolicy = Policy(1, "source-1");
        var newPolicy = Policy(2, "source-2");
        var oldReservation = Reservation(store, oldPolicy, sourceEpoch: 1, "apply-1", "object-uid-1", "1");
        var newReservation = Reservation(store, newPolicy, sourceEpoch: 2, "apply-2", "object-uid-1", "1");
        var reservations = new FakeReservationReader();
        reservations.Add(oldReservation);
        var provider = PlatformEgressFixtures.Cilium(store, exactHost: true, reservationReader: reservations);

        var delayedApply = provider.ApplyReservedAsync(oldReservation, CancellationToken.None);
        await api.OlderPutEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        reservations.Add(newReservation);
        PlatformEgressStatus newStatus;
        try
        {
            newStatus = await provider.ApplyReservedAsync(newReservation, CancellationToken.None);
        }
        finally
        {
            api.ReleaseOlderPut.TrySetResult();
        }
        var oldStatus = await delayedApply;
        var staleRetry = await provider.ApplyReservedAsync(oldReservation, CancellationToken.None);

        Assert.True(newStatus.Matches(newPolicy.IntentHash, newPolicy.SourceHash));
        Assert.Equal(PlatformEgressApplyState.Failed, oldStatus.State);
        Assert.Equal("kubernetes_conflict", oldStatus.Message);
        Assert.Equal("stale_apply_reservation", staleRetry.Message);
        Assert.Equal("object-uid-1", api.Current.Metadata.Uid);
        Assert.Equal("2", api.Current.Metadata.Annotations[CiliumPlatformEgressProvider.SourceEpochAnnotation]);
        Assert.Equal("2", api.Current.Metadata.Annotations[CiliumPlatformEgressProvider.GenerationAnnotation]);
        Assert.Equal(1, api.AcceptedWrites);
        Assert.Equal(2, api.PutBodies.Count);
        Assert.All(api.PutBodies, body =>
        {
            Assert.Equal("object-uid-1", body.Metadata.Uid);
            Assert.Equal("1", body.Metadata.ResourceVersion);
        });
    }

    [Fact]
    public async Task RetryRejectsAnOlderReservationAfterANewerOneIsSaved()
    {
        var api = new FakeKubernetesApi();
        using var httpClient = new HttpClient(api) { BaseAddress = new Uri("https://kubernetes.test/") };
        var store = new KubernetesCiliumPolicyResourceStore(httpClient);
        var oldReservation = Reservation(store, Policy(1, "source-1"), 1, "apply-1", "object-uid-1", "1");
        var newReservation = Reservation(store, Policy(2, "source-2"), 2, "apply-2", "object-uid-1", "1");
        var reservations = new FakeReservationReader();
        reservations.Add(oldReservation);
        reservations.Add(newReservation);
        var provider = PlatformEgressFixtures.Cilium(store, exactHost: true, reservationReader: reservations);

        var stale = await provider.ApplyReservedAsync(oldReservation, CancellationToken.None);

        Assert.Equal(PlatformEgressApplyState.Failed, stale.State);
        Assert.Equal("stale_apply_reservation", stale.Message);
        Assert.Empty(api.PutBodies);
        Assert.Equal("1", api.Current.Metadata.ResourceVersion);
    }

    [Fact]
    public async Task NewAttemptCanRefreshTheObjectVersionWithoutChangingThePolicyGeneration()
    {
        var api = new FakeKubernetesApi();
        using var httpClient = new HttpClient(api) { BaseAddress = new Uri("https://kubernetes.test/") };
        var store = new KubernetesCiliumPolicyResourceStore(httpClient);
        var policy = Policy(1, "source-1");
        var oldAttempt = Reservation(store, policy, 1, "apply-1", "object-uid-1", "1");
        var currentAttempt = Reservation(store, policy, 1, "apply-2", "object-uid-1", "2");
        var reservations = new FakeReservationReader();
        reservations.Add(oldAttempt);
        reservations.Add(currentAttempt);
        api.Current = api.Current with
        {
            Metadata = api.Current.Metadata with { ResourceVersion = "2" },
        };
        var provider = PlatformEgressFixtures.Cilium(store, exactHost: true, reservationReader: reservations);

        var stale = await provider.ApplyReservedAsync(oldAttempt, CancellationToken.None);
        var applied = await provider.ApplyReservedAsync(currentAttempt, CancellationToken.None);

        Assert.Equal("stale_apply_reservation", stale.Message);
        Assert.True(applied.Matches(policy.IntentHash, policy.SourceHash));
        Assert.Equal("1", api.Current.Metadata.Annotations[CiliumPlatformEgressProvider.GenerationAnnotation]);
        Assert.Equal(1, api.AcceptedWrites);
    }

    [Fact]
    public async Task ExactReservationReplayRecoversWhenTheApiCommitsButTheResponseIsLost()
    {
        var api = new FakeKubernetesApi { DropNextPutResponse = true };
        using var httpClient = new HttpClient(api) { BaseAddress = new Uri("https://kubernetes.test/") };
        var store = new KubernetesCiliumPolicyResourceStore(httpClient);
        var policy = Policy(1, "source-1");
        var reservation = Reservation(store, policy, sourceEpoch: 1, "apply-1", "object-uid-1", "1");
        var reservations = new FakeReservationReader();
        reservations.Add(reservation);
        var provider = PlatformEgressFixtures.Cilium(store, exactHost: true, reservationReader: reservations);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.ApplyReservedAsync(reservation, CancellationToken.None));
        var replay = await provider.ApplyReservedAsync(reservation, CancellationToken.None);

        Assert.True(replay.Matches(policy.IntentHash, policy.SourceHash));
        Assert.Equal(1, api.AcceptedWrites);
        Assert.Single(api.PutBodies);
        Assert.Equal("2", api.Current.Metadata.ResourceVersion);
    }

    [Fact]
    public async Task ApplyRejectsPolicyContentThatDiffersFromTheSavedReservation()
    {
        var api = new FakeKubernetesApi();
        using var httpClient = new HttpClient(api) { BaseAddress = new Uri("https://kubernetes.test/") };
        var store = new KubernetesCiliumPolicyResourceStore(httpClient);
        var saved = Reservation(store, Policy(1, "source-1"), 1, "apply-1", "object-uid-1", "1");
        var reservations = new FakeReservationReader();
        reservations.Add(saved);
        var provider = PlatformEgressFixtures.Cilium(store, exactHost: true, reservationReader: reservations);
        var tampered = saved with { Policy = Policy(1, "different-source") };

        var status = await provider.ApplyReservedAsync(tampered, CancellationToken.None);

        Assert.Equal(PlatformEgressApplyState.Failed, status.State);
        Assert.Equal("invalid_apply_reservation", status.Message);
        Assert.Empty(api.PutBodies);
    }

    [Fact]
    public async Task ReservationRejectsARecreatedTargetAndConflictingContentAtTheSameVersion()
    {
        var api = new FakeKubernetesApi();
        using var httpClient = new HttpClient(api) { BaseAddress = new Uri("https://kubernetes.test/") };
        var store = new KubernetesCiliumPolicyResourceStore(httpClient);
        var policy = Policy(1, "source-1");
        var reservation = Reservation(store, policy, sourceEpoch: 1, "apply-1", "object-uid-1", "1");
        var reservations = new FakeReservationReader();
        reservations.Add(reservation);
        var provider = PlatformEgressFixtures.Cilium(store, exactHost: true, reservationReader: reservations);

        api.Current = api.Current with
        {
            Metadata = api.Current.Metadata with { Uid = "replacement-uid", ResourceVersion = "2" },
        };
        var replacedTarget = await provider.ApplyReservedAsync(reservation, CancellationToken.None);

        Assert.Equal("stale_object_uid", replacedTarget.Message);
        Assert.Empty(api.PutBodies);

        api.Current = api.Seed;
        var firstApply = await provider.ApplyReservedAsync(reservation, CancellationToken.None);
        Assert.True(firstApply.Matches(policy.IntentHash, policy.SourceHash));
        var conflictingReservation = Reservation(
            store,
            Policy(1, "different-source"),
            sourceEpoch: 1,
            "apply-different-content",
            "object-uid-1",
            api.Current.Metadata.ResourceVersion!);
        reservations.Add(conflictingReservation);
        var conflict = await provider.ApplyReservedAsync(conflictingReservation, CancellationToken.None);

        Assert.Equal(PlatformEgressApplyState.Failed, conflict.State);
        Assert.Equal("generation_conflict", conflict.Message);
        Assert.Equal(1, api.AcceptedWrites);
    }

    [Fact]
    public async Task ReservationRejectsMalformedPolicyDestinationsBeforeWriting()
    {
        var api = new FakeKubernetesApi();
        using var httpClient = new HttpClient(api) { BaseAddress = new Uri("https://kubernetes.test/") };
        var store = new KubernetesCiliumPolicyResourceStore(httpClient);
        var provider = PlatformEgressFixtures.Cilium(store, exactHost: true);
        var policy = Policy(1, "source-1");
        var invalidPolicies = new[]
        {
            policy with { Destinations = policy.Destinations.SetItem(0, null!) },
            PlatformEgressPolicy.Create(
                1,
                PlatformEgressMode.Allowlist,
                [new PlatformEgressDestination("a.example.com", 443), new PlatformEgressDestination("b.example.com", 443)],
                PlatformEgressFixtures.Hash("unordered")) with
            {
                Destinations =
                [
                    new PlatformEgressDestination("b.example.com", 443),
                    new PlatformEgressDestination("a.example.com", 443),
                ],
            },
        };

        foreach (var invalidPolicy in invalidPolicies)
        {
            var invalid = Reservation(store, invalidPolicy, sourceEpoch: 1, "apply-1", "object-uid-1", "1");
            var status = await provider.ApplyReservedAsync(invalid, CancellationToken.None);
            Assert.Equal(PlatformEgressApplyState.Failed, status.State);
            Assert.Equal("invalid_apply_reservation", status.Message);
        }
        Assert.Empty(api.PutBodies);
    }

    private static PlatformEgressApplyReservation Reservation(
        KubernetesCiliumPolicyResourceStore store,
        PlatformEgressPolicy policy,
        long sourceEpoch,
        string idempotencyKey,
        string objectUid,
        string resourceVersion) =>
        new(
            Guid.NewGuid(),
            store.GetTargetId("agentweaver", CiliumPlatformEgressProvider.PolicyName),
            idempotencyKey,
            sourceEpoch,
            policy.Generation,
            1,
            "projects-config:platform:default:test",
            objectUid,
            resourceVersion,
            policy,
            PlatformEgressFixtures.Hash(idempotencyKey),
            DateTimeOffset.UtcNow,
            Replayed: false);

    private static PlatformEgressPolicy Policy(long generation, string source) =>
        PlatformEgressPolicy.Create(
            generation,
            PlatformEgressMode.Allowlist,
            [new PlatformEgressDestination("api.example.com", 443)],
            PlatformEgressFixtures.Hash(source));

    private sealed class FakeReservationReader : IPlatformEgressApplyReservationReader
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string TargetId, string IdempotencyKey), PlatformEgressApplyReservation> _rows = [];

        public void Add(PlatformEgressApplyReservation reservation)
        {
            lock (_gate)
            {
                var latest = _rows.Values
                    .Where(row => row.TargetId == reservation.TargetId)
                    .OrderBy(row => row.SourceEpoch)
                    .ThenBy(row => row.Generation)
                    .ThenBy(row => row.CreatedAt)
                    .LastOrDefault();
                if (latest is not null && reservation.CreatedAt <= latest.CreatedAt)
                    reservation = reservation with { CreatedAt = latest.CreatedAt.AddTicks(10) };
                _rows.Add((reservation.TargetId, reservation.IdempotencyKey), reservation);
            }
        }

        public Task<PlatformEgressApplyReservation?> FindAsync(
            string targetId,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            lock (_gate)
                return Task.FromResult(_rows.GetValueOrDefault((targetId, idempotencyKey)) is { } row
                    ? row with { Replayed = true }
                    : null);
        }

        public Task<PlatformEgressApplyReservation?> FindLatestAsync(
            string targetId,
            CancellationToken cancellationToken)
        {
            lock (_gate)
                return Task.FromResult(_rows.Values
                    .Where(row => row.TargetId == targetId)
                    .OrderBy(row => row.SourceEpoch)
                    .ThenBy(row => row.Generation)
                    .ThenBy(row => row.CreatedAt)
                    .LastOrDefault() is { } row
                    ? row with { Replayed = true }
                    : null);
        }
    }

    private sealed class FakeKubernetesApi : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly object _gate = new();
        private CiliumNetworkPolicyDocument _current = SeedPolicy();
        private int _resourceVersion = 1;

        public TaskCompletionSource OlderPutEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOlderPut { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<CiliumNetworkPolicyDocument> PutBodies { get; } = new();
        public bool DelayOlderPut { get; set; }
        public bool DropNextPutResponse { get; set; }
        public int AcceptedWrites { get; private set; }
        public CiliumNetworkPolicyDocument Seed { get; } = SeedPolicy();

        public CiliumNetworkPolicyDocument Current
        {
            get
            {
                lock (_gate)
                    return _current;
            }
            set
            {
                lock (_gate)
                {
                    _current = value;
                    if (int.TryParse(value.Metadata.ResourceVersion, out var version))
                        _resourceVersion = version;
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, Current);
            if (request.Method != HttpMethod.Put)
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);

            var body = await request.Content!.ReadFromJsonAsync<CiliumNetworkPolicyDocument>(
                JsonOptions, cancellationToken);
            Assert.NotNull(body);
            PutBodies.Enqueue(body);
            if (DelayOlderPut &&
                body.Metadata.Annotations.GetValueOrDefault(CiliumPlatformEgressProvider.SourceEpochAnnotation) == "1")
            {
                OlderPutEntered.TrySetResult();
                await ReleaseOlderPut.Task.WaitAsync(cancellationToken);
            }

            lock (_gate)
            {
                if (body.Metadata.Uid != _current.Metadata.Uid ||
                    body.Metadata.ResourceVersion != _current.Metadata.ResourceVersion)
                    return new HttpResponseMessage(HttpStatusCode.Conflict);

                _current = body with
                {
                    Metadata = body.Metadata with
                    {
                        ResourceVersion = (++_resourceVersion).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                };
                AcceptedWrites++;
                if (DropNextPutResponse)
                {
                    DropNextPutResponse = false;
                    throw new HttpRequestException("The Kubernetes response was lost after the write.");
                }
                return Json(HttpStatusCode.OK, _current);
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, CiliumNetworkPolicyDocument body) =>
            new(status) { Content = JsonContent.Create(body, options: JsonOptions) };

        private static CiliumNetworkPolicyDocument SeedPolicy() =>
            new(
                "cilium.io/v2",
                "CiliumNetworkPolicy",
                new CiliumPolicyMetadata(
                    CiliumPlatformEgressProvider.PolicyName,
                    "agentweaver",
                    ImmutableDictionary<string, string>.Empty.Add("agentweaver.io/service", "agenthost"),
                    ImmutableDictionary<string, string>.Empty,
                    "1",
                    Uid: "object-uid-1"),
                new CiliumPolicySpec(
                    new CiliumEndpointSelector(
                        ImmutableDictionary<string, string>.Empty.Add("agentweaver.io/workload", "agenthost")),
                    [
                        new CiliumEgressRuleDocument(
                            ToEndpoints:
                            [
                                new CiliumEndpointSelector(ImmutableDictionary<string, string>.Empty
                                    .Add("k8s:io.kubernetes.pod.namespace", "kube-system")
                                    .Add("k8s:k8s-app", "kube-dns")),
                            ],
                            ToPorts: [new CiliumPortRules([new CiliumPort("53", "UDP"), new CiliumPort("53", "TCP")])]),
                    ]));
    }
}
