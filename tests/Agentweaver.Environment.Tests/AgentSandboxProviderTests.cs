using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class AgentSandboxProviderTests
{
    [Fact]
    public async Task ListOwnedUsesPinnedOptionsNamespaceInsteadOfCurrentDefaults()
    {
        var currentOptions = new AgentSandboxOptions(
            1,
            "current-options",
            "current-namespace",
            "azure-files-csi",
            "ghcr.io/agentweaver/agenthost:1",
            "kata-vm",
            "kata-qemu",
            "500m",
            "512Mi",
            1,
            1);
        var pinnedOptions = currentOptions with
        {
            OptionsRevision = "pinned-options",
            Namespace = "agentweaver"
        };
        var handler = new FakeKubernetesHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://kubernetes.example/")
        };
        var provider = new AgentSandboxProvider(
            currentOptions,
            new KubernetesAgentSandboxClient(httpClient));
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var intent = new SandboxLeaseProvisionIntent(
            AgentSandboxProviderMetadata.ProviderId,
            AgentSandboxProviderMetadata.AdapterVersion.ToString(),
            pinnedOptions.OptionsSchemaVersion,
            pinnedOptions.OptionsRevision,
            JsonSerializer.SerializeToElement(pinnedOptions, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Json("{\"providers\":[]}"),
            Json("{\"contractVersion\":1}"));

        var observations = await provider.ListOwnedAsync(
            new SandboxListOwnedRequest(new EnvironmentGenerationFence(owner, 1), 1, intent));

        Assert.Empty(observations);
        Assert.Equal("agentweaver", handler.LastSandboxClaimsNamespace);
    }

    [Fact]
    public async Task ProvisionIsIdempotentDescribeWithholdsReadyWithoutNetworkGenerationAndReleaseUsesUidPreconditions()
    {
        var options = new AgentSandboxOptions(
            1,
            "sandbox-options-1",
            "agentweaver",
            "azure-files-csi",
            "ghcr.io/agentweaver/agenthost:1",
            "kata-vm",
            "kata-qemu",
            "500m",
            "512Mi",
            1,
            1);
        var registration = AgentSandboxProviderMetadata.CreateRegistration(options);
        var catalog = ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.Sandbox, registration.Descriptor.Id)],
            []);
        Assert.True(catalog.IsSuccess);
        var resolution = new ProviderResolver(catalog.Value!).Resolve(new ProviderResolutionRequest(
            ProviderSeam.Sandbox,
            null,
            AgentSandboxProviderMetadata.AdapterVersion,
            options.OptionsSchemaVersion,
            registration.Descriptor.AdvertisedCapabilities));
        Assert.True(resolution.IsSuccess);
        var candidate = resolution.Value!.Candidate!;
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var fence = new EnvironmentGenerationFence(owner, 1);
        var operationId = Guid.NewGuid();
        var plannedResource = SandboxResourceIdentity.CreatePlannedReference(
            candidate.ProviderId,
            candidate.OptionsRevision,
            fence,
            resourceGeneration: 1,
            fencingGeneration: 1,
            operationId);
        var storageResource = new ProviderResourceRef(
            ProviderSeam.Storage,
            options.WorkspaceStorageProviderId,
            "workspace-claim-uid",
            1);
        var workspaceSpec = new WorkspaceVolumeSpec(
            "workspace-a",
            owner.ProjectId,
            new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Run, owner.RunId),
            owner.EnvironmentId,
            WorkspaceVolumeBindingMode.Environment,
            WorkspaceVolumeAccessMode.ReadWriteOnce,
            10,
            "azure-files",
            WorkspaceVolumeConsistency.Strict,
            WorkspaceVolumeReclaimPolicy.Delete,
            WorkspaceVolumeOwnerDeletionPolicy.Delete,
            []);
        var workspaceReference = new WorkspaceVolumeReference(owner.ProjectId, "workspace-a", 1);
        var mount = new WorkspaceVolumeMountDeclaration(
            workspaceReference,
            "/workspace/agentweaver/project",
            ReadOnly: false);
        var storageBinding = new WorkspaceVolumeProviderBindingSnapshot(
            storageResource.ProviderId,
            "1.0.0",
            1,
            "storage-options-1",
            Json("{\"namespace\":\"agentweaver\"}"),
            Json("{\"namespace\":\"agentweaver\",\"claimName\":\"workspace-claim\",\"claimUid\":\"workspace-claim-uid\"}"));
        var storage = new WorkspaceVolumeResource(
            storageResource,
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                WorkspaceVolumeCapabilities.ReadWriteOnce,
                WorkspaceVolumeCapabilities.ReadOnlyMount),
            storageBinding);
        var profile = new WorkspaceSandboxAttachmentProfile(
            plannedResource,
            Enum.GetValues<WorkspaceVolumeAccessMode>().ToImmutableHashSet(),
            ImmutableHashSet.Create(WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim),
            ImmutableHashSet.Create(StringComparer.Ordinal, storageResource.ProviderId),
            SupportsReadOnlyMounts: true);
        var negotiation = WorkspaceVolumeAttachmentNegotiator.Negotiate(
            owner.ProjectId,
            owner.EnvironmentId,
            owner.RunId,
            workspaceSpec,
            storage,
            new WorkspaceVolumeMountManifest([mount]),
            mount,
            plannedResource,
            fence,
            dataGeneration: 0,
            profile,
            WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim);
        var attachmentDescriptor = JsonSerializer.SerializeToElement(
            new AgentSandboxPersistentVolumeClaimAttachment(
                1,
                storageResource.ProviderId,
                "agentweaver",
                "workspace-claim",
                "workspace-claim-uid"));
        var request = new SandboxProvisionRequest(
            fence,
            candidate,
            1,
            1,
            operationId,
            ImmutableDictionary.CreateRange(
                StringComparer.Ordinal,
                [new KeyValuePair<string, string>("egress.example/selector", "environment-a")]),
            new SandboxWorkspaceAttachment(negotiation, attachmentDescriptor));
        SandboxProvisionRequest NewRequest(long resourceGeneration, long fencingGeneration, Guid operationId)
        {
            var planned = SandboxResourceIdentity.CreatePlannedReference(
                candidate.ProviderId,
                candidate.OptionsRevision,
                fence,
                resourceGeneration,
                fencingGeneration,
                operationId);
            return request with
            {
                ResourceGeneration = resourceGeneration,
                FencingGeneration = fencingGeneration,
                OperationId = operationId,
                Workspace = request.Workspace with
                {
                    Negotiation = negotiation with { SandboxResource = planned }
                }
            };
        }

        var handler = new FakeKubernetesHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://kubernetes.example/")
        };
        var kubernetesClient = new KubernetesAgentSandboxClient(httpClient);
        var provider = new AgentSandboxProvider(options, kubernetesClient);

        var provisioned = await provider.ProvisionAsync(request);
        var duplicate = await provider.ProvisionAsync(request);

        Assert.Equal(3, handler.CreateCount);
        Assert.Equal(provisioned.Resource, duplicate.Resource);
        Assert.Equal(provisioned.Endpoint, duplicate.Endpoint);
        var claimBody = handler.GetCreated("sandboxclaims");
        Assert.Equal("SandboxClaim", claimBody["kind"]!.GetValue<string>());
        Assert.Equal(
            "extensions.agents.x-k8s.io/v1beta1",
            claimBody["apiVersion"]!.GetValue<string>());
        Assert.Equal(
            "DeleteForeground",
            claimBody["spec"]!["lifecycle"]!["shutdownPolicy"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(
            claimBody["metadata"]!["labels"]!["agentweaver.dev/sandbox-operation"]!.GetValue<string>()));

        handler.AddReadySandbox(provisioned.Resource.ResourceId);
        var observation = await provider.DescribeAsync(new SandboxDescribeRequest(
            fence,
            provisioned.Resource,
            1,
            provisioned.ProviderBinding));

        Assert.Equal(SandboxObservedState.Pending, observation.State);
        Assert.True(observation.VmIsolationVerified);
        Assert.True(observation.WorkspaceAttachmentVerified);
        Assert.Null(observation.VerifiedNetworkGeneration);
        Assert.DoesNotContain(
            observation.StartupPhases,
            phase => phase.Phase is SandboxStartupPhase.Configured or SandboxStartupPhase.Ready);

        var receipt = await provider.ReleaseAsync(new SandboxReleaseRequest(
            fence,
            provisioned.Resource,
            1,
            provisioned.ProviderBinding,
            "release-a"));

        Assert.Equal(SandboxReleaseDisposition.Released, receipt.Disposition);
        Assert.Equal(3, handler.DeleteUidPreconditions.Count);
        Assert.All(handler.DeleteUidPreconditions, Assert.True);

        var resumableRequest = NewRequest(2, 2, Guid.NewGuid());
        handler.FailNextClaimCreate = true;
        var partialProvision = await Assert.ThrowsAsync<SandboxProviderException>(
            () => provider.ProvisionAsync(resumableRequest));
        Assert.True(partialProvision.EffectMayHaveApplied);
        Assert.Equal(5, handler.CreateCount);
        var resumed = await provider.ProvisionAsync(resumableRequest);
        Assert.Equal(6, handler.CreateCount);
        var resumedReceipt = await provider.ReleaseAsync(new SandboxReleaseRequest(
            fence,
            resumed.Resource,
            2,
            resumed.ProviderBinding,
            "release-resumed"));
        Assert.Equal(SandboxReleaseDisposition.Released, resumedReceipt.Disposition);

        var retiringRequest = NewRequest(3, 3, Guid.NewGuid());
        handler.FailNextClaimCreate = true;
        _ = await Assert.ThrowsAsync<SandboxProviderException>(() =>
            provider.ProvisionAsync(retiringRequest));
        var recoveryIntent = new SandboxLeaseProvisionIntent(
            AgentSandboxProviderMetadata.ProviderId,
            AgentSandboxProviderMetadata.AdapterVersion.ToString(),
            options.OptionsSchemaVersion,
            options.OptionsRevision,
            JsonSerializer.SerializeToElement(options, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Json("{\"providers\":[]}"),
            JsonSerializer.SerializeToElement(new
            {
                contractVersion = 1,
                request = new
                {
                    volumeId = "workspace-a",
                    volumeResourceGeneration = 1,
                    dataGeneration = 0,
                    mountPath = "/workspace/agentweaver/project",
                    readOnly = false,
                    networkPolicyGeneration = 1,
                    idempotencyKey = "provision-c"
                },
                workspace = retiringRequest.Workspace,
                egressSelectorLabels = retiringRequest.EgressSelectorLabels,
                workspaceAttachmentTransitionRevision = 2
            }, RecoveryJsonOptions()));
        var now = DateTimeOffset.UtcNow;
        var partialLease = new SandboxLeaseSnapshot(
            fence,
            3,
            3,
            4,
            retiringRequest.OperationId,
            SandboxLeaseState.Releasing,
            recoveryIntent,
            ProvisionedResource: null,
            SandboxRetirementReason.AuthorizedAbandon,
            TerminalEvidence: null,
            "actor-a",
            1,
            "release-partial",
            IsCurrent: true,
            now)
        {
            RetiringIssuer = "https://projects.example",
            ProviderRequestFingerprint = new string('a', 64),
            LeaseRevision = 4,
            LeaseExpiresAt = now.AddSeconds(60)
        };
        var partialReleaseRequest = new SandboxPartialReleaseRequest(fence, partialLease).Validate();
        handler.DeleteUidPreconditions.Clear();
        var partialRelease = await provider.ReleasePartialAsync(partialReleaseRequest);

        Assert.Equal(SandboxReleaseDisposition.Released, partialRelease.Disposition);
        Assert.Equal("resource-uid-7", partialRelease.TemplateUid);
        Assert.Equal("resource-uid-8", partialRelease.WarmPoolUid);
        Assert.Equal(2, handler.DeleteUidPreconditions.Count);
        Assert.All(handler.DeleteUidPreconditions, Assert.True);
        Assert.Equal(8, handler.CreateCount);
    }

    private static JsonSerializerOptions RecoveryJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class FakeKubernetesHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, JsonNode> _resources = new(StringComparer.Ordinal);
        private JsonNode? _podList;

        public int CreateCount { get; private set; }
        public List<bool> DeleteUidPreconditions { get; } = [];
        public string? LastSandboxClaimsNamespace { get; private set; }
        public bool FailNextClaimCreate { get; set; }

        public FakeKubernetesHandler()
        {
            _resources["apis/node.k8s.io/v1/runtimeclasses/kata-vm"] = JsonNode.Parse(
                """
                {"apiVersion":"node.k8s.io/v1","kind":"RuntimeClass","metadata":{"name":"kata-vm"},
                 "handler":"kata-qemu"}
                """)!;
            _resources["api/v1/namespaces/agentweaver/persistentvolumeclaims/workspace-claim"] =
                JsonNode.Parse(
                    """
                    {"apiVersion":"v1","kind":"PersistentVolumeClaim",
                     "metadata":{"name":"workspace-claim","namespace":"agentweaver","uid":"workspace-claim-uid",
                      "annotations":{"agentweaver.dev/project-id":"project-a",
                       "agentweaver.dev/volume-id":"workspace-a","agentweaver.dev/generation":"1",
                       "agentweaver.dev/environment-id":"environment-a"}},
                     "status":{"phase":"Bound"}}
                    """)!;
        }

        public JsonNode GetCreated(string resource)
        {
            var plural = resource switch
            {
                "sandboxclaims" => "sandboxclaims",
                "sandboxwarmpools" => "sandboxwarmpools",
                "sandboxtemplates" => "sandboxtemplates",
                _ => throw new ArgumentOutOfRangeException(nameof(resource))
            };
            return _resources.Single(pair =>
                    pair.Key.Contains($"/{plural}/", StringComparison.Ordinal))
                .Value.DeepClone();
        }

        public void AddReadySandbox(string claimUid)
        {
            var claimPath = _resources.Keys.Single(path =>
                path.Contains("/sandboxclaims/", StringComparison.Ordinal));
            var claim = _resources[claimPath];
            claim["status"] = new JsonObject
            {
                ["sandbox"] = new JsonObject { ["name"] = "sandbox-a" }
            };
            var claimMetadata = claim["metadata"]!.AsObject();
            var labels = claimMetadata["labels"]!.DeepClone().AsObject();
            labels["agents.x-k8s.io/claim-uid"] = claimUid;
            labels["egress.example/selector"] = "environment-a";
            _resources["apis/agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxes/sandbox-a"] =
                JsonNode.Parse(
                    """
                    {"apiVersion":"agents.x-k8s.io/v1beta1","kind":"Sandbox",
                     "metadata":{"name":"sandbox-a","namespace":"agentweaver","uid":"sandbox-uid","generation":1,
                      "ownerReferences":[{"uid":"claim-uid","controller":true}]},
                     "status":{"conditions":[{"type":"Ready","status":"True",
                      "lastTransitionTime":"2026-10-06T12:00:00Z"}]}}
                    """)!;
            _resources["apis/agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxes/sandbox-a"]!
                ["metadata"]!["ownerReferences"]![0]!["uid"] = claimUid;
            _podList = JsonSerializer.SerializeToNode(new
            {
                items = new[]
                {
                    new
                    {
                        apiVersion = "v1",
                        kind = "Pod",
                        metadata = new
                        {
                            name = "sandbox-pod",
                            @namespace = "agentweaver",
                            uid = "pod-uid",
                            labels,
                            ownerReferences = new[] { new { uid = "sandbox-uid", controller = true } }
                        },
                        spec = new
                        {
                            runtimeClassName = "kata-vm",
                            automountServiceAccountToken = false,
                            hostNetwork = false,
                            volumes = new[]
                            {
                                new
                                {
                                    name = "agentweaver-workspace",
                                    persistentVolumeClaim = new
                                    {
                                        claimName = "workspace-claim",
                                        readOnly = false
                                    }
                                }
                            },
                            containers = new[]
                            {
                                new
                                {
                                    name = "agenthost",
                                    volumeMounts = new[]
                                    {
                                        new
                                        {
                                            name = "agentweaver-workspace",
                                            mountPath = "/workspace/agentweaver/project",
                                            readOnly = false
                                        }
                                    }
                                }
                            }
                        },
                        status = new
                        {
                            phase = "Running",
                            conditions = new[] { new { type = "PodScheduled", status = "True" } },
                            containerStatuses = new[]
                            {
                                new
                                {
                                    name = "agenthost",
                                    imageID = "ghcr.io/agentweaver/agenthost@sha256:deadbeef",
                                    ready = true,
                                    state = new
                                    {
                                        running = new { startedAt = "2026-10-06T12:00:00Z" }
                                    }
                                }
                            }
                        }
                    }
                }
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (request.Method == HttpMethod.Get)
            {
                if (path == "api/v1/namespaces/agentweaver/pods")
                    return JsonResponse(HttpStatusCode.OK, _podList ?? new JsonObject { ["items"] = new JsonArray() });
                if (path.EndsWith("/sandboxclaims", StringComparison.Ordinal))
                {
                    var segments = path.Split('/');
                    var namespaceIndex = Array.IndexOf(segments, "namespaces");
                    LastSandboxClaimsNamespace = segments[namespaceIndex + 1];
                    return JsonResponse(HttpStatusCode.OK, new JsonObject { ["items"] = new JsonArray() });
                }
                if (path == "apis/agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxes")
                    return JsonResponse(HttpStatusCode.OK, new JsonObject { ["items"] = new JsonArray() });
                return _resources.TryGetValue(path, out var resource)
                    ? JsonResponse(HttpStatusCode.OK, resource)
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Post)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false))!;
                if (body["kind"]!.GetValue<string>() == "SandboxClaim" && FailNextClaimCreate)
                {
                    FailNextClaimCreate = false;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                body["metadata"]!["uid"] = $"resource-uid-{++CreateCount}";
                var name = body["metadata"]!["name"]!.GetValue<string>();
                _resources[$"{path}/{Uri.EscapeDataString(name)}"] = body.DeepClone();
                return JsonResponse(HttpStatusCode.Created, body);
            }

            if (request.Method == HttpMethod.Patch)
            {
                var patch = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false))!.AsArray();
                var current = _resources[path];
                var expectedUid = patch[0]!["value"]!.GetValue<string>();
                if (current["metadata"]!["uid"]!.GetValue<string>() != expectedUid)
                    return new HttpResponseMessage(HttpStatusCode.Conflict);
                current["metadata"]!["annotations"]!["agentweaver.dev/provider-binding"] =
                    patch[1]!["value"]!.GetValue<string>();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Delete)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false))!;
                var expectedUid = body["preconditions"]!["uid"]!.GetValue<string>();
                var matches = _resources.TryGetValue(path, out var current) &&
                    current["metadata"]!["uid"]!.GetValue<string>() == expectedUid;
                DeleteUidPreconditions.Add(matches);
                if (!matches)
                    return new HttpResponseMessage(HttpStatusCode.Conflict);
                _resources.Remove(path);
                if (path.Contains("/sandboxclaims/", StringComparison.Ordinal))
                {
                    _podList = null;
                    _resources.Remove(
                        "apis/agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxes/sandbox-a");
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, JsonNode body) =>
            new(status)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
    }
}
