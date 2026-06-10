using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Scaffolder.AgentRuntime;
using Scaffolder.Domain;
using Scaffolder.SandboxExec;
using Scaffolder.Tests.Helpers;

namespace Scaffolder.Tests.Foundry;

/// <summary>
/// Unit tests for FoundryAgentRunner streaming implementation.
/// All scenarios use a deterministic FakeStreamingChatClient — no live LLM required.
/// </summary>
public sealed class FoundryStreamingTests : IDisposable
{
    private readonly string _workDir;

    public FoundryStreamingTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"foundry-stream-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    // ---- Infrastructure ----

    private FoundryAgentRunner Runner(IChatClient client)
        => new(client, SandboxExecutorFactory.CreatePassthrough(), new StubPolicyStore(), new InMemoryShellApprovalStore(), NullLogger<FoundryAgentRunner>.Instance);

    private static (ChannelWriter<RunEvent> writer, Func<List<RunEvent>> drain) MakeChannel()
    {
        var ch = Channel.CreateUnbounded<RunEvent>();
        return (ch.Writer, () =>
        {
            ch.Writer.TryComplete();
            var list = new List<RunEvent>();
            while (ch.Reader.TryRead(out var e)) list.Add(e);
            return list;
        });
    }

    /// <summary>Reads a named property from an anonymous-type event payload via reflection.</summary>
    private static string? Prop(object payload, string name)
        => payload.GetType().GetProperty(name)?.GetValue(payload)?.ToString();

    private static ChatResponseUpdate TextUpdate(string text, string? messageId = null)
        => new(ChatRole.Assistant, text) { MessageId = messageId };

    private static ChatResponseUpdate FunctionCallUpdate(
        string callId, string name, Dictionary<string, object?> args)
        => new(ChatRole.Assistant, (IList<AIContent>)[new FunctionCallContent(callId, name, args)]);

    private static ChatResponseUpdate EmptyUpdate()
        => new() { Role = ChatRole.Assistant };

    // ---- T-1 ----

    [Fact]
    public async Task StreamingTextTurn_EmitsDeltasNotWholeMessage()
    {
        var client = new FakeStreamingChatClient(
            new TurnSetup([TextUpdate("hello"), TextUpdate(" "), TextUpdate("world")]));

        var (writer, drain) = MakeChannel();
        await Runner(client).ExecuteAsync("task", _workDir, ModelSource.MicrosoftFoundry, "r1", writer, CancellationToken.None);
        var events = drain();

        var deltas = events.Where(e => e.Type == "agent.message.delta").ToList();
        deltas.Should().HaveCount(3);
        deltas.Select(e => Prop(e.Payload, "delta")).Should().Equal("hello", " ", "world");

        events.Should().NotContain(e => e.Type == "agent.message");
        events.Should().Contain(e => e.Type == "agent.turn.start");
        events.Should().Contain(e => e.Type == "agent.turn.end");
        // run.completed is emitted only by the watch loop, not the runner.
        events.Should().NotContain(e => e.Type == "run.completed");
    }

    // ---- T-2 ----

    [Fact]
    public async Task ToolCallTurn_NoDeltasForToolTurn_ToolLoopRuns()
    {
        var client = new FakeStreamingChatClient(
            new TurnSetup([FunctionCallUpdate("c1", "read_file", new() { ["path"] = "x.txt" })]),
            new TurnSetup([TextUpdate("Done.")]));

        var (writer, drain) = MakeChannel();
        await Runner(client).ExecuteAsync("task", _workDir, ModelSource.MicrosoftFoundry, "r2", writer, CancellationToken.None);
        var events = drain();

        // tool.call for turn-1 tool call
        var toolCall = events.FirstOrDefault(e => e.Type == "tool.call");
        toolCall.Should().NotBeNull();
        Prop(toolCall!.Payload, "callId").Should().Be("c1");

        // Turn-2 text delta present
        var deltas = events.Where(e => e.Type == "agent.message.delta").ToList();
        deltas.Should().HaveCount(1);
        Prop(deltas[0].Payload, "delta").Should().Be("Done.");

        // No whole agent.message emitted (deltas cover all text)
        events.Should().NotContain(e => e.Type == "agent.message");

        events.Should().Contain(e => e.Type == "agent.turn.end");
        // run.completed is emitted only by the watch loop, not the runner.
        events.Should().NotContain(e => e.Type == "run.completed");
    }

    // ---- T-3 ----

    [Fact]
    public async Task ToolCallOnlyTurn_NoTextEmitted_FallbackNotTriggered()
    {
        // Turn 1 has only a function call — no text deltas, assistantMessage.Text is empty
        // so neither agent.message.delta nor fallback agent.message should fire for that turn.
        var client = new FakeStreamingChatClient(
            new TurnSetup([FunctionCallUpdate("c3", "read_file", new() { ["path"] = "readme.txt" })]),
            new TurnSetup([TextUpdate("Summary.")]));

        var (writer, drain) = MakeChannel();
        await Runner(client).ExecuteAsync("task", _workDir, ModelSource.MicrosoftFoundry, "r3", writer, CancellationToken.None);
        var events = drain();

        // Exactly one delta (from turn 2 "Summary."), none from turn 1
        events.Where(e => e.Type == "agent.message.delta").Should().HaveCount(1);

        // No whole agent.message ever — no fallback triggered
        events.Should().NotContain(e => e.Type == "agent.message");

        // Tool loop did run for turn 1
        events.Should().Contain(e => e.Type == "tool.call");

        // run.completed is emitted only by the watch loop, not the runner.
        events.Should().Contain(e => e.Type == "agent.turn.end");
        events.Should().NotContain(e => e.Type == "run.completed");
    }

    // ---- T-4 ----

    [Fact]
    public async Task FunctionCallArgs_ReconstructedAndToolLoopRuns()
    {
        // FunctionCallContent delivered in a single streaming update (ToChatResponse assembles it).
        // Using write_file so the tool actually succeeds within the sandbox.
        var client = new FakeStreamingChatClient(
            new TurnSetup([FunctionCallUpdate("c4", "edit",
                new() { ["path"] = "out.txt", ["content"] = "hello world" })]),
            new TurnSetup([TextUpdate("Wrote the file.")]));

        var (writer, drain) = MakeChannel();
        await Runner(client).ExecuteAsync("task", _workDir, ModelSource.MicrosoftFoundry, "r4", writer, CancellationToken.None);
        var events = drain();

        // tool.call carries correct arguments
        var toolCall = events.First(e => e.Type == "tool.call");
        Prop(toolCall.Payload, "callId").Should().Be("c4");
        Prop(toolCall.Payload, "toolName").Should().Be("edit");

        // tool.result (not tool.error) confirms the call succeeded
        events.Should().Contain(e => e.Type == "tool.result");
        events.Should().NotContain(e => e.Type == "tool.error");

        // File was actually written inside the sandbox
        File.Exists(Path.Combine(_workDir, "out.txt")).Should().BeTrue();
        File.ReadAllText(Path.Combine(_workDir, "out.txt")).Should().Be("hello world");

        // run.completed is emitted only by the watch loop, not the runner.
        events.Should().Contain(e => e.Type == "agent.turn.end");
        events.Should().NotContain(e => e.Type == "run.completed");
    }

    // ---- T-5: MF2 — cancellation is not failure ----

    [Fact]
    public async Task CancellationMidStream_DoesNotEmitRunFailed()
    {
        // The fake client yields one delta then throws OperationCanceledException.
        var client = new FakeStreamingChatClient(
            new TurnSetup([TextUpdate("chunk1")], ThrowAfter: new OperationCanceledException("cancelled")));

        var (writer, drain) = MakeChannel();
        var runner = Runner(client);

        Func<Task> act = () => runner.ExecuteAsync(
            "task", _workDir, ModelSource.MicrosoftFoundry, "r5", writer, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();

        var events = drain();
        // MF2: cancellation must NOT produce run.failed
        events.Should().NotContain(e => e.Type == "run.failed");
        // The delta emitted before cancellation is still present
        events.Should().Contain(e => e.Type == "agent.message.delta");
    }

    // ---- T-6 ----

    [Fact]
    public async Task StreamingError_EmitsRunFailed()
    {
        var client = new FakeStreamingChatClient(
            new TurnSetup([TextUpdate("a"), TextUpdate("b")],
                ThrowAfter: new InvalidOperationException("boom")));

        var (writer, drain) = MakeChannel();
        var runner = Runner(client);

        Func<Task> act = () => runner.ExecuteAsync(
            "task", _workDir, ModelSource.MicrosoftFoundry, "r6", writer, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

        var events = drain();
        events.Should().Contain(e => e.Type == "run.failed");
        var failed = events.First(e => e.Type == "run.failed");
        Prop(failed.Payload, "errorMessage").Should().NotBeNullOrEmpty();
    }

    // ---- T-7 ----

    [Fact]
    public async Task EmptyStreamingResponse_RunCompletes()
    {
        // Client yields no updates — empty stream, no text, no tool calls.
        var client = new FakeStreamingChatClient(new TurnSetup([]));

        var (writer, drain) = MakeChannel();
        var result = await Runner(client).ExecuteAsync(
            "task", _workDir, ModelSource.MicrosoftFoundry, "r7", writer, CancellationToken.None);

        var events = drain();
        // Runner no longer emits run.completed — the watch loop does that.
        events.Should().Contain(e => e.Type == "agent.turn.end");
        events.Should().NotContain(e => e.Type == "run.completed");
        events.Should().NotContain(e => e.Type == "run.failed");
        events.Should().NotContain(e => e.Type == "agent.message");
        events.Should().NotContain(e => e.Type == "agent.message.delta");
        result.Should().BeEmpty();
    }

    // ---- T-8 ----

    [Fact]
    public async Task DeltaMessageId_PinnedToFirstNonNullAcrossChunks()
    {
        // First update carries MessageId "msg-1"; subsequent updates have null.
        // All deltas should carry "msg-1" (first-non-null wins and is pinned).
        var client = new FakeStreamingChatClient(
            new TurnSetup([
                TextUpdate("a", messageId: "msg-1"),
                TextUpdate("b", messageId: null),
                TextUpdate("c", messageId: null),
            ]));

        var (writer, drain) = MakeChannel();
        await Runner(client).ExecuteAsync("task", _workDir, ModelSource.MicrosoftFoundry, "r8", writer, CancellationToken.None);
        var events = drain();

        var deltas = events.Where(e => e.Type == "agent.message.delta").ToList();
        deltas.Should().HaveCount(3);
        deltas.Select(e => Prop(e.Payload, "messageId")).Should().AllBe("msg-1");
    }

    // ---- T-9 ----

    [Fact]
    public async Task MaxTurns_StillEnforced()
    {
        // Feed 30 tool-call-only turns — the runner must bail after MaxTurns (30).
        const int MaxTurns = 30;
        var turns = Enumerable.Range(0, MaxTurns)
            .Select(i => new TurnSetup([FunctionCallUpdate($"c{i}", "read_file", new() { ["path"] = "f.txt" })]))
            .ToArray();

        var client = new FakeStreamingChatClient(turns);
        var (writer, drain) = MakeChannel();

        await Runner(client).ExecuteAsync(
            "task", _workDir, ModelSource.MicrosoftFoundry, "r9", writer, CancellationToken.None);

        var events = drain();
        var failed = events.FirstOrDefault(e => e.Type == "run.failed");
        failed.Should().NotBeNull();
        Prop(failed!.Payload, "errorMessage").Should().Be("Step limit reached.");
        events.Should().NotContain(e => e.Type == "run.completed");

        // Exactly 30 tool.call events — one per turn
        events.Where(e => e.Type == "tool.call").Should().HaveCount(MaxTurns);
    }

    // ---- T-10: tool events grouped inside their turn (FIX 1) ----

    [Fact]
    public async Task ToolCallTurn_ToolEventsAppearBeforeTurnEnd()
    {
        // Turn 0 has a tool call; turn 1 is a plain text final turn.
        // tool.call and tool.result for turn 0 must appear BEFORE that turn's agent.turn.end.
        var client = new FakeStreamingChatClient(
            new TurnSetup([FunctionCallUpdate("c10", "edit",
                new() { ["path"] = "a.txt", ["content"] = "hi" })]),
            new TurnSetup([TextUpdate("Done.")]));

        var (writer, drain) = MakeChannel();
        await Runner(client).ExecuteAsync("task", _workDir, ModelSource.MicrosoftFoundry, "r10", writer, CancellationToken.None);
        var events = drain();

        var toolCallSeq  = events.First(e => e.Type == "tool.call").Sequence;
        var toolResultSeq = events.First(e => e.Type == "tool.result").Sequence;
        // The first agent.turn.end is turn 0's — tool events must precede it.
        var turn0EndSeq  = events.First(e => e.Type == "agent.turn.end").Sequence;

        toolCallSeq.Should().BeLessThan(turn0EndSeq,
            "tool.call must be emitted before agent.turn.end closes the turn");
        toolResultSeq.Should().BeLessThan(turn0EndSeq,
            "tool.result must be emitted before agent.turn.end closes the turn");
    }

    // ---- T-11: runner emits agent.turn.end (not run.completed) on normal exit ----

    [Fact]
    public async Task NormalExit_EmitsAgentTurnEnd_NotRunCompleted()
    {
        var client = new FakeStreamingChatClient(
            new TurnSetup([TextUpdate("Final answer.")]));

        var (writer, drain) = MakeChannel();
        await Runner(client).ExecuteAsync("task", _workDir, ModelSource.MicrosoftFoundry, "r11", writer, CancellationToken.None);
        var events = drain();

        // Runner must emit agent.turn.end to close the turn bubble.
        events.Should().Contain(e => e.Type == "agent.turn.end");
        // run.completed is the watch loop's responsibility, not the runner's.
        events.Should().NotContain(e => e.Type == "run.completed");
    }

    // ---- MF1 regression: multi-turn text accumulation ----

    [Fact]
    public async Task MultiTurnText_ReturnedSummaryIsLastTurnOnly()
    {
        // Turn 0 streams "FIRST" AND includes a function call, so the tool loop runs and
        // the outer turn loop advances to turn 1.  Turn 1 streams "SECOND".
        // sb.Clear() at the top of turn 1 (MF1) must erase "FIRST" so the returned
        // string is only "SECOND", not "FIRSTSECOND".
        var client = new FakeStreamingChatClient(
            new TurnSetup([
                TextUpdate("FIRST"),
                FunctionCallUpdate("cx", "read_file", new() { ["path"] = "z.txt" }),
            ]),
            new TurnSetup([TextUpdate("SECOND")]));

        var (writer, drain) = MakeChannel();
        var result = await Runner(client).ExecuteAsync(
            "task", _workDir, ModelSource.MicrosoftFoundry, "r-mf1", writer, CancellationToken.None);

        // MF1: only the last turn's text is returned
        result.Should().Be("SECOND");

        var events = drain();
        events.Should().Contain(e => e.Type == "agent.turn.end");
        events.Should().NotContain(e => e.Type == "run.completed");
    }
}

// ---- Fake IChatClient ----

internal sealed record TurnSetup(
    IReadOnlyList<ChatResponseUpdate> Updates,
    Exception? ThrowAfter = null);

internal sealed class FakeStreamingChatClient : IChatClient
{
    private readonly Queue<TurnSetup> _turns;

    public FakeStreamingChatClient(params TurnSetup[] turns)
        => _turns = new Queue<TurnSetup>(turns);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_turns.Count == 0)
            throw new InvalidOperationException("FakeStreamingChatClient: no more turns configured.");

        var setup = _turns.Dequeue();

        foreach (var update in setup.Updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }

        if (setup.ThrowAfter is not null)
            throw setup.ThrowAfter;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("FakeStreamingChatClient only supports streaming.");

    public object? GetService(Type serviceType, object? serviceKey) => null;
    public void Dispose() { }
}

// ---- ToAsyncEnumerable helper (not needed — FakeStreamingChatClient uses async iterator directly) ----
