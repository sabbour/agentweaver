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

    /// <summary>The policy step kinds that were absorbed by existing workflow gates.</summary>
    public IReadOnlyList<ReviewStepKind> AbsorbedStepKinds { get; init; } = [];

    /// <summary>The policy step kinds that were injected as new nodes.</summary>
    public IReadOnlyList<ReviewStepKind> InjectedStepKinds { get; init; } = [];
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
                AbsorbedStepKinds = [],
                InjectedStepKinds = [],
            };
        }

        var existingIds = new HashSet<string>(workflow.Nodes.Select(n => n.Id), StringComparer.Ordinal);
        var producerId = workflow.Start;
        var preMergeGateKinds = FindPreMergeGateKinds(workflow, mergeNode.Id);
        var absorbedKinds = new List<ReviewStepKind>();
        var stepsToInject = new List<ReviewStep>();

        foreach (var step in policy.Steps)
        {
            if (preMergeGateKinds.Contains(step.Kind))
            {
                absorbedKinds.Add(step.Kind);
                continue;
            }

            stepsToInject.Add(step);
            preMergeGateKinds.Add(step.Kind);
        }

        if (stepsToInject.Count == 0)
        {
            return new WorkflowComposition
            {
                Effective = workflow,
                InjectedNodeIds = [],
                AnchorMergeNodeId = mergeNode.Id,
                AbsorbedStepKinds = absorbedKinds,
                InjectedStepKinds = [],
            };
        }

        var injectedSteps = new List<WorkflowNode>(stepsToInject.Count);
        var injectedStepIds = new List<string>(stepsToInject.Count);
        var injectedEdges = new List<WorkflowEdge>();
        string? safetyTerminalId = null;
        string? declinedTerminalId = null;

        // 1) Create one gate node per policy step, preserving declared order.
        foreach (var step in stepsToInject)
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
                GateKind = KindSlug(step.Kind),
                Branches = BranchesFor(step.Kind),
            });
        }

        // 2) Wire each gate to the next gate (or the merge node for the last), plus its loop/terminal edges.
        for (var i = 0; i < stepsToInject.Count; i++)
        {
            var step = stepsToInject[i];
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
            AbsorbedStepKinds = absorbedKinds,
            InjectedStepKinds = stepsToInject.Select(s => s.Kind).ToList(),
        };
    }

    /// <summary>
    /// Runtime-safe composition for the current MAF binder. Every supported injected kind has a live
    /// executor binding in Stage 2; any future/unknown kind fails here with policy context instead of
    /// becoming an unbound workflow node.
    /// </summary>
    public static WorkflowComposition ComposeForRuntime(WorkflowDefinition workflow, ReviewPolicy policy)
    {
        var composition = Compose(workflow, policy);
        var unsupported = composition.InjectedStepKinds
            .Where(k => k is not ReviewStepKind.Rai and not ReviewStepKind.Rubberduck and not ReviewStepKind.HumanReview)
            .ToList();
        if (unsupported.Count > 0)
        {
            throw new ReviewPolicyCompositionException(
                "review_policy_unsupported_gate",
                $"Active review policy '{policy.Name}' cannot be composed into live runtime workflow '{workflow.Id}': " +
                $"unsupported review step kind(s): {string.Join(", ", unsupported.Select(KindSlug))}.");
        }

        return composition;
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

    private static HashSet<ReviewStepKind> FindPreMergeGateKinds(WorkflowDefinition workflow, string mergeNodeId)
    {
        var canReachMerge = NodesThatCanReach(workflow, mergeNodeId);
        return workflow.Nodes
            .Where(n => n.Type == WorkflowNodeType.Check && canReachMerge.Contains(n.Id))
            .Select(TryGetGateKind)
            .Where(k => k.HasValue)
            .Select(k => k!.Value)
            .ToHashSet();
    }

    private static HashSet<string> NodesThatCanReach(WorkflowDefinition workflow, string targetNodeId)
    {
        var reverse = workflow.Nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in workflow.Edges)
        {
            if (!reverse.ContainsKey(edge.To))
                reverse[edge.To] = [];
            reverse[edge.To].Add(edge.From);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(targetNodeId);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node)) continue;
            if (!reverse.TryGetValue(node, out var prev)) continue;
            foreach (var p in prev) stack.Push(p);
        }
        return seen;
    }

    private static ReviewStepKind? TryGetGateKind(WorkflowNode node)
    {
        var raw = !string.IsNullOrWhiteSpace(node.GateKind) ? node.GateKind! : node.Id;
        return Normalize(raw) switch
        {
            "rai" => ReviewStepKind.Rai,
            "review" or "human_review" => ReviewStepKind.HumanReview,
            "rubberduck" or "rubber_duck" => ReviewStepKind.Rubberduck,
            _ => null,
        };
    }

    private static string Normalize(string raw) =>
        raw.Trim().Replace('-', '_').Replace(' ', '_').ToLowerInvariant();
}
