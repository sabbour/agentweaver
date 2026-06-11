using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Scaffolder.AgentRuntime.Workflow;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Runs;
using Scaffolder.Domain;
using Scaffolder.Tests.Helpers;

namespace Scaffolder.Tests.Api;

/// <summary>
/// Focused unit tests for RunWatchLoopService.HandleTerminalOutputAsync return values.
/// Verifies that genuinely terminal outputs return true (triggering cleanup) while
/// a leaked "blocked" output returns false (preserving registry + checkpoints).
/// </summary>
public sealed class RunWatchLoopTerminalOutputTests : IClassFixture<ReviewWebApplicationFactory>
{
    private readonly ReviewWebApplicationFactory _factory;

    public RunWatchLoopTerminalOutputTests(ReviewWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HandleTerminalOutput_MergedStatus_ReturnsTrue()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new MergeOutput(runId, "merged", "merged:abc123"), "terminal-merge");

        var result = await svc.HandleTerminalOutputAsync(runId, woe, entry, entry.Generation, CancellationToken.None);

        result.Should().BeTrue("a merged output is genuinely terminal");
    }

    [Fact]
    public async Task HandleTerminalOutput_MergeFailedStatus_ReturnsTrue()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new MergeOutput(runId, "merge_failed", "conflict"), "terminal-merge");

        var result = await svc.HandleTerminalOutputAsync(runId, woe, entry, entry.Generation, CancellationToken.None);

        result.Should().BeTrue("a merge_failed output is genuinely terminal");
    }

    [Fact]
    public async Task HandleTerminalOutput_BlockedStatus_ReturnsFalse()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new MergeOutput(runId, "blocked", "dirty_working_tree"), "terminal-merge");

        var result = await svc.HandleTerminalOutputAsync(runId, woe, entry, entry.Generation, CancellationToken.None);

        result.Should().BeFalse("a blocked output is non-terminal; run must remain recoverable");
    }

    [Fact]
    public async Task HandleTerminalOutput_BlockedStatus_DoesNotCompleteStream()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new MergeOutput(runId, "blocked", "dirty_working_tree"), "terminal-merge");

        await svc.HandleTerminalOutputAsync(runId, woe, entry, entry.Generation, CancellationToken.None);

        entry.IsCompleted.Should().BeFalse(
            "a blocked output must not mark the stream as completed so clients stay connected");
    }

    [Fact]
    public async Task HandleTerminalOutput_NoChanges_ReturnsTrue()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new NoChangesOutput(runId), "terminal-no-op");

        var result = await svc.HandleTerminalOutputAsync(runId, woe, entry, entry.Generation, CancellationToken.None);

        result.Should().BeTrue("a no_changes output is genuinely terminal");
    }

    [Fact]
    public async Task HandleTerminalOutput_Declined_ReturnsTrue()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new DeclinedOutput(runId), "terminal-declined");

        var result = await svc.HandleTerminalOutputAsync(runId, woe, entry, entry.Generation, CancellationToken.None);

        result.Should().BeTrue("a declined output is genuinely terminal");
    }

    [Fact]
    public async Task HandleTerminalOutput_ContentSafetyFailed_ReturnsTrue()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new ContentSafetyFailedOutput(runId), "terminal-safety-failed");

        var result = await svc.HandleTerminalOutputAsync(runId, woe, entry, entry.Generation, CancellationToken.None);

        result.Should().BeTrue("a content_safety output is genuinely terminal");
    }

    private (RunWatchLoopService Service, RunStreamEntry Entry, string RunId) CreateServiceAndEntry()
    {
        var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RunWatchLoopService>();
        var runId = RunId.New().ToString();
        var streamStore = scope.ServiceProvider.GetRequiredService<RunStreamStore>();
        var entry = streamStore.Create(runId, ReviewWebApplicationFactory.OwnerUser);
        return (svc, entry, runId);
    }
}
