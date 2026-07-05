using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Workflows;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Feature 015 US5 — unit tests for <see cref="WorkflowSelector"/>: single-workflow pass-through (no
/// LLM call), the multi-workflow LLM path (prompt shape, parse + validate), and the deterministic
/// fallback to the project default on an invalid id or malformed JSON. The LLM is a recording fake
/// so the logic is exercised with no real Copilot dependency.
/// </summary>
public sealed class WorkflowSelectorTests
{
    private sealed class FakeModel : IWorkflowSelectionModel
    {
        private readonly string? _response;
        public int Calls { get; private set; }
        public string? LastPrompt { get; private set; }

        public FakeModel(string? response) => _response = response;

        public Task<string?> CompleteAsync(string prompt, WorkflowSelectionContext context, CancellationToken ct)
        {
            Calls++;
            LastPrompt = prompt;
            return Task.FromResult(_response);
        }
    }

    /// <summary>Returns each queued response in turn (last response repeats once the queue drains).</summary>
    private sealed class SequenceModel : IWorkflowSelectionModel
    {
        private readonly string?[] _responses;
        public int Calls { get; private set; }
        public string? LastPrompt { get; private set; }

        public SequenceModel(params string?[] responses) => _responses = responses;

        public Task<string?> CompleteAsync(string prompt, WorkflowSelectionContext context, CancellationToken ct)
        {
            LastPrompt = prompt;
            var index = Math.Min(Calls, _responses.Length - 1);
            Calls++;
            return Task.FromResult(_responses[index]);
        }
    }

    private static WorkflowDefinition Workflow(string id, string name, string description) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        Start = "start",
        Nodes = [new WorkflowNode { Id = "start", Type = WorkflowNodeType.Terminal, Label = "start" }],
        Edges = [],
    };

    private static WorkflowSelector Selector(IWorkflowSelectionModel model) =>
        new(model, NullLogger<WorkflowSelector>.Instance);

    [Fact]
    public async Task SingleWorkflow_ReturnsIt_WithoutLlmCall()
    {
        var model = new FakeModel(null);
        var only = Workflow("content-authoring", "Content Authoring", "Draft and publish content.");
        var context = new WorkflowSelectionContext("p1", "Write a blog post", ["Writer"], [only]);

        var result = await Selector(model).SelectAsync(context);

        result.Selected.Should().BeSameAs(only);
        result.WasAutoSelected.Should().BeFalse();
        model.Calls.Should().Be(0);
    }

    [Fact]
    public async Task MultiWorkflow_CallsLlm_ParsesAndValidatesSelection()
    {
        var model = new FakeModel("""{"selected": "bug-fix", "rationale": "A one-line null check is a quick fix."}""");
        var delivery = Workflow("software-delivery", "Software Delivery", "Net-new feature delivery pipeline.");
        var bug = Workflow("bug-fix", "Bug Fix", "Fast remediation of a specific defect.");
        var review = Workflow("content-authoring", "Content Authoring", "Draft and publish content.");
        var context = new WorkflowSelectionContext(
            "p1", "Fix the null check in X", ["Implementer", "Reviewer"], [delivery, bug, review]);

        var result = await Selector(model).SelectAsync(context);

        model.Calls.Should().Be(1);
        result.Selected.Should().BeSameAs(bug);
        result.WasAutoSelected.Should().BeTrue();
        result.Rationale.Should().Contain("quick fix");

        // Prompt carries the task, roles, and every candidate's id/name/description.
        model.LastPrompt.Should().Contain("Task: Fix the null check in X");
        model.LastPrompt.Should().Contain("Team roles: Implementer, Reviewer");
        model.LastPrompt.Should().Contain("- bug-fix [built-in/library]: Bug Fix — Fast remediation of a specific defect.");
        model.LastPrompt.Should().Contain("- software-delivery [built-in/library]: Software Delivery —");
        model.LastPrompt.Should().Contain("Match on PROCESS FIT");
        model.LastPrompt.Should().Contain("\"selected\"");
        // Issue #176 reconciliation: output-artifact overlap is not process fit.
        model.LastPrompt.Should().Contain("Producing the same KIND of output artifact");
    }

    [Fact]
    public async Task MultiWorkflow_PromptMarksCustomWorkflowsAndPrefersThemWhenFit()
    {
        var model = new FakeModel("""{"selected": "travel-planner", "rationale": "The custom workflow matches the travel planning process."}""");
        var def = Workflow("default", "Default", "General-purpose agent workflow.");
        var pm = Workflow("pm-discovery", "Product Management Discovery", "Software product discovery and requirements.");
        var travel = Workflow("travel-planner", "Travel Planner", "Research destinations, draft itineraries, review plans, and track bookings.");
        var context = new WorkflowSelectionContext(
            "p1",
            "Plan a family trip to Japan",
            ["Researcher", "Writer"],
            [def, pm, travel],
            new HashSet<string>(["travel-planner"], StringComparer.Ordinal));

        var result = await Selector(model).SelectAsync(context);

        result.Selected.Should().BeSameAs(travel);
        model.LastPrompt.Should().Contain("- travel-planner [project/custom]: Travel Planner —");
        model.LastPrompt.Should().Contain("Prefer project/custom workflows over generic built-in/library workflows");
        model.LastPrompt.Should().Contain("Do NOT select by name similarity");
        model.LastPrompt.Should().Contain("select the first listed workflow (the project default)");
    }

    [Fact]
    public async Task MultiWorkflow_InvalidSelectedId_RetriesThenFallsBackToDefault()
    {
        var model = new FakeModel("""{"selected": "does-not-exist", "rationale": "n/a"}""");
        var def = Workflow("default", "Default", "The general-purpose pipeline.");
        var bug = Workflow("bug-fix", "Bug Fix", "Fast remediation of a specific defect.");
        var context = new WorkflowSelectionContext("p1", "Do something", ["Implementer"], [def, bug]);

        var result = await Selector(model).SelectAsync(context);

        // Unknown id triggers ONE stricter re-prompt before the deterministic fallback.
        model.Calls.Should().Be(2);
        result.Selected.Should().BeSameAs(def);
        result.WasAutoSelected.Should().BeTrue();
    }

    [Fact]
    public async Task MultiWorkflow_MalformedJson_RetriesThenFallsBackToDefault()
    {
        var model = new FakeModel("I think you should use the bug-fix workflow, definitely.");
        var def = Workflow("default", "Default", "The general-purpose pipeline.");
        var bug = Workflow("bug-fix", "Bug Fix", "Fast remediation of a specific defect.");
        var context = new WorkflowSelectionContext("p1", "Do something", ["Implementer"], [def, bug]);

        var result = await Selector(model).SelectAsync(context);

        model.Calls.Should().Be(2);
        result.Selected.Should().BeSameAs(def);
        result.WasAutoSelected.Should().BeTrue();

        // The fallback default is a general-purpose workflow, never a review-only one.
        result.Rationale.Should().Contain("Default");
    }

    [Fact]
    public async Task MultiWorkflow_RetrySucceedsAfterUnparseableFirstReply()
    {
        var model = new SequenceModel(
            "Sure! You probably want bug-fix.",
            """{"selected": "bug-fix", "rationale": "A targeted defect fix."}""");
        var def = Workflow("default", "Default", "The general-purpose pipeline.");
        var bug = Workflow("bug-fix", "Bug Fix", "Fast remediation of a specific defect.");
        var context = new WorkflowSelectionContext("p1", "Fix the bug", ["Implementer"], [def, bug]);

        var result = await Selector(model).SelectAsync(context);

        model.Calls.Should().Be(2);
        result.Selected.Should().BeSameAs(bug);
        result.WasAutoSelected.Should().BeTrue();
        result.Rationale.Should().Contain("defect fix");
        model.LastPrompt.Should().Contain("previous reply could not be parsed");
    }

    [Fact]
    public async Task MultiWorkflow_ParsesJsonWrappedInMarkdownFence()
    {
        var model = new FakeModel(
            "Here is my choice:\n```json\n{\"selected\": \"bug-fix\", \"rationale\": \"Small fix.\"}\n```\nThanks!");
        var def = Workflow("default", "Default", "The general-purpose pipeline.");
        var bug = Workflow("bug-fix", "Bug Fix", "Fast remediation of a specific defect.");
        var context = new WorkflowSelectionContext("p1", "Fix it", ["Implementer"], [def, bug]);

        var result = await Selector(model).SelectAsync(context);

        model.Calls.Should().Be(1);
        result.Selected.Should().BeSameAs(bug);
    }

    [Fact]
    public async Task MultiWorkflow_MatchesIdWithUnderscoresAndDisplayName()
    {
        // Model answers with an underscore variant of the id — normalization folds it to the hyphen id.
        var underscore = new FakeModel("""{"selected": "bug_fix", "rationale": "x"}""");
        var byName = new FakeModel("""{"selected": "Bug Fix", "rationale": "x"}""");
        var def = Workflow("default", "Default", "The general-purpose pipeline.");
        var bug = Workflow("bug-fix", "Bug Fix", "Fast remediation of a specific defect.");
        var context = new WorkflowSelectionContext("p1", "Fix it", ["Implementer"], [def, bug]);

        (await Selector(underscore).SelectAsync(context)).Selected.Should().BeSameAs(bug);
        (await Selector(byName).SelectAsync(context)).Selected.Should().BeSameAs(bug);
    }

    [Fact]
    public async Task MultiWorkflow_AcceptsBareTopLevelJsonString()
    {
        // The model answers with a bare top-level JSON string instead of an object.
        var model = new FakeModel("\"bug-fix\"");
        var def = Workflow("default", "Default", "The general-purpose pipeline.");
        var bug = Workflow("bug-fix", "Bug Fix", "Fast remediation of a specific defect.");
        var context = new WorkflowSelectionContext("p1", "Fix it", ["Implementer"], [def, bug]);

        var result = await Selector(model).SelectAsync(context);

        model.Calls.Should().Be(1);
        result.Selected.Should().BeSameAs(bug);
        result.WasAutoSelected.Should().BeTrue();
    }

    [Fact]
    public async Task MultiWorkflow_AcceptsFencedTopLevelString()
    {
        var model = new FakeModel("```json\n\"bug-fix\"\n```");
        var def = Workflow("default", "Default", "The general-purpose pipeline.");
        var bug = Workflow("bug-fix", "Bug Fix", "Fast remediation of a specific defect.");
        var context = new WorkflowSelectionContext("p1", "Fix it", ["Implementer"], [def, bug]);

        var result = await Selector(model).SelectAsync(context);

        model.Calls.Should().Be(1);
        result.Selected.Should().BeSameAs(bug);
    }

    [Fact]
    public async Task MultiWorkflow_NeverFallsBackToCodeReviewWorkflow()
    {
        // A stale code-review definition lingers first in the list; a parse failure must NOT select it.
        var model = new FakeModel("no json here at all");
        var codeReview = Workflow("code-review", "Code Review", "Review-only pipeline.");
        var def = Workflow("default", "Default", "The general-purpose pipeline.");
        var context = new WorkflowSelectionContext("p1", "Build a thing", ["Implementer"], [codeReview, def]);

        var result = await Selector(model).SelectAsync(context);

        result.Selected.Should().BeSameAs(def);
        result.Selected.Id.Should().NotBe("code-review");
    }

    [Theory]
    [InlineData("use bug-fix", "bug-fix")]
    [InlineData("  USE  software-delivery  ", "software-delivery")]
    [InlineData("use code_review", "code_review")]
    public void TryParseOverride_RecognizesUseCommand(string message, string expectedId)
    {
        WorkflowSelector.TryParseOverride(message, out var id).Should().BeTrue();
        id.Should().Be(expectedId);
    }

    [Theory]
    [InlineData("please use the bug-fix workflow")]
    [InlineData("using bug-fix")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseOverride_RejectsNonCommands(string? message)
    {
        WorkflowSelector.TryParseOverride(message, out _).Should().BeFalse();
    }
}
