using Agentweaver.Api.Workflows;

namespace Agentweaver.Api.ReviewPolicies;

/// <summary>
/// The result of composing a <see cref="ReviewPolicy"/> onto a <see cref="WorkflowDefinition"/>.
/// </summary>
public sealed record WorkflowComposition
{
    /// <summary>The transformed workflow with the policy's review steps injected pre-merge.</summary>
    public required WorkflowDefinition Effective { get; init; }

    /// <summary>The ids of the injected review-step nodes, in injection order (excludes injected
    /// terminals). Empty when the workflow has no merge node to gate.</summary>
    public required IReadOnlyList<string> InjectedNodeIds { get; init; }

    /// <summary>The id of the merge node the steps were injected before, or null when none exists.</summary>
    public string? AnchorMergeNodeId { get; init; }
}

/// <summary>
/// Pure composition transform that injects a project's review policy into a workflow at the implicit
/// pre-merge review point (Feature 010, FR-026/028). Given a workflow and a policy it produces an
/// EFFECTIVE workflow whose merge node is gated by the policy's review steps placed immediately before
/// it, in declared order. This is a deterministic graph transform only — it does NOT rewire the live
/// runtime executor (out of scope); its correctness is proven by unit tests over placement and ordering.
///
/// Mapping (FR-026): an RAI step becomes a content-safety gate that fails safe on a content-safety RED
/// (FR-030); a Rubber-duck step becomes a request-changes-to-producer review loop; a Human-review step
/// becomes an approval gate that gates merge on explicit human approval (FR-029). The producer that
/// request-changes loops back to is the workflow's start node.
///
/// The produced workflow is itself a valid <see cref="WorkflowDefinition"/> (it round-trips through the
/// workflow validator): every injected check declares its verdicts and has a matching outgoing edge for
/// each, and every injected edge references an existing node.
/// </summary>
public static class ReviewPolicyComposer
{
    private const string PassVerdict = "pass";
    private const string ReviseVerdict = "revise";
    private const string ApprovedVerdict = "approved";
    private const string DeclinedVerdict = "declined";
    private const string SafetyFailedVerdict = "safety-failed";

    /// <summary>
    /// Injects <paramref name="policy"/>'s review steps into <paramref name="workflow"/> immediately
    /// before the merge node. When the workflow has no merge node there is no irreversible action to
    /// gate, so the workflow is returned unchanged with an empty injection set.
    /// </summary>
    public static WorkflowComposition Compose(WorkflowDefinition workflow, ReviewPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(policy);

        var mergeNode = workflow.Nodes.FirstOrDefault(n => n.Type == WorkflowNodeType.Merge);
        if (mergeNode is null || policy.Steps.Count == 0)
        {
            return new WorkflowComposition
            {
                Effective = workflow,
                InjectedNodeIds = [],
                AnchorMergeNodeId = mergeNode?.Id,
            };
        }

        var existingIds = new HashSet<string>(workflow.Nodes.Select(n => n.Id), StringComparer.Ordinal);
        var producerId = workflow.Start;

        var injectedSteps = new List<WorkflowNode>(policy.Steps.Count);
        var injectedStepIds = new List<string>(policy.Steps.Count);
        var injectedEdges = new List<WorkflowEdge>();
        string? safetyTerminalId = null;
        string? declinedTerminalId = null;

        // 1) Create one gate node per policy step, preserving declared order.
        foreach (var step in policy.Steps)
        {
            var id = UniqueId($"policy-{KindSlug(step.Kind)}", existingIds);
            existingIds.Add(id);
            injectedStepIds.Add(id);
            injectedSteps.Add(new WorkflowNode
            {
                Id = id,
                Type = WorkflowNodeType.Check,
                Label = step.Label ?? DefaultLabel(step.Kind),
                Role = "review",
                Kind = "gate",
                Branches = BranchesFor(step.Kind),
            });
        }

        // 2) Wire each gate to the next gate (or the merge node for the last), plus its loop/terminal edges.
        for (var i = 0; i < policy.Steps.Count; i++)
        {
            var step = policy.Steps[i];
            var nodeId = injectedStepIds[i];
            var forwardTarget = i + 1 < injectedStepIds.Count ? injectedStepIds[i + 1] : mergeNode.Id;

            switch (step.Kind)
            {
                case ReviewStepKind.Rai:
                    safetyTerminalId ??= UniqueId("policy-terminal-safety-failed", existingIds);
                    if (!existingIds.Contains(safetyTerminalId)) existingIds.Add(safetyTerminalId);
                    injectedEdges.Add(new WorkflowEdge { From = nodeId, To = forwardTarget, When = PassVerdict });
                    injectedEdges.Add(new WorkflowEdge { From = nodeId, To = producerId, When = ReviseVerdict });
                    injectedEdges.Add(new WorkflowEdge { From = nodeId, To = safetyTerminalId, When = SafetyFailedVerdict });
                    break;

                case ReviewStepKind.Rubberduck:
                    injectedEdges.Add(new WorkflowEdge { From = nodeId, To = forwardTarget, When = PassVerdict });
                    injectedEdges.Add(new WorkflowEdge { From = nodeId, To = producerId, When = ReviseVerdict });
                    break;

                case ReviewStepKind.HumanReview:
                    declinedTerminalId ??= UniqueId("policy-terminal-declined", existingIds);
                    if (!existingIds.Contains(declinedTerminalId)) existingIds.Add(declinedTerminalId);
                    injectedEdges.Add(new WorkflowEdge { From = nodeId, To = forwardTarget, When = ApprovedVerdict });
                    injectedEdges.Add(new WorkflowEdge { From = nodeId, To = producerId, When = ReviseVerdict });
                    injectedEdges.Add(new WorkflowEdge { From = nodeId, To = declinedTerminalId, When = DeclinedVerdict });
                    break;
            }
        }

        // 3) Re-point every edge that fed the merge node so it now enters the first injected gate; the
        //    review chain leads back into the merge node on pass/approved. Merge's outgoing edges are
        //    untouched, so the rest of the run is preserved.
        var firstGateId = injectedStepIds[0];
        var rewrittenEdges = workflow.Edges
            .Select(e => string.Equals(e.To, mergeNode.Id, StringComparison.Ordinal)
                ? e with { To = firstGateId }
                : e)
            .ToList();

        var terminals = new List<WorkflowNode>();
        if (safetyTerminalId is not null)
            terminals.Add(new WorkflowNode { Id = safetyTerminalId, Type = WorkflowNodeType.Terminal, Label = "Safety failed", Role = "plumbing", Kind = "terminal" });
        if (declinedTerminalId is not null)
            terminals.Add(new WorkflowNode { Id = declinedTerminalId, Type = WorkflowNodeType.Terminal, Label = "Declined", Role = "plumbing", Kind = "terminal" });

        var effective = workflow with
        {
            Nodes = [.. workflow.Nodes, .. injectedSteps, .. terminals],
            Edges = [.. rewrittenEdges, .. injectedEdges],
        };

        return new WorkflowComposition
        {
            Effective = effective,
            InjectedNodeIds = injectedStepIds,
            AnchorMergeNodeId = mergeNode.Id,
        };
    }

    private static IReadOnlyList<string> BranchesFor(ReviewStepKind kind) => kind switch
    {
        ReviewStepKind.Rai => [PassVerdict, ReviseVerdict, SafetyFailedVerdict],
        ReviewStepKind.Rubberduck => [PassVerdict, ReviseVerdict],
        ReviewStepKind.HumanReview => [ApprovedVerdict, ReviseVerdict, DeclinedVerdict],
        _ => [PassVerdict],
    };

    private static string KindSlug(ReviewStepKind kind) => kind switch
    {
        ReviewStepKind.Rai => "rai",
        ReviewStepKind.Rubberduck => "rubberduck",
        ReviewStepKind.HumanReview => "human-review",
        _ => "review",
    };

    private static string DefaultLabel(ReviewStepKind kind) => kind switch
    {
        ReviewStepKind.Rai => "RAI review",
        ReviewStepKind.Rubberduck => "Rubber-duck review",
        ReviewStepKind.HumanReview => "Human review",
        _ => "Review",
    };

    private static string UniqueId(string preferred, HashSet<string> taken)
    {
        if (!taken.Contains(preferred)) return preferred;
        for (var i = 2; ; i++)
        {
            var candidate = $"{preferred}-{i}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}
