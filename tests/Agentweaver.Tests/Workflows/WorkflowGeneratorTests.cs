using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Generation;
using Agentweaver.Api.Workflows;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Contracts;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Tests for the LLM workflow generator (Feature 015 US10, FR-056â€“FR-061). Unit tests exercise
/// <see cref="CopilotWorkflowGenerator"/> against a scripted <see cref="IAgentRunner"/> so the
/// prompt â†’ validate â†’ correction-pass pipeline runs without the live model; an integration-style test
/// drives the generate endpoint through a stub generator. Validation reuses the real
/// <see cref="WorkflowDefinitionLoader"/> (Principle VII).
/// </summary>
public sealed class WorkflowGeneratorTests
{
    private const string ValidWorkflowYaml = """
        id: generated-flow
        name: Generated Flow
        description: A generated workflow for tests.
        version: "1.0"
        start: agent
        nodes:
          - id: agent
            type: prompt
            label: Agent
          - id: build-test
            type: build_test
            label: Build & Test
            role: review
            agent: qa-engineer
          - id: human-review
            type: check
            label: Human Review
            role: review
            gate_kind: human-review
            branches:
              - approved
              - request-changes
              - declined
          - id: declined
            type: terminal
            label: Declined
          - id: done
            type: terminal
            label: Done
        edges:
          - from: agent
            to: build-test
          - from: build-test
            to: human-review
            when: approved
          - from: build-test
            to: agent
            when: request-changes
          - from: build-test
            to: declined
            when: declined
          - from: human-review
            to: done
            when: approved
          - from: human-review
            to: agent
            when: request-changes
          - from: human-review
            to: declined
            when: declined
        """;

    // YAML that parses but fails schema validation (no start/nodes) â†’ drives a correction pass.
    private const string InvalidWorkflowYaml = "name: Broken Workflow\n";

    private const string SoftwareWorkflowWithoutHumanReviewYaml = """
        id: software-without-sign-off
        name: Software Without Sign-off
        description: A software workflow missing mandatory human approval.
        version: "1.0"
        start: implement
        nodes:
          - id: implement
            type: prompt
            label: Implement
            role: backend-engineer
            prompt: "Implement the requested feature."
          - id: build-test
            type: build_test
            label: Build & Test
            role: review
            agent: qa-engineer
          - id: declined
            type: terminal
            label: Declined
          - id: done
            type: terminal
            label: Done
        edges:
          - from: implement
            to: build-test
          - from: build-test
            to: done
            when: approved
          - from: build-test
            to: implement
            when: request-changes
          - from: build-test
            to: declined
            when: declined
        """;

    private const string SoftwareWorkflowWithoutBuildTestYaml = """
        id: software-without-build-test
        name: Software Without Build Test
        description: A software workflow missing mandatory build and test validation.
        version: "1.0"
        start: implement
        nodes:
          - id: implement
            type: prompt
            label: Implement
            role: backend-engineer
            prompt: "Implement the requested feature."
          - id: human-review
            type: check
            label: Human Review
            role: review
            gate_kind: human-review
            branches:
              - approved
              - request-changes
              - declined
          - id: declined
            type: terminal
            label: Declined
          - id: done
            type: terminal
            label: Done
        edges:
          - from: implement
            to: human-review
          - from: human-review
            to: done
            when: approved
          - from: human-review
            to: implement
            when: request-changes
          - from: human-review
            to: declined
            when: declined
        """;

    private const string ContentWorkflowWithoutBuildTestYaml = """
        id: newsletter
        name: Newsletter
        description: An editorial workflow.
        version: "1.0"
        start: draft
        nodes:
          - id: draft
            type: prompt
            label: Draft
            role: writer
            prompt: "Write the newsletter."
          - id: done
            type: terminal
            label: Done
        edges:
          - from: draft
            to: done
        """;

    private const string SoftwareWorkflowWithHumanReviewYaml = """
        id: software-with-sign-off
        name: Software With Sign-off
        description: A software workflow with mandatory human approval.
        version: "1.0"
        start: implement
        nodes:
          - id: implement
            type: prompt
            label: Implement
            role: backend-engineer
            prompt: "Implement the requested feature."
          - id: build-test
            type: build_test
            label: Build & Test
            role: review
            agent: qa-engineer
          - id: human-review
            type: check
            label: Human Review
            role: review
            gate_kind: human-review
            branches:
              - approved
              - request-changes
              - declined
          - id: declined
            type: terminal
            label: Declined
          - id: done
            type: terminal
            label: Done
        edges:
          - from: implement
            to: build-test
          - from: build-test
            to: human-review
            when: approved
          - from: build-test
            to: implement
            when: request-changes
          - from: build-test
            to: declined
            when: declined
          - from: human-review
            to: done
            when: approved
          - from: human-review
            to: implement
            when: request-changes
          - from: human-review
            to: declined
            when: declined
        """;

    private const string SoftwareWorkflowWithUnreachableGatesYaml = """
        id: software-with-unreachable-gates
        name: Software With Unreachable Gates
        description: A software workflow with disconnected mandatory gates.
        version: "1.0"
        start: implement
        nodes:
          - id: implement
            type: prompt
            label: Implement
            role: backend-engineer
            prompt: "Implement the requested feature."
          - id: build-test
            type: build_test
            label: Build & Test
            role: review
            agent: qa-engineer
          - id: human-review
            type: check
            label: Human Review
            role: review
            gate_kind: human-review
            branches:
              - approved
              - request-changes
              - declined
          - id: declined
            type: terminal
            label: Declined
          - id: done
            type: terminal
            label: Done
        edges:
          - from: implement
            to: done
          - from: build-test
            to: human-review
            when: approved
          - from: build-test
            to: implement
            when: request-changes
          - from: build-test
            to: declined
            when: declined
          - from: human-review
            to: done
            when: approved
          - from: human-review
            to: implement
            when: request-changes
          - from: human-review
            to: declined
            when: declined
        """;

    private const string SoftwareWorkflowWithRaiBeforeBuildTestYaml = """
        id: software-with-rai-before-build-test
        name: Software With RAI Before Build & Test
        description: A software workflow with the required safety, build, and human review sequence.
        version: "1.0"
        start: implement
        nodes:
          - id: implement
            type: prompt
            label: Implement
            role: backend-engineer
            prompt: "Implement the requested feature."
          - id: rai-safety
            type: check
            label: RAI Safety
            role: review
            gate_kind: rai
            branches:
              - pass
              - revise
          - id: build-test
            type: build_test
            label: Build & Test
            role: review
            agent: qa-engineer
          - id: human-review
            type: check
            label: Human Review
            role: review
            gate_kind: human-review
            branches:
              - approved
              - request-changes
              - declined
          - id: declined
            type: terminal
            label: Declined
          - id: done
            type: terminal
            label: Done
        edges:
          - from: implement
            to: rai-safety
          - from: rai-safety
            to: build-test
            when: pass
          - from: rai-safety
            to: implement
            when: revise
          - from: build-test
            to: human-review
            when: approved
          - from: build-test
            to: implement
            when: request-changes
          - from: build-test
            to: declined
            when: declined
          - from: human-review
            to: done
            when: approved
          - from: human-review
            to: implement
            when: request-changes
          - from: human-review
            to: declined
            when: declined
        """;
    private const string ScheduleTriggerWorkflowYaml = """
        id: monday-triage
        name: Monday Triage
        description: Triage GitHub issues every Monday morning.
        version: "1.0"
        trigger:
          type: schedule
          interval: weekly
          day_of_week: monday
          time_of_day: "09:00"
        start: triage
        nodes:
          - id: triage
            type: prompt
            label: Triage
            role: backend-engineer
            prompt: "Review the latest open issues."
          - id: done
            type: terminal
            label: Done
        edges:
          - from: triage
            to: done
        """;

    private const string EventTriggerWorkflowYaml = """
        id: comment-triage
        name: Comment Triage
        description: Start triage when someone issues the triage command.
        version: "1.0"
        trigger:
          type: event
          event_name: github.issue_comment.created
          if:
            - comment_matches: { pattern: "^/agentweaver:triage$" }
        start: triage
        nodes:
          - id: triage
            type: prompt
            label: Triage
            role: backend-engineer
            prompt: "Triage the referenced issue."
          - id: done
            type: terminal
            label: Done
        edges:
          - from: triage
            to: done
        """;

    private const string PeerReviewApprovalForwardWorkflowYaml = """
        id: weekly-aks-issue-triage
        name: Weekly AKS Issue Triage
        description: Review issue triage findings and produce a report.
        version: "1.0"
        start: triage-issues
        nodes:
          - id: triage-issues
            type: prompt
            label: Triage issues
            role: product-manager
            prompt: "Classify open Azure/AKS issues and draft PRDs."
          - id: review-prds
            type: peer_review
            label: Review PRDs
            role: product-manager
            prompt: "Review the proposed PRDs for completeness."
          - id: triage-report
            type: prompt
            label: Triage report
            role: product-manager
            prompt: "Produce the approved weekly triage report."
          - id: declined
            type: terminal
            label: Declined
          - id: done
            type: terminal
            label: Done
        edges:
          - from: triage-issues
            to: review-prds
          - from: review-prds
            to: triage-report
            when: approved
          - from: review-prds
            to: triage-issues
            when: request-changes
          - from: review-prds
            to: declined
            when: declined
          - from: triage-report
            to: done
        """;

    private const string InvalidTriggerWorkflowYaml = """
        id: broken-trigger
        name: Broken Trigger
        description: Invalid trigger used to force a correction pass.
        version: "1.0"
        trigger:
          type: event
          event_name: github.issue_comment.created
          if:
            - comment_matches: { pattern: "(" }
        start: triage
        nodes:
          - id: triage
            type: prompt
            label: Triage
            role: backend-engineer
            prompt: "Triage the referenced issue."
          - id: done
            type: terminal
            label: Done
        edges:
          - from: triage
            to: done
        """;

    private static CopilotWorkflowGenerator CreateGenerator(
        IAgentRunner runner,
        IDictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Providers:GitHubCopilot:Model"] = "gpt-4o",
        };
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
                values[key] = value;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        return new CopilotWorkflowGenerator(runner, new CatalogReader(), config, NullLogger<CopilotWorkflowGenerator>.Instance);
    }

    [Fact]
    public async Task GenerateAsync_CreatePrompt_ExcludesReservedOrchestrationRoles()
    {
        // Regression for #311: Scribe, Work Monitor, Rai, and Coordinator are platform-owned
        // orchestration roles provisioned automatically for every team. They must never be offered
        // to the workflow generator as an assignable `role`/`agent` for a `prompt`/`peer_review` node.
        var runner = new ScriptedAgentRunner(ValidWorkflowYaml);
        var generator = CreateGenerator(runner);

        await generator.GenerateAsync(new WorkflowGenerationRequest("A simple manual workflow."));

        runner.LastTask.Should().NotBeNull();
        var task = runner.LastTask!;
        var rolesSectionStart = task.IndexOf("Available roles for the", StringComparison.Ordinal);
        rolesSectionStart.Should().BeGreaterThan(-1, "the roles list section must be present in the prompt");
        var rolesSectionEnd = task.IndexOf("BESPOKE ROLES:", rolesSectionStart, StringComparison.Ordinal);
        rolesSectionEnd.Should().BeGreaterThan(rolesSectionStart);
        var rolesSection = task[rolesSectionStart..rolesSectionEnd];

        foreach (var reservedId in new[] { "scribe", "work-monitor", "coordinator", "rai" })
            rolesSection.Should().NotMatchRegex($@"(?im)^- {reservedId}:",
                $"reserved role '{reservedId}' must not appear in the catalog roles offered to the workflow generator");
    }

    [Fact]
    public async Task GenerateAsync_CreatePrompt_StatesStructuralRulesOnce()
    {
        var runner = new ScriptedAgentRunner(ValidWorkflowYaml);
        var generator = CreateGenerator(runner);

        await generator.GenerateAsync(new WorkflowGenerationRequest("A simple manual workflow."));

        var prompt = runner.LastTask!;
        CountOccurrences(prompt, "fan_out").Should().Be(1);
        CountOccurrences(prompt, "merge-and-scribe tail").Should().Be(1);
        CountOccurrences(prompt, "MANDATORY BUILD & TEST STEP").Should().Be(1);
    }

    [Fact]
    public async Task GenerateAsync_UsesGpt54GenerationModelByDefault()
    {
        var runner = new ScriptedAgentRunner(ValidWorkflowYaml);
        var generator = CreateGenerator(runner);

        await generator.GenerateAsync(new WorkflowGenerationRequest("A simple manual workflow."));

        runner.LastModelId.Should().Be(GenerationModelOptions.DefaultModel);
    }

    [Fact]
    public async Task GenerateAsync_UsesConfiguredWorkflowGenerationModel()
    {
        var runner = new ScriptedAgentRunner(ValidWorkflowYaml);
        var generator = CreateGenerator(runner, new Dictionary<string, string?>
        {
            ["Generation:Model"] = "gpt-5.4-mini",
            ["Generation:WorkflowModel"] = "claude-sonnet-4.6",
        });

        await generator.GenerateAsync(new WorkflowGenerationRequest("A simple manual workflow."));

        runner.LastModelId.Should().Be("claude-sonnet-4.6");
    }

    [Fact]
    public async Task GenerateAsync_UsesProjectWorkflowGenerationModelWhenProvided()
    {
        var runner = new ScriptedAgentRunner(ValidWorkflowYaml);
        var generator = CreateGenerator(runner, new Dictionary<string, string?>
        {
            ["Generation:WorkflowModel"] = "claude-sonnet-4.6",
        });

        await generator.GenerateAsync(new WorkflowGenerationRequest(
            "A simple manual workflow.",
            ProjectId: "00000000-0000-0000-0000-000000000712",
            GenerationModel: "gpt-5-mini"));

        runner.LastModelId.Should().Be("gpt-5-mini");
        runner.LastProjectId.Should().Be("00000000-0000-0000-0000-000000000712");
    }


    [Fact]
    public async Task ValidResponse_ReturnsParsedWorkflow_NotCorrected()
    {
        var runner = new ScriptedAgentRunner(ValidWorkflowYaml);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest("A simple manual workflow."));

        result.WasCorrected.Should().BeFalse();
        result.Workflow.Id.Should().Be("generated-flow");
        result.Workflow.Nodes.Should().HaveCount(5);
        result.GeneratedYaml.Should().Contain("id: generated-flow");
        runner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ValidResponseWithMarkdownFences_IsCleanedAndParsed()
    {
        var fenced = "```yaml\n" + ValidWorkflowYaml + "\n```";
        var runner = new ScriptedAgentRunner(fenced);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest("A simple manual workflow."));

        result.WasCorrected.Should().BeFalse();
        result.GeneratedYaml.Should().NotContain("```");
        result.Workflow.Id.Should().Be("generated-flow");
    }

    [Fact]
    public async Task PeerReviewApprovalThenAgentTurn_GeneratesRunnableWorkflow()
    {
        var runner = new ScriptedAgentRunner(PeerReviewApprovalForwardWorkflowYaml);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest(
            "Weekly AKS issue triage: classify open issues, group duplicate feature requests, identify gaps not already documented, write PRDs, name features, review PRDs, and produce a triage report without writing back to Azure/AKS unless explicitly approved.",
            TargetRepository: "Azure/AKS",
            ContentOnly: true));

        result.WasCorrected.Should().BeFalse();
        result.Workflow.Id.Should().Be("weekly-aks-issue-triage");
        RunWorkflowGraphBinder.GetBindabilityErrors(result.Workflow).Should().BeEmpty();
        runner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task PeerReviewStart_TriggersCorrectionPassBeforeWorkflowIsReturned()
    {
        var invalidStart = PeerReviewApprovalForwardWorkflowYaml.Replace(
            "start: triage-issues", "start: review-prds", StringComparison.Ordinal);
        var runner = new ScriptedAgentRunner(invalidStart, PeerReviewApprovalForwardWorkflowYaml);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest(
            "Generate an issue-triage workflow with a review and a final report.",
            TargetRepository: "Azure/AKS",
            ContentOnly: true));

        result.WasCorrected.Should().BeTrue();
        result.Workflow.Id.Should().Be("weekly-aks-issue-triage");
        runner.CallCount.Should().Be(2);
        runner.LastTask.Should().Contain("Cannot bind start node 'review-prds'");
        runner.LastTask.Should().Contain("Choose a prompt or publish node as start");
    }

    [Fact]
    public async Task InvalidThenValid_TriggersCorrectionPass_ReturnsCorrected()
    {
        var runner = new ScriptedAgentRunner(InvalidWorkflowYaml, ValidWorkflowYaml);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest("A workflow that needs fixing."));

        result.WasCorrected.Should().BeTrue();
        result.Workflow.Id.Should().Be("generated-flow");
        runner.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task SoftwareWorkflowWithoutHumanSignOff_TriggersCorrectionPass()
    {
        var runner = new ScriptedAgentRunner(
            SoftwareWorkflowWithoutHumanReviewYaml,
            SoftwareWorkflowWithHumanReviewYaml);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest(
            "Implement a software feature with automated tests.",
            TeamRoles: ["backend-engineer"]));

        result.WasCorrected.Should().BeTrue();
        runner.CallCount.Should().Be(2);
        var humanReview = result.Workflow.Nodes.Should().ContainSingle(node =>
            node.Type == WorkflowNodeType.Check &&
            string.Equals(node.GateKind, "human-review", StringComparison.OrdinalIgnoreCase)).Subject;
        result.Workflow.Edges.Should().Contain(edge =>
            edge.From == "build-test" && edge.To == humanReview.Id && edge.When == "approved");
    }

    [Fact]
    public async Task SoftwareWorkflowWithoutBuildTest_TriggersCorrectionPass()
    {
        var runner = new ScriptedAgentRunner(
            SoftwareWorkflowWithoutBuildTestYaml,
            SoftwareWorkflowWithHumanReviewYaml);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest(
            "Create a compiler utility.",
            TeamRoles: ["core-implementer"]));

        result.WasCorrected.Should().BeTrue();
        runner.CallCount.Should().Be(2);
        result.Workflow.Nodes.Should().ContainSingle(node => node.Type == WorkflowNodeType.BuildTest);
    }

    [Fact]
    public async Task ContentAddWorkflowWithoutBuildTest_IsReturnedWithoutCorrection()
    {
        var runner = new ScriptedAgentRunner(ContentWorkflowWithoutBuildTestYaml);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest(
            "Add a section to the newsletter.",
            ContentOnly: true));

        result.WasCorrected.Should().BeFalse();
        runner.CallCount.Should().Be(1);
        result.Workflow.Nodes.Should().NotContain(node => node.Type == WorkflowNodeType.BuildTest);
    }

    [Fact]
    public async Task SoftwareWorkflowWithUnreachableGates_TriggersCorrectionPass()
    {
        var runner = new ScriptedAgentRunner(
            SoftwareWorkflowWithUnreachableGatesYaml,
            SoftwareWorkflowWithHumanReviewYaml);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest(
            "Implement the requested feature.",
            TeamRoles: ["backend-engineer"]));

        result.WasCorrected.Should().BeTrue();
        runner.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task SoftwareWorkflowWithRaiBeforeBuildTest_IsReturnedWithoutCorrection()
    {
        var runner = new ScriptedAgentRunner(SoftwareWorkflowWithRaiBeforeBuildTestYaml);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest(
            "Implement a software feature with safety review.",
            TeamRoles: ["backend-engineer"]));

        result.WasCorrected.Should().BeFalse();
        runner.CallCount.Should().Be(1);
        RunWorkflowGraphBinder.GetBindabilityErrors(result.Workflow).Should().BeEmpty();
    }

    [Fact]
    public async Task SoftwareWorkflowWithRaiAfterHumanReview_TriggersCorrectionPass()
    {
        var raiAfterHumanReview = SoftwareWorkflowWithRaiBeforeBuildTestYaml.Replace(
            """
              - id: rai-safety
            """,
            """
              - id: after-human-review
                type: prompt
                label: Continue After Human Review
                role: backend-engineer
                prompt: "Continue the implementation after review."
              - id: rai-safety
            """,
            StringComparison.Ordinal).Replace(
            """
              - from: implement
                to: rai-safety
            """,
            """
              - from: implement
                to: human-review
            """,
            StringComparison.Ordinal).Replace(
            """
              - from: rai-safety
                to: build-test
                when: pass
            """,
            """
              - from: after-human-review
                to: rai-safety
              - from: rai-safety
                to: build-test
                when: pass
            """,
            StringComparison.Ordinal).Replace(
            """
              - from: human-review
                to: done
                when: approved
            """,
            """
              - from: human-review
                to: after-human-review
                when: approved
            """,
            StringComparison.Ordinal);
        var runner = new ScriptedAgentRunner(raiAfterHumanReview, SoftwareWorkflowWithRaiBeforeBuildTestYaml);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest(
            "Implement a software feature with safety review.",
            TeamRoles: ["backend-engineer"]));

        result.WasCorrected.Should().BeTrue();
        runner.CallCount.Should().Be(2);
        runner.LastTask.Should().Contain("must not route its approved verdict to an agent");
    }

    [Fact]
    public async Task SoftwareWorkflowWithRaiBypass_TriggersCorrectionPass()
    {
        var raiBypass = SoftwareWorkflowWithRaiBeforeBuildTestYaml.Replace(
            """
              - from: implement
                to: rai-safety
            """,
            """
              - from: implement
                to: rai-safety
              - from: implement
                to: build-test
            """,
            StringComparison.Ordinal);
        var runner = new ScriptedAgentRunner(raiBypass, SoftwareWorkflowWithRaiBeforeBuildTestYaml);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest(
            "Implement a software feature with safety review.",
            TeamRoles: ["backend-engineer"]));

        result.WasCorrected.Should().BeTrue();
        runner.CallCount.Should().Be(2);
        runner.LastTask.Should().Contain("must not be reachable before every reachable RAI safety gate");
    }
    [Fact]
    public async Task SoftwareWorkflowWithDuplicateBuildTests_TriggersCorrectionPass()
    {
        var duplicateBuildTest = SoftwareWorkflowWithHumanReviewYaml.Replace(
            """
              - id: human-review
            """,
            """
              - id: build-test-duplicate
                type: build_test
                label: Build & Test Again
                role: review
                agent: qa-engineer
              - id: human-review
            """,
            StringComparison.Ordinal).Replace(
            """
              - from: build-test
                to: human-review
                when: approved
            """,
            """
              - from: build-test
                to: human-review
                when: approved
              - from: build-test-duplicate
                to: human-review
                when: approved
            """,
            StringComparison.Ordinal);
        var runner = new ScriptedAgentRunner(duplicateBuildTest, SoftwareWorkflowWithHumanReviewYaml);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest(
            "Implement a software feature with automated tests.",
            TeamRoles: ["backend-engineer"]));

        result.WasCorrected.Should().BeTrue();
        runner.CallCount.Should().Be(2);
        result.Workflow.Nodes.Should().ContainSingle(node => node.Type == WorkflowNodeType.BuildTest);
    }

    [Fact]
    public async Task BothPassesInvalid_ThrowsWorkflowGenerationException()
    {
        var runner = new ScriptedAgentRunner(InvalidWorkflowYaml, InvalidWorkflowYaml);
        var generator = CreateGenerator(runner);

        var act = () => generator.GenerateAsync(new WorkflowGenerationRequest("An unfixable description."));

        await act.Should().ThrowAsync<WorkflowGenerationException>();
        runner.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task PromptIncludesTargetRepositoryContext_FromExplicitTarget()
    {
        var runner = new ScriptedAgentRunner(ValidWorkflowYaml);
        var generator = CreateGenerator(runner);

        await generator.GenerateAsync(new WorkflowGenerationRequest(
            "Every Monday: triage GitHub issues",
            TargetRepository: "Azure/aks"));

        runner.LastTask.Should().Contain("<<<TARGET_REPOSITORY>>>");
        runner.LastTask.Should().Contain("Azure/aks");
    }

    [Fact]
    public async Task ScheduleTriggerResponse_ReturnsParsedScheduleTrigger()
    {
        var runner = new ScriptedAgentRunner(ScheduleTriggerWorkflowYaml);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest("Run this every Monday at 9am UTC.", ContentOnly: true));

        result.Workflow.Trigger.Should().NotBeNull();
        result.Workflow.Trigger!.Type.Should().Be(WorkflowTriggerType.Schedule);
        result.Workflow.Trigger.Interval.Should().Be(WorkflowScheduleInterval.Weekly);
        result.Workflow.Trigger.DayOfWeek.Should().Be(DayOfWeek.Monday);
        result.Workflow.Trigger.TimeOfDay.Should().Be(new TimeOnly(9, 0));
    }

    [Fact]
    public async Task EventTriggerResponse_ReturnsParsedEventTriggerPredicate()
    {
        var runner = new ScriptedAgentRunner(EventTriggerWorkflowYaml);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest("Whenever someone comments /agentweaver:triage, run triage.", ContentOnly: true));

        result.Workflow.Trigger.Should().NotBeNull();
        result.Workflow.Trigger!.Type.Should().Be(WorkflowTriggerType.Event);
        result.Workflow.Trigger.EventName.Should().Be("github.issue_comment.created");
        result.Workflow.Trigger.If.Should().ContainSingle();
        result.Workflow.Trigger.If[0].CommentMatches.Should().NotBeNull();
        result.Workflow.Trigger.If[0].CommentMatches!.Pattern.Should().Be("^/agentweaver:triage$");
    }

    [Fact]
    public async Task InvalidTriggerThenValid_TriggersCorrectionPass()
    {
        var runner = new ScriptedAgentRunner(InvalidTriggerWorkflowYaml, EventTriggerWorkflowYaml);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest("Whenever someone comments /agentweaver:triage, run triage.", ContentOnly: true));

        result.WasCorrected.Should().BeTrue();
        result.Workflow.Trigger.Should().NotBeNull();
        result.Workflow.Trigger!.If[0].CommentMatches!.Pattern.Should().Be("^/agentweaver:triage$");
        runner.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task EditModePrompt_PreservesBaseWorkflowAndRequiresCustomizedCopyForBuiltIns()
    {
        var runner = new ScriptedAgentRunner(ValidWorkflowYaml.Replace("id: generated-flow", "id: custom-default"));
        var generator = CreateGenerator(runner);

        await generator.GenerateAsync(new WorkflowGenerationRequest(
            "Update the existing build and test gate placement.",
            BaseWorkflowId: "default",
            BaseWorkflowYaml: DefaultWorkflowTemplate.Yaml,
            BaseWorkflowIsBuiltIn: true));

        runner.LastTask.Should().Contain("EDIT MODE");
        runner.LastTask.Should().Contain("Return a DRAFT preview only");
        runner.LastTask.Should().Contain("Apply ONLY the requested natural-language change");
        runner.LastTask.Should().Contain("built-in/library and immutable");
        runner.LastTask.Should().Contain("MUST fork it into a project-owned customized copy");
        runner.LastTask.Should().Contain("BASE WORKFLOW YAML");
        runner.LastTask.Should().Contain("SELF-CHECK BEFORE RETURNING");
        runner.LastTask.Should().Contain("MANDATORY BUILD & TEST STEP (software workflows)");
    }

    [Fact]
    public async Task EditModePrompt_StatesSharedGatePlacementOnce()
    {
        var runner = new ScriptedAgentRunner(ValidWorkflowYaml.Replace("id: generated-flow", "id: custom-default"));
        var generator = CreateGenerator(runner);

        await generator.GenerateAsync(new WorkflowGenerationRequest(
            "Update the existing build and test gate placement.",
            BaseWorkflowId: "default",
            BaseWorkflowYaml: DefaultWorkflowTemplate.Yaml,
            BaseWorkflowIsBuiltIn: true));

        var prompt = runner.LastTask!;
        CountOccurrences(prompt, "MANDATORY BUILD & TEST STEP").Should().Be(1);
        CountOccurrences(prompt, "immediately after any RAI safety check").Should().Be(1);
    }

    [Fact]
    public async Task EditModeBuiltInReturningSameId_TriggersCorrectionPass()
    {
        var invalidSameId = ValidWorkflowYaml.Replace("id: generated-flow", "id: default");
        var correctedCopy = ValidWorkflowYaml.Replace("id: generated-flow", "id: default-custom");
        var runner = new ScriptedAgentRunner(invalidSameId, correctedCopy);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest(
            "Update the QA gate.",
            BaseWorkflowId: "default",
            BaseWorkflowYaml: DefaultWorkflowTemplate.Yaml,
            BaseWorkflowIsBuiltIn: true));

        result.WasCorrected.Should().BeTrue();
        result.Workflow.Id.Should().Be("default-custom");
        runner.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task PromptExtractsTargetRepositoryContext_FromDescriptionUrl()
    {
        var runner = new ScriptedAgentRunner(ValidWorkflowYaml);
        var generator = CreateGenerator(runner);

        await generator.GenerateAsync(new WorkflowGenerationRequest(
            "Every Monday: triage github issues (http://github.com/Azure/aks/issues)"));

        runner.LastTask.Should().Contain("Azure/aks");
    }

    [Fact]
    public async Task MissingId_IsDerivedFromDescriptionSlug()
    {
        // Same valid workflow body but without an `id:` line; the generator injects a slug from the
        // description (FR â€” id generation).
        var noId = """
            name: No Id Flow
            description: A workflow with no id.
            version: "1.0"
            start: agent
            nodes:
              - id: agent
                type: prompt
                label: Agent
              - id: scribe
                type: scribe
                label: Scribe
              - id: done
                type: terminal
                label: Done
            edges:
              - from: agent
                to: scribe
              - from: scribe
                to: done
            """;
        var runner = new ScriptedAgentRunner(noId);
        var generator = CreateGenerator(runner);

        var result = await generator.GenerateAsync(new WorkflowGenerationRequest("Review and Merge PRs", ContentOnly: true));

        result.Workflow.Id.Should().Be("review-and-merge-prs");
    }

    [Fact]
    public async Task EmptyDescription_Throws()
    {
        var generator = CreateGenerator(new ScriptedAgentRunner(ValidWorkflowYaml));
        var act = () => generator.GenerateAsync(new WorkflowGenerationRequest("   "));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static int CountOccurrences(string value, string text) =>
        value.Split(text, StringSplitOptions.None).Length - 1;

    // â”€â”€ Endpoint integration (stub generator) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task GenerateEndpoint_WithConfirmedTeam_ExcludesReservedRolesFromTeamRoles()
    {
        // Regression for #311: CastingService.ConfirmProposalAsync writes the built-in orchestration
        // agents (Scribe, Ralph/Work Monitor, Rai, Coordinator) into every confirmed team.md, with
        // role ids "scribe", "work-monitor", "rai-reviewer", "coordinator". TryReadTeamRoles must strip
        // those before they reach the workflow generator as assignable roles.
        await using var factory = new StubWorkflowGeneratorFactory();
        var client = factory.CreateAuthenticatedClient();

        var dir = factory.NewWorkingDirectory();
        var create = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"WfGen ReservedRoles Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var projectId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;

        await factory.PrepareAiExecutionAsync(client, "casting_generation", projectId);
        var propose = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/casting/proposals",
            new { mode = "scenario", template_id = "quick-software-development" });
        propose.StatusCode.Should().Be(HttpStatusCode.OK, await propose.Content.ReadAsStringAsync());
        var proposalId = (await propose.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("proposal_id").GetString()!;

        var confirm = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/casting/proposals/{proposalId}/confirm", new { });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK, await confirm.Content.ReadAsStringAsync());

        await factory.PrepareAiExecutionAsync(client, "workflow_generation", projectId);
        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/workflows/generate",
            new { description = "A manual review-and-merge workflow." });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        var generator = factory.Services.GetRequiredService<IWorkflowGenerator>()
            .Should().BeOfType<StubWorkflowGenerator>().Subject;
        generator.LastRequest.Should().NotBeNull();
        generator.LastRequest!.TeamRoles.Should().NotBeNull();
        generator.LastRequest.TeamRoles.Should().NotContain(
            new[] { "scribe", "work-monitor", "ralph", "rai", "rai-reviewer", "coordinator" });
    }

    [Fact]
    public async Task GenerateEndpoint_Returns200_WithYamlAndWorkflowId()
    {
        await using var factory = new StubWorkflowGeneratorFactory();
        var client = factory.CreateAuthenticatedClient();

        var dir = factory.NewWorkingDirectory();
        var create = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"WfGen Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var projectId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;

        await factory.PrepareAiExecutionAsync(client, "workflow_generation", projectId);
        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/workflows/generate",
            new { description = "A manual review-and-merge workflow." });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("yaml").GetString().Should().Contain("id: generated-flow");
        body.GetProperty("workflowId").GetString().Should().Be("generated-flow");
        body.GetProperty("wasCorrected").GetBoolean().Should().BeFalse();

        var generator = factory.Services.GetRequiredService<IWorkflowGenerator>()
            .Should().BeOfType<StubWorkflowGenerator>().Subject;
        generator.CallCount.Should().Be(1);
        generator.LastRequest.Should().NotBeNull();
        generator.LastRequest!.Description.Should().Be("A manual review-and-merge workflow.");
        generator.LastRequest.GenerationModel.Should().Be(GenerationModelOptions.DefaultModel);
    }

    [Fact]
    public async Task GenerateEndpoint_UsesProjectWorkflowGenerationModel()
    {
        await using var factory = new StubWorkflowGeneratorFactory();
        var client = factory.CreateAuthenticatedClient();

        var dir = factory.NewWorkingDirectory();
        var create = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"WfGen Model Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var projectId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;

        var update = await client.PutAsJsonAsync(
            $"/api/projects/{projectId}/provider-settings",
            new { workflow_generation_model = "gpt-5-mini" });
        update.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await factory.PrepareAiExecutionAsync(client, "workflow_generation", projectId);
        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/workflows/generate",
            new { description = "A manual review workflow." });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var generator = factory.Services.GetRequiredService<IWorkflowGenerator>()
            .Should().BeOfType<StubWorkflowGenerator>().Subject;
        generator.LastRequest.Should().NotBeNull();
        generator.LastRequest!.GenerationModel.Should().Be("gpt-5-mini");
    }

    [Fact]
    public async Task GenerateEndpoint_WithBaseWorkflowId_ReturnsEditDraftWithoutSaving()
    {
        await using var factory = new StubWorkflowGeneratorFactory();
        var client = factory.CreateAuthenticatedClient();

        var dir = factory.NewWorkingDirectory();
        var create = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"WfEdit Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var projectId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;

        await factory.PrepareAiExecutionAsync(client, "workflow_generation", projectId);
        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/workflows/generate",
            new { description = "Add a QA gate.", base_workflow_id = "default" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("mode").GetString().Should().Be("edit");
        body.GetProperty("base_workflow_id").GetString().Should().Be("default");
        body.GetProperty("base_workflow_is_built_in").GetBoolean().Should().BeTrue();
        body.GetProperty("workflowId").GetString().Should().Be("generated-flow");

        var workflowsDir = Path.Combine(dir, ".agentweaver", "workflows");
        File.Exists(Path.Combine(workflowsDir, "generated-flow.yaml")).Should().BeFalse(
            "generation returns a preview draft and must not save it");

        var generator = factory.Services.GetRequiredService<IWorkflowGenerator>()
            .Should().BeOfType<StubWorkflowGenerator>().Subject;
        generator.LastRequest.Should().NotBeNull();
        generator.LastRequest!.IsEdit.Should().BeTrue();
        generator.LastRequest.BaseWorkflowId.Should().Be("default");
        generator.LastRequest.BaseWorkflowIsBuiltIn.Should().BeTrue();
        generator.LastRequest.BaseWorkflowYaml.Should().Contain("id: default");
    }

    [Fact]
    public async Task GenerateEndpoint_WithBaseYaml_SupportsIterativeDraftEditing()
    {
        await using var factory = new StubWorkflowGeneratorFactory();
        var client = factory.CreateAuthenticatedClient();

        var dir = factory.NewWorkingDirectory();
        var create = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"WfDraftEdit Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var projectId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;

        await factory.PrepareAiExecutionAsync(client, "workflow_generation", projectId);
        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/workflows/generate",
            new { description = "Rename the first step.", base_yaml = ValidWorkflowYaml });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var generator = factory.Services.GetRequiredService<IWorkflowGenerator>()
            .Should().BeOfType<StubWorkflowGenerator>().Subject;
        generator.LastRequest.Should().NotBeNull();
        generator.LastRequest!.IsEdit.Should().BeTrue();
        generator.LastRequest.BaseWorkflowId.Should().Be("generated-flow");
        generator.LastRequest.BaseWorkflowYaml.Should().Be(ValidWorkflowYaml);
    }

    [Fact]
    public async Task GenerateEndpoint_MissingDescription_Returns400()
    {
        await using var factory = new StubWorkflowGeneratorFactory();
        var client = factory.CreateAuthenticatedClient();

        var dir = factory.NewWorkingDirectory();
        var create = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"WfGen Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        var projectId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;

        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/workflows/generate", new { description = "" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("description");
    }

    /// <summary>Scripted <see cref="IAgentRunner"/>: returns a queued response per call so the
    /// generator's first/correction passes are deterministic.</summary>
    private sealed class ScriptedAgentRunner : IAgentRunner
    {
        private readonly Queue<string> _responses;
        public int CallCount { get; private set; }
        public string? LastTask { get; private set; }
        public string? LastModelId { get; private set; }
        public string? LastProjectId { get; private set; }
        public ModelSource? LastModelSource { get; private set; }
        public CopilotOperationCapability? LastCopilotCapability { get; private set; }

        public ScriptedAgentRunner(params string[] responses) => _responses = new Queue<string>(responses);

        public Task<string> ExecuteAsync(
            string task, string workingDirectory, string repositoryPath, ModelSource modelSource,
            string runId, string? modelId, ChannelWriter<RunEvent>? stream, CancellationToken ct,
            string? systemPromptContext = null, string? userId = null)
        {
            CallCount++;
            LastTask = task;
            LastModelId = modelId;
            LastModelSource = modelSource;
            var next = _responses.Count > 0 ? _responses.Dequeue() : string.Empty;
            return Task.FromResult(next);
        }

        public Task<string> ExecuteForProjectAsync(
            string task,
            string workingDirectory,
            string repositoryPath,
            ModelSource modelSource,
            string runId,
            string? modelId,
            ChannelWriter<RunEvent>? stream,
            CancellationToken ct,
            string? systemPromptContext = null,
            string? userId = null,
            string? projectId = null,
            CopilotOperationCapability? copilotCapability = null,
            ByokProviderConfiguration? byokProviderConfiguration = null,
            IModelInvocationGuard? modelInvocationGuard = null)
        {
            LastProjectId = projectId;
            LastCopilotCapability = copilotCapability;
            return ExecuteAsync(
                task, workingDirectory, repositoryPath, modelSource, runId, modelId, stream, ct,
                systemPromptContext, userId);
        }
    }

    /// <summary>Stub <see cref="IWorkflowGenerator"/>: returns a fixed valid draft so the endpoint's
    /// HTTP/auth/serialization path is exercised without the model.</summary>
    private sealed class StubWorkflowGenerator : IWorkflowGenerator
    {
        public int CallCount { get; private set; }
        public WorkflowGenerationRequest? LastRequest { get; private set; }

        public Task<WorkflowGenerationResult> GenerateAsync(WorkflowGenerationRequest request, CancellationToken ct = default)
        {
            CallCount++;
            LastRequest = request;
            var loaded = WorkflowDefinitionLoader.Load(ValidWorkflowYaml, "stub");
            return Task.FromResult(new WorkflowGenerationResult(loaded.Definition!, ValidWorkflowYaml, WasCorrected: false));
        }
    }

    /// <summary>Project test factory that swaps in the stub generator for the generate endpoint test.</summary>
    private sealed class StubWorkflowGeneratorFactory : ApiWebApplicationFactory
    {
        public const string TestApiKey = "wfgen-test-api-key-77001";
        public const string TestUser = "wfgen-test-user";

        public StubWorkflowGeneratorFactory()
            : base("agentweaver-wfgen", createWorkspaceRoot: true)
        {
        }

        public HttpClient CreateAuthenticatedClient()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestApiKey);
            return client;
        }

        public async Task PrepareAiExecutionAsync(HttpClient client, string operation, string projectId)
        {
            await using (var scope = Services.CreateAsyncScope())
            {
                var settings = scope.ServiceProvider.GetRequiredService<ByokProviderConfigurationService>();
                var provider = await settings.GetAsync(CancellationToken.None)
                    ?? await settings.AddAsync(
                        new ByokProviderConfiguration(
                            string.Empty,
                            "Workflow test provider",
                            "openai",
                            "https://api.example.test/v1",
                            "gpt-5",
                            "test-key"),
                        CancellationToken.None);
                await settings.SetActiveAsync(provider.Id, CancellationToken.None);
            }

            var response = await client.PostAsJsonAsync(
                "/api/ai/execution-context",
                new { operation, project_id = projectId });
            response.EnsureSuccessStatusCode();
            var context = await response.Content.ReadFromJsonAsync<AiExecutionContextResponse>()
                ?? throw new InvalidOperationException("AI execution context response was empty.");
            var providerKey = context.ExecutionKey
                ?? throw new InvalidOperationException("AI execution context did not return a provider key.");
            client.DefaultRequestHeaders.Remove(AiExecutionPlanHeaders.ProviderKey);
            client.DefaultRequestHeaders.Add(AiExecutionPlanHeaders.ProviderKey, providerKey);
        }

        public string NewWorkingDirectory() => CreateWorkspaceDirectory();

        protected override void ConfigureTestConfiguration(IDictionary<string, string?> configuration)
        {
            configuration["Auth:ApiKey"] = TestApiKey;
            configuration["Auth:User"] = TestUser;
            configuration["Auth:GitHub:ClientId"] = "test-github-client-id";
            configuration["Auth:GitHub:BaseUrl"] = "https://github.com";
        }

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            RemoveService<Agentweaver.Api.Git.ProjectGitInitializer>(services);
            services.AddSingleton<Agentweaver.Api.Git.ProjectGitInitializer, NoOpProjectGitInitializer>();

            RemoveService<IWorkflowGenerator>(services);
            services.AddSingleton<IWorkflowGenerator, StubWorkflowGenerator>();
        }
    }
}
