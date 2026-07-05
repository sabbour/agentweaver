using FluentAssertions;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Agentweaver.Api.Coordinator;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Unit tests for <see cref="CopilotWorkflowSelectionModel.CaptureResponseTextAsync"/> — the
/// dual-path capture logic that handles both incremental delta text AND the consolidated
/// final-message path (SDK AssistantMessageEvent in RawRepresentation).
///
/// <para>
/// These tests exercise the specific defect Smith flagged in the review of issue #183: when
/// the Copilot SDK delivers the answer only as a final AssistantMessageEvent (no Text deltas),
/// the delta-only capture code returned null, causing WorkflowSelector to silently fall back
/// to the default workflow and reintroducing the "defaults to Generic Workflow" symptom.
/// </para>
/// </summary>
public sealed class CopilotWorkflowSelectionModelTests
{
    // --- helpers ---

    private static AssistantMessageEvent FinalMessageEvent(string content) =>
        new() { Data = new AssistantMessageData { MessageId = "msg-1", Content = content } };

    private static AgentResponseUpdate FinalMessageOnlyChunk(string content)
    {
        var aiContent = new TextContent("") { RawRepresentation = FinalMessageEvent(content) };
        return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent> { aiContent });
    }

    private static AgentResponseUpdate DeltaChunk(string text) =>
        new(ChatRole.Assistant, text);

    private static async IAsyncEnumerable<AgentResponseUpdate?> Stream(
        params AgentResponseUpdate?[] chunks)
    {
        foreach (var c in chunks)
            yield return c;
        await Task.CompletedTask;
    }

    // --- blocking defect: final-message-only path ---

    [Fact]
    public async Task CaptureResponseText_DeltaOnly_ReturnsAccumulatedText()
    {
        // Sanity check: the delta path that already worked continues to work.
        var stream = Stream(DeltaChunk("{\"selected\": \"bug-fix\", "), DeltaChunk("\"rationale\": \"x\"}"));

        var result = await CopilotWorkflowSelectionModel.CaptureResponseTextAsync(stream, CancellationToken.None);

        result.Should().Be("{\"selected\": \"bug-fix\", \"rationale\": \"x\"}");
    }

    [Fact]
    public async Task CaptureResponseText_FinalMessageOnly_ReturnsContent()
    {
        // THE BLOCKING DEFECT: SDK delivers no delta Text, only a final AssistantMessageEvent.
        // Delta-only code returned null; fixed code returns the event content.
        var json = """{"selected": "bug-fix", "rationale": "Targeted defect fix."}""";
        var stream = Stream(FinalMessageOnlyChunk(json));

        var result = await CopilotWorkflowSelectionModel.CaptureResponseTextAsync(stream, CancellationToken.None);

        result.Should().Be(json);
    }

    [Fact]
    public async Task CaptureResponseText_DeltasThenFinalMessage_DeltasWin_NotDoubleCounted()
    {
        // When deltas are streamed first, the subsequent AssistantMessageEvent must be ignored
        // so the content is not concatenated twice.
        var json = """{"selected": "bug-fix", "rationale": "x"}""";
        var stream = Stream(
            DeltaChunk("{\"selected\": \"bug-fix\", "),
            DeltaChunk("\"rationale\": \"x\"}"),
            FinalMessageOnlyChunk(json));   // this chunk must be ignored

        var result = await CopilotWorkflowSelectionModel.CaptureResponseTextAsync(stream, CancellationToken.None);

        result.Should().Be(json);
    }

    [Fact]
    public async Task CaptureResponseText_EmptyStream_ReturnsNull()
    {
        var stream = Stream();
        var result = await CopilotWorkflowSelectionModel.CaptureResponseTextAsync(stream, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task CaptureResponseText_NullChunksIgnored()
    {
        var json = """{"selected": "bug-fix", "rationale": "x"}""";
        var stream = Stream(null, null, FinalMessageOnlyChunk(json));

        var result = await CopilotWorkflowSelectionModel.CaptureResponseTextAsync(stream, CancellationToken.None);

        result.Should().Be(json);
    }

    [Fact]
    public async Task CaptureResponseText_FinalMessageOnly_WhitespaceIsTrimmed()
    {
        var json = """  {"selected": "bug-fix", "rationale": "x"}  """;
        var stream = Stream(FinalMessageOnlyChunk(json));

        var result = await CopilotWorkflowSelectionModel.CaptureResponseTextAsync(stream, CancellationToken.None);

        result.Should().Be(json.Trim());
    }
}
