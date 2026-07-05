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
        // Response has no JSON and no verbatim workflow id -> TryParse and last-resort both
        // fail -> two attempts -> deterministic fallback.
        var model = new FakeModel("I think you should just go with something simple, definitely.");
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
        // First response has no JSON and no verbatim workflow id (so last-resort also fails),
        // forcing a retry. The second response is valid JSON -> succeeds on attempt 2.
        var model = new SequenceModel(
            "The task seems complex and I need to think more carefully about the options.",
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

    // --- Parse hardening tests (issue #183) ---

    [Fact]
    public async Task MultiWorkflow_ThinkWrappedJson_ParsesCorrectly()
    {
        // The think block contains {braces} that would fool ExtractFirstJsonObject without
        // think-block stripping: it would grab "{bug-fix, software-delivery}" (invalid JSON)
        // and miss the actual answer, causing two failures and a fallback to default.
        var response =
            "<think>\n" +
            "Candidates: {bug-fix, software-delivery}. The task is a defect fix, so bug-fix fits.\n" +
            "</think>\n" +
            "{\"selected\": \"bug-fix\", \"rationale\": \"Targeted defect remediation.\"}";

        var model = new FakeModel(response);
        var def = Workflow("software-delivery", "Software Delivery", "Net-new feature pipeline.");
        var bug = Workflow("bug-fix", "Bug Fix", "Fast remediation of a specific defect.");
        var context = new WorkflowSelectionContext("p1", "Fix the null check", ["Implementer"], [def, bug]);

        var result = await Selector(model).SelectAsync(context);

        // Should resolve on the first attempt — no fallback.
        model.Calls.Should().Be(1);
        result.Selected.Should().BeSameAs(bug);
        result.WasAutoSelected.Should().BeTrue();
        result.Rationale.Should().Contain("defect");
    }

    [Fact]
    public async Task MultiWorkflow_ThinkingTagWrappedJson_ParsesCorrectly()
    {
        // Same as above but with <thinking> tags (alternate reasoning block name).
        var response =
            "<thinking>\n" +
            "Options: {software-delivery, bug-fix}. Null-check fix -> bug-fix.\n" +
            "</thinking>\n" +
            "{\"selected\": \"bug-fix\", \"rationale\": \"Null-check is a defect fix.\"}";

        var model = new FakeModel(response);
        var def = Workflow("software-delivery", "Software Delivery", "Net-new feature pipeline.");
        var bug = Workflow("bug-fix", "Bug Fix", "Fast remediation of a specific defect.");
        var context = new WorkflowSelectionContext("p1", "Fix the null check", ["Implementer"], [def, bug]);

        var result = await Selector(model).SelectAsync(context);

        model.Calls.Should().Be(1);
        result.Selected.Should().BeSameAs(bug);
    }

    [Fact]
    public async Task MultiWorkflow_BareWorkflowIdInProse_ResolvesViaLastResort()
    {
        // The model responds with plain prose and no JSON at all. TryParse returns false on both
        // attempts without the last-resort path. With it, the sole verbatim id in the response
        // is identified and selected on the first attempt.
        var model = new FakeModel("I recommend using the bug-fix workflow for this task.");
        var def = Workflow("software-delivery", "Software Delivery", "Net-new feature pipeline.");
        var bug = Workflow("bug-fix", "Bug Fix", "Fast remediation of a specific defect.");
        var context = new WorkflowSelectionContext("p1", "Fix the null check", ["Implementer"], [def, bug]);

        var result = await Selector(model).SelectAsync(context);

        // Last-resort fires on attempt 1 — only 1 model call needed.
        model.Calls.Should().Be(1);
        result.Selected.Should().BeSameAs(bug);
        result.WasAutoSelected.Should().BeTrue();
    }

    [Fact]
    public async Task MultiWorkflow_BareWorkflowIdWithUnderscores_ResolvesViaLastResort()
    {
        // Model uses underscore variant of the id (common LLM hallucination).
        var model = new FakeModel("The best choice is bug_fix for a targeted defect fix.");
        var def = Workflow("software-delivery", "Software Delivery", "Net-new feature pipeline.");
        var bug = Workflow("bug-fix", "Bug Fix", "Fast remediation of a specific defect.");
        var context = new WorkflowSelectionContext("p1", "Fix it", ["Implementer"], [def, bug]);

        var result = await Selector(model).SelectAsync(context);

        model.Calls.Should().Be(1);
        result.Selected.Should().BeSameAs(bug);
    }

    [Fact]
    public async Task MultiWorkflow_AmbiguousProseMultipleIds_RetriesThenFallsBack()
    {
        // Both workflow ids appear in the response — ambiguous, last-resort must NOT guess.
        var model = new FakeModel("Could be bug-fix or software-delivery, hard to say.");
        var def = Workflow("software-delivery", "Software Delivery", "Net-new feature pipeline.");
        var bug = Workflow("bug-fix", "Bug Fix", "Fast remediation of a specific defect.");
        var context = new WorkflowSelectionContext("p1", "Do something", ["Implementer"], [def, bug]);

        var result = await Selector(model).SelectAsync(context);

        // Both attempts fail (no JSON, ambiguous last-resort) -> deterministic fallback.
        model.Calls.Should().Be(2);
        result.Selected.Id.Should().Be("software-delivery"); // first in list -> default
    }

    [Fact]
    public async Task MultiWorkflow_EmptyResponse_FallsBackCleanly()
    {
        // Null response (e.g., model call failed) must not crash and must produce the correct
        // rationale message so operators can diagnose the fallback.
        var model = new FakeModel(null);
        var def = Workflow("default", "Default", "The general-purpose pipeline.");
        var bug = Workflow("bug-fix", "Bug Fix", "Fast remediation of a specific defect.");
        var context = new WorkflowSelectionContext("p1", "Fix something", ["Implementer"], [def, bug]);

        var result = await Selector(model).SelectAsync(context);

        model.Calls.Should().Be(2);
        result.Selected.Should().BeSameAs(def);
        result.WasAutoSelected.Should().BeTrue();
        result.Rationale.Should().Contain("could not be parsed");
    }
}
