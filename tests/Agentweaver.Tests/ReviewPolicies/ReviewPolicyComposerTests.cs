using FluentAssertions;
using Agentweaver.Api.ReviewPolicies;
using Agentweaver.Api.Workflows;

namespace Agentweaver.Tests.ReviewPolicies;

/// <summary>
/// Tests for <see cref="ReviewPolicyComposer"/> (Feature 010, FR-026/028/030/032). The composer is the
/// pure-logic heart of review policies: given a workflow and a named policy it absorbs already-present
/// gates and injects only missing review steps immediately before the merge node. These tests prove the
/// default identity overlay, runtime-safe unbound-gate diagnostics, and structural validity.
/// </summary>
public sealed class ReviewPolicyComposerTests
{
    private static WorkflowDefinition DefaultWorkflow =>
        BuiltInWorkflows.Default.Definition!;

    private static ReviewPolicy DefaultPolicy =>
        BuiltInReviewPolicies.Default.Policy!;

    private static ReviewPolicy PolicyOf(params ReviewStepKind[] kinds) => new()
    {
        Name = "test",
        Steps = kinds.Select(k => new ReviewStep { Kind = k }).ToList(),
    };

    [Fact]
    public void DefaultBuiltInPolicy_IsRaiAndHumanReview_RubberduckIsOptIn()
    {
        // Stage 2 Option B: the safe default mirrors the baked-in RAI + human-review workflow gates.
        var kinds = DefaultPolicy.Steps.Select(s => s.Kind).ToList();
        kinds.Should().Equal(ReviewStepKind.Rai, ReviewStepKind.HumanReview);
        kinds.Should().NotContain(ReviewStepKind.Rubberduck);
    }

    [Fact]
    public void NormalizeLegacyMaterializedDefault_RewritesOnlyUntouchedLegacyDefault()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "stage2-policy-normalization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var legacyPath = Path.Combine(root, "default.yaml");
            File.WriteAllText(legacyPath, DefaultReviewPolicyTemplate.LegacyRubberduckDefaultYaml);

            var normalized = DefaultReviewPolicyTemplate.TryNormalizeLegacyMaterializedDefault(
                legacyPath,
                File.ReadAllText(legacyPath),
                out var yamlToLoad,
                out var error);

            normalized.Should().BeTrue();
            error.Should().BeNull();
            yamlToLoad.Should().Be(DefaultReviewPolicyTemplate.Yaml);
            File.ReadAllText(legacyPath).Should().Be(DefaultReviewPolicyTemplate.Yaml);

            var result = ReviewPolicyLoader.Load(yamlToLoad, "default.yaml");
            result.Policy!.Steps.Select(s => s.Kind).Should().Equal(ReviewStepKind.Rai, ReviewStepKind.HumanReview);

            var customizedDir = Path.Combine(root, "customized");
            Directory.CreateDirectory(customizedDir);
            var customizedPath = Path.Combine(customizedDir, "default.yaml");
            var customized = DefaultReviewPolicyTemplate.LegacyRubberduckDefaultYaml + Environment.NewLine + "# user customization";
            File.WriteAllText(customizedPath, customized);

            DefaultReviewPolicyTemplate.TryNormalizeLegacyMaterializedDefault(
                    customizedPath,
                    File.ReadAllText(customizedPath),
                    out var customizedYaml,
                    out var customizedError)
                .Should().BeFalse();
            customizedError.Should().BeNull();
            customizedYaml.Should().Be(customized);
            File.ReadAllText(customizedPath).Should().Be(customized);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Compose_DefaultPolicy_AbsorbsExistingRaiAndHumanReviewAsIdentity()
    {
        var composition = ReviewPolicyComposer.Compose(DefaultWorkflow, DefaultPolicy);

        composition.AnchorMergeNodeId.Should().Be("merge");
        composition.InjectedNodeIds.Should().BeEmpty();
        composition.AbsorbedStepKinds.Should().Equal(ReviewStepKind.Rai, ReviewStepKind.HumanReview);
        composition.Effective.Should().BeSameAs(DefaultWorkflow);
    }

    [Fact]
    public void ComposeForRuntime_DefaultPolicy_ReturnsIdentityWithoutPolicyHookFailure()
    {
        var composition = ReviewPolicyComposer.ComposeForRuntime(DefaultWorkflow, DefaultPolicy);

        composition.Effective.Should().BeSameAs(DefaultWorkflow);
        composition.InjectedNodeIds.Should().BeEmpty();
    }

    [Fact]
    public void Compose_InjectsMissingRaiThenRubberduckImmediatelyBeforeMerge_ForDefinitionOnly()
    {
        var workflow = DefaultWorkflow with
        {
            Nodes = DefaultWorkflow.Nodes
                .Where(n => n.Id is not "rai" and not "review" and not "terminal-safety-failed" and not "terminal-declined")
                .ToList(),
            Edges =
            [
                new WorkflowEdge { From = "agent", To = "merge" },
                new WorkflowEdge { From = "merge", To = "scribe", When = "merged" },
                new WorkflowEdge { From = "scribe", To = "done" },
            ],
        };

        var composition = ReviewPolicyComposer.Compose(workflow, PolicyOf(ReviewStepKind.Rai, ReviewStepKind.Rubberduck));

        composition.AnchorMergeNodeId.Should().Be("merge");
        composition.InjectedNodeIds.Should().Equal("policy-rai", "policy-rubberduck");

        var effective = composition.Effective;

        // Injected nodes exist and are check gates.
        effective.Nodes.Should().Contain(n => n.Id == "policy-rai" && n.Type == WorkflowNodeType.Check);
        effective.Nodes.Should().Contain(n => n.Id == "policy-rubberduck" && n.Type == WorkflowNodeType.Check);

        // Chain order: rai --pass--> rubberduck --pass--> merge (placed immediately before merge).
        effective.Edges.Should().ContainSingle(e => e.From == "policy-rai" && e.To == "policy-rubberduck" && e.When == "pass");
        effective.Edges.Should().ContainSingle(e => e.From == "policy-rubberduck" && e.To == "merge" && e.When == "pass");

        // Every edge that previously fed merge is re-pointed to the first injected gate; the ONLY
        // remaining incoming edge to merge comes from the last injected step.
        effective.Edges.Where(e => e.To == "merge").Select(e => e.From)
            .Should().OnlyContain(from => from == "policy-rubberduck");
    }

    [Fact]
    public void ComposeForRuntime_AllowsSupportedInjectedRubberduckGate()
    {
        var workflow = DefaultWorkflow with
        {
            Nodes = DefaultWorkflow.Nodes
                .Where(n => n.Id is not "rai" and not "review" and not "terminal-safety-failed" and not "terminal-declined")
                .ToList(),
            Edges =
            [
                new WorkflowEdge { From = "agent", To = "merge" },
                new WorkflowEdge { From = "merge", To = "scribe", When = "merged" },
                new WorkflowEdge { From = "scribe", To = "done" },
            ],
        };

        var composition = ReviewPolicyComposer.ComposeForRuntime(
            workflow,
            PolicyOf(ReviewStepKind.Rai, ReviewStepKind.Rubberduck, ReviewStepKind.HumanReview));

        composition.InjectedNodeIds.Should().Equal("policy-rai", "policy-rubberduck", "policy-human-review");
    }

    [Fact]
    public void Compose_RaiStep_FailsSafeToTerminalAndLoopsToProducer()
    {
        // FR-030: an RAI step preserves content-safety failure on a dedicated stop path.
        var workflowWithoutRai = DefaultWorkflow with
        {
            Nodes = DefaultWorkflow.Nodes
                .Where(n => n.Id is not "rai" and not "terminal-safety-failed")
                .ToList(),
            Edges =
            [
                new WorkflowEdge { From = "agent", To = "review" },
                new WorkflowEdge { From = "review", To = "merge", When = "approved" },
                new WorkflowEdge { From = "review", To = "agent", When = "request-changes" },
                new WorkflowEdge { From = "review", To = "terminal-declined", When = "declined" },
                new WorkflowEdge { From = "merge", To = "scribe", When = "merged" },
                new WorkflowEdge { From = "merge", To = "review", When = "blocked" },
                new WorkflowEdge { From = "scribe", To = "done" },
            ],
        };

        var composition = ReviewPolicyComposer.Compose(workflowWithoutRai, PolicyOf(ReviewStepKind.Rai));
        var effective = composition.Effective;

        var safetyEdge = effective.Edges.Should()
            .ContainSingle(e => e.From == "policy-rai" && e.When == "safety-failed").Subject;
        effective.Nodes.Should().Contain(n => n.Id == safetyEdge.To && n.Type == WorkflowNodeType.Terminal);

        // request-changes loops back to the producer (the workflow start node).
        effective.Edges.Should().ContainSingle(e => e.From == "policy-rai" && e.To == workflowWithoutRai.Start && e.When == "revise");
    }

    [Fact]
    public void Compose_HumanReviewOptIn_PlacedLastGatingMergeOnApproval()
    {
        // FR-029/FR-032: human-review is opt-in; when configured it is the final gate before merge.
        var workflowWithoutHumanReview = DefaultWorkflow with
        {
            Nodes = DefaultWorkflow.Nodes
                .Where(n => n.Id is not "review" and not "terminal-declined")
                .ToList(),
            Edges =
            [
                new WorkflowEdge { From = "agent", To = "rai" },
                new WorkflowEdge { From = "rai", To = "agent", When = "revise" },
                new WorkflowEdge { From = "rai", To = "terminal-safety-failed", When = "safety-failed" },
                new WorkflowEdge { From = "rai", To = "scribe", When = "no-changes" },
                new WorkflowEdge { From = "rai", To = "merge", When = "review" },
                new WorkflowEdge { From = "merge", To = "scribe", When = "merged" },
                new WorkflowEdge { From = "scribe", To = "done" },
            ],
        };
        var policy = PolicyOf(ReviewStepKind.Rai, ReviewStepKind.HumanReview);
        var composition = ReviewPolicyComposer.Compose(workflowWithoutHumanReview, policy);

        composition.InjectedNodeIds.Should().Equal("policy-human-review");

        var effective = composition.Effective;

        // Human review is last: it gates merge on explicit approval.
        effective.Edges.Should().ContainSingle(e => e.From == "policy-human-review" && e.To == "merge" && e.When == "approved");
        effective.Edges.Where(e => e.To == "merge").Select(e => e.From)
            .Should().OnlyContain(from => from == "policy-human-review");

        // Declined routes to an injected terminal.
        var declinedEdge = effective.Edges.Should()
            .ContainSingle(e => e.From == "policy-human-review" && e.When == "declined").Subject;
        effective.Nodes.Should().Contain(n => n.Id == declinedEdge.To && n.Type == WorkflowNodeType.Terminal);
    }

    [Fact]
    public void Compose_NoMergeNode_ReturnsWorkflowUnchanged()
    {
        // No irreversible action to gate: the workflow is returned unchanged with no injection.
        var workflow = new WorkflowDefinition
        {
            Id = "no-merge",
            Name = "No merge",
            Trigger = new WorkflowTrigger { Type = WorkflowTriggerType.Manual },
            Start = "agent",
            Nodes =
            [
                new WorkflowNode { Id = "agent", Type = WorkflowNodeType.Prompt, Label = "Agent" },
                new WorkflowNode { Id = "done", Type = WorkflowNodeType.Terminal, Label = "Done" },
            ],
            Edges = [new WorkflowEdge { From = "agent", To = "done" }],
        };

        var composition = ReviewPolicyComposer.Compose(workflow, DefaultPolicy);

        composition.AnchorMergeNodeId.Should().BeNull();
        composition.InjectedNodeIds.Should().BeEmpty();
        composition.Effective.Should().BeSameAs(workflow);
    }

    [Fact]
    public void Compose_ProducesStructurallyValidGraph()
    {
        var policy = PolicyOf(ReviewStepKind.Rai, ReviewStepKind.HumanReview);
        var effective = ReviewPolicyComposer.Compose(DefaultWorkflow, policy).Effective;

        var nodeIds = effective.Nodes.Select(n => n.Id).ToHashSet();

        // No duplicate node ids.
        effective.Nodes.Select(n => n.Id).Should().OnlyHaveUniqueItems();

        // Every edge references existing nodes (no dangling edges).
        foreach (var edge in effective.Edges)
        {
            nodeIds.Should().Contain(edge.From);
            nodeIds.Should().Contain(edge.To);
        }

        // Every check node declares verdicts and has a matching outgoing edge for each (FR-016 parity).
        foreach (var check in effective.Nodes.Where(n => n.Type == WorkflowNodeType.Check))
        {
            check.Branches.Should().NotBeEmpty();
            var outgoing = effective.Edges.Where(e => e.From == check.Id).Select(e => e.When).ToHashSet();
            foreach (var verdict in check.Branches)
                outgoing.Should().Contain(verdict, $"check '{check.Id}' must route verdict '{verdict}'");
        }
    }
}
