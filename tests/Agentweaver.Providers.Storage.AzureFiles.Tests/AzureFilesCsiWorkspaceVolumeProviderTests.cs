using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Providers.Storage.AzureFiles;
using Xunit;

namespace Agentweaver.Providers.Storage.AzureFiles.Tests;

public sealed class AzureFilesCsiWorkspaceVolumeProviderTests
{
    [Fact]
    public async Task ProvisionRetriesByStableClaimAndReturnsTheActualResourceGeneration()
    {
        var client = new FakeAzureFilesCsiClient();
        var provider = CreateProvider(client);
        var request = new WorkspaceVolumeProvisionRequest(Spec(), 3, "provision-1");

        var first = await provider.ProvisionAsync(request);
        var retry = await provider.ProvisionAsync(request);

        Assert.Equal(first.Resource, retry.Resource);
        Assert.True(first.NegotiatedCapabilities.SetEquals(retry.NegotiatedCapabilities));
        Assert.Equal(2, client.EnsureClaimCalls);
        Assert.Equal("claim-uid-1", first.Resource.ResourceId);
        Assert.Equal(3, first.Resource.Generation);
        Assert.Equal(
            WorkspaceVolumeCapabilities.ReadWriteMany,
            Assert.Single(first.NegotiatedCapabilities,
                capability => capability == WorkspaceVolumeCapabilities.ReadWriteMany));
        Assert.DoesNotContain(WorkspaceVolumeCapabilities.ExistingVolumeAttach, first.NegotiatedCapabilities);
        Assert.DoesNotContain(WorkspaceVolumeCapabilities.DurableFlush, first.NegotiatedCapabilities);
        var registration = AzureFilesCsiProviderMetadata.CreateRegistration(Options);
        Assert.Equal(AzureFilesCsiProviderMetadata.ProviderId, registration.Descriptor.Id);
        Assert.Equal(ProviderSeam.Storage, registration.Descriptor.Seam);
        Assert.Equal("azure-files-options-1", registration.OptionsRevision);
        Assert.DoesNotContain(
            WorkspaceVolumeCapabilities.ExistingVolumeAttach,
            registration.Descriptor.AdvertisedCapabilities);
        Assert.DoesNotContain(
            WorkspaceVolumeCapabilities.DurableFlush,
            registration.Descriptor.AdvertisedCapabilities);
        Assert.DoesNotContain(
            client.Claims.Values.Single().Annotations.Values,
            value => value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                     value.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReplacementGenerationUsesANewClaimAndOldGenerationCannotReleaseIt()
    {
        var client = new FakeAzureFilesCsiClient();
        var provider = CreateProvider(client);
        var spec = Spec(reclaimPolicy: WorkspaceVolumeReclaimPolicy.Delete);
        var first = await provider.ProvisionAsync(
            new WorkspaceVolumeProvisionRequest(spec, 1, "provision-generation-1"));
        var replacement = await provider.ProvisionAsync(
            new WorkspaceVolumeProvisionRequest(spec, 2, "provision-generation-2"));
        var firstName = AzureFilesCsiWorkspaceVolumeProvider.GetClaimName(
            spec.ProjectId, spec.VolumeId, first.Resource.Generation);
        var replacementName = AzureFilesCsiWorkspaceVolumeProvider.GetClaimName(
            spec.ProjectId, spec.VolumeId, replacement.Resource.Generation);
        var firstKey = $"{Options.Namespace}/{firstName}";
        var replacementKey = $"{Options.Namespace}/{replacementName}";

        Assert.NotEqual(firstName, replacementName);
        Assert.Contains(firstKey, client.Claims.Keys);
        Assert.Contains(replacementKey, client.Claims.Keys);
        Assert.Throws<ArgumentException>(() =>
            new WorkspaceVolumeReleaseRequest(
                new WorkspaceVolumeReference(spec.ProjectId, spec.VolumeId, first.Resource.Generation),
                replacement.Resource,
                spec.BindingMode,
                WorkspaceVolumeReclaimPolicy.Delete,
                spec.OwnerDeletionPolicy,
                replacement.ProviderBinding,
                "stale-release").Validate());

        var receipt = await provider.ReleaseAsync(ReleaseRequest(
            spec, first, WorkspaceVolumeReclaimPolicy.Delete, "release-generation-1"));

        Assert.Equal(first.Resource, receipt.Resource);
        Assert.Equal("release-generation-1", receipt.IdempotencyKey);
        Assert.Equal(WorkspaceVolumeReleaseDisposition.Released, receipt.Disposition);
        Assert.DoesNotContain(firstKey, client.Claims.Keys);
        Assert.Contains(replacementKey, client.Claims.Keys);
    }

    [Fact]
    public async Task WaitsForBoundAndRejectsAnUnsupportedSandboxAssumption()
    {
        var client = new FakeAzureFilesCsiClient { PendingReadsBeforeBound = 1 };
        var provider = CreateProvider(client, provisioningTimeoutSeconds: 2);

        var resource = await provider.ProvisionAsync(
            new WorkspaceVolumeProvisionRequest(Spec(), 1, "provision-2"));

        Assert.Equal("claim-uid-1", resource.Resource.ResourceId);
        Assert.DoesNotContain(WorkspaceVolumeCapabilities.ExistingVolumeAttach, resource.NegotiatedCapabilities);
    }

    [Fact]
    public async Task RejectsUnapprovedMountOptionsBeforeCreatingAClaim()
    {
        var client = new FakeAzureFilesCsiClient
        {
            StorageClass = new AzureFilesStorageClassSnapshot(
                Options.StorageClassName,
                "file.csi.azure.com",
                ["dir_mode=0777", "file_mode=0644", "uid=1000", "gid=1000", "mfsymlinks", "cache=strict", "actimeo=30"])
        };
        var provider = CreateProvider(client);

        var exception = await Assert.ThrowsAsync<AzureFilesCsiException>(() =>
            provider.ProvisionAsync(new WorkspaceVolumeProvisionRequest(Spec(), 1, "provision-3")));

        Assert.Equal("mount_options_invalid", exception.Code);
        Assert.Equal(0, client.EnsureClaimCalls);
    }

    [Fact]
    public async Task RejectsNonStrictConsistencyAndCapacityBeforeProvisioning()
    {
        var client = new FakeAzureFilesCsiClient();
        var provider = CreateProvider(client, maximumCapacityGiB: 8);

        var consistency = await Assert.ThrowsAsync<AzureFilesCsiException>(() =>
            provider.ProvisionAsync(new WorkspaceVolumeProvisionRequest(
                Spec(consistency: WorkspaceVolumeConsistency.Eventual), 1, "provision-4")));
        var capacity = await Assert.ThrowsAsync<AzureFilesCsiException>(() =>
            provider.ProvisionAsync(new WorkspaceVolumeProvisionRequest(
                Spec(capacityGiB: 9), 1, "provision-5")));

        Assert.Equal("consistency_unsupported", consistency.Code);
        Assert.Equal("capacity_exceeded", capacity.Code);
        Assert.Equal(0, client.EnsureClaimCalls);
    }

    [Fact]
    public async Task RejectsAClaimReplacedByAnotherOwnerOrGeneration()
    {
        var client = new FakeAzureFilesCsiClient { ReplaceClaimIdentity = true };
        var provider = CreateProvider(client);

        var exception = await Assert.ThrowsAsync<AzureFilesCsiException>(() =>
            provider.ProvisionAsync(new WorkspaceVolumeProvisionRequest(Spec(), 1, "provision-6")));

        Assert.Equal("claim_identity_mismatch", exception.Code);
    }

    [Fact]
    public async Task ReportsFailedClaimsExplicitly()
    {
        var client = new FakeAzureFilesCsiClient { InitialPhase = "Failed" };
        var provider = CreateProvider(client);

        var exception = await Assert.ThrowsAsync<AzureFilesCsiException>(() =>
            provider.ProvisionAsync(new WorkspaceVolumeProvisionRequest(Spec(), 1, "provision-7")));

        Assert.Equal("claim_failed", exception.Code);
    }

    [Fact]
    public async Task RetainKeepsTheClaimAndDeleteUsesItsUidPrecondition()
    {
        var client = new FakeAzureFilesCsiClient();
        var provider = CreateProvider(client);
        var retained = Spec(volumeId: "retained");
        var deleted = Spec(volumeId: "deleted", reclaimPolicy: WorkspaceVolumeReclaimPolicy.Delete);
        var retainedResource = await provider.ProvisionAsync(
            new WorkspaceVolumeProvisionRequest(retained, 1, "provision-retained"));
        var deletedResource = await provider.ProvisionAsync(
            new WorkspaceVolumeProvisionRequest(deleted, 1, "provision-deleted"));

        var retainedReceipt = await provider.ReleaseAsync(ReleaseRequest(
            retained, retainedResource, WorkspaceVolumeReclaimPolicy.Retain, "release-retained"));
        var deletedReceipt = await provider.ReleaseAsync(ReleaseRequest(
            deleted, deletedResource, WorkspaceVolumeReclaimPolicy.Delete, "release-deleted"));

        var retainedName = AzureFilesCsiWorkspaceVolumeProvider.GetClaimName(
            retained.ProjectId, retained.VolumeId, 1);
        var deletedName = AzureFilesCsiWorkspaceVolumeProvider.GetClaimName(
            deleted.ProjectId, deleted.VolumeId, 1);
        Assert.Contains($"{Options.Namespace}/{retainedName}", client.Claims.Keys);
        Assert.Equal("Retain", client.PersistentVolumes[client.Claims[$"{Options.Namespace}/{retainedName}"].VolumeName!]
            .ReclaimPolicy);
        Assert.DoesNotContain($"{Options.Namespace}/{deletedName}", client.Claims.Keys);
        Assert.Contains(("claim-uid-2", deletedName), client.DeletedClaims);
        Assert.Equal(WorkspaceVolumeReleaseDisposition.Retained, retainedReceipt.Disposition);
        Assert.Equal(retainedResource.Resource, retainedReceipt.Resource);
        Assert.Equal(WorkspaceVolumeReleaseDisposition.Released, deletedReceipt.Disposition);
        Assert.Equal(deletedResource.Resource, deletedReceipt.Resource);
    }

    [Fact]
    public async Task ReleaseRetryProvesKnownAbsenceAndUnconfirmedDeleteReturnsNoReceipt()
    {
        var client = new FakeAzureFilesCsiClient();
        var provider = CreateProvider(client);
        var resource = await provider.ProvisionAsync(
            new WorkspaceVolumeProvisionRequest(
                Spec(reclaimPolicy: WorkspaceVolumeReclaimPolicy.Delete),
                1,
                "provision-release-retry"));
        var request = ReleaseRequest(
            Spec(reclaimPolicy: WorkspaceVolumeReclaimPolicy.Delete),
            resource,
            WorkspaceVolumeReclaimPolicy.Delete,
            "release-retry");

        var released = await provider.ReleaseAsync(request);
        var retry = await provider.ReleaseAsync(request);

        Assert.Equal(WorkspaceVolumeReleaseDisposition.Released, released.Disposition);
        Assert.Equal(WorkspaceVolumeReleaseDisposition.Released, retry.Disposition);
        Assert.Equal(request.Resource, retry.Resource);
        Assert.Equal(request.IdempotencyKey, retry.IdempotencyKey);

        var pendingClient = new FakeAzureFilesCsiClient { ClaimRemainsAfterDelete = true };
        var pendingProvider = CreateProvider(pendingClient);
        var pendingResource = await pendingProvider.ProvisionAsync(
            new WorkspaceVolumeProvisionRequest(
                Spec(reclaimPolicy: WorkspaceVolumeReclaimPolicy.Delete),
                1,
                "provision-release-pending"));
        var pendingRequest = ReleaseRequest(
            Spec(reclaimPolicy: WorkspaceVolumeReclaimPolicy.Delete),
            pendingResource,
            WorkspaceVolumeReclaimPolicy.Delete,
            "release-pending");

        var exception = await Assert.ThrowsAsync<AzureFilesCsiException>(() =>
            pendingProvider.ReleaseAsync(pendingRequest));

        Assert.Equal("claim_release_unconfirmed", exception.Code);
        var pendingClaimName = AzureFilesCsiWorkspaceVolumeProvider.GetClaimName(
            "project-1", "volume-1", 1);
        Assert.Contains($"{Options.Namespace}/{pendingClaimName}", pendingClient.Claims.Keys);
    }

    [Fact]
    public async Task DeleteRequiresExactSavedPvAbsenceAfterTheClaimDisappears()
    {
        var client = new FakeAzureFilesCsiClient();
        var provider = CreateProvider(client);
        var spec = Spec(reclaimPolicy: WorkspaceVolumeReclaimPolicy.Delete);
        var resource = await provider.ProvisionAsync(
            new WorkspaceVolumeProvisionRequest(spec, 1, "provision-pv-proof"));
        var request = ReleaseRequest(spec, resource, WorkspaceVolumeReclaimPolicy.Delete, "release-pv-proof");
        var claimName = AzureFilesCsiWorkspaceVolumeProvider.GetClaimName(spec.ProjectId, spec.VolumeId, 1);
        var claimKey = $"{Options.Namespace}/{claimName}";
        client.Claims.Remove(claimKey);

        var stillPresent = await Assert.ThrowsAsync<AzureFilesCsiException>(() =>
            provider.ReleaseAsync(request));
        Assert.Equal("persistent_volume_release_unconfirmed", stillPresent.Code);

        var pvName = resource.ProviderBinding.ReleaseDescriptor
            .GetProperty("persistentVolumeName").GetString()!;
        client.PersistentVolumes.Remove(pvName);
        var released = await provider.ReleaseAsync(request);
        Assert.Equal(WorkspaceVolumeReleaseDisposition.Released, released.Disposition);

        var replacementClient = new FakeAzureFilesCsiClient();
        var replacementProvider = CreateProvider(replacementClient);
        var replacementResource = await replacementProvider.ProvisionAsync(
            new WorkspaceVolumeProvisionRequest(spec, 1, "provision-pv-uid-proof"));
        var replacementRequest = ReleaseRequest(
            spec, replacementResource, WorkspaceVolumeReclaimPolicy.Delete, "release-pv-uid-proof");
        var replacementClaim = AzureFilesCsiWorkspaceVolumeProvider.GetClaimName(
            spec.ProjectId, spec.VolumeId, 1);
        replacementClient.Claims.Remove($"{Options.Namespace}/{replacementClaim}");
        var replacementPvName = replacementResource.ProviderBinding.ReleaseDescriptor
            .GetProperty("persistentVolumeName").GetString()!;
        replacementClient.PersistentVolumes[replacementPvName] =
            replacementClient.PersistentVolumes[replacementPvName] with { Uid = "different-pv-uid" };

        var mismatched = await Assert.ThrowsAsync<AzureFilesCsiException>(() =>
            replacementProvider.ReleaseAsync(replacementRequest));
        Assert.Equal("persistent_volume_mismatch", mismatched.Code);
    }

    [Fact]
    public async Task ReleaseUsesTheSavedOptionsAndDescriptorInsteadOfCurrentDefaults()
    {
        var client = new FakeAzureFilesCsiClient();
        var originalProvider = CreateProvider(client);
        var spec = Spec(reclaimPolicy: WorkspaceVolumeReclaimPolicy.Delete);
        var resource = await originalProvider.ProvisionAsync(
            new WorkspaceVolumeProvisionRequest(spec, 1, "provision-pinned-options"));
        var request = ReleaseRequest(spec, resource, WorkspaceVolumeReclaimPolicy.Delete, "release-pinned-options");
        var reconfiguredProvider = new AzureFilesCsiWorkspaceVolumeProvider(
            Options with
            {
                OptionsRevision = "azure-files-options-2",
                Namespace = "other-namespace",
                StorageClassName = "other-storage-class"
            },
            client);
        var savedClaimName = resource.ProviderBinding.ReleaseDescriptor
            .GetProperty("claimName").GetString()!;

        var receipt = await reconfiguredProvider.ReleaseAsync(request);

        Assert.Equal(WorkspaceVolumeReleaseDisposition.Released, receipt.Disposition);
        Assert.DoesNotContain($"{Options.Namespace}/{savedClaimName}", client.Claims.Keys);
        Assert.Equal($"{Options.Namespace}/{savedClaimName}", client.LastClaimLookup);
    }

    [Fact]
    public async Task RetainReleaseReportsAnAlreadyAbsentClaimWithoutDeleting()
    {
        var client = new FakeAzureFilesCsiClient();
        var provider = CreateProvider(client);
        var spec = Spec();
        var resource = await provider.ProvisionAsync(
            new WorkspaceVolumeProvisionRequest(spec, 1, "provision-retained-absent"));
        var claimName = AzureFilesCsiWorkspaceVolumeProvider.GetClaimName(
            spec.ProjectId, spec.VolumeId, 1);
        client.Claims.Remove($"{Options.Namespace}/{claimName}");

        var receipt = await provider.ReleaseAsync(ReleaseRequest(
            spec, resource, WorkspaceVolumeReclaimPolicy.Retain, "release-retained-absent"));

        Assert.Equal(WorkspaceVolumeReleaseDisposition.Retained, receipt.Disposition);
        Assert.Equal(resource.Resource, receipt.Resource);
        Assert.Empty(client.DeletedClaims);
    }

    [Fact]
    public async Task RejectsReleaseByTheWrongProviderBeforeReadingKubernetes()
    {
        var client = new FakeAzureFilesCsiClient();
        var provider = CreateProvider(client);
        var request = new WorkspaceVolumeReleaseRequest(
            new WorkspaceVolumeReference("project-1", "volume-1", 1),
            new ProviderResourceRef(ProviderSeam.Storage, "other-provider", "claim-uid-1", 1),
            WorkspaceVolumeBindingMode.Environment,
            WorkspaceVolumeReclaimPolicy.Delete,
            WorkspaceVolumeOwnerDeletionPolicy.Delete,
            new WorkspaceVolumeProviderBindingSnapshot(
                "other-provider",
                "1.0.0",
                1,
                "test-options",
                JsonSerializer.SerializeToElement(new { setting = "test" }),
                JsonSerializer.SerializeToElement(new { resource = "test" })),
            "release-wrong-provider");

        var exception = await Assert.ThrowsAsync<AzureFilesCsiException>(() =>
            provider.ReleaseAsync(request));

        Assert.Equal("provider_binding_unsupported", exception.Code);
        Assert.Equal(0, client.GetClaimCalls);
    }

    [Fact]
    public async Task RejectsReleaseWhenClaimIdentityDoesNotMatch()
    {
        var client = new FakeAzureFilesCsiClient();
        var provider = CreateProvider(client);
        var resource = await provider.ProvisionAsync(new WorkspaceVolumeProvisionRequest(Spec(), 1, "provision-8"));
        var claimName = AzureFilesCsiWorkspaceVolumeProvider.GetClaimName("project-1", "volume-1", 1);
        var claimKey = $"{Options.Namespace}/{claimName}";
        client.Claims[claimKey] = client.Claims[claimKey] with
        {
            Annotations = client.Claims[claimKey].Annotations.SetItem(
                "agentweaver.dev/project-id", "other-project")
        };

        var exception = await Assert.ThrowsAsync<AzureFilesCsiException>(() =>
            provider.ReleaseAsync(ReleaseRequest(
                Spec(reclaimPolicy: WorkspaceVolumeReclaimPolicy.Delete),
                resource,
                WorkspaceVolumeReclaimPolicy.Delete,
                "release-8")));

        Assert.Equal("claim_identity_mismatch", exception.Code);
    }

    [Fact]
    public async Task RejectsReleaseWhenThePinnedClaimUidIsStale()
    {
        var client = new FakeAzureFilesCsiClient();
        var provider = CreateProvider(client);
        var resource = await provider.ProvisionAsync(
            new WorkspaceVolumeProvisionRequest(Spec(), 1, "provision-stale"));
        var stale = resource.Resource with { ResourceId = "old-claim-uid" };

        var exception = await Assert.ThrowsAsync<AzureFilesCsiException>(() =>
            provider.ReleaseAsync(new WorkspaceVolumeReleaseRequest(
                new WorkspaceVolumeReference("project-1", "volume-1", 1),
                stale,
                Spec(reclaimPolicy: WorkspaceVolumeReclaimPolicy.Delete).BindingMode,
                WorkspaceVolumeReclaimPolicy.Delete,
                Spec(reclaimPolicy: WorkspaceVolumeReclaimPolicy.Delete).OwnerDeletionPolicy,
                resource.ProviderBinding,
                "release-stale")));

        Assert.Equal("provider_binding_mismatch", exception.Code);
        Assert.Empty(client.DeletedClaims);
    }

    [Fact]
    public async Task KubernetesClientUsesUidTestsForReclaimAndDelete()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.Equal("PATCH", request.Method.Method);
            Assert.Equal("/api/v1/persistentvolumes/pv-1", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://kubernetes.example/") };
        var client = new KubernetesAzureFilesCsiClient(httpClient);

        await client.SetPersistentVolumeReclaimPolicyAsync(
            "pv-1", "pv-uid-1", WorkspaceVolumeReclaimPolicy.Retain);
        var patch = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("test", patch.RootElement[0].GetProperty("op").GetString());
        Assert.Equal("/metadata/uid", patch.RootElement[0].GetProperty("path").GetString());
        Assert.Equal("pv-uid-1", patch.RootElement[0].GetProperty("value").GetString());
        Assert.Equal("Retain", patch.RootElement[1].GetProperty("value").GetString());

        var deleteHandler = new RecordingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var deleteClient = new HttpClient(deleteHandler)
        {
            BaseAddress = new Uri("https://kubernetes.example/")
        };
        await new KubernetesAzureFilesCsiClient(deleteClient)
            .DeleteClaimAsync("agentweaver", "claim-1", "claim-uid-1");
        using var deleteOptions = JsonDocument.Parse(deleteHandler.RequestBody!);
        Assert.Equal(
            "claim-uid-1",
            deleteOptions.RootElement.GetProperty("preconditions").GetProperty("uid").GetString());
    }

    [Fact]
    public void KubernetesClientRejectsCredentialsEmbeddedInTheApiAddress()
    {
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://user:password@kubernetes.example/")
        };

        Assert.Throws<ArgumentException>(() => new KubernetesAzureFilesCsiClient(httpClient));
    }

    [Fact]
    public async Task KubernetesClientCreatesAnOwnedPvcWithoutCredentialValues()
    {
        var request = new AzureFilesClaimRequest(
            "agentweaver",
            "aw-volume",
            "project-1",
            "volume-1",
            4,
            new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Run, "run-1"),
            "azure-files-premium",
            8,
            WorkspaceVolumeAccessMode.ReadWriteMany);
        var handler = new RecordingHandler((message, _) =>
        {
            if (message.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            Assert.Equal(HttpMethod.Post, message.Method);
            return Task.FromResult(JsonResponse(HttpStatusCode.Created, ClaimJson(request, "claim-uid-4", "Pending")));
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://kubernetes.example/") };
        var claim = await new KubernetesAzureFilesCsiClient(httpClient).EnsureClaimAsync(request);

        Assert.Equal("Pending", claim.Phase);
        Assert.Equal("4", claim.Annotations["agentweaver.dev/generation"]);
        Assert.EndsWith("/api/v1/namespaces/agentweaver/persistentvolumeclaims", handler.RequestPath);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var spec = body.RootElement.GetProperty("spec");
        Assert.Equal("azure-files-premium", spec.GetProperty("storageClassName").GetString());
        Assert.Equal("ReadWriteMany", spec.GetProperty("accessModes")[0].GetString());
        Assert.Equal("8Gi", spec.GetProperty("resources").GetProperty("requests").GetProperty("storage").GetString());
        Assert.DoesNotContain("password", handler.RequestBody!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", handler.RequestBody!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KubernetesClientParsesCanonicalTerabyteCapacityAsIntegralGibibytes()
    {
        var request = new AzureFilesClaimRequest(
            "agentweaver",
            "aw-volume",
            "project-1",
            "volume-1",
            1,
            new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Run, "run-1"),
            "azure-files-premium",
            1024,
            WorkspaceVolumeAccessMode.ReadWriteMany);
        var canonicalClaim = ClaimJson(request, "claim-uid-1", "Bound")
            .Replace("1024Gi", "1Ti", StringComparison.Ordinal);
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, canonicalClaim)));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://kubernetes.example/")
        };

        var claim = await new KubernetesAzureFilesCsiClient(httpClient)
            .GetClaimAsync(request.Namespace, request.Name);

        Assert.NotNull(claim);
        Assert.Equal(1024, claim.CapacityGiB);
    }

    [Fact]
    public async Task KubernetesClientRejectsCapacityThatIsNotAnIntegralGibibyte()
    {
        var request = new AzureFilesClaimRequest(
            "agentweaver",
            "aw-volume",
            "project-1",
            "volume-1",
            1,
            new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Run, "run-1"),
            "azure-files-premium",
            2,
            WorkspaceVolumeAccessMode.ReadWriteMany);
        var fractionalClaim = ClaimJson(request, "claim-uid-1", "Bound")
            .Replace("2Gi", "1536Mi", StringComparison.Ordinal);
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, fractionalClaim)));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://kubernetes.example/")
        };

        var exception = await Assert.ThrowsAsync<AzureFilesCsiException>(() =>
            new KubernetesAzureFilesCsiClient(httpClient).GetClaimAsync(request.Namespace, request.Name));

        Assert.Equal("kubernetes_response_invalid", exception.Code);
    }

    [Fact]
    public async Task KubernetesClientUsesExistingClaimAfterAConcurrentCreate()
    {
        var request = new AzureFilesClaimRequest(
            "agentweaver",
            "aw-volume",
            "project-1",
            "volume-1",
            1,
            new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Run, "run-1"),
            "azure-files-premium",
            8,
            WorkspaceVolumeAccessMode.ReadWriteOnce);
        var getCount = 0;
        var handler = new RecordingHandler((message, _) =>
        {
            if (message.Method == HttpMethod.Post)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
            getCount++;
            return Task.FromResult(getCount == 1
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(HttpStatusCode.OK, ClaimJson(request, "claim-uid-1", "Pending")));
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://kubernetes.example/") };

        var claim = await new KubernetesAzureFilesCsiClient(httpClient).EnsureClaimAsync(request);

        Assert.Equal("claim-uid-1", claim.Uid);
        Assert.Equal(2, getCount);
    }

    private static AzureFilesCsiWorkspaceVolumeProvider CreateProvider(
        FakeAzureFilesCsiClient client,
        long maximumCapacityGiB = 32,
        int provisioningTimeoutSeconds = 10) =>
        new(Options with
        {
            MaximumCapacityGiB = maximumCapacityGiB,
            ProvisioningTimeoutSeconds = provisioningTimeoutSeconds,
            PollIntervalMilliseconds = 1
        }, client);

    private static AzureFilesCsiOptions Options { get; } = new(
        AzureFilesCsiOptions.CurrentOptionsSchemaVersion,
        "azure-files-options-1",
        "agentweaver",
        "azure-files-premium",
        32,
        10,
        5);

    private static WorkspaceVolumeSpec Spec(
        string volumeId = "volume-1",
        long capacityGiB = 8,
        WorkspaceVolumeConsistency consistency = WorkspaceVolumeConsistency.Strict,
        WorkspaceVolumeReclaimPolicy reclaimPolicy = WorkspaceVolumeReclaimPolicy.Retain,
        WorkspaceVolumeOwnerDeletionPolicy ownerDeletionPolicy = WorkspaceVolumeOwnerDeletionPolicy.Delete) =>
        new(
            volumeId,
            "project-1",
            new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Run, "run-1"),
            "environment-1",
            WorkspaceVolumeBindingMode.Environment,
            WorkspaceVolumeAccessMode.ReadWriteMany,
            capacityGiB,
            Options.StorageClassName,
            consistency,
            reclaimPolicy,
            ownerDeletionPolicy,
            []);

    private static WorkspaceVolumeReleaseRequest ReleaseRequest(
        WorkspaceVolumeSpec spec,
        WorkspaceVolumeResource resource,
        WorkspaceVolumeReclaimPolicy reclaimPolicy,
        string idempotencyKey) =>
        new(
            new WorkspaceVolumeReference(
                spec.ProjectId,
                spec.VolumeId,
                resource.Resource.Generation),
            resource.Resource,
            spec.BindingMode,
            reclaimPolicy,
            spec.OwnerDeletionPolicy,
            resource.ProviderBinding,
            idempotencyKey);

    private static string ClaimJson(AzureFilesClaimRequest request, string uid, string phase) =>
        JsonSerializer.Serialize(new
        {
            metadata = new
            {
                @namespace = request.Namespace,
                name = request.Name,
                uid,
                annotations = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["agentweaver.dev/project-id"] = request.ProjectId,
                    ["agentweaver.dev/volume-id"] = request.VolumeId,
                    ["agentweaver.dev/generation"] = request.ResourceGeneration.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ["agentweaver.dev/owner-kind"] = request.Owner.Kind.ToString(),
                    ["agentweaver.dev/owner-id"] = request.Owner.Id
                }
            },
            spec = new
            {
                volumeName = (string?)null,
                storageClassName = request.StorageClassName,
                resources = new { requests = new Dictionary<string, string> { ["storage"] = $"{request.CapacityGiB}Gi" } },
                accessModes = new[]
                {
                    request.AccessMode switch
                    {
                        WorkspaceVolumeAccessMode.ReadWriteOnce => "ReadWriteOnce",
                        WorkspaceVolumeAccessMode.ReadWriteMany => "ReadWriteMany",
                        WorkspaceVolumeAccessMode.ReadOnlyMany => "ReadOnlyMany",
                        _ => throw new ArgumentOutOfRangeException()
                    }
                }
            },
            status = new { phase }
        });

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeAzureFilesCsiClient : IAzureFilesCsiClient
    {
        private readonly Dictionary<string, AzureFilesClaimRequest> _requests = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _pendingReads = new(StringComparer.Ordinal);
        private int _nextUid = 1;

        public AzureFilesStorageClassSnapshot? StorageClass { get; set; } =
            new(
                Options.StorageClassName,
                "file.csi.azure.com",
                ["dir_mode=0755", "file_mode=0644", "uid=1000", "gid=1000", "mfsymlinks", "cache=strict", "actimeo=30"]);

        public string InitialPhase { get; set; } = "Bound";
        public int PendingReadsBeforeBound { get; set; }
        public bool ReplaceClaimIdentity { get; set; }
        public bool ClaimRemainsAfterDelete { get; set; }
        public int EnsureClaimCalls { get; private set; }
        public int GetClaimCalls { get; private set; }
        public AzureFilesClaimRequest? LastClaimRequest { get; private set; }
        public string? LastClaimLookup { get; private set; }
        public Dictionary<string, AzureFilesClaimSnapshot> Claims { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, AzureFilesPersistentVolumeSnapshot> PersistentVolumes { get; } =
            new(StringComparer.Ordinal);
        public List<(string Uid, string Name)> DeletedClaims { get; } = [];

        public Task<AzureFilesStorageClassSnapshot?> GetStorageClassAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(StorageClass?.Name == name ? StorageClass : null);

        public Task<AzureFilesClaimSnapshot> EnsureClaimAsync(
            AzureFilesClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            EnsureClaimCalls++;
            LastClaimRequest = request;
            var key = Key(request.Namespace, request.Name);
            if (!Claims.TryGetValue(key, out var claim))
            {
                var uid = $"claim-uid-{_nextUid++}";
                var phase = PendingReadsBeforeBound > 0 ? "Pending" : InitialPhase;
                claim = Claim(request, uid, phase, ReplaceClaimIdentity);
                Claims.Add(key, claim);
                _requests.Add(key, request);
                _pendingReads[key] = 0;
                if (phase == "Bound")
                    EnsurePersistentVolume(claim, request);
            }
            return Task.FromResult(claim);
        }

        public Task<AzureFilesClaimSnapshot?> GetClaimAsync(
            string kubernetesNamespace,
            string name,
            CancellationToken cancellationToken = default)
        {
            GetClaimCalls++;
            var key = Key(kubernetesNamespace, name);
            LastClaimLookup = key;
            if (!Claims.TryGetValue(key, out var claim))
                return Task.FromResult<AzureFilesClaimSnapshot?>(null);
            if (claim.Phase == "Pending")
            {
                var reads = _pendingReads[key] + 1;
                _pendingReads[key] = reads;
                if (reads > PendingReadsBeforeBound)
                {
                    claim = claim with { Phase = "Bound", VolumeName = $"pv-{claim.Name}" };
                    Claims[key] = claim;
                    EnsurePersistentVolume(claim, _requests[key]);
                }
            }
            return Task.FromResult<AzureFilesClaimSnapshot?>(claim);
        }

        public Task<AzureFilesPersistentVolumeSnapshot?> GetPersistentVolumeAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PersistentVolumes.GetValueOrDefault(name));

        public Task SetPersistentVolumeReclaimPolicyAsync(
            string name,
            string expectedUid,
            WorkspaceVolumeReclaimPolicy reclaimPolicy,
            CancellationToken cancellationToken = default)
        {
            var volume = PersistentVolumes[name];
            if (volume.Uid != expectedUid)
                throw new AzureFilesCsiException("persistent_volume_mismatch", "Unexpected test PV UID.");
            PersistentVolumes[name] = volume with
            {
                ReclaimPolicy = reclaimPolicy == WorkspaceVolumeReclaimPolicy.Delete ? "Delete" : "Retain"
            };
            return Task.CompletedTask;
        }

        public Task DeleteClaimAsync(
            string kubernetesNamespace,
            string name,
            string expectedUid,
            CancellationToken cancellationToken = default)
        {
            var key = Key(kubernetesNamespace, name);
            if (Claims.TryGetValue(key, out var claim) && claim.Uid != expectedUid)
                throw new AzureFilesCsiException("claim_identity_mismatch", "Unexpected test claim UID.");
            DeletedClaims.Add((expectedUid, name));
            if (!ClaimRemainsAfterDelete)
            {
                if (claim?.VolumeName is { } volumeName)
                    PersistentVolumes.Remove(volumeName);
                Claims.Remove(key);
            }
            return Task.CompletedTask;
        }

        private void EnsurePersistentVolume(AzureFilesClaimSnapshot claim, AzureFilesClaimRequest request)
        {
            var name = claim.VolumeName ?? $"pv-{claim.Name}";
            PersistentVolumes[name] = new AzureFilesPersistentVolumeSnapshot(
                name,
                $"pv-uid-{claim.Uid}",
                request.StorageClassName,
                "Delete",
                request.Namespace,
                request.Name,
                claim.Uid);
        }

        private static AzureFilesClaimSnapshot Claim(
            AzureFilesClaimRequest request,
            string uid,
            string phase,
            bool replaceIdentity)
        {
            var annotations = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            annotations["agentweaver.dev/project-id"] = replaceIdentity ? "other-project" : request.ProjectId;
            annotations["agentweaver.dev/volume-id"] = request.VolumeId;
            annotations["agentweaver.dev/generation"] = request.ResourceGeneration.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            annotations["agentweaver.dev/owner-kind"] = request.Owner.Kind.ToString();
            annotations["agentweaver.dev/owner-id"] = request.Owner.Id;
            return new AzureFilesClaimSnapshot(
                request.Namespace,
                request.Name,
                uid,
                phase,
                phase == "Bound" ? $"pv-{request.Name}" : null,
                request.StorageClassName,
                request.CapacityGiB,
                [request.AccessMode],
                annotations.ToImmutable());
        }

        private static string Key(string kubernetesNamespace, string name) =>
            $"{kubernetesNamespace}/{name}";
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public string? RequestPath { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri?.AbsolutePath;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return await send(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
