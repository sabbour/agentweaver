using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class RuntimeOwnerContextTests
{
    private const string SelectionHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void FixedRuntimeRequiresTheExactConfirmedAssociation()
    {
        var (plan, context, parent, association) = CreateFixedAssociation();

        Assert.True(CoordinationEndpoints.MatchesFixedRuntimeAssociation(
            association, plan, context, parent, association.ChildSessionId, SelectionHash, 3,
            Checkpoint(plan), CurrentDecision()));
        var copied = association with
        {
            Specification = association.Specification with
            {
                DeclaredOutputs = [.. association.Specification.DeclaredOutputs]
            }
        };
        Assert.True(CoordinationEndpoints.MatchesFixedRuntimeAssociation(
            copied, plan, context, parent, association.ChildSessionId, SelectionHash, 3,
            Checkpoint(plan), CurrentDecision()));
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("fence")]
    [InlineData("decision")]
    [InlineData("plan")]
    [InlineData("workflow")]
    [InlineData("revision")]
    [InlineData("catalog")]
    [InlineData("step")]
    [InlineData("agent")]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("association")]
    [InlineData("child")]
    [InlineData("task")]
    [InlineData("outputs")]
    public void ChangedFixedRuntimeBindingIsRejected(string changed)
    {
        var (plan, context, parent, association) = CreateFixedAssociation();
        var invalid = changed switch
        {
            "selection" => association with { AcceptedSelectionHash = new string('B', 64) },
            "fence" => association with { ExecutionFence = 2 },
            "decision" => association with { DecisionStateVersion = 3 },
            "plan" => association with { WorkPlanId = "another-plan" },
            "workflow" => association with { WorkflowId = "another-workflow" },
            "revision" => association with { DefinitionRevision = "another-revision" },
            "catalog" => association with { CatalogVersion = "another-catalog" },
            "step" => association with { StepId = "another-step" },
            "agent" => association with { AgentId = "agent-2" },
            "model" => association with { ModelSelectionReference = "model:provider/beta" },
            "provider" => association with { IsolationProviderId = "another-provider" },
            "association" => association with { AssociationId = "another-association" },
            "child" => association with { ChildSessionId = "another-child" },
            "task" => association with { Specification = association.Specification with { Task = "Different task." } },
            "outputs" => association with { Specification = association.Specification with { DeclaredOutputs = ["src/extra.cs"] } },
            _ => throw new ArgumentOutOfRangeException(nameof(changed))
        };

        Assert.False(CoordinationEndpoints.MatchesFixedRuntimeAssociation(
            invalid, plan, context, parent, association.ChildSessionId, SelectionHash, 3,
            Checkpoint(plan), CurrentDecision()));
    }

    [Fact]
    public void FixedRuntimeCannotMoveToAnotherParentOrActivation()
    {
        var (plan, context, parent, association) = CreateFixedAssociation();
        var foreignParent = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.MatchesFixedRuntimeAssociation(
                association, plan, context, new SessionIdentity(parent.ProjectId, parent.RunId, "another-root"),
                association.ChildSessionId, SelectionHash, 3, Checkpoint(plan), CurrentDecision()));
        Assert.Equal("maf_fixed_work_child_identity_invalid", foreignParent.Code);
        var activation = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.MatchesFixedRuntimeAssociation(
                association with { ActivationRevision = 2 }, plan, context, parent,
                association.ChildSessionId, SelectionHash, 3, Checkpoint(plan), CurrentDecision()));
        Assert.Equal("maf_fixed_work_child_identity_invalid", activation.Code);
    }

    [Fact]
    public void FixedRuntimeCannotUseAChangedAcceptedModelSelection()
    {
        var (plan, context, parent, association) = CreateFixedAssociation();
        var changed = context with
        {
            Roles = [new RoleRunSelection("engineer", ["agent-1"], ["model:provider/beta"], ["worktree"])]
        };
        Assert.False(CoordinationEndpoints.MatchesFixedRuntimeAssociation(
            association, plan, changed, parent, association.ChildSessionId, SelectionHash, 3,
            Checkpoint(plan), CurrentDecision()));
    }

    [Fact]
    public void FixedRuntimeCannotUseAnUnavailableAcceptedSelection()
    {
        var (plan, context, parent, association) = CreateFixedAssociation();

        Assert.False(CoordinationEndpoints.MatchesFixedRuntimeAssociation(
            association, plan, context with { Roles = [] }, parent,
            association.ChildSessionId, SelectionHash, 3, Checkpoint(plan), CurrentDecision()));
    }

    private static (WorkPlanSnapshot Plan, WorkPlanRunSelectionContext Context,
        SessionIdentity Parent, MafExecutionFixedWorkAssociation Association) CreateFixedAssociation()
    {
        var step = WorkflowTestData.Fixed("setup", 0, [], ["docs/plan.md"]) with
        {
            AllowedRoles = ["engineer"],
            FixedWork = new FixedWorkSpecification(
                "Prepare plan", "Create the prescribed plan.", "engineer", "planning", "worktree", ["docs/plan.md"])
        };
        var context = new WorkPlanRunSelectionContext(
            [new RoleRunSelection("engineer", ["agent-1"], ["model:provider/alpha"], ["worktree"])],
            WorkflowTestData.PinnedSandboxBinding())
        {
            ModelTaskSelections =
            [
                new(Agentweaver.Abstractions.ModelTaskKind.Straightforward, null, null,
                    "model:provider/alpha")
            ]
        };
        var validated = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(WorkflowTestData.Definition(WorkflowDefinitionOrigin.BuiltIn, step)),
            WorkflowTestData.Plan(), context);
        Assert.True(validated.IsValid, string.Join("; ", validated.Issues.Select(issue => issue.Message)));
        var plan = validated.Value!;
        var parent = new SessionIdentity("project-1", "run-1", "root");
        var associationId = MafExecutionPlanner.CreateFixedWorkAssociationId(parent, plan.Plan.Id, step.Id, 1);
        var child = MafExecutionPlanner.CreateChildSessionId(parent, plan.Plan.Id, associationId);
        return (plan, context, parent, MafExecutionPlanner.CreateFixedWorkAssociation(
            plan, context, parent, step, SelectionHash, 3, 4, 1, child));
    }

    private static MafExecutionCheckpoint Checkpoint(WorkPlanSnapshot plan) =>
        new(1, plan.Plan.Id, 4, MafExecutionProgress.Empty);

    private static CoordinatorDecisionCurrentState CurrentDecision() =>
        new(CoordinatorDecisionState.Create(3), 4, SelectionHash);
}
