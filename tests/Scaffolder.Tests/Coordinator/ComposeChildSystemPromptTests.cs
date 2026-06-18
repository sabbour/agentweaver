using FluentAssertions;
using Scaffolder.Api.Runs;

namespace Scaffolder.Tests.Coordinator;

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
    public void WithoutCharter_ReturnsBoundaryOnly_NeverNull(string? charter)
    {
        var prompt = RunOrchestrator.ComposeChildSystemPrompt(charter);

        prompt.Should().NotBeNull("a child must ALWAYS receive the sandbox boundary");
        prompt.Should().NotBeEmpty();
        prompt.Should().ContainEquivalentOf("working directory");
        prompt.Should().ContainEquivalentOf("session-state");
        prompt.Should().NotContain("---",
            "with no charter there is no charter/boundary separator — only the boundary remains");
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
