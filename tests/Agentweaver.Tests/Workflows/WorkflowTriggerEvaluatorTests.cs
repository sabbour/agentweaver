using FluentAssertions;
using Agentweaver.Api.Workflows;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Unit tests for <see cref="WorkflowTriggerEvaluator"/>: a workflow's declared trigger is matched
/// against how a run was invoked (manual interactive start vs. heartbeat backlog pickup) so the
/// coordinator never selects a workflow whose trigger does not fit the invocation.
/// </summary>
public sealed class WorkflowTriggerEvaluatorTests
{
    private static WorkflowDefinition Workflow(string id, WorkflowTriggerType type, WorkflowEventType? evt = null) => new()
    {
        Id = id,
        Name = id,
        Trigger = new WorkflowTrigger { Type = type, Event = evt },
        Start = "start",
        Nodes = [new WorkflowNode { Id = "start", Type = WorkflowNodeType.Terminal, Label = "start" }],
        Edges = [],
    };

    [Theory]
    [InlineData(WorkflowTriggerType.Manual, true)]
    [InlineData(WorkflowTriggerType.Heartbeat, false)]
    [InlineData(WorkflowTriggerType.Event, false)]
    public void Manual_Invocation_OnlyMatchesManualTrigger(WorkflowTriggerType type, bool expected)
    {
        var evt = type == WorkflowTriggerType.Event ? (WorkflowEventType?)WorkflowEventType.TaskAddedToReady : null;
        var trigger = new WorkflowTrigger { Type = type, Event = evt };

        WorkflowTriggerEvaluator.IsEligible(trigger, WorkflowInvocationKind.Manual).Should().Be(expected);
    }

    [Theory]
    [InlineData(WorkflowTriggerType.Manual, false)]
    [InlineData(WorkflowTriggerType.Heartbeat, true)]
    [InlineData(WorkflowTriggerType.Event, true)]
    public void Heartbeat_Invocation_MatchesHeartbeatAndTaskAddedToReadyEvent(WorkflowTriggerType type, bool expected)
    {
        var evt = type == WorkflowTriggerType.Event ? (WorkflowEventType?)WorkflowEventType.TaskAddedToReady : null;
        var trigger = new WorkflowTrigger { Type = type, Event = evt };

        WorkflowTriggerEvaluator.IsEligible(trigger, WorkflowInvocationKind.Heartbeat).Should().Be(expected);
    }

    [Fact]
    public void Heartbeat_Invocation_EventTriggerWithoutTaskAddedToReady_IsNotEligible()
    {
        // An Event trigger with a null/other event must not match the task-added-to-ready pickup.
        var trigger = new WorkflowTrigger { Type = WorkflowTriggerType.Event, Event = null };

        WorkflowTriggerEvaluator.IsEligible(trigger, WorkflowInvocationKind.Heartbeat).Should().BeFalse();
    }

    [Fact]
    public void Filter_KeepsOnlyMatchingTriggers_PreservingOrder()
    {
        var manual = Workflow("manual-wf", WorkflowTriggerType.Manual);
        var heartbeat = Workflow("heartbeat-wf", WorkflowTriggerType.Heartbeat);
        var evt = Workflow("event-wf", WorkflowTriggerType.Event, WorkflowEventType.TaskAddedToReady);
        var candidates = new[] { manual, heartbeat, evt };

        var manualEligible = WorkflowTriggerEvaluator.Filter(candidates, WorkflowInvocationKind.Manual);
        manualEligible.Should().ContainSingle().Which.Should().BeSameAs(manual);

        var heartbeatEligible = WorkflowTriggerEvaluator.Filter(candidates, WorkflowInvocationKind.Heartbeat);
        heartbeatEligible.Select(w => w.Id).Should().ContainInOrder("heartbeat-wf", "event-wf");
        heartbeatEligible.Should().NotContain(manual);
    }

    [Fact]
    public void Filter_NoMatch_ReturnsEmpty()
    {
        var manual = Workflow("manual-wf", WorkflowTriggerType.Manual);

        WorkflowTriggerEvaluator.Filter([manual], WorkflowInvocationKind.Heartbeat).Should().BeEmpty();
    }
}
