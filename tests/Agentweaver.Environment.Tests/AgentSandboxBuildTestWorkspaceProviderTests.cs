using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Xunit;
using PreparationReceipt = Agentweaver.Abstractions.SandboxBuildTestBindingPreparation.SandboxBuildTestWorkspacePreparationReceipt;

namespace Agentweaver.Environment.Tests;

public sealed partial class AgentSandboxProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildTestPodsKeepVersionOneBehaviorAndFenceVersionTwoPreparationAndReadOnlyCapture(bool versionTwo)
    {
        using var fixture = await CreateBuildTestProviderFixtureAsync(versionTwo);
        var provider = fixture.Provider;
        var request = fixture.Request;
        var pod = await provider.CreateGatedAsync(request, CancellationToken.None);
        var commandSpec = fixture.Handler.GetPod(pod)["spec"]!;
        Assert.Equal(versionTwo ? 2 : 1, commandSpec["volumes"]!.AsArray().Count);
        Assert.Single(commandSpec["containers"]![0]!["volumeMounts"]!.AsArray());
        if (versionTwo)
        {
            var init = Assert.Single(commandSpec["initContainers"]!.AsArray())!;
            Assert.Equal(SandboxBuildTestLimits.WorkspacePreparationContainerName, init["name"]!.GetValue<string>());
            Assert.Equal(request.AcceptedCommand.ExecutionOptions.CollectorImageReference, init["image"]!.GetValue<string>());
            Assert.Equal("hold", init["args"]!.AsArray()[^1]!.GetValue<string>());
            Assert.Equal("/state", init["volumeMounts"]![1]!["mountPath"]!.GetValue<string>());
            Assert.Equal("/workspace/agentweaver/project/" + request.AcceptedCommand.WorkspaceSource!.PreparedRepositoryRelativePath,
                commandSpec["containers"]![0]!["workingDir"]!.GetValue<string>());
        }
        else
        {
            Assert.Null(commandSpec["initContainers"]);
        }
        await provider.ReleaseSchedulingGateAsync(request, pod, CancellationToken.None);
        var preparation = CompleteBuildTestCommand(fixture, pod);
        if (versionTwo)
        {
            var restored = await provider.PrepareWorkspaceAsync(request, pod, CancellationToken.None);
            Assert.Equal(preparation, restored);
        }
        var collectorRequest = new SandboxBuildTestOutputCollectorRequest(
            request.AcceptedCommand.OperationId, request.AcceptedCommand.ImmutableHash,
            fixture.ApiRequest.ComputeRequestFingerprint(request.AcceptedCommand), request.AcceptedCommand.Checkpoint,
            "/workspace/agentweaver/project", request.AcceptedCommand.ExecutionOptions.MaximumFileOutputBytes,
            request.AcceptedCommand.Outputs)
        {
            AcceptedCommandContractVersion = versionTwo ? 2 : null
        };
        var collector = await provider.CreateOutputCollectorGatedAsync(request, collectorRequest, CancellationToken.None);
        var collectorSpec = fixture.Handler.GetPod(collector)["spec"]!;
        var container = collectorSpec["containers"]![0]!;
        Assert.True(container["volumeMounts"]![0]!["readOnly"]!.GetValue<bool>());
        Assert.Equal(versionTwo ? 2 : 1, container["volumeMounts"]!.AsArray().Count);
        if (versionTwo)
        {
            Assert.Null(collectorSpec["activeDeadlineSeconds"]);
            Assert.Equal(SandboxBuildTestLimits.WorkspaceCaptureContainerName, container["name"]!.GetValue<string>());
            Assert.False(container["volumeMounts"]![1]!["readOnly"]!.GetValue<bool>());
            Assert.Equal("hold", container["args"]!.AsArray()[^1]!.GetValue<string>());
            Assert.Null(await provider.CaptureWorkspaceAsync(request, collector, preparation!, CancellationToken.None));
        }
        else
        {
            Assert.NotNull(collectorSpec["activeDeadlineSeconds"]);
            Assert.Equal(SandboxBuildTestLimits.OutputCollectorContainerName, container["name"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task VersionTwoProviderPreparesAndRestoresTheCollectorWithNoDeclaredOutputs()
    {
        using var fixture = await CreateBuildTestProviderFixtureAsync(true, emptyOutputs: true);
        var request = fixture.Request;
        var pod = await fixture.Provider.CreateGatedAsync(request, CancellationToken.None);
        await fixture.Provider.ReleaseSchedulingGateAsync(request, pod, CancellationToken.None);
        var preparation = CompleteBuildTestCommand(fixture, pod)!;
        Assert.Equal(preparation,
            await fixture.Provider.PrepareWorkspaceAsync(request, pod, CancellationToken.None));
        var collectorRequest = new SandboxBuildTestOutputCollectorRequest(
            request.AcceptedCommand.OperationId, request.AcceptedCommand.ImmutableHash,
            fixture.ApiRequest.ComputeRequestFingerprint(request.AcceptedCommand), request.AcceptedCommand.Checkpoint,
            "/workspace/agentweaver/project", request.AcceptedCommand.ExecutionOptions.MaximumFileOutputBytes, [])
        {
            AcceptedCommandContractVersion = 2
        };
        var collector = await fixture.Provider.CreateOutputCollectorGatedAsync(
            request, collectorRequest, CancellationToken.None);
        var container = fixture.Handler.GetPod(collector)["spec"]!["containers"]![0]!;
        Assert.Equal(SandboxBuildTestLimits.WorkspaceCaptureContainerName, container["name"]!.GetValue<string>());
        Assert.True(container["volumeMounts"]![0]!["readOnly"]!.GetValue<bool>());
        Assert.Equal(collector,
            await fixture.Provider.GetOutputCollectorAsync(request, CancellationToken.None));
        Assert.Null(await fixture.Provider.CaptureWorkspaceAsync(
            request, collector, preparation, CancellationToken.None));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, null)]
    [InlineData(true, 1)]
    public async Task CollectorRejectsAChangedAcceptedCommandVersionBeforeTransport(
        bool versionTwo, int? collectorVersion)
    {
        using var fixture = await CreateBuildTestProviderFixtureAsync(versionTwo);
        var request = fixture.Request;
        var collectorRequest = new SandboxBuildTestOutputCollectorRequest(
            request.AcceptedCommand.OperationId, request.AcceptedCommand.ImmutableHash,
            fixture.ApiRequest.ComputeRequestFingerprint(request.AcceptedCommand), request.AcceptedCommand.Checkpoint,
            "/workspace/agentweaver/project", request.AcceptedCommand.ExecutionOptions.MaximumFileOutputBytes,
            request.AcceptedCommand.Outputs)
        {
            AcceptedCommandContractVersion = collectorVersion
        };
        var requests = fixture.Handler.RequestCount;
        var creates = fixture.Handler.CreateCount;

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Provider.CreateOutputCollectorGatedAsync(request, collectorRequest, CancellationToken.None));

        Assert.Equal("The trusted output collector request does not match the accepted command.", failure.Message);
        Assert.Equal(requests, fixture.Handler.RequestCount);
        Assert.Equal(creates, fixture.Handler.CreateCount);
        Assert.Empty(fixture.Handler.DeleteUidPreconditions);
    }

    [Theory]
    [InlineData("private-mount")]
    [InlineData("working-path")]
    [InlineData("pod-uid")]
    [InlineData("preparation-uid")]
    public async Task VersionTwoProviderRejectsChangedPreparationOrPodBeforeNativeEffects(string fault)
    {
        using var fixture = await CreateBuildTestProviderFixtureAsync(true);
        var pod = await fixture.Provider.CreateGatedAsync(fixture.Request, CancellationToken.None);
        await fixture.Provider.ReleaseSchedulingGateAsync(fixture.Request, pod, CancellationToken.None);
        var preparation = CompleteBuildTestCommand(fixture, pod)!;
        var resource = fixture.Handler.GetPod(pod);
        switch (fault)
        {
            case "private-mount":
                resource["spec"]!["initContainers"]![0]!["volumeMounts"]![1]!["readOnly"] = true;
                break;
            case "working-path":
                resource["spec"]!["containers"]![0]!["workingDir"] = "/workspace/other";
                break;
            case "pod-uid":
                resource["metadata"]!["uid"] = "foreign-pod-uid";
                break;
            case "preparation-uid":
                SetPreparationLog(fixture, pod, preparation with { PodUid = "foreign-pod-uid" });
                break;
        }
        var creates = fixture.Handler.CreateCount;
        var failure = await Assert.ThrowsAsync<SandboxProviderException>(() =>
            fixture.Provider.PrepareWorkspaceAsync(fixture.Request, pod, CancellationToken.None));
        Assert.Equal("provider_resource_mismatch", failure.Code);
        Assert.Equal(creates, fixture.Handler.CreateCount);
        Assert.Empty(fixture.Handler.DeleteUidPreconditions);
    }

    private static async Task<BuildTestProviderFixture> CreateBuildTestProviderFixtureAsync(
        bool versionTwo, bool emptyOutputs = false)
    {
        var command = SandboxBuildTestCommandContractTests.CreateFixture().Command;
        if (versionTwo)
            command = command with
            {
                ContractVersion = 2,
                WorkspaceSource = SandboxBuildTestCommandContractTests.WorkspaceSource() with
                {
                    Identity = new("project-a", "run-a", command.Checkpoint.SessionId)
                },
                WorkspaceSeed = new byte[1024].ToImmutableArray(),
                ExecutionOptions = command.ExecutionOptions with { CollectorMode = SandboxBuildTestLimits.WorkspaceCollectorMode }
            };
        if (emptyOutputs)
            command = command with { Outputs = [] };
        command = command with { Checkpoint = command.Checkpoint with { ProjectId = "project-a", RunId = "run-a" } };
        command = command with { ImmutableHash = command.ComputeImmutableHash() };
        var options = new AgentSandboxOptions(
            AgentSandboxOptions.CurrentOptionsSchemaVersion, "buildtest-options", "agentweaver", "azure-files-csi",
            "ghcr.io/agentweaver/agenthost@sha256:" + new string('a', 64), "linux/amd64",
            1, "kata-vm", "kata-qemu", "500m", "512Mi", 1, 1, new(30, 30, 30, 30, 30, 120))
        {
            AcceptedBuildTestProfile = command.ExecutionOptions
        };
        var registration = AgentSandboxProviderMetadata.CreateRegistration(options);
        var catalog = ProviderCatalog.Create([registration],
            [new ProviderSelection(ProviderSeam.Sandbox, registration.Descriptor.Id)], []).Value!;
        var candidate = new ProviderResolver(catalog).Resolve(new ProviderResolutionRequest(
            ProviderSeam.Sandbox, null, AgentSandboxProviderMetadata.AdapterVersion,
            options.OptionsSchemaVersion, registration.Descriptor.AdvertisedCapabilities)).Value!.Candidate!;
        var fence = new EnvironmentGenerationFence(new("tenant", "project-a", "run-a", "environment-a"), 1);
        var operationId = Guid.NewGuid();
        var planned = SandboxResourceIdentity.CreatePlannedReference(candidate.ProviderId, candidate.OptionsRevision,
            fence, 1, 1, operationId);
        var volume = new WorkspaceVolumeReference("project-a", "workspace-a", 1);
        var workspace = new SandboxWorkspaceAttachment(
            new("project-a", "environment-a", "run-a", volume, planned,
                new(ProviderSeam.Storage, "azure-files-csi", "workspace-claim-uid", 1),
                fence, 0, "/workspace/agentweaver/project", false, WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim),
            JsonSerializer.SerializeToElement(new AgentSandboxPersistentVolumeClaimAttachment(
                1, "azure-files-csi", "agentweaver", "workspace-claim", "workspace-claim-uid")));
        var labels = ImmutableDictionary<string, string>.Empty.Add("egress.example/selector", "environment-a");
        var handler = new FakeKubernetesHandler();
        handler.EnableBuildTestWorkspace();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://kubernetes.example/") };
        var provider = new AgentSandboxProvider(options, new KubernetesAgentSandboxClient(client));
        var provisioned = await provider.ProvisionAsync(new(fence, candidate, 1, 1, operationId, labels, workspace));
        var binding = new SandboxBuildTestExpectedBinding(fence, operationId, provisioned.Resource, 1, volume, 0, 1)
        {
            SandboxProviderBinding = provisioned.ProviderBinding
        };
        var api = new SandboxBuildTestApiRequest(command.Checkpoint, binding);
        var commandPolicy = AddPolicy(SandboxBuildTestLimits.CommandPolicyRole);
        var collectorPolicy = AddPolicy(SandboxBuildTestLimits.CollectorPolicyRole);
        return new(provider, handler, client, api, new(command, binding,
            new(commandPolicy.Uid, commandPolicy.Hash, 1, commandPolicy.Labels))
        {
            CollectorPolicy = new(collectorPolicy.Uid, collectorPolicy.Hash, 1, collectorPolicy.Labels)
        });

        (string Uid, string Hash, ImmutableDictionary<string, string> Labels) AddPolicy(string role)
        {
            var selector = labels.Add(SandboxBuildTestLimits.PolicyOperationLabel, command.OperationId.ToString("N"))
                .Add(SandboxBuildTestLimits.PolicyRoleLabel, role);
            var spec = JsonSerializer.SerializeToElement(new
            {
                endpointSelector = new { matchLabels = selector },
                enableDefaultDeny = new { ingress = true, egress = true },
                egress = Array.Empty<object>()
            });
            var name = role == SandboxBuildTestLimits.CommandPolicyRole
                ? AgentSandboxBuildTestPolicyNames.Command(command.OperationId)
                : AgentSandboxBuildTestPolicyNames.Collector(command.OperationId);
            var uid = role + "-policy-uid";
            handler.AddResource("apis/cilium.io/v2/namespaces/agentweaver/ciliumnetworkpolicies/" + name,
                JsonSerializer.SerializeToNode(new
                {
                    metadata = new { name, @namespace = "agentweaver", uid }, spec
                })!);
            return (uid, SandboxBuildTestCommandPolicyCanonicalization.ComputeSpecSha256(spec), selector);
        }
    }

    private static PreparationReceipt? CompleteBuildTestCommand(BuildTestProviderFixture fixture, SandboxBuildTestPodReference pod)
    {
        var command = fixture.Request.AcceptedCommand;
        var preparation = command.WorkspaceSource is { } source
            ? new PreparationReceipt(command.OperationId, command.ImmutableHash,
                fixture.ApiRequest.ComputeRequestFingerprint(command), command.Checkpoint, pod.Uid,
                source.PreparedRepositoryRelativePath, source.SeedSha256, source.BaselineTreeSha)
            : null;
        var resource = fixture.Handler.GetPod(pod);
        resource["status"] = JsonNode.Parse("""
            {"phase":"Succeeded","containerStatuses":[{"name":"buildtest-command","restartCount":0,
              "state":{"terminated":{"exitCode":0,"startedAt":"2026-10-06T12:00:00Z","finishedAt":"2026-10-06T12:00:01Z"}}}]}
            """);
        if (preparation is not null)
        {
            resource["status"]!["initContainerStatuses"] = JsonSerializer.SerializeToNode(new[]
            {
                new { name = SandboxBuildTestLimits.WorkspacePreparationContainerName, state = new { terminated = new { exitCode = 0 } } }
            });
            SetPreparationLog(fixture, pod, preparation);
        }
        return preparation;
    }

    private static void SetPreparationLog(BuildTestProviderFixture fixture, SandboxBuildTestPodReference pod, PreparationReceipt receipt)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        fixture.Handler.Logs[$"/api/v1/namespaces/{pod.KubernetesNamespace}/pods/{pod.Name}/log?container=" +
            SandboxBuildTestLimits.WorkspacePreparationContainerName + "&follow=true&timestamps=false"] =
            JsonSerializer.Serialize(receipt, options);
    }

    private sealed record BuildTestProviderFixture(
        AgentSandboxProvider Provider, FakeKubernetesHandler Handler, HttpClient Client,
        SandboxBuildTestApiRequest ApiRequest, SandboxBuildTestProviderRequest Request) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }
}
