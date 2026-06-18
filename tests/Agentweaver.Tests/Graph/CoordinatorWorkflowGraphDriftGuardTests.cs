using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Runs.Graph;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Graph;

/// <summary>
/// Build-time DRIFT GUARD for the dynamic workflow graph descriptor. This is the ONLY place
/// reflection over the MAF workflow is used: it builds both pipeline variants, reflects the real
/// executors/edges (<see cref="Workflow.ReflectExecutors"/> / <see cref="Workflow.ReflectEdges"/>)
/// and asserts the self-describing descriptor stays in sync with what is actually wired. Any future
/// change to <c>RunWorkflowFactory.BuildWorkflow</c> that adds an executor without metadata, or
/// drops a logical node, fails here instead of silently shipping a stale/incorrect graph.
///
/// Class name carries "Coordinator" so it runs under the coordinator-filtered suite.
/// </summary>
public sealed class CoordinatorWorkflowGraphDriftGuardTests : IClassFixture<CoordinatorWebApplicationFactory>
{
    private readonly CoordinatorWebApplicationFactory _factory;

    public CoordinatorWorkflowGraphDriftGuardTests(CoordinatorWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private RunWorkflowFactory Factory =>
        _factory.Services.GetRequiredService<RunWorkflowFactory>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryReflectedExecutor_IsRepresentedOrExplicitlyHiddenOrKnownFallback(bool isChild)
    {
        var (wf, descriptor) = Factory.BuildWorkflowForTest(isChild);
        var nodeIds = descriptor.Nodes.Select(n => n.Id).ToHashSet();
        var executors = wf.ReflectExecutors();

        foreach (var (id, binding) in executors)
        {
            if (binding.RawValue is IWorkflowNodeMeta meta)
            {
                if (meta.Hidden)
                    continue; // plumbing: intentionally dropped from the descriptor.

                nodeIds.Should().Contain(meta.LogicalNodeId,
                    $"reflected executor '{id}' (logical '{meta.LogicalNodeId}') must be a descriptor node — " +
                    "BuildWorkflow drifted from the graph descriptor");
            }
            else
            {
                // The only non-self-describing node allowed is the framework RequestPort review gate.
                id.Should().Be(GraphDescriptorBuilder.ReviewGatePortId,
                    $"reflected executor '{id}' has no IWorkflowNodeMeta and is not the known review-gate fallback");
            }
        }
    }

    [Fact]
    public void FullVariant_EveryDescriptorNode_MapsToRealExecutorOrKnownPort()
    {
        var (wf, descriptor) = Factory.BuildWorkflowForTest(isChild: false);
        var executors = wf.ReflectExecutors();
        var ports = wf.ReflectPorts();

        foreach (var node in descriptor.Nodes)
        {
            var backedByExecutor = executors.Values.Any(b =>
                b.RawValue is IWorkflowNodeMeta m && !m.Hidden && m.LogicalNodeId == node.Id);

            var backedByKnownPort = node.Id == "review"
                && (ports.ContainsKey(GraphDescriptorBuilder.ReviewGatePortId)
                    || executors.ContainsKey(GraphDescriptorBuilder.ReviewGatePortId));

            (backedByExecutor || backedByKnownPort).Should().BeTrue(
                $"descriptor node '{node.Id}' must correspond to a real wired executor (or the known review-gate port)");
        }
    }

    [Fact]
    public void FullVariant_ReviewGateFallback_IsActuallyPresent()
    {
        // Pin the single allowed id->label fallback so it can never silently disappear.
        var (wf, descriptor) = Factory.BuildWorkflowForTest(isChild: false);
        var ports = wf.ReflectPorts();
        var executors = wf.ReflectExecutors();

        (ports.ContainsKey(GraphDescriptorBuilder.ReviewGatePortId)
            || executors.ContainsKey(GraphDescriptorBuilder.ReviewGatePortId))
            .Should().BeTrue("the review gate must be wired as the 'review-gate' RequestPort");

        descriptor.Nodes.Should().ContainSingle(n => n.Id == "review");
    }

    [Fact]
    public void ReflectedEdges_AreNonEmpty_ForBothVariants()
    {
        // Sanity: the reflected MAF graph has edges (so the drift comparison is meaningful).
        var (full, _) = Factory.BuildWorkflowForTest(isChild: false);
        var (child, _) = Factory.BuildWorkflowForTest(isChild: true);

        Dictionary<string, HashSet<EdgeInfo>> fullEdges = full.ReflectEdges();
        Dictionary<string, HashSet<EdgeInfo>> childEdges = child.ReflectEdges();

        fullEdges.Should().NotBeEmpty();
        childEdges.Should().NotBeEmpty();
    }
}
