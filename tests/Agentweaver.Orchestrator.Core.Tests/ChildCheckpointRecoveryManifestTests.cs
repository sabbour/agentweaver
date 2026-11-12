using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class ChildCheckpointRecoveryManifestTests
{
    [Fact]
    public void ManifestRoundTripsDeterministicallyAndRejectsChangedBytes()
    {
        var manifest = CreateManifest();
        var encoded = ChildCheckpointRecoveryManifestCodec.Encode(manifest);

        var restored = ChildCheckpointRecoveryManifestCodec.Decode(encoded.Json, encoded.Sha256);
        Assert.Equal(encoded, ChildCheckpointRecoveryManifestCodec.Encode(restored));
        Assert.Equal("child-1", Assert.Single(restored.Members).Session.SessionId);
        var failure = Assert.Throws<CoordinationException>(() =>
            ChildCheckpointRecoveryManifestCodec.Decode(encoded.Json + " ", encoded.Sha256));
        Assert.Equal("checkpoint_recovery_manifest_invalid", failure.Code);
    }

    [Fact]
    public void ManifestRejectsDuplicateMembersAndBoundsWithoutTruncation()
    {
        var manifest = CreateManifest();
        var member = Assert.Single(manifest.Members);
        var duplicate = Assert.Throws<CoordinationException>(() =>
            ChildCheckpointRecoveryManifestCodec.Encode(manifest with { Members = [member, member] }));
        Assert.Equal("checkpoint_recovery_manifest_invalid", duplicate.Code);
        Assert.Equal("checkpoint_recovery_manifest_invalid",
            Assert.Throws<CoordinationException>(() =>
                ChildCheckpointRecoveryManifestCodec.Encode(manifest with
                {
                    Members = [member with { NativeOrigin = CreateNative("prepared", 1) }]
                })).Code);
        var tooMany = Enumerable.Range(0, 513).Select(index => member with
        {
            AssociationId = $"work-{index}",
            Session = member.Session with { SessionId = $"child-{index}" }
        }).ToImmutableArray();
        Assert.Equal("checkpoint_recovery_manifest_invalid",
            Assert.Throws<CoordinationException>(() =>
                ChildCheckpointRecoveryManifestCodec.Encode(manifest with { Members = tooMany })).Code);
        Assert.Equal("checkpoint_recovery_manifest_too_large",
            Assert.Throws<CoordinationException>(() =>
                ChildCheckpointRecoveryManifestCodec.Encode(manifest with
                {
                    WorkPlanHash = new string('a', ChildCheckpointRecoveryManifestCodec.MaximumBytes)
                })).Code);
    }

    [Fact]
    public void ManifestBudgetRejectsAccumulatedEvidenceBeforeBuildingTheManifest()
    {
        var budget = new ChildCheckpointRecoveryManifestCodec.ByteBudget();
        var half = new string('a', ChildCheckpointRecoveryManifestCodec.MaximumBytes / 2);
        budget.Include(half);
        var failure = Assert.Throws<CoordinationException>(() => budget.Include(half));
        Assert.Equal("checkpoint_recovery_manifest_too_large", failure.Code);
    }

    [Fact]
    public void ReceiptLinksRejectAnOversizedOrNonObjectEntry()
    {
        var exact = JsonSerializer.SerializeToElement(new
        {
            receipt = new string('a', ChildCheckpointRecoveryManifestCodec.MaximumReceiptBytes - 2)
        });
        ChildCheckpointRecoveryCensus.ValidateReceiptLinks(exact);

        var tooLarge = JsonSerializer.SerializeToElement(new
        {
            receipt = new string('a', ChildCheckpointRecoveryManifestCodec.MaximumReceiptBytes - 1)
        });
        Assert.Equal("checkpoint_recovery_boundary_unsupported",
            Assert.Throws<CoordinationException>(() =>
                ChildCheckpointRecoveryCensus.ValidateReceiptLinks(tooLarge)).Code);
        Assert.Equal("checkpoint_recovery_boundary_unsupported",
            Assert.Throws<CoordinationException>(() =>
                ChildCheckpointRecoveryCensus.ValidateReceiptLinks(JsonSerializer.SerializeToElement(new[] { 1 }))).Code);
    }

    [Theory]
    [InlineData(null, 0, "pendingUnsent", null)]
    [InlineData("prepared", 1, "pendingUnsent", null)]
    [InlineData("prepared", 2, "unresolved", "native_state_unavailable")]
    [InlineData("sending", 1, "unresolved", "native_send_or_accounting_unresolved")]
    [InlineData("indeterminate", 1, "unresolved", "native_send_or_accounting_unresolved")]
    [InlineData("terminal_pending_accounting", 1, "unresolved", "native_send_or_accounting_unresolved")]
    [InlineData("completed", 1, "unresolved", "original_child_material_unavailable")]
    public void DispositionKeepsUncertainSendsHeld(
        string? nativeState, long ownerRevision, string expected, string? reason)
    {
        var member = Assert.Single(CreateManifest().Members);
        var native = nativeState is null ? null : CreateNative(nativeState, ownerRevision);
        var intent = CreateIntent();

        Assert.Equal((expected, reason), ChildCheckpointRecoveryCensus.Classify(
            member.Session, member.Startup, native, intent, MafExecutionTaskStatus.Running, false, false));
    }

    [Fact]
    public void DispositionRequiresBothTerminalReceiptAndPricedNativeEvidence()
    {
        var member = Assert.Single(CreateManifest().Members);
        var completed = CreateNative("completed", 2);
        Assert.Equal(("joined", (string?)null), ChildCheckpointRecoveryCensus.Classify(
            member.Session, member.Startup, completed, null, MafExecutionTaskStatus.Succeeded, true, true));
        Assert.Equal(("unresolved", "child_terminal_evidence_unavailable"), ChildCheckpointRecoveryCensus.Classify(
            member.Session, member.Startup, completed, null, MafExecutionTaskStatus.Succeeded, true, false));
        Assert.Equal(("unresolved", "child_terminal_evidence_unavailable"), ChildCheckpointRecoveryCensus.Classify(
            member.Session, member.Startup, completed with { AccountingStatus = "pending" }, null,
            MafExecutionTaskStatus.Succeeded, true, true));
    }

    [Fact]
    public void MissingCurrentStartupEvidenceCannotBecomePendingUnsent()
    {
        var member = Assert.Single(CreateManifest().Members);
        var stale = member.Startup with { CurrentRegistrationRevision = 2 };
        Assert.Equal(("unresolved", "child_startup_evidence_unavailable"),
            ChildCheckpointRecoveryCensus.Classify(
                member.Session, stale, null, CreateIntent(), MafExecutionTaskStatus.Running, false, false));
    }

    [Fact]
    public void JoinedReceiptMustMatchNativeOriginAndSavedChild()
    {
        var binding = Assert.Single(CreateManifest().Members).Startup.Binding;
        var native = CreateNative("completed", 4) with
        {
            DispatchId = binding.DispatchMessageId,
            TerminalOutcome = "native-completed"
        };
        var selection = new string('1', 64);
        var workItemHash = new string('6', 64);
        var receipt = new RuntimeChildTurnCompletionReceipt(
            1, binding.DispatchMessageId, new string('a', 64), binding.PromptHash, new string('b', 64),
            binding.WorkPlanId, binding.AssociationId, binding.ChildSessionId, "sdk-session",
            native.RuntimeInstanceId, native.RegistrationRevision, native.RuntimeBindingHash, selection,
            native.ExecutionFence, native.SdkSourceReceiptHash,
            new SandboxImageIdentity("sha256:" + new string('d', 64), "linux/amd64", 1),
            native.ModelSelectionReference, native.SelectedModelId, "hosted-copilot", null,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            new string('c', 64), new string('d', 64), 2, 3, [],
            new(binding.DispatchMessageId, 1, SessionMaterialKind.TurnContent, binding.PromptHash, new string('e', 64)),
            new(Guid.NewGuid(), 2, SessionMaterialKind.TurnContent, new string('b', 64), new string('f', 64)))
        {
            WorkItemBindingSha256 = workItemHash
        };

        Assert.True(ChildCheckpointRecoveryCensus.IsJoinedReceiptBound(
            receipt, native, binding, selection, workItemHash));
        Assert.False(ChildCheckpointRecoveryCensus.IsJoinedReceiptBound(
            receipt with { RegistrationRevision = native.RegistrationRevision + 1 },
            native, binding, selection, workItemHash));
        Assert.False(ChildCheckpointRecoveryCensus.IsJoinedReceiptBound(
            receipt, native with { TerminalOutcome = "uncertain" }, binding, selection, workItemHash));
    }

    private static ChildCheckpointRecoveryManifest CreateManifest()
    {
        var session = new ChildRecoverySession(
            "child-1", "root", "root", false, "issuer", "actor", 1,
            "active", "active", 1, 2, "work-1", null);
        var binding = new RuntimeChildStartBinding(
            "root", "child-1", "plan-1", "work-1", "work-1", "step-1",
            "agent-1", "model-1", "issuer", "actor", "context-1", 1, 1,
            Guid.NewGuid(), Guid.NewGuid(), new string('a', 64));
        var placement = new RuntimeRunStartPlacementReceipt(
            1, "environment-1", "profile-1", "volume-1", "sandbox-key", 1, 0, 1,
            Guid.NewGuid(), new ProviderResourceRef(ProviderSeam.Sandbox, "sandbox-1", "resource-1", 1),
            1, 1, 1, 1, DateTimeOffset.Parse("2030-01-01T00:00:00Z")) { PodUid = "pod-1" };
        var startup = new ChildRecoveryStartup(
            binding, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "ready", "ready",
            "environment-1", Guid.NewGuid(), 2, false, new string('b', 64), Guid.NewGuid(), 1, 1,
            "Active", DateTimeOffset.Parse("2030-01-01T00:00:00Z"), new string('c', 64), placement,
            new string('d', 64), Guid.NewGuid(), new string('e', 64), []);
        var member = new ChildRecoveryMember(
            "work-1", session, "step-1", "agent-1", "model-1", null, "sandbox-1",
            new string('f', 64), new string('6', 64), null, startup, binding.DispatchMessageId, binding.PromptHash,
            new string('1', 64), null, null, "Running", "pendingUnsent", null, null, true, null, null);
        var rootRuntime = Guid.NewGuid();
        return new ChildCheckpointRecoveryManifest(
            1, Guid.NewGuid(), new SessionIdentity("project-1", "run-1", "root"), "tenant-1",
            "issuer", "actor", new string('1', 64), "plan-1", new string('2', 64),
            new string('3', 64), "checkpoint-1", 1, new string('4', 64), 1, 1, 2,
            rootRuntime, 1, new ChildRecoveryRootStartup(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "ready", "ready",
                "root-environment", rootRuntime, 1, 1, new string('7', 64), new string('8', 64),
                placement with { EnvironmentId = "root-environment" }, new string('9', 64),
                Guid.NewGuid(), new string('a', 64), []),
            [member], [], [], null, [], []);
    }

    private static MafExecutionDispatchIntent CreateIntent() =>
        new("work-1", "child-1", Guid.NewGuid(), new string('a', 64))
        {
            EgressReceipt = new RuntimeEgressVerificationReceipt(
                1, new EnvironmentGenerationFence(
                    new EnvironmentOwnerIdentity("tenant-1", "project-1", "run-1", "environment-1"), 1),
                1, "context-1", new string('c', 64))
        };

    private static ChildRecoveryNativeOrigin CreateNative(string state, long ownerRevision) =>
        new(Guid.NewGuid(), "checkpoint-1", 1, 1, 1, new string('1', 64), new string('a', 64),
            new string('2', 64), ownerRevision, Guid.NewGuid(), 1, new string('3', 64),
            new string('4', 64), "model-1", "model-id", null, false, false, state, null,
            "source-complete", null, null, new string('5', 64), "priced", null, null, null);
}
