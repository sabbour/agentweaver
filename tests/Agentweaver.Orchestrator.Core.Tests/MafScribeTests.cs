using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafScribeTests
{
    private static readonly SessionIdentity Root = new("project-1", "run-1", "root");
    private static readonly CoordinationActor Actor = new("https://identity.test/", "owner");

    [Fact]
    public void OpenResultBelongsToAcceptedProducerAndPreservesCanonicalFullOutput()
    {
        var (plan, state) = OpenResult("Full output\n\"quoted\" result with provenance.");
        var intent = MafScribeContract.CreateIntent(Root, plan, state, "scribe", "first", Actor);
        Assert.Equal("agent-1", intent.AgentId);
        using var content = JsonDocument.Parse(intent.Content);
        Assert.Equal(state.Results["first"], content.RootElement.GetProperty("result").GetString());
        Assert.Equal(Root.ProjectId, content.RootElement.GetProperty("projectId").GetString());
        Assert.Equal(Root.RunId, content.RootElement.GetProperty("runId").GetString());
        Assert.Equal("first", content.RootElement.GetProperty("associationId").GetString());
        Assert.Equal(MafExecutionIds.CreateChildSessionId(Root, plan.Plan.Id, "first"),
            content.RootElement.GetProperty("childSessionId").GetString());
        Assert.Equal(intent, MafScribeContract.CreateIntent(Root, plan, state, "scribe", "first", Actor));
    }

    [Fact]
    public void FixedResultBelongsToPersistedProducerNotScribe()
    {
        var fixedStep = WorkflowTestData.Fixed("setup", 0, [], ["docs/plan.md"]) with
        {
            AllowedRoles = ["engineer"],
            FixedWork = new("Prepare plan", "Create the plan.", "engineer", "planning", "worktree",
                ["docs/plan.md"])
        };
        var context = WorkflowTestData.SelectionContext();
        var plan = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(WorkflowTestData.Definition(
                WorkflowDefinitionOrigin.BuiltIn, fixedStep,
                WorkflowTestData.Platform("scribe", 1, ["setup"], WorkflowPlatformGate.Scribe))),
            WorkflowTestData.Plan(), context).Value!;
        Assert.NotNull(plan);
        var associationId = MafExecutionIds.CreateFixedWorkAssociationId(Root, plan.Plan.Id, "setup", 1);
        var association = MafExecutionPlanner.CreateFixedWorkAssociation(
            plan, context, Root, fixedStep, new string('a', 64), 1, 1, 1,
            MafExecutionIds.CreateChildSessionId(Root, plan.Plan.Id, associationId));
        var state = new MafExecutionCheckpoint(1, plan.Plan.Id, 1, MafExecutionProgress.Empty with
        {
            FixedWorkItems = MafExecutionProgress.Empty.FixedWorkItems.Add(
                associationId, MafExecutionTaskStatus.Succeeded)
        })
        {
            FixedWorkAssociations = ImmutableDictionary<string, MafExecutionFixedWorkAssociation>.Empty
                .Add(associationId, association),
            Results = ImmutableDictionary<string, string>.Empty
                .Add(associationId, "Completed fixed output.")
        };
        var intent = MafScribeContract.CreateIntent(Root, plan, state, "scribe", associationId, Actor);
        Assert.Equal(association.AgentId, intent.AgentId);
        Assert.NotEqual("scribe", intent.AgentId);
        Assert.Throws<CoordinationException>(() => MafScribeContract.CreateIntent(
            Root, plan, state with
            {
                FixedWorkAssociations = state.FixedWorkAssociations.SetItem(
                    associationId, association with { ChildSessionId = "wrong-child" })
            }, "scribe", associationId, Actor));
    }

    [Fact]
    public void SerializedLimitIsExactAndOutputIsNeverTruncated()
    {
        var (plan, initial) = OpenResult("x");
        var baseline = MafScribeContract.CreateIntent(Root, plan, initial, "scribe", "first", Actor);
        var result = new string('x', MafScribeContract.MaximumContentLength - baseline.Content.Length + 1);
        var state = initial with { Results = initial.Results.SetItem("first", result) };
        var exact = MafScribeContract.CreateIntent(Root, plan, state, "scribe", "first", Actor);
        Assert.Equal(40_000, exact.Content.Length);
        using var content = JsonDocument.Parse(exact.Content);
        Assert.Equal(result, content.RootElement.GetProperty("result").GetString());
        var failure = Assert.Throws<CoordinationException>(() => MafScribeContract.CreateIntent(
            Root, plan, state with { Results = state.Results.SetItem("first", result + "x") },
            "scribe", "first", Actor));
        Assert.Equal("maf_execution_scribe_payload_limit_exceeded", failure.Code);
        Assert.Throws<CoordinationException>(() => MafScribeContract.CreateIntent(
            Root, plan, state with { Results = state.Results.SetItem("first", result + "\u0001") },
            "scribe", "first", Actor));
    }

    [Fact]
    public void ReplayBindsExactActorPayloadProjectRunPlanAndAssociation()
    {
        var (plan, state) = OpenResult("Original result.");
        var intent = MafScribeContract.CreateIntent(Root, plan, state, "scribe", "first", Actor);
        var restored = JsonSerializer.Deserialize<MafExecutionScribeIntent>(JsonSerializer.Serialize(intent))!;
        MafScribeContract.RequireReplay(restored, intent, Actor);
        var otherActor = Actor with { Subject = "other-owner" };
        var changedActor = MafScribeContract.CreateIntent(Root, plan, state, "scribe", "first", otherActor);
        Assert.Equal(intent.IdempotencyKey, changedActor.IdempotencyKey);
        Assert.Throws<CoordinationException>(() =>
            MafScribeContract.RequireReplay(restored, changedActor, otherActor));
        var changedResult = MafScribeContract.CreateIntent(Root, plan,
            state with { Results = state.Results.SetItem("first", "Changed result.") }, "scribe", "first", Actor);
        Assert.Equal(intent.IdempotencyKey, changedResult.IdempotencyKey);
        Assert.Throws<CoordinationException>(() => MafScribeContract.RequireReplay(restored, changedResult, Actor));
        foreach (var changed in new[]
                 {
                     intent with { Identity = new("other-project", Root.RunId, Root.SessionId) },
                     intent with { Identity = new(Root.ProjectId, "other-run", Root.SessionId) },
                     intent with { WorkPlanId = "other-plan" },
                     intent with { AssociationId = "other-child" }
                 })
            Assert.Throws<CoordinationException>(() => MafScribeContract.RequireReplay(restored, changed, Actor));
    }

    [Fact]
    public void OnlyCompletedChildResultsCanBecomeKnowledgeIntents()
    {
        var (plan, state) = OpenResult("Result.");
        Assert.Throws<CoordinationException>(() => MafScribeContract.CreateIntent(
            Root, plan, state, "implement", "first", Actor));
        Assert.Throws<CoordinationException>(() => MafScribeContract.CreateIntent(
            Root, plan, state, "scribe", "unknown", Actor));
        Assert.Throws<CoordinationException>(() => MafScribeContract.CreateIntent(
            Root, plan, state with { Progress = MafExecutionProgress.Empty }, "scribe", "first", Actor));
    }

    private static (WorkPlanSnapshot Plan, MafExecutionCheckpoint State) OpenResult(string result)
    {
        var plan = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(WorkflowTestData.Definition(
                WorkflowDefinitionOrigin.BuiltIn, WorkflowTestData.Open("implement", 0, []),
                WorkflowTestData.Platform("scribe", 1, ["implement"], WorkflowPlatformGate.Scribe))),
            WorkflowTestData.Plan(WorkflowTestData.Item("first")),
            WorkflowTestData.SelectionContext()).Value!;
        Assert.NotNull(plan);
        return (plan, new MafExecutionCheckpoint(1, plan.Plan.Id, 1, MafExecutionProgress.Empty with
        {
            WorkItems = MafExecutionProgress.Empty.WorkItems.Add("first", MafExecutionTaskStatus.Succeeded)
        })
        {
            Results = ImmutableDictionary<string, string>.Empty.Add("first", result)
        });
    }
}
