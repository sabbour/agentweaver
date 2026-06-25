using FluentAssertions;
using Agentweaver.Api.Runs;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Unit tests for <see cref="RunOrchestrator.ComposeChildSystemPrompt"/> (Feature 008 Defect C). A
/// coordinator child must receive a LEAN prompt: its charter EXACTLY ONCE plus an explicit
/// working-directory sandbox boundary, and it must ALWAYS get the boundary (never null) so it does
/// not try to write artifacts outside its worktree (the stall that hung child 6694939a).
/// </summary>
public sealed class ComposeChildSystemPromptTests
{
    private const string Charter = "# Morpheus Charter\nYou are the backend dev. Be surgical.";

    [Fact]
    public void WithCharter_IncludesCharterExactlyOnce_AndBoundary()
    {
        var prompt = RunOrchestrator.ComposeChildSystemPrompt(Charter);

        prompt.Should().NotBeNull();
        CountOccurrences(prompt, Charter).Should().Be(1, "the child charter must appear exactly once");

        // The sandbox boundary instruction must be present and explicit about the worktree limits.
        prompt.Should().ContainEquivalentOf("working directory");
        prompt.Should().ContainEquivalentOf("session-state");
        prompt.Should().ContainEquivalentOf(".copilot");
        prompt.Should().ContainEquivalentOf("temp");
        prompt.Should().ContainEquivalentOf("never write",
            "the boundary must forbid writes outside the working directory");
        prompt.Should().ContainEquivalentOf("adapt",
            "a rejected write must instruct the child to adapt and write within the working directory");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithoutCharter_ReturnsBoundaryAndDeliverableCapture_NeverNull(string? charter)
    {
        var prompt = RunOrchestrator.ComposeChildSystemPrompt(charter);

        prompt.Should().NotBeNull("a child must ALWAYS receive the sandbox boundary");
        prompt.Should().NotBeEmpty();
        prompt.Should().ContainEquivalentOf("working directory");
        prompt.Should().ContainEquivalentOf("session-state");
        // Without a charter there is a boundary + deliverable-capture section (separator between them).
        prompt.Should().Contain("---",
            "boundary and deliverable-capture sections are always separated by a divider");
        prompt.Should().ContainEquivalentOf("deliverable",
            "the deliverable capture instruction must always be present");
        prompt.Should().ContainEquivalentOf("committed",
            "the prompt must explain that files are committed when the turn ends");
    }

    [Fact]
    public void WithDecisions_IncludesDecisionsAfterCharter_BeforeBoundary()
    {
        const string decisions =
            "## Boundaries and Decisions\n### Use Postgres\n**Type:** architectural | **Decided by:** Architect\nAll persistence uses Postgres.";

        var prompt = RunOrchestrator.ComposeChildSystemPrompt(Charter, decisions);

        prompt.Should().NotBeNull();
        CountOccurrences(prompt, Charter).Should().Be(1, "the child charter must appear exactly once");
        prompt.Should().Contain("## Boundaries and Decisions",
            "active decisions must be injected into the child worker prompt");
        prompt.Should().Contain("All persistence uses Postgres.");

        // Order: charter, then decisions, then the sandbox boundary.
        prompt.IndexOf(Charter, StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf("## Boundaries and Decisions", StringComparison.Ordinal));
        prompt.IndexOf("## Boundaries and Decisions", StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf("Working-directory sandbox boundary", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithoutDecisions_OmitsDecisionsBlock_StillHasBoundary(string? decisions)
    {
        var prompt = RunOrchestrator.ComposeChildSystemPrompt(Charter, decisions);

        prompt.Should().NotBeNull();
        prompt.Should().NotContain("## Boundaries and Decisions",
            "no decisions block should appear when there are no active decisions");
        prompt.Should().ContainEquivalentOf("working directory");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
