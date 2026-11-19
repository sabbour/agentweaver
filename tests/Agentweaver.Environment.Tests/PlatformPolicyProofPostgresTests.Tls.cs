using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Environment.Tests;

public sealed partial class PlatformPolicyProofPostgresTests
{
    [Fact]
    public async Task FactoryUsesDistinctCredentialsAgainstTheSameTlsApi()
    {
        using var certificates = new PlatformPolicySourceTlsServerFixture();
        await using var api = await ProofTlsApi.StartAsync(certificates);
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".proof-tokens", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var readerToken = Path.Combine(directory, "reader");
        var writerToken = Path.Combine(directory, "writer");
        await File.WriteAllTextAsync(readerToken, "reader-token");
        await File.WriteAllTextAsync(writerToken, "writer-token");
        try
        {
            var ca = await File.ReadAllBytesAsync(certificates.BackendCaPath);
            using var factory = PlatformPolicyProofFactory.Create(
                api.Address.ToString(), ca, readerToken, writerToken, fixture.DataSource,
                new CiliumEgressProviderOptions("agentweaver", new Version(1, 0), 1, "test",
                    ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty),
                new CiliumPlatformEgressOptions("agentweaver", true, []), TimeProvider.System);
            using var identityClient = new HttpClient { BaseAddress = api.Address };
            var targetId = new KubernetesCiliumPolicyResourceStore(identityClient)
                .GetTargetId("agentweaver", CiliumPlatformEgressProvider.PolicyName);
            var request = Request(targetId, Guid.NewGuid().ToString("N"), 1) with
            {
                ObjectUid = api.Policy.Current!.Metadata.Uid!,
                ResourceVersion = api.Policy.Current.Metadata.ResourceVersion!,
            };
            var reservation = await new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System)
                .ReserveAsync(request, CancellationToken.None);
            var frozen = new PlatformPolicyProofFrozenTarget(
                Guid.NewGuid(), targetId, "cluster-" + Guid.NewGuid().ToString("N"), "agentweaver", "namespace-uid-1",
                1, Hash("guard"), Hash("profile"));
            var preparation = new PlatformPolicyProofPreparation(
                "preparation", 1, reservation.SourceId, reservation.SourceRevision, reservation.Policy.SourceHash,
                PlatformEgressAcsCompiler.CompilerVersion, reservation.Policy.Destinations);
            var receipt = await factory.Coordinator.ProveAsync(
                preparation, frozen, reservation, CancellationToken.None);
            Assert.NotNull(await factory.Coordinator.ReadCurrentReceiptAsync(targetId, frozen.HoldId, CancellationToken.None));
            Assert.Equal(reservation.ReservationId, receipt.ReservationId);
            Assert.Contains(api.Requests, call => call.Method == "PUT" && call.Token == "Bearer writer-token");
            const string namedPath = "/apis/cilium.io/v2/namespaces/agentweaver/ciliumnetworkpolicies/agenthost-platform-egress";
            Assert.Contains(api.Requests, call => call.Method == "GET" && call.Path == namedPath && call.Token == "Bearer reader-token");
            Assert.Contains(api.Requests, call => call.Method == "GET" && call.Path == namedPath && call.Token == "Bearer writer-token");
            Assert.All(api.Requests.Where(call => !call.Path.EndsWith("/agenthost-platform-egress")),
                call => Assert.Equal("Bearer reader-token", call.Token));
            Assert.Equal(2, api.Requests.Count(call => call.Path == "/api/v1/namespaces/agentweaver"));
            Assert.Equal(2, api.Requests.Count(call => call.Path.EndsWith("/networkpolicies")));
            Assert.Equal(2, api.Requests.Count(call => call.Path.EndsWith("/ciliumnetworkpolicies")));
            Assert.Equal(2, api.Requests.Count(call => call.Path.EndsWith("/ciliumclusterwidenetworkpolicies")));
            Assert.Equal(2, api.Requests.Count(call =>
                call.Path.EndsWith("/agenthost-platform-egress") && call.Token == "Bearer reader-token"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FactoryRejectsWrongHostnameAndUntrustedChain(bool wrongHostname)
    {
        using var certificates = new PlatformPolicySourceTlsServerFixture();
        await using var api = await ProofTlsApi.StartAsync(certificates);
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".proof-tokens", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var readerToken = Path.Combine(directory, "reader");
        var writerToken = Path.Combine(directory, "writer");
        await File.WriteAllTextAsync(readerToken, "reader-token");
        await File.WriteAllTextAsync(writerToken, "writer-token");
        try
        {
            var origin = wrongHostname
                ? new UriBuilder(api.Address) { Host = "127.0.0.1" }.Uri
                : api.Address;
            var ca = await File.ReadAllBytesAsync(wrongHostname ? certificates.BackendCaPath : certificates.OtherCaPath);
            using var factory = PlatformPolicyProofFactory.Create(
                origin.ToString(), ca, readerToken, writerToken, fixture.DataSource,
                new CiliumEgressProviderOptions("agentweaver", new Version(1, 0), 1, "test",
                    ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty),
                new CiliumPlatformEgressOptions("agentweaver", true, []), TimeProvider.System);
            using var identityClient = new HttpClient { BaseAddress = origin };
            var targetId = new KubernetesCiliumPolicyResourceStore(identityClient)
                .GetTargetId("agentweaver", CiliumPlatformEgressProvider.PolicyName);
            var request = Request(targetId, Guid.NewGuid().ToString("N"), 1) with
            {
                ObjectUid = api.Policy.Current!.Metadata.Uid!,
                ResourceVersion = api.Policy.Current.Metadata.ResourceVersion!,
            };
            var reservation = await new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System)
                .ReserveAsync(request, CancellationToken.None);
            var frozen = new PlatformPolicyProofFrozenTarget(
                Guid.NewGuid(), targetId, "cluster-" + Guid.NewGuid().ToString("N"), "agentweaver", "namespace-uid-1",
                1, Hash("guard"), Hash("profile"));
            var preparation = new PlatformPolicyProofPreparation(
                "preparation", 1, reservation.SourceId, reservation.SourceRevision, reservation.Policy.SourceHash,
                PlatformEgressAcsCompiler.CompilerVersion, reservation.Policy.Destinations);
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                factory.Coordinator.ProveAsync(preparation, frozen, reservation, CancellationToken.None));
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                factory.ReadWriterAsync("agentweaver", CiliumPlatformEgressProvider.PolicyName, CancellationToken.None));
            Assert.Empty(api.Requests);
            Assert.Null(await factory.Coordinator.ReadCurrentReceiptAsync(targetId, frozen.HoldId, CancellationToken.None));
            await using var query = fixture.DataSource.CreateCommand(
                "SELECT count(*) FROM environment.platform_policy_proof_holds WHERE hold_id=@hold");
            query.Parameters.AddWithValue("hold", frozen.HoldId);
            Assert.Equal(1L, await query.ExecuteScalarAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FactoryRejectsReaderAndWriterRedirectsWithoutForwardingCredentials()
    {
        using var certificates = new PlatformPolicySourceTlsServerFixture();
        await using var api = await ProofTlsApi.StartAsync(certificates);
        await using var destination = await ProofTlsApi.StartAsync(certificates);
        api.RedirectTo = destination.Address;
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".proof-tokens", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var readerToken = Path.Combine(directory, "reader");
        var writerToken = Path.Combine(directory, "writer");
        await File.WriteAllTextAsync(readerToken, "reader-token");
        await File.WriteAllTextAsync(writerToken, "writer-token");
        try
        {
            using var factory = PlatformPolicyProofFactory.Create(
                api.Address.ToString(), await File.ReadAllBytesAsync(certificates.BackendCaPath),
                readerToken, writerToken, fixture.DataSource,
                new CiliumEgressProviderOptions("agentweaver", new Version(1, 0), 1, "test",
                    ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty),
                new CiliumPlatformEgressOptions("agentweaver", true, []), TimeProvider.System);
            using var identityClient = new HttpClient { BaseAddress = api.Address };
            var targetId = new KubernetesCiliumPolicyResourceStore(identityClient)
                .GetTargetId("agentweaver", CiliumPlatformEgressProvider.PolicyName);
            var request = Request(targetId, Guid.NewGuid().ToString("N"), 1) with
            {
                ObjectUid = api.Policy.Current!.Metadata.Uid!,
                ResourceVersion = api.Policy.Current.Metadata.ResourceVersion!,
            };
            var reservation = await new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System)
                .ReserveAsync(request, CancellationToken.None);
            var frozen = new PlatformPolicyProofFrozenTarget(
                Guid.NewGuid(), targetId, "cluster-" + Guid.NewGuid().ToString("N"), "agentweaver", "namespace-uid-1",
                1, Hash("guard"), Hash("profile"));
            var preparation = new PlatformPolicyProofPreparation(
                "preparation", 1, reservation.SourceId, reservation.SourceRevision, reservation.Policy.SourceHash,
                PlatformEgressAcsCompiler.CompilerVersion, reservation.Policy.Destinations);
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                factory.Coordinator.ProveAsync(preparation, frozen, reservation, CancellationToken.None));
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                factory.ReadWriterAsync("agentweaver", CiliumPlatformEgressProvider.PolicyName, CancellationToken.None));
            Assert.Contains(api.Requests, call => call.Token == "Bearer reader-token");
            Assert.Contains(api.Requests, call => call.Token == "Bearer writer-token");
            Assert.Empty(destination.Requests);
            Assert.Null(await factory.Coordinator.ReadCurrentReceiptAsync(targetId, frozen.HoldId, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedInventoryRetainsHoldAndDoesNotRepeatPut(bool growAfterPut)
    {
        using var certificates = new PlatformPolicySourceTlsServerFixture();
        await using var api = await ProofTlsApi.StartAsync(certificates);
        if (growAfterPut)
            api.GrowAfterPut = true;
        else
            api.AddPadding();
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".proof-tokens", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var readerToken = Path.Combine(directory, "reader");
        var writerToken = Path.Combine(directory, "writer");
        await File.WriteAllTextAsync(readerToken, "reader-token");
        await File.WriteAllTextAsync(writerToken, "writer-token");
        try
        {
            using var factory = PlatformPolicyProofFactory.Create(
                api.Address.ToString(), await File.ReadAllBytesAsync(certificates.BackendCaPath),
                readerToken, writerToken, fixture.DataSource,
                new CiliumEgressProviderOptions("agentweaver", new Version(1, 0), 1, "test",
                    ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty),
                new CiliumPlatformEgressOptions("agentweaver", true, []), TimeProvider.System);
            using var identityClient = new HttpClient { BaseAddress = api.Address };
            var targetId = new KubernetesCiliumPolicyResourceStore(identityClient)
                .GetTargetId("agentweaver", CiliumPlatformEgressProvider.PolicyName);
            var request = Request(targetId, Guid.NewGuid().ToString("N"), 1) with
            {
                ObjectUid = api.Policy.Current!.Metadata.Uid!,
                ResourceVersion = api.Policy.Current.Metadata.ResourceVersion!,
            };
            var reservation = await new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System)
                .ReserveAsync(request, CancellationToken.None);
            var frozen = new PlatformPolicyProofFrozenTarget(
                Guid.NewGuid(), targetId, "cluster-" + Guid.NewGuid().ToString("N"), "agentweaver", "namespace-uid-1",
                1, Hash("guard"), Hash("profile"));
            var preparation = new PlatformPolicyProofPreparation(
                "preparation", 1, reservation.SourceId, reservation.SourceRevision, reservation.Policy.SourceHash,
                PlatformEgressAcsCompiler.CompilerVersion, reservation.Policy.Destinations);
            for (var i = 0; i < 2; i++)
            {
                var error = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
                    factory.Coordinator.ProveAsync(preparation, frozen, reservation, CancellationToken.None));
                Assert.Equal("policy_semantics_unsupported", error.Code);
                Assert.Equal(growAfterPut ? 1 : 0, api.Requests.Count(call => call.Method == "PUT"));
                Assert.Null(await factory.Coordinator.ReadCurrentReceiptAsync(targetId, frozen.HoldId, CancellationToken.None));
            }
            await using var query = fixture.DataSource.CreateCommand(
                "SELECT count(*) FROM environment.platform_policy_proof_holds WHERE hold_id=@hold");
            query.Parameters.AddWithValue("hold", frozen.HoldId);
            Assert.Equal(1L, await query.ExecuteScalarAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OversizedRenderedCandidateStopsBeforeReservedPut()
    {
        using var certificates = new PlatformPolicySourceTlsServerFixture();
        await using var api = await ProofTlsApi.StartAsync(certificates);
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".proof-tokens", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var readerToken = Path.Combine(directory, "reader");
        var writerToken = Path.Combine(directory, "writer");
        await File.WriteAllTextAsync(readerToken, "reader-token");
        await File.WriteAllTextAsync(writerToken, "writer-token");
        try
        {
            var ca = await File.ReadAllBytesAsync(certificates.BackendCaPath);
            using var factory = PlatformPolicyProofFactory.Create(
                api.Address.ToString(), ca, readerToken, writerToken, fixture.DataSource,
                new CiliumEgressProviderOptions("agentweaver", new Version(1, 0), 1, "test",
                    ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty),
                new CiliumPlatformEgressOptions("agentweaver", true, []), TimeProvider.System);
            using var readerClient = new HttpClient(KubernetesServiceAccountHandler.Create(readerToken, ca))
            {
                BaseAddress = api.Address,
            };
            var reader = new KubernetesPlatformPolicyInventoryReader(readerClient);
            var targetId = new KubernetesCiliumPolicyResourceStore(readerClient)
                .GetTargetId("agentweaver", CiliumPlatformEgressProvider.PolicyName);
            var request = Request(targetId, Guid.NewGuid().ToString("N"), 1) with
            {
                ObjectUid = api.Policy.Current!.Metadata.Uid!,
                ResourceVersion = api.Policy.Current.Metadata.ResourceVersion!,
            };
            var reservation = await new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System)
                .ReserveAsync(request, CancellationToken.None);
            var frozen = new PlatformPolicyProofFrozenTarget(
                Guid.NewGuid(), targetId, "cluster-" + Guid.NewGuid().ToString("N"), "agentweaver", "namespace-uid-1",
                1, Hash("guard"), Hash("profile"));
            var preparation = new PlatformPolicyProofPreparation(
                "preparation", 1, reservation.SourceId, reservation.SourceRevision, reservation.Policy.SourceHash,
                PlatformEgressAcsCompiler.CompilerVersion, reservation.Policy.Destinations);
            var ceiling = SystemOnlyPolicyCeiling.Create(preparation.SystemDestinations);
            api.AddPadding(1);
            var initial = await reader.CaptureAsync(frozen.PhysicalTarget, frozen.Capture(ceiling), CancellationToken.None);
            var initialBytes = Encoding.UTF8.GetByteCount(PlatformPolicyProofSnapshot.Serialize(initial));
            api.AddPadding(1 + PlatformPolicyProofSnapshot.MaxBytes - initialBytes - 30);
            var current = await reader.CaptureAsync(frozen.PhysicalTarget, frozen.Capture(ceiling), CancellationToken.None);
            Assert.Equal(PlatformPolicyProofSnapshot.MaxBytes - 30,
                Encoding.UTF8.GetByteCount(PlatformPolicyProofSnapshot.Serialize(current)));
            var error = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
                factory.Coordinator.ProveAsync(preparation, frozen, reservation, CancellationToken.None));
            Assert.Equal("policy_semantics_unsupported", error.Code);
            Assert.Contains(api.Requests, call =>
                call.Path.EndsWith("/agenthost-platform-egress") && call.Token == "Bearer reader-token");
            Assert.DoesNotContain(api.Requests, call => call.Method == "PUT");
            Assert.Null(await factory.Coordinator.ReadCurrentReceiptAsync(targetId, frozen.HoldId, CancellationToken.None));
            await using var query = fixture.DataSource.CreateCommand(
                "SELECT count(*) FROM environment.platform_policy_proof_holds WHERE hold_id=@hold");
            query.Parameters.AddWithValue("hold", frozen.HoldId);
            Assert.Equal(1L, await query.ExecuteScalarAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RetainedCreationTimestampMakesCandidateTooLargeBeforePut()
    {
        using var certificates = new PlatformPolicySourceTlsServerFixture();
        await using var api = await ProofTlsApi.StartAsync(certificates);
        api.CreationTimestamp = "2026-11-19T00:00:00Z";
        var policy = api.Policy.Current!;
        api.Policy.Current = policy with { Metadata = policy.Metadata with { Uid = "00000000-0000-4000-8000-000000000100" } };
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".proof-tokens", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var readerToken = Path.Combine(directory, "reader");
        var writerToken = Path.Combine(directory, "writer");
        await File.WriteAllTextAsync(readerToken, "reader-token");
        await File.WriteAllTextAsync(writerToken, "writer-token");
        try
        {
            var ca = await File.ReadAllBytesAsync(certificates.BackendCaPath);
            var providerOptions = new CiliumEgressProviderOptions("agentweaver", new Version(1, 0), 1, "test",
                ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty);
            var options = new CiliumPlatformEgressOptions("agentweaver", true, []);
            using var factory = PlatformPolicyProofFactory.Create(
                api.Address.ToString(), ca, readerToken, writerToken, fixture.DataSource,
                providerOptions, options, TimeProvider.System);
            using var client = new HttpClient(KubernetesServiceAccountHandler.Create(readerToken, ca))
            {
                BaseAddress = api.Address,
            };
            var reader = new KubernetesPlatformPolicyInventoryReader(client);
            var resource = new KubernetesCiliumPolicyResourceStore(client);
            var targetId = resource.GetTargetId("agentweaver", CiliumPlatformEgressProvider.PolicyName);
            var reservations = new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System);
            var reservation = await reservations.ReserveAsync(
                Request(targetId, Guid.NewGuid().ToString("N"), 1) with
                {
                    ObjectUid = api.Policy.Current!.Metadata.Uid!,
                    ResourceVersion = api.Policy.Current.Metadata.ResourceVersion!,
                }, CancellationToken.None);
            var frozen = new PlatformPolicyProofFrozenTarget(
                Guid.NewGuid(), targetId, "cluster-" + Guid.NewGuid().ToString("N"), "agentweaver", "namespace-uid-1",
                1, Hash("guard"), Hash("profile"));
            var preparation = new PlatformPolicyProofPreparation(
                "preparation", 1, reservation.SourceId, reservation.SourceRevision, reservation.Policy.SourceHash,
                PlatformEgressAcsCompiler.CompilerVersion, reservation.Policy.Destinations);
            var ceiling = SystemOnlyPolicyCeiling.Create(preparation.SystemDestinations);
            var provider = new CiliumPlatformEgressProvider(resource, providerOptions, options, TimeProvider.System, reservations);
            async Task<(KubernetesPlatformPolicyInventory Inventory, KubernetesPlatformPolicyRecord Candidate)> CaptureAsync()
            {
                var inventory = await reader.CaptureAsync(frozen.PhysicalTarget, frozen.Capture(ceiling), CancellationToken.None);
                var named = await reader.GetNamedAsync("agentweaver", CiliumPlatformEgressProvider.PolicyName, CancellationToken.None);
                var typed = JsonSerializer.Deserialize<CiliumNetworkPolicyDocument>(named.Content.GetRawText(),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return (inventory, PlatformPolicyProofCoordinator.ToRecord(
                    provider.RenderForProof(typed, reservation.Policy, reservation.SourceEpoch)));
            }
            api.AddPaddingPolicies(220_000);
            var initial = await CaptureAsync();
            var typedBytes = Encoding.UTF8.GetByteCount(
                PlatformPolicyProofSnapshot.Serialize(initial.Inventory.Replace(initial.Candidate)));
            api.PaddingPolicies[^1]["metadata"]!["annotations"]!["test-padding"] =
                new string('x', 1 + PlatformPolicyProofSnapshot.MaxBytes - typedBytes - 1);
            var current = await CaptureAsync();
            Assert.Equal(6, current.Inventory.Policies.Length);
            Assert.All(current.Inventory.Policies, policy =>
            {
                var metadata = policy.Content.GetProperty("metadata");
                if (metadata.TryGetProperty("annotations", out var annotations))
                    Assert.True(annotations.EnumerateObject().Sum(annotation =>
                        Encoding.UTF8.GetByteCount(annotation.Name) +
                        Encoding.UTF8.GetByteCount(annotation.Value.GetString()!)) <= 256 * 1024);
            });
            var named = current.Inventory.Policies.Single(policy => policy.Kind == "CiliumNetworkPolicy");
            Assert.Contains("creationTimestamp", named.CanonicalContent);
            Assert.DoesNotContain("creationTimestamp", current.Candidate.CanonicalContent);
            Assert.True(Encoding.UTF8.GetByteCount(PlatformPolicyProofSnapshot.Serialize(current.Inventory))
                < PlatformPolicyProofSnapshot.MaxBytes);
            Assert.Equal(PlatformPolicyProofSnapshot.MaxBytes - 1,
                Encoding.UTF8.GetByteCount(PlatformPolicyProofSnapshot.Serialize(current.Inventory.Replace(current.Candidate))));
            var evaluation = SystemOnlyPolicySetEvaluator.Evaluate(current.Inventory.Replace(current.Candidate), ceiling);
            Assert.True(evaluation.IsSafe);
            Assert.Equal(6, evaluation.SelectedPolicyCount);
            var retainedCandidate = current.Candidate with
            {
                CanonicalContent = current.Candidate.CanonicalContent.Replace(
                    "\"metadata\":{", "\"metadata\":{\"creationTimestamp\":\"2026-11-19T00:00:00Z\",",
                    StringComparison.Ordinal),
            };
            Assert.Contains("creationTimestamp", retainedCandidate.CanonicalContent);
            var sizeError = Assert.Throws<PlatformPolicyProofException>(() =>
                PlatformPolicyProofSnapshot.Serialize(current.Inventory.Replace(retainedCandidate)));
            Assert.Equal("policy_semantics_unsupported", sizeError.Code);

            var error = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
                factory.Coordinator.ProveAsync(preparation, frozen, reservation, CancellationToken.None));
            Assert.Equal("policy_semantics_unsupported", error.Code);
            Assert.DoesNotContain(api.Requests, call => call.Method == "PUT");
            Assert.Null(await factory.Coordinator.ReadCurrentReceiptAsync(targetId, frozen.HoldId, CancellationToken.None));
            await using var query = fixture.DataSource.CreateCommand(
                "SELECT count(*) FROM environment.platform_policy_proof_holds WHERE hold_id=@hold");
            query.Parameters.AddWithValue("hold", frozen.HoldId);
            Assert.Equal(1L, await query.ExecuteScalarAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ProofTlsApi : IAsyncDisposable
    {
        private readonly WebApplication app;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        internal FakeCiliumPolicyStore Policy { get; } = new();
        internal ConcurrentQueue<(string Method, string Path, string Token)> Requests { get; } = new();
        internal Uri Address { get; private set; } = null!;
        internal Uri? RedirectTo { get; set; }
        internal bool GrowAfterPut { get; set; }
        internal string? CreationTimestamp { get; set; }
        internal List<JsonNode> PaddingPolicies { get; } = [];

        internal void AddPaddingPolicies(int length)
        {
            for (var i = 0; i < 5; i++)
            {
                PaddingPolicies.Add(JsonSerializer.SerializeToNode(new
                {
                    apiVersion = "networking.k8s.io/v1",
                    kind = "NetworkPolicy",
                    metadata = new
                    {
                        name = $"padding-{i}",
                        @namespace = "agentweaver",
                        uid = $"00000000-0000-4000-8000-{i + 1:000000000000}",
                        resourceVersion = "1",
                        creationTimestamp = "2026-11-19T00:00:00Z",
                        annotations = new Dictionary<string, string> { ["test-padding"] = new('x', i < 4 ? length : 1) },
                    },
                    spec = new
                    {
                        podSelector = new { matchLabels = new Dictionary<string, string> { ["agentweaver.io/workload"] = "agenthost" } },
                        policyTypes = new[] { "Ingress" },
                    },
                })!);
            }
        }

        private JsonNode PolicyJson()
        {
            var policy = JsonSerializer.SerializeToNode(Policy.Current, JsonOptions)!;
            if (CreationTimestamp is not null)
                policy["metadata"]!["creationTimestamp"] = CreationTimestamp;
            return policy;
        }

        internal void AddPadding(int length = 1_048_576)
        {
            var policy = Policy.Current!;
            Policy.Current = policy with
            {
                Metadata = policy.Metadata with
                {
                    Annotations = policy.Metadata.Annotations.SetItem("test-padding", new string('x', length)),
                },
            };
        }

        private ProofTlsApi(WebApplication app) => this.app = app;

        internal static async Task<ProofTlsApi> StartAsync(PlatformPolicySourceTlsServerFixture certificates)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificates.ProjectsServer)));
            var app = builder.Build();
            var server = new ProofTlsApi(app);
            app.Run(server.HandleAsync);
            await app.StartAsync();
            var actual = Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses);
            server.Address = new UriBuilder("https", "localhost", new Uri(actual).Port).Uri;
            return server;
        }

        private async Task HandleAsync(HttpContext context)
        {
            var path = context.Request.Path.Value!;
            Requests.Enqueue((context.Request.Method, path, context.Request.Headers.Authorization.ToString()));
            if (RedirectTo is not null)
            {
                context.Response.Redirect(RedirectTo.ToString());
                return;
            }
            context.Response.ContentType = "application/json";
            if (path == "/api/v1/namespaces/agentweaver")
            {
                await context.Response.WriteAsync("""{"metadata":{"uid":"namespace-uid-1"}}""");
                return;
            }
            if (path.EndsWith("/agenthost-platform-egress", StringComparison.Ordinal))
            {
                if (context.Request.Method == "PUT")
                {
                    var policy = await JsonSerializer.DeserializeAsync<CiliumNetworkPolicyDocument>(
                        context.Request.Body, JsonOptions, context.RequestAborted);
                    await Policy.ReplaceAsync(policy!, policy!.Metadata.Uid!, policy.Metadata.ResourceVersion!,
                        context.RequestAborted);
                    if (GrowAfterPut)
                        AddPadding();
                }
                await JsonSerializer.SerializeAsync(context.Response.Body, PolicyJson(), JsonOptions);
                return;
            }
            var kind = path.Contains("ciliumclusterwidenetworkpolicies", StringComparison.Ordinal)
                ? "CiliumClusterwideNetworkPolicyList"
                : path.Contains("ciliumnetworkpolicies", StringComparison.Ordinal)
                    ? "CiliumNetworkPolicyList"
                    : "NetworkPolicyList";
            var items = kind switch
            {
                "CiliumNetworkPolicyList" => new[] { PolicyJson() },
                "NetworkPolicyList" => PaddingPolicies.ToArray(),
                _ => [],
            };
            await JsonSerializer.SerializeAsync(context.Response.Body, new
            {
                apiVersion = kind == "NetworkPolicyList" ? "networking.k8s.io/v1" : "cilium.io/v2",
                kind,
                metadata = new { resourceVersion = "1" },
                items,
            }, JsonOptions);
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
