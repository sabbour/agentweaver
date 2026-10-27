using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Agentweaver.Providers.Sandbox.AgentSandbox;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentP1CreationReadTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T12:00:00Z");
    private static readonly EnvironmentGenerationFence Fence = new(new("tenant", "project", "run", "environment"), 1);
    private static readonly P1InvocationBinding Binding = new(
        1, Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), "tenant", "project", "run", "root",
        "https://issuer.test", "actor", Guid.NewGuid(), 1, new string('b', 64), 1, 1, Now.AddMinutes(-2));
    private static readonly ProviderResourceRef Sandbox = new(ProviderSeam.Sandbox, "sandbox", "claim", 1);
    private static readonly ProviderResourceRef Storage = new(ProviderSeam.Storage, "storage", "pvc", 1);
    private static readonly Guid SandboxOperation = Guid.NewGuid();

    [Fact]
    public void CurrentWorkspaceProjectionSeparatesPlannedAndReturnedIdentities()
    {
        const string mountPath = "/workspace/agentweaver/project";
        var empty = JsonSerializer.SerializeToElement(new { });
        var planned = SandboxResourceIdentity.CreatePlannedReference(
            Sandbox.ProviderId, "options", Fence, 1, 1, SandboxOperation);
        Assert.NotEqual(planned.ResourceId, Sandbox.ResourceId);
        var request = new SandboxProvisionApiRequest("volume", 1, 0, mountPath, false, 1, "sandbox");
        var negotiation = new WorkspaceVolumeAttachmentNegotiation(
            "project", "environment", "run", new("project", "volume", 1), planned, Storage, Fence,
            0, mountPath, false, WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim).Validate();
        var attachment = new SandboxWorkspaceAttachment(negotiation, JsonSerializer.SerializeToElement(
            new AgentSandboxPersistentVolumeClaimAttachment(1, Storage.ProviderId, "namespace", "pvc", Storage.ResourceId),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var recovery = new EnvironmentSandboxManager.SandboxProviderRecoveryIntent(
            1, request, attachment, ImmutableDictionary<string, string>.Empty, 4);
        var resource = new SandboxProvisionedResource(Sandbox, new(Guid.NewGuid()), new("placement"),
            ImmutableHashSet.Create(SandboxCapabilities.VmIsolation), [],
            new(Sandbox.ProviderId, "1.0.0", 1, "options", empty, empty));
        var lease = new SandboxLeaseSnapshot(
            Fence, 1, 1, 1, SandboxOperation, SandboxLeaseState.Active,
            new(Sandbox.ProviderId, "1.0.0", 1, "options", empty, empty,
                JsonSerializer.SerializeToElement(recovery, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            resource, null, null, null, null, null, true, Now)
        { LeaseRevision = 3, LeaseExpiresAt = Now.AddMinutes(1), CreatedAt = Now.AddMinutes(-1) };
        var spec = new WorkspaceVolumeSpec("volume", "project", new(WorkspaceVolumeOwnerKind.Run, "run"),
            "environment", WorkspaceVolumeBindingMode.Environment, WorkspaceVolumeAccessMode.ReadWriteMany,
            8, Storage.ProviderId, WorkspaceVolumeConsistency.Strict, WorkspaceVolumeReclaimPolicy.Delete,
            WorkspaceVolumeOwnerDeletionPolicy.Retain, []);
        var snapshot = new EnvironmentWorkspaceVolumeSnapshot(
            Fence, "volume", 4, 1, 0, EnvironmentWorkspaceVolumeState.Attached, EnvironmentWorkspaceVolumeOperation.Attach,
            JsonSerializer.SerializeToElement(spec), Storage,
            new(Storage.ProviderId, "1.0.0", 1, "options", empty,
                JsonSerializer.SerializeToElement(new { @namespace = "namespace", claimName = "pvc", claimUid = Storage.ResourceId })));

        var projected = EnvironmentSandboxManager.ProjectRuntimeWorkspace(Fence, lease, snapshot);
        Assert.Equal(planned, projected.Workspace.SandboxResource);
        Assert.Equal(Storage, projected.Workspace.StorageResource);
        var foreign = recovery with { Workspace = attachment with
            { Negotiation = negotiation with { SandboxResource = planned with { ResourceId = "foreign" } } } };
        Assert.Throws<EnvironmentLifecycleException>(() => EnvironmentSandboxManager.ProjectRuntimeWorkspace(
            Fence, lease with { ProvisionIntent = lease.ProvisionIntent with
                { ProviderRequest = JsonSerializer.SerializeToElement(foreign, new JsonSerializerOptions(JsonSerializerDefaults.Web)) } },
            snapshot));
    }

    [Fact]
    public void ReadRequiresActualCompletedRecordsAndDoesNotRetrofitMissingLinks()
    {
        var records = Records();
        foreach (var missing in records)
        {
            var failure = Assert.Throws<RuntimeAuthorizationException>(() => Join(records.Where(r => r != missing)));
            Assert.Equal("p1_creation_link_not_retained", failure.Code);
        }
        Assert.Equal(4, records.Length);
        Assert.Equal(SandboxOperation, Join(records).SandboxProvision.OperationId);
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("owner")]
    [InlineData("lifecycle")]
    [InlineData("pending")]
    [InlineData("uncertain")]
    [InlineData("resource")]
    [InlineData("resource-generation")]
    [InlineData("provider-fence")]
    [InlineData("future-lease")]
    [InlineData("operation")]
    [InlineData("metadata-volume")]
    [InlineData("duplicate")]
    public void ReadRejectsForeignStaleReplacedOrIncompleteJoins(string fault)
    {
        var records = Records();
        var sandbox = records[3];
        records[3] = fault switch
        {
            "binding" => sandbox with { Binding = Binding with { CoreInvocationId = Guid.NewGuid() } },
            "owner" => sandbox with { Fence = new(new("foreign", "project", "run", "environment"), 1) },
            "lifecycle" => sandbox with { Fence = new(Fence.Owner, 2) },
            "pending" => sandbox with { State = "Pending" },
            "uncertain" => sandbox with { State = "Uncertain" },
            "resource" => sandbox with { Receipt = sandbox.Receipt with { ProviderResource = Sandbox with { ResourceId = "replacement" } } },
            "resource-generation" => sandbox with { Receipt = sandbox.Receipt with { ResourceGeneration = 2 } },
            "provider-fence" => sandbox with { Receipt = sandbox.Receipt with { ProviderFencingGeneration = 2 } },
            "future-lease" => sandbox with { Receipt = sandbox.Receipt with { LeaseRevision = 20 } },
            "operation" => sandbox with { Receipt = sandbox.Receipt with { OperationId = Guid.NewGuid() } },
            _ => sandbox
        };
        if (fault == "metadata-volume")
            records[1] = records[1] with { Receipt = records[1].Receipt with { ResourceId = "foreign-volume" } };
        Assert.Throws<RuntimeAuthorizationException>(() => Join(fault == "duplicate" ? [.. records, records[0]] : records));
    }

    private static EnvironmentP1CreationBinding Join(IEnumerable<EnvironmentP1CreationRecord> records) =>
        EnvironmentP1CreationReader.Join(
            Binding, Fence, "volume", Storage, 4, Sandbox, 1, 3,
            Pod(), records, Now);

    private static SandboxPodObservation Pod() => new(
        1, Now, Fence, Sandbox, 1, SandboxOperation, new string('c', 64), "namespace", "claim",
        "sandbox-uid", "pod", "pod-uid", "42", 1, "Running",
        new("namespace", "workspace-pvc", "pvc-uid", "/workspace/project", false), true);

    private static EnvironmentP1CreationRecord[] Records() =>
    [
        Record(new(Guid.NewGuid(), "Environment", "environment", null, null, null, null, null, Now.AddMinutes(-1), Now.AddMinutes(-1))),
        Record(new(Guid.NewGuid(), "WorkspaceVolumeMetadata", "volume", 0, null, 1, null, null, Now.AddMinutes(-1), Now.AddMinutes(-1))),
        Record(new(Guid.NewGuid(), "WorkspaceVolume", "volume", 1, null, 2, null, Storage, Now.AddMinutes(-1), Now.AddMinutes(-1))),
        Record(new(SandboxOperation, "Sandbox", SandboxOperation.ToString("D"), 1, 1, null, 2, Sandbox, Now.AddMinutes(-1), Now.AddMinutes(-1)))
    ];

    private static EnvironmentP1CreationRecord Record(EnvironmentP1CreationReceipt receipt) =>
        new(Fence, Binding, "Completed", receipt);
}
