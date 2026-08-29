using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.Api.Skills;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Coordinator;

public sealed class RunBoundCopilotClassifierTests
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder().Build();

    [Fact]
    public async Task WorkflowSelection_uses_its_explicit_run_capability_or_fails_closed()
    {
        var model = new WorkflowModel();
        var context = new WorkflowSelectionContext("project", "task", [], [], RunId: "run-workflow");

        (await model.CompleteAsync("prompt", context, CancellationToken.None)).Should().Be("""{"selected":"default"}""");
        model.CapabilityRunId.Should().Be("run-workflow");

        (await model.CompleteAsync("prompt", context with { RunId = null }, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AssemblyGate_uses_its_explicit_run_capability_or_fails_closed()
    {
        var model = new AssemblyModel();
        var context = new AssemblyGateCodeClassificationContext("run-assembly", "project", "user", "title", "scope", []);

        (await model.ClassifyAsync(context, CancellationToken.None)).Should().BeTrue();
        model.CapabilityRunId.Should().Be("run-assembly");

        (await model.ClassifyAsync(context with { RunId = "" }, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task OutcomeReply_uses_its_explicit_run_capability_or_fails_closed()
    {
        var model = new OutcomeModel();
        var context = new OutcomeSpecReplyClassificationContext("run-outcome", "project", "user", "yes", null, null, null, null, null);

        (await model.ClassifyAsync(context, CancellationToken.None)).Should().Be(OutcomeSpecReplyKind.Confirm);
        model.CapabilityRunId.Should().Be("run-outcome");

        (await model.ClassifyAsync(context with { RunId = "" }, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task PreviewClassifier_uses_its_explicit_run_capability_or_fails_closed()
    {
        var model = new PreviewModel();
        var context = new PreviewApplicabilityClassificationContext("run-preview", "project", "user", "diff");

        (await model.ClassifyApplicabilityAsync(context, CancellationToken.None)).Should().BeTrue();
        model.CapabilityRunId.Should().Be("run-preview");

        (await model.ClassifyApplicabilityAsync(context with { RunId = "" }, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task StoryIndependence_uses_its_explicit_run_capability_or_fails_closed()
    {
        var model = new StoryModel();
        var context = new StoryIndependenceClassificationContext(
            "run-story", "project", "user", "outcome", null, null,
            [new StoryComponentInput("one", "title", "scope", [])], []);

        (await model.ClassifyAsync(context, CancellationToken.None))!.IsIndependentDeliverable.Should().BeTrue();
        model.CapabilityRunId.Should().Be("run-story");

        (await model.ClassifyAsync(context with { RunId = "" }, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task PreviewCommand_uses_its_explicit_run_capability_or_fails_closed()
    {
        var model = new PreviewCommandModel();
        var context = new PreviewCommandModelContext("run-command", "project", "user", Directory.GetCurrentDirectory());

        (await model.ProposeCommandAsync(context, CancellationToken.None))!.Previewable.Should().BeFalse();
        model.CapabilityRunId.Should().Be("run-command");

        (await model.ProposeCommandAsync(context with { RunId = "" }, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task MarketplaceClassifier_requires_an_explicit_capability_and_never_uses_ambient_scope()
    {
        var model = new MarketplaceModel();
        var tree = new[] { "skills/example/SKILL.md" };

        (await model.ClassifyAsync("owner", "repo", "main", tree, "run-marketplace", CancellationToken.None))!
            .Should().ContainSingle().Which.Location.Should().Be("skills/example");
        model.CapabilityRunId.Should().Be("run-marketplace");

        (await model.ClassifyAsync("owner", "repo", "main", tree, null, CancellationToken.None)).Should().BeNull();
    }

    private sealed class WorkflowModel : CopilotWorkflowSelectionModel
    {
        public string? CapabilityRunId { get; private set; }
        public WorkflowModel() : base(null!, NullLogger<CopilotWorkflowSelectionModel>.Instance, Configuration) { }
        protected override Task<string?> RunModelTurnAsync(string runId, string prompt, CancellationToken ct)
        {
            CapabilityRunId = runId;
            return Task.FromResult<string?>("""{"selected":"default"}""");
        }
    }

    private sealed class AssemblyModel : CopilotAssemblyGateCodeClassifier
    {
        public string? CapabilityRunId { get; private set; }
        public AssemblyModel() : base(null!, NullLogger<CopilotAssemblyGateCodeClassifier>.Instance, Configuration) { }
        protected override Task<string?> RunModelTurnAsync(string runId, string prompt, CancellationToken ct)
        {
            CapabilityRunId = runId;
            return Task.FromResult<string?>("""{"produces_code":true}""");
        }
    }

    private sealed class OutcomeModel : CopilotOutcomeSpecReplyClassifier
    {
        public string? CapabilityRunId { get; private set; }
        public OutcomeModel() : base(null!, NullLogger<CopilotOutcomeSpecReplyClassifier>.Instance, Configuration) { }
        protected override Task<string?> RunModelTurnAsync(string runId, string prompt, CancellationToken ct)
        {
            CapabilityRunId = runId;
            return Task.FromResult<string?>("""{"decision":"confirm"}""");
        }
    }

    private sealed class PreviewModel : CopilotPreviewClassifier
    {
        public string? CapabilityRunId { get; private set; }
        public PreviewModel() : base(null!, NullLogger<CopilotPreviewClassifier>.Instance, Configuration) { }
        protected override Task<string?> RunModelTurnAsync(string runId, string charter, string prompt, CancellationToken ct)
        {
            CapabilityRunId = runId;
            return Task.FromResult<string?>("""{"preview_required":true}""");
        }
    }

    private sealed class StoryModel : CopilotStoryIndependenceClassifier
    {
        public string? CapabilityRunId { get; private set; }
        public StoryModel() : base(null!, NullLogger<CopilotStoryIndependenceClassifier>.Instance, Configuration) { }
        protected override Task<string?> RunModelTurnAsync(string runId, string prompt, CancellationToken ct)
        {
            CapabilityRunId = runId;
            return Task.FromResult<string?>("""{"is_independent_deliverable":true,"independence_rationale":"independent"}""");
        }
    }

    private sealed class PreviewCommandModel : CopilotPreviewCommandModel
    {
        public string? CapabilityRunId { get; private set; }
        public PreviewCommandModel() : base(null!, NullLogger<CopilotPreviewCommandModel>.Instance, Configuration) { }
        protected override Task<string?> RunModelTurnAsync(string runId, string prompt, CancellationToken ct)
        {
            CapabilityRunId = runId;
            return Task.FromResult<string?>("""{"previewable":false}""");
        }
    }

    private sealed class MarketplaceModel : CopilotMarketplaceCatalogClassifier
    {
        public string? CapabilityRunId { get; private set; }
        public MarketplaceModel() : base(null!, NullLogger<CopilotMarketplaceCatalogClassifier>.Instance, Configuration) { }
        protected override Task<string?> RunModelTurnAsync(string runId, string prompt, CancellationToken ct)
        {
            CapabilityRunId = runId;
            return Task.FromResult<string?>("""{"skills":[{"location":"skills/example","name":"example","description":"Example"}]}""");
        }
    }
}
