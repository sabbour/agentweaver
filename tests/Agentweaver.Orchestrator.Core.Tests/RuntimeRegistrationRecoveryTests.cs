using System.Collections.Immutable;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class RuntimeRegistrationRecoveryTests
{
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
    public void RecoveryRegistrationAllowsOnlyItsPinnedPendingGateVersion()
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
                DecisionStateVersion = decisionVersion
            });

        CoordinationEndpoints.RequireCoordinatorRootDecision(decision, owner, authorization);

        Assert.Equal("runtime_owner_context_unavailable",
            Assert.Throws<CoordinationException>(() =>
                CoordinationEndpoints.RequireCoordinatorRootDecision(decision, owner)).Code);
        var staleAuthorization = authorization with
        {
            Status = authorization.Status with { DecisionStateVersion = decisionVersion - 1 }
        };
        Assert.Equal("checkpoint_recovery_registration_stale",
            Assert.Throws<CoordinationException>(() =>
                CoordinationEndpoints.RequireCoordinatorRootDecision(
                    decision, owner, staleAuthorization)).Code);
    }
}
