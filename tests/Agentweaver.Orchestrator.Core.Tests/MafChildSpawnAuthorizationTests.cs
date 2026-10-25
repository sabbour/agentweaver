using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafChildSpawnAuthorizationTests
{
    [Fact]
    public void IntentHashBindsIdentityPlanSelectionFenceAndFullWorkItem()
    {
        var intent = Intent();
        var hash = MafChildSpawnAuthorizationContract.ComputeIntentHash(intent);

        Assert.Equal(hash, MafChildSpawnAuthorizationContract.ComputeIntentHash(intent));
        Assert.NotEqual(hash, MafChildSpawnAuthorizationContract.ComputeIntentHash(
            intent with { IntentId = Guid.NewGuid() }));
        Assert.NotEqual(hash, MafChildSpawnAuthorizationContract.ComputeIntentHash(
            intent with { ParentSessionId = "different-parent" }));
        Assert.NotEqual(hash, MafChildSpawnAuthorizationContract.ComputeIntentHash(
            intent with { RootSessionId = "different-root" }));
        Assert.NotEqual(hash, MafChildSpawnAuthorizationContract.ComputeIntentHash(
            intent with { PlanId = "different-plan" }));
        Assert.NotEqual(hash, MafChildSpawnAuthorizationContract.ComputeIntentHash(
            intent with { PlanRevision = intent.PlanRevision + 1 }));
        Assert.NotEqual(hash, MafChildSpawnAuthorizationContract.ComputeIntentHash(
            intent with
            {
                WorkflowStepId = "different-step",
                WorkItem = intent.WorkItem with { WorkflowStepId = "different-step" }
            }));
        Assert.NotEqual(hash, MafChildSpawnAuthorizationContract.ComputeIntentHash(
            intent with { ContextRevision = "context-2" }));
        Assert.NotEqual(hash, MafChildSpawnAuthorizationContract.ComputeIntentHash(
            intent with { ExecutionFence = 8 }));
        Assert.NotEqual(hash, MafChildSpawnAuthorizationContract.ComputeIntentHash(
            intent with { WorkItem = intent.WorkItem with { Title = "different title" } }));
        Assert.NotEqual(hash, MafChildSpawnAuthorizationContract.ComputeIntentHash(
            intent with { WorkItem = intent.WorkItem with { Task = "different task" } }));
        Assert.NotEqual(hash, MafChildSpawnAuthorizationContract.ComputeIntentHash(
            intent with { WorkItem = intent.WorkItem with { DeclaredOutputs = ["src/other.cs"] } }));
    }

    [Theory]
    [InlineData(PolicyEvaluationOutcome.Deny, PolicyEvaluationReasonCode.PlatformRuleDenied)]
    [InlineData(PolicyEvaluationOutcome.Error, PolicyEvaluationReasonCode.EvaluationFailed)]
    public void DenyAndErrorCannotCreateAllowEvidence(
        PolicyEvaluationOutcome outcome,
        PolicyEvaluationReasonCode reasonCode)
    {
        Assert.Throws<ArgumentException>(() =>
            MafChildSpawnAuthorizationContract.CreateEvidence(
                Intent(), Authority(), Guid.NewGuid(), outcome, reasonCode, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void EvidenceRequiresExactIntentAndCurrentAuthority()
    {
        var intent = Intent();
        var authority = Authority();
        var evidence = MafChildSpawnAuthorizationContract.CreateEvidence(
            intent,
            authority,
            Guid.NewGuid(),
            PolicyEvaluationOutcome.Allow,
            PolicyEvaluationReasonCode.Allowed,
            DateTimeOffset.UtcNow);

        Assert.True(MafChildSpawnAuthorizationContract.MatchesCurrent(evidence, intent, authority));
        Assert.False(MafChildSpawnAuthorizationContract.MatchesCurrent(
            evidence,
            intent with
            {
                WorkflowStepId = "different-step",
                WorkItem = intent.WorkItem with { WorkflowStepId = "different-step" }
            },
            authority));
        Assert.False(MafChildSpawnAuthorizationContract.MatchesCurrent(
            evidence, intent with { PlanId = "different-plan" }, authority));
        Assert.False(MafChildSpawnAuthorizationContract.MatchesCurrent(
            evidence, intent with { PlanRevision = intent.PlanRevision + 1 }, authority));
        Assert.False(MafChildSpawnAuthorizationContract.MatchesCurrent(
            evidence, intent with { ExecutionFence = 8 }, authority));
        Assert.False(MafChildSpawnAuthorizationContract.MatchesCurrent(
            evidence, intent with { ContextRevision = "context-2" }, authority));
        Assert.False(MafChildSpawnAuthorizationContract.MatchesCurrent(
            evidence, intent, authority with { ActorId = "different-actor" }));
        Assert.False(MafChildSpawnAuthorizationContract.MatchesCurrent(
            evidence, intent, authority with { ActorIssuer = "https://other.example/" }));
        Assert.False(MafChildSpawnAuthorizationContract.MatchesCurrent(
            evidence, intent, authority with { TenantId = "different-tenant" }));
        Assert.False(MafChildSpawnAuthorizationContract.MatchesCurrent(
            evidence, intent, authority with { AcceptedSelectionHash = new string('b', 64) }));
        Assert.False(MafChildSpawnAuthorizationContract.MatchesCurrent(
            evidence, intent, authority with { MembershipRevision = 3 }));
        Assert.False(MafChildSpawnAuthorizationContract.MatchesCurrent(
            evidence, intent, authority with { RoleRevision = 4 }));
        Assert.False(MafChildSpawnAuthorizationContract.MatchesCurrent(
            evidence, intent, authority with { PolicyOptionsRevision = "policy-2" }));
        Assert.False(MafChildSpawnAuthorizationContract.MatchesCurrent(
            evidence, intent, authority with { PolicyProviderId = "different-policy" }));
        Assert.False(MafChildSpawnAuthorizationContract.MatchesCurrent(
            evidence, intent, authority with { PolicyAdapterVersion = "different-version" }));
    }

    private static MafChildSpawnIntent Intent()
    {
        var item = new WorkPlanItem(
            "work-item-1",
            "implementation",
            "Implement feature",
            "Complete the implementation",
            "developer",
            "agent-1",
            "execute",
            "model-1",
            "sandbox",
            "isolated",
            [],
            []);
        return new MafChildSpawnIntent(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "parent-session",
            "root-session",
            "plan-1",
            4,
            "implementation",
            item.Id,
            "context-1",
            7,
            item);
    }

    private static MafChildSpawnAuthorizationAuthority Authority() => new(
        "project-1",
        "run-1",
        "https://identity.example/",
        "actor-1",
        "tenant-1",
        new string('a', 64),
        "context-1",
        1,
        1,
        1,
        2,
        3,
        AgtPolicyProvider.ProviderId,
        AgtPolicyProvider.AdapterVersion.ToString(),
        AgtPolicyProvider.OptionsSchemaVersion,
        "policy-1");
}
