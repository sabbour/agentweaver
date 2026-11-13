using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class RuntimeRegistrationRecoveryTests
{
    [Theory]
    [InlineData("source")]
    [InlineData("hash")]
    [InlineData("fence")]
    [InlineData("registration")]
    [InlineData("gate")]
    [InlineData("checkpoint")]
    [InlineData("decision")]
    [InlineData("phase")]
    [InlineData("recovery-checkpoint-hash")]
    [InlineData("supersedes-operation")]
    [InlineData("supersession-receipt")]
    [InlineData("supersession-payload-hash")]
    public void RecoveryProgressCannotRewriteItsAdmission(string change)
    {
        var current = new CheckpointRecoveryStatus(
            "pending", "source", 1, 1, 2, Guid.NewGuid(), 1)
        {
            SourceCheckpointHash = new string('a', 64),
            RecoveryCheckpointId = "recovery",
            RecoveryCheckpointRevision = 2,
            DecisionStateVersion = 2,
            GateRequestId = "gate",
            GateAuthorizedActorId = "actor",
            RecoveryCheckpointHash = new string('c', 64),
            SupersedesOperationId = Guid.NewGuid(),
            SupersessionReceiptId = Guid.NewGuid(),
            SupersessionPayloadHash = new string('d', 64)
        };
        var progress = current with { CurrentStep = "bootstrap", RuntimeInstanceId = Guid.NewGuid() };
        CoordinationOwnerStore.RequireImmutableCheckpointRecoveryAdmission(current, progress);
        var changed = change switch
        {
            "source" => progress with { SourceCheckpointId = "other" },
            "hash" => progress with { SourceCheckpointHash = new string('b', 64) },
            "fence" => progress with { ExecutionFence = 3 },
            "registration" => progress with { PreviousRegistrationRevision = 2 },
            "gate" => progress with { GateRequestId = "other" },
            "checkpoint" => progress with { RecoveryCheckpointId = "other" },
            "decision" => progress with { DecisionStateVersion = 3 },
            "phase" => progress with { Phase = "unknown" },
            "recovery-checkpoint-hash" => progress with { RecoveryCheckpointHash = new string('e', 64) },
            "supersedes-operation" => progress with { SupersedesOperationId = Guid.NewGuid() },
            "supersession-receipt" => progress with { SupersessionReceiptId = Guid.NewGuid() },
            "supersession-payload-hash" => progress with { SupersessionPayloadHash = new string('f', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };

        var error = Assert.Throws<CoordinationException>(() =>
            CoordinationOwnerStore.RequireImmutableCheckpointRecoveryAdmission(current, changed));
        Assert.Equal("checkpoint_recovery_progress_conflict", error.Code);
    }

    [Theory]
    [InlineData("recovery-checkpoint-hash")]
    [InlineData("supersedes-operation")]
    [InlineData("supersession-receipt")]
    [InlineData("supersession-payload-hash")]
    public void RecoveryProgressCannotChangeOrDropSupersessionAnchors(string field)
    {
        var current = new CheckpointRecoveryStatus(
            "pending", "source", 1, 1, 2, Guid.NewGuid(), 1)
        {
            SourceCheckpointHash = new string('a', 64),
            RecoveryCheckpointId = "recovery",
            RecoveryCheckpointRevision = 2,
            RecoveryCheckpointHash = new string('c', 64),
            SupersedesOperationId = Guid.NewGuid(),
            SupersessionReceiptId = Guid.NewGuid(),
            SupersessionPayloadHash = new string('d', 64),
            DecisionStateVersion = 2
        };
        var dropped = field switch
        {
            "recovery-checkpoint-hash" => current with { RecoveryCheckpointHash = null },
            "supersedes-operation" => current with { SupersedesOperationId = null },
            "supersession-receipt" => current with { SupersessionReceiptId = null },
            "supersession-payload-hash" => current with { SupersessionPayloadHash = null },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        var error = Assert.Throws<CoordinationException>(() =>
            CoordinationOwnerStore.RequireImmutableCheckpointRecoveryAdmission(current, dropped));
        Assert.Equal("checkpoint_recovery_progress_conflict", error.Code);
    }

    [Fact]
    public void SupersessionReverseLinksAreDerivedOnly()
    {
        var current = new CheckpointRecoveryStatus(
            "pending", "source", 1, 1, 2, Guid.NewGuid(), 1)
        {
            SourceCheckpointHash = new string('a', 64),
            RecoveryCheckpointId = "recovery",
            RecoveryCheckpointRevision = 2,
            DecisionStateVersion = 2
        };

        var error = Assert.Throws<CoordinationException>(() =>
            CoordinationOwnerStore.RequireImmutableCheckpointRecoveryAdmission(
                current, current with { SupersededByOperationId = Guid.NewGuid() }));

        Assert.Equal("checkpoint_recovery_progress_conflict", error.Code);
    }

    [Fact]
    public void PreviousRuntimeEvidenceIsScopedToItsRecoveryOperation()
    {
        var operationId = Guid.NewGuid();
        var links = new Dictionary<string, JsonElement>
        {
            ["checkpointRecoveryEvidenceOperationId"] = JsonSerializer.SerializeToElement(operationId),
            ["checkpointRecoveryPreviousRegistration"] = JsonSerializer.SerializeToElement("registration"),
            ["checkpointRecoveryPreviousPlacement"] = JsonSerializer.SerializeToElement("placement")
        };

        Assert.True(RuntimeRunStartContinuation.HasPreviousCheckpointRecoveryEvidence(operationId, links));
        Assert.False(RuntimeRunStartContinuation.HasPreviousCheckpointRecoveryEvidence(Guid.NewGuid(), links));
    }

    [Fact]
    public void CheckpointRecoverySelectionHashComparisonIgnoresHexCaseButRejectsDifferentHashes()
    {
        var intentHash = new string('a', 64);
        var manifestHash = intentHash.ToUpperInvariant();

        Assert.True(RuntimeRunStartContinuation.HasMatchingCheckpointRecoverySelectionHash(
            intentHash, manifestHash));
        Assert.False(RuntimeRunStartContinuation.HasMatchingCheckpointRecoverySelectionHash(
            intentHash, new string('b', 64)));
    }

    [Fact]
    public void NewRecoveryReplacesPreviousRuntimeEvidenceFromAnOlderOperation()
    {
        var oldOperationId = Guid.NewGuid();
        var newOperationId = Guid.NewGuid();
        var links = new Dictionary<string, JsonElement>
        {
            ["checkpointRecoveryEvidenceOperationId"] = JsonSerializer.SerializeToElement(oldOperationId),
            ["checkpointRecoveryPreviousRegistration"] = JsonSerializer.SerializeToElement("older-registration"),
            ["checkpointRecoveryPreviousPlacement"] = JsonSerializer.SerializeToElement("older-placement"),
            ["checkpointRecoveryPreviousBootstrapOperationId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()),
            ["checkpointRecoveryBootstrapEvidenceOperationId"] = JsonSerializer.SerializeToElement(oldOperationId),
            ["bootstrap"] = JsonSerializer.SerializeToElement("older-bootstrap")
        };
        var bootstrapOperationId = Guid.NewGuid();
        var failedHostRegistration = JsonSerializer.SerializeToElement("failed-host-registration");
        var failedHostPlacement = JsonSerializer.SerializeToElement("failed-host-placement");

        RuntimeRunStartContinuation.SetPreviousCheckpointRecoveryEvidence(
            links, newOperationId, failedHostRegistration, failedHostPlacement,
            JsonSerializer.SerializeToElement(bootstrapOperationId));

        Assert.True(RuntimeRunStartContinuation.HasPreviousCheckpointRecoveryEvidence(newOperationId, links));
        Assert.Equal("failed-host-registration",
            links["checkpointRecoveryPreviousRegistration"].GetString());
        Assert.Equal("failed-host-placement", links["checkpointRecoveryPreviousPlacement"].GetString());
        Assert.Equal(bootstrapOperationId,
            links["checkpointRecoveryPreviousBootstrapOperationId"].GetGuid());
        Assert.False(RuntimeRunStartContinuation.HasCheckpointRecoveryBootstrapEvidence(newOperationId, links));
        RuntimeRunStartContinuation.SetCheckpointRecoveryBootstrapEvidence(
            links, newOperationId, JsonSerializer.SerializeToElement("failed-host-bootstrap"));
        Assert.True(RuntimeRunStartContinuation.HasCheckpointRecoveryBootstrapEvidence(newOperationId, links));
        Assert.Equal("failed-host-bootstrap", links["bootstrap"].GetString());
    }

    [Theory]
    [InlineData("runtime_delivery_unavailable")]
    [InlineData("runtime_receiver_unavailable")]
    [InlineData("runtime_source_credential_unavailable")]
    [InlineData("runtime_configuration_conflict")]
    [InlineData("runtime_operation_conflict")]
    public void SpentHostBootstrapErrorsBlockOnlyAfterTheReceiptAndActivationStart(string reason)
    {
        Assert.Equal(
            ("blocked", "checkpoint_recovery_host_bootstrap_spent"),
            RuntimeRunStartContinuation.ClassifyCheckpointRecoveryFailure(
                true, reason, transportOrTimeout: false));
        Assert.Equal(
            ("blocked", "checkpoint_recovery_host_bootstrap_spent"),
            RuntimeRunStartContinuation.ClassifyCheckpointRecoveryFailure(
                true, reason, transportOrTimeout: false));
        Assert.Equal(
            ("blocked", reason),
            RuntimeRunStartContinuation.ClassifyCheckpointRecoveryFailure(
                false, reason, transportOrTimeout: false));
    }

    [Fact]
    public void CheckpointRecoveryRetriesOnlyTransportAndTimeoutFailures()
    {
        Assert.Equal(
            ("pending", "runtime_start_owner_unavailable"),
            RuntimeRunStartContinuation.ClassifyCheckpointRecoveryFailure(
                true, "runtime_start_owner_unavailable", transportOrTimeout: true));
        Assert.Equal(
            ("pending", "checkpoint_recovery_continuation_timeout"),
            RuntimeRunStartContinuation.ClassifyCheckpointRecoveryFailure(
                true, "checkpoint_recovery_continuation_timeout", transportOrTimeout: false));
        Assert.Equal(
            ("pending", "runtime_activation_owner_unavailable"),
            RuntimeRunStartContinuation.ClassifyCheckpointRecoveryFailure(
                true, "runtime_activation_owner_unavailable", transportOrTimeout: true));
        Assert.Equal(
            ("pending", "runtime_owner_unavailable"),
            RuntimeRunStartContinuation.ClassifyCheckpointRecoveryFailure(
                true, "runtime_owner_unavailable", transportOrTimeout: true));
        Assert.Equal(
            ("pending", "runtime_activation_owner_timeout"),
            RuntimeRunStartContinuation.ClassifyCheckpointRecoveryFailure(
                true, "runtime_activation_owner_timeout", transportOrTimeout: true));
        Assert.Equal(
            ("blocked", "runtime_other_unavailable"),
            RuntimeRunStartContinuation.ClassifyCheckpointRecoveryFailure(
                true, "runtime_other_unavailable", transportOrTimeout: false));
    }

    [Fact]
    public async Task BlockedCheckpointRecoveryDoesNotStartAnotherContinuation()
    {
        var result = new RunExecutionTransitionResult(
            Guid.NewGuid(),
            new SessionIdentity("project", "run", "root"),
            "active", "active", 4, 5, 0, 1, 2, 1, 2,
            null, null, null, null, ImmutableArray<SessionExecutionFenceChange>.Empty)
        {
            CheckpointRecovery = new CheckpointRecoveryStatus(
                "blocked", "source", 1, 4, 5, Guid.NewGuid(), 1)
        };
        var continuation = new RuntimeRunStartContinuation(
            null!, null!, null!, null!, null!, null!);

        var replay = await continuation.ContinueCheckpointRecoveryAsync(
            null!, null!, null!, result, null!, null!, null!, null!, null!, null!, CancellationToken.None);

        Assert.Same(result, replay);
    }

    [Fact]
    public void CheckpointRecoveryOwnerUsesTheCurrentFenceTurnIdentity()
    {
        var binding = new RuntimeBinding(
            "issuer", "actor", "tenant", "project", "run", "session", "agent", "old-turn",
            1, 1, 1, "context", new string('a', 64), 1, "environment", "placement", 1,
            "profile", new Uri("https://runtime.test/"), new Uri("https://runtime.test/"));
        var identity = new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId);

        var owner = RuntimeRunStartContinuation.CreateCheckpointRecoveryOwnerContext(binding, 5);

        Assert.Equal(5, owner.ExecutionFence);
        Assert.Equal(CoordinationOwnerStore.CreateCoordinatorRootRuntimeTurnId(identity, 5), owner.TurnId);
        Assert.NotEqual(binding.TurnId, owner.TurnId);

        var childOwner = RuntimeRunStartContinuation.CreateCheckpointRecoveryOwnerContext(binding, 5, 3);
        Assert.Equal(3, childOwner.LogicalTurnOrdinal);
        Assert.Equal(CoordinationOwnerStore.CreateRuntimeTurnId(identity, 5, 3), childOwner.TurnId);
    }

    [Fact]
    public void RecoveryReplayRestoresOnlyTheExactRegistrationRevocation()
    {
        var binding = new RuntimeBinding(
            "issuer", "actor", "tenant", "project", "run", "session", "agent", "turn",
            1, 1, 1, "context", new string('a', 64), 1, "environment", "placement", 1,
            "profile", new Uri("https://runtime.test/"), new Uri("https://runtime.test/"));
        var active = new RuntimeRegistration(
            Guid.NewGuid(), 1, binding, RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(5));
        var revoked = active with
        {
            Revision = active.Revision + 1,
            State = RuntimeRegistrationState.Revoked
        };

        Assert.Equal(active, RuntimeRegistrationOwner.RestoreReleasedRegistrationForRecovery(
            active, active.Revision));
        Assert.Equal(active, RuntimeRegistrationOwner.RestoreReleasedRegistrationForRecovery(
            revoked, active.Revision));
        Assert.Equal("runtime_registration_stale", Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeRegistrationOwner.RestoreReleasedRegistrationForRecovery(
                revoked, revoked.Revision)).Code);
    }

    [Fact]
    public void RecoveryRegistrationAllowsOnlyItsPinnedGateVersionWhilePending()
    {
        const long fence = 4;
        const long decisionVersion = 12;
        const string selectionHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var asked = CoordinatorDecisionFlow.AskQuestion(
            CoordinatorDecisionState.Create(fence),
            "recovery-gate",
            "scope-question",
            "Choose a repository.",
            "actor",
            ["repository-a"],
            allowsFreeform: false);
        Assert.True(asked.IsSuccess);
        var decision = new CoordinatorDecisionCurrentState(asked.State!, decisionVersion, selectionHash);
        var owner = new SessionRuntimeOwnerState(
            "root", null, "tenant", selectionHash, fence, 0, 1, "runtime-turn")
        {
            IsCoordinatorRoot = true
        };
        var authorization = new CheckpointRecoveryRegistrationAuthorization(
            Guid.NewGuid(),
            new CheckpointRecoveryStatus(
                "pending", "source-checkpoint", 1, fence - 1, fence, Guid.NewGuid(), 1)
            {
                DecisionStateVersion = decisionVersion,
                GateRequestId = "recovery-gate",
                GateAuthorizedActorId = "actor"
            });

        CoordinationEndpoints.RequireCoordinatorRootDecision(decision, owner, authorization);
        CoordinationEndpoints.RequireCoordinatorRootDecision(
            decision, owner, authorization with
            {
                Status = authorization.Status with { Phase = "ready" }
            });
        var answered = asked.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            "recovery-gate", "actor", fence, "repository-a", null));
        Assert.True(answered.IsAccepted);
        CoordinationEndpoints.RequireCoordinatorRootDecision(
            new CoordinatorDecisionCurrentState(
                answered.State, decisionVersion + 1, selectionHash),
            owner,
            authorization with { Status = authorization.Status with { Phase = "ready" } });

        Assert.Equal("runtime_owner_context_unavailable",
            Assert.Throws<CoordinationException>(() =>
                CoordinationEndpoints.RequireCoordinatorRootDecision(decision, owner)).Code);
        Assert.Equal("checkpoint_recovery_registration_stale",
            Assert.Throws<CoordinationException>(() =>
                CoordinationEndpoints.RequireCoordinatorRootDecision(
                    decision with { State = CoordinatorDecisionState.Create(fence) }, owner, authorization)).Code);
        var staleAuthorization = authorization with
        {
            Status = authorization.Status with { DecisionStateVersion = decisionVersion - 1 }
        };
        Assert.Equal("checkpoint_recovery_registration_stale",
            Assert.Throws<CoordinationException>(() =>
                CoordinationEndpoints.RequireCoordinatorRootDecision(
                    decision, owner, staleAuthorization)).Code);
        Assert.Equal("checkpoint_recovery_registration_stale",
            Assert.Throws<CoordinationException>(() =>
                CoordinationEndpoints.RequireCoordinatorRootDecision(
                    decision, owner, authorization with
                    {
                        Status = authorization.Status with { GateRequestId = "other-gate" }
                    })).Code);
    }

    [Fact]
    public void ChildJoinRecoveryAllowsItsExactDecisionWithoutAPendingGate()
    {
        const long fence = 4;
        const long decisionVersion = 12;
        const string selectionHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var decision = new CoordinatorDecisionCurrentState(
            CoordinatorDecisionState.Create(fence), decisionVersion, selectionHash);
        var owner = new SessionRuntimeOwnerState(
            "root", null, "tenant", selectionHash, fence, 0, 1, "runtime-turn")
        {
            IsCoordinatorRoot = true
        };
        var authorization = new CheckpointRecoveryRegistrationAuthorization(
            Guid.NewGuid(),
            new CheckpointRecoveryStatus(
                "pending", "source-checkpoint", 1, fence - 1, fence, Guid.NewGuid(), 1)
            {
                BoundaryKind = "childJoin",
                DecisionStateVersion = decisionVersion,
                DispatchHeld = true
            });

        CoordinationEndpoints.RequireCoordinatorRootDecision(decision, owner, authorization);
        Assert.Equal("checkpoint_recovery_registration_stale",
            Assert.Throws<CoordinationException>(() =>
                CoordinationEndpoints.RequireCoordinatorRootDecision(
                    decision with { StateVersion = decisionVersion + 1 }, owner, authorization)).Code);
        Assert.Equal("checkpoint_recovery_registration_stale",
            Assert.Throws<CoordinationException>(() =>
                CoordinationEndpoints.RequireCoordinatorRootDecision(
                    decision, owner, authorization with
                    {
                        Status = authorization.Status with { GateRequestId = "unexpected-gate" }
                    })).Code);
    }

    [Fact]
    public void HostRecoveryRequiresTheExactPendingGateAndDecisionVersion()
    {
        const long fence = 9;
        const long decisionVersion = 14;
        const string requestId = "recovery-gate";
        const string actor = "actor";
        var asked = CoordinatorDecisionFlow.AskQuestion(
            CoordinatorDecisionState.Create(fence),
            requestId,
            "scope-question",
            "Choose a repository.",
            actor,
            ["repository-a"],
            allowsFreeform: false);
        Assert.True(asked.IsSuccess);
        var current = new CoordinatorDecisionCurrentState(
            asked.State!, decisionVersion, new string('a', 64));
        var recovery = new CheckpointRecoveryStatus(
            "pending", "source-checkpoint", 1, fence - 1, fence, Guid.NewGuid(), 1)
        {
            DecisionStateVersion = decisionVersion,
            GateRequestId = requestId,
            GateAuthorizedActorId = actor
        };

        CoordinationEndpoints.RequireCheckpointRecoveryGate(current, recovery, fence, actor);

        Assert.Equal("checkpoint_recovery_boundary_stale", Assert.Throws<RuntimeAuthorizationException>(() =>
            CoordinationEndpoints.RequireCheckpointRecoveryGate(
                current with { StateVersion = decisionVersion + 1 }, recovery, fence, actor)).Code);
        Assert.Equal("checkpoint_recovery_boundary_stale", Assert.Throws<RuntimeAuthorizationException>(() =>
            CoordinationEndpoints.RequireCheckpointRecoveryGate(
                current with { State = CoordinatorDecisionState.Create(fence) }, recovery, fence, actor)).Code);
        Assert.Equal("checkpoint_recovery_boundary_stale", Assert.Throws<RuntimeAuthorizationException>(() =>
            CoordinationEndpoints.RequireCheckpointRecoveryGate(current, recovery, fence, "other-actor")).Code);
    }

    [Fact]
    public void ChildJoinNeverUsesRootOnlyContinuation()
    {
        var gateRecovery = new CheckpointRecoveryStatus(
            "pending", "source-checkpoint", 1, 4, 5, Guid.NewGuid(), 1)
        {
            BoundaryKind = "pendingGate"
        };
        var childRecovery = gateRecovery with
        {
            BoundaryKind = "childJoin",
            GateRequestId = "preserved-gate",
            GateAuthorizedActorId = "actor"
        };

        Assert.True(CoordinationEndpoints.CanContinueRootCheckpointRecovery(gateRecovery));
        Assert.True(CoordinationEndpoints.CanContinueRootCheckpointRecovery(gateRecovery with
        {
            BoundaryKind = null
        }));
        Assert.False(CoordinationEndpoints.CanContinueRootCheckpointRecovery(childRecovery));
        Assert.False(CoordinationEndpoints.CanContinueRootCheckpointRecovery(childRecovery with
        {
            Phase = "ready"
        }));
        Assert.False(CoordinationEndpoints.CanContinueRootCheckpointRecovery(gateRecovery with
        {
            BoundaryKind = "unknown"
        }));
    }
}
