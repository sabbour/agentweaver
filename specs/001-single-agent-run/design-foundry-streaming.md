# Design: Foundry Agent Runner — Streaming Token Deltas

**Date**: 2026-06-08
**Author**: Tank (Backend Engineer)
**Status**: Design-complete — ready for implementation
**Requested by**: Ahmed Sabbour (@sabbour)

---

## 1. Problem Statement

`FoundryAgentRunner` calls `IChatClient.GetResponseAsync(...)` (line 69), which blocks
until the model finishes the entire response and then emits a single `agent.message` event
containing the full text.  The web timeline cannot render character-by-character streaming
for Foundry runs the way it already does for GitHub Copilot runs (`GitHubCopilotAgentRunner`
emits `agent.message.delta` events).

The fix is to replace the blocking call with `GetStreamingResponseAsync(...)`, emit
`agent.message.delta` per text chunk, and reconstruct the full `ChatMessage`
(including `FunctionCallContent`) so the existing multi-turn tool-calling loop,
governance checks, and message history are unaffected.

---

## 2. SDK Confirmation

**Package**: `Microsoft.Extensions.AI` **10.5.1**
(confirmed from `packages\Scaffolder.AgentRuntime\Scaffolder.AgentRuntime.csproj` line 12).

The following APIs were verified by reflecting the installed NuGet assembly at
`%USERPROFILE%\.nuget\packages\microsoft.extensions.ai.abstractions\10.5.1\lib\net9.0\Microsoft.Extensions.AI.Abstractions.dll`:

| API | Signature (verified) |
|---|---|
| `IChatClient.GetStreamingResponseAsync` | `IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options, CancellationToken cancellationToken)` |
| `ChatResponseExtensions.ToChatResponse` | `ChatResponse ToChatResponse(IEnumerable<ChatResponseUpdate> updates)` |
| `ChatResponseExtensions.ToChatResponseAsync` | `Task<ChatResponse> ToChatResponseAsync(IAsyncEnumerable<ChatResponseUpdate> updates, CancellationToken cancellationToken)` |
| `ChatResponseUpdate.Text` | `string? Text { get; }` — non-null when the update carries token text |
| `ChatResponseUpdate.Contents` | `IList<AIContent>? Contents { get; }` — carries `FunctionCallContent` fragments |
| `ChatResponseUpdate.MessageId` | `string? MessageId { get; }` — stable per assistant message across updates |

**`ToChatResponse(IEnumerable<ChatResponseUpdate>)`** is the sync accumulator.  It merges
all updates — including incremental `FunctionCallContent` argument JSON fragments — into a
full `ChatResponse` with a correctly-populated `Messages` list.  This is the method the
implementation will use (manual accumulation to a `List<ChatResponseUpdate>` during the
`await foreach`, then a single `.ToChatResponse()` call after the loop).

`ToChatResponseAsync` is deliberately **not** used here because we need to emit deltas
*during* enumeration, which requires our own `await foreach` loop.

---

## 3. Current vs. New Turn Loop

### 3.1 Current (non-streaming) — for reference

```csharp
// CURRENT (line 69–77 in FoundryAgentRunner.cs)
ChatResponse response = await chatClient.GetResponseAsync(messages, options, ct);
var assistantMessage = response.Messages.Last();
messages.Add(assistantMessage);

if (!string.IsNullOrWhiteSpace(assistantMessage.Text))
{
    sb.Clear();
    sb.Append(assistantMessage.Text);
    Emit("agent.message", new { content = assistantMessage.Text });
}
```

### 3.2 New (streaming) — pseudocode / C#-ish

The loop body between `Emit("agent.turn.start", ...)` and `Emit("agent.turn.end", ...)`
is replaced as follows.  Everything outside these lines (turn counter, tool invocation,
governance checks, `messages.Add(toolResults)`, MaxTurns, run.completed/failed) remains
**unchanged**.

```csharp
Emit("agent.turn.start", new { turnId = turn.ToString() });

// --- NEW: streaming section (replaces GetResponseAsync call) ---

var updates = new List<ChatResponseUpdate>();
var hadTextDelta = false;

// Stable messageId for this turn: first non-null value seen across all updates.
string? turnMessageId = null;

try
{
    await foreach (var update in chatClient.GetStreamingResponseAsync(messages, options, ct)
                                           .WithCancellation(ct))
    {
        updates.Add(update);

        // Capture a stable messageId for the turn once (first non-null wins).
        if (turnMessageId is null && update.MessageId is not null)
            turnMessageId = update.MessageId;

        var deltaText = update.Text;
        if (!string.IsNullOrEmpty(deltaText))
        {
            sb.Append(deltaText);                               // accumulate full text
            Emit("agent.message.delta", new { delta = deltaText, messageId = turnMessageId });
            hadTextDelta = true;
        }
    }
}
catch (Exception ex)
{
    _logger.LogError(ex, "GetStreamingResponseAsync threw — turn={Turn}, workingDirectory={WorkingDirectory}",
        turn, workingDirectory);
    Emit("run.failed", new { errorMessage = "The agent encountered an internal error." });
    throw;   // caller (ExecuteAsync) does not catch, so the run ends immediately
}

// Reconstruct the full assistant ChatMessage (with FunctionCallContent) from collected updates.
ChatResponse response = updates.ToChatResponse();
var assistantMessage  = response.Messages.Last();
messages.Add(assistantMessage);                                // feed history — unchanged

// --- Double-emit avoidance ---
// Emit agent.message ONLY when no text deltas were produced for this turn
// (tool-call-only turn, or empty response).  The frontend renders deltas OR
// a whole agent.message, never both for the same text.
if (!hadTextDelta && !string.IsNullOrWhiteSpace(assistantMessage.Text))
{
    // sb was not appended during streaming; fill it now for the return value.
    sb.Clear();
    sb.Append(assistantMessage.Text);
    Emit("agent.message", new { content = assistantMessage.Text });
}

var calls = assistantMessage.Contents.OfType<FunctionCallContent>().ToList();

Emit("agent.turn.end", new { turnId = turn.ToString() });

// ... rest of tool loop unchanged ...
```

Key observations:
- `sb` accumulates text **during** the delta loop; the existing `sb.Clear()` / `sb.Append`
  idiom before `Emit("agent.message", ...)` is only reached in the fallback path.
- `assistantMessage` is obtained from `response.Messages.Last()` — same source as before —
  so all downstream code (`calls`, governance, `messages.Add`) is structurally unchanged.
- The `try/catch` wraps only the streaming enumeration; tool invocation errors are handled
  by the existing per-tool try/catch (unchanged).

---

## 4. Double-Emit-Avoidance Rule

This mirrors the GitHub Copilot runner's approach exactly:

| Situation | What is emitted |
|---|---|
| Turn produces text tokens | `agent.message.delta` × N (one per update with non-empty `.Text`). No `agent.message`. |
| Turn produces only tool calls (no text) | Neither `agent.message.delta` nor `agent.message`. Frontend receives turn start/end and tool.call/result/error only. |
| Turn produces non-empty text but ZERO streaming text deltas (e.g. streaming pipeline is bypassed by a middleware or the provider returns the whole message in a single update with no `.Text` on intermediate updates — edge case) | Single `agent.message` fallback event. The `hadTextDelta` flag guards this. |
| Turn produces only whitespace | Neither (guarded by `IsNullOrWhiteSpace`). |

The frontend's reducer (see `design-run-timeline-ui.md §3.3`) already handles both paths:
- `agent.message.delta` → `addStreamingMessage` / `appendDeltaToMessage`
- `agent.message` → `addSettledMessage`

Because the Foundry runner will now produce `agent.message.delta` events, the `AgentMessageBubble`
`streaming` flag and blinking cursor will work identically to Copilot runs.

---

## 5. Event Shape Parity with Copilot Runner

The `agent.message.delta` payload emitted by the Copilot runner (`GitHubCopilotAgentRunner.cs`
line 127) is:

```csharp
new { delta = text, messageId }
```

The Foundry runner will emit **exactly the same shape**:

```csharp
Emit("agent.message.delta", new { delta = deltaText, messageId = turnMessageId });
```

`turnMessageId` is `string?`, matching the Copilot runner where `messageId` can also be null
(the SDK sometimes omits it).  The frontend reducer treats a null `messageId` as a sentinel
that means "there is at most one streaming message active" — safe for both runners.

---

## 6. Tool Call Delivery Risk (Streaming vs. Non-Streaming)

**Risk**: Azure OpenAI delivers `FunctionCallContent` data incrementally across multiple
streaming updates.  The model's choice of tool and its argument JSON are streamed
character-by-character in `ChatResponseUpdate.Contents`, not delivered atomically in one
update.

**Mitigation**: `ToChatResponse(updates)` (from `ChatResponseExtensions`) is specifically
designed to merge incremental content fragments.  The resulting `ChatResponse.Messages.Last()`
will contain fully-assembled `FunctionCallContent` items with complete `Arguments`
dictionaries — identical to what `GetResponseAsync` returns.  No change to the downstream
tool loop is required.

**Verification plan**: the fake streaming client in the test plan (§8) should yield a
`FunctionCallContent` split across two updates and assert that the reconstructed message
contains a single, fully-populated `FunctionCallContent`.

---

## 7. Edge Cases

| Case | Handling |
|---|---|
| **Empty / whitespace response** | `string.IsNullOrEmpty(deltaText)` guard skips delta emission; `IsNullOrWhiteSpace` guard skips fallback `agent.message`; tool-call list will be empty so run.completed fires with empty summary — same as current behaviour. |
| **Tool-call-only turn (no assistant text)** | `hadTextDelta` stays false; `assistantMessage.Text` will be null/empty; neither delta nor `agent.message` emitted; tool calls are extracted from reconstructed `assistantMessage.Contents` normally. |
| **`updates` is empty** (provider yields nothing before closing the stream) | `updates.ToChatResponse()` will produce a `ChatResponse` with a single empty `ChatMessage`. `assistantMessage.Text` is null; `calls` is empty; run.completed fires with empty summary. Safe. |
| **Cancellation mid-stream** | `WithCancellation(ct)` propagates `OperationCanceledException` out of the `await foreach`; the `catch (Exception ex)` block emits `run.failed` and re-throws, same as a general streaming error. |
| **Streaming error mid-turn** | Caught by the `try/catch` around the `await foreach`; `run.failed` emitted; exception propagated. Partial `updates` already collected are discarded (we never call `ToChatResponse` after an exception). |
| **`ToChatResponse` on partial updates** | This path is never reached (reconstruction only happens after the `await foreach` completes normally). |
| **Provider returns full text in one update** | `hadTextDelta` becomes true from the single delta; fallback `agent.message` is skipped; functionally identical to the streaming case. |
| **`MessageId` null on all updates** | `turnMessageId` stays null; frontend reducer treats null messageId as singleton message — safe (same as Copilot runner null-id path). |

---

## 8. Test Plan

All tests go in `tests\Scaffolder.Tests\`.  Use xunit + FluentAssertions (existing test
infrastructure).  No live LLM is required — a fake `IChatClient` drives all scenarios.

### 8.1 New file: `FoundryStreamingTests.cs`

#### Test infrastructure — `FakeStreamingChatClient`

```csharp
// Implements IChatClient.  Constructor takes a list of ChatResponseUpdate sequences
// (one sequence per turn call) and returns them via GetStreamingResponseAsync.
// GetResponseAsync throws NotSupportedException to guarantee it is never called.
internal sealed class FakeStreamingChatClient : IChatClient
{
    private readonly Queue<IReadOnlyList<ChatResponseUpdate>> _turns;

    public FakeStreamingChatClient(params IReadOnlyList<ChatResponseUpdate>[] turns)
        => _turns = new Queue<IReadOnlyList<ChatResponseUpdate>>(turns);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        if (_turns.Count == 0) throw new InvalidOperationException("No more turns configured.");
        return _turns.Dequeue().ToAsyncEnumerable();   // System.Linq.Async or simple helper
    }

    public Task<ChatResponse> GetResponseAsync(...) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? key) => null;
    public void Dispose() { }
}
```

#### Test cases

| # | Test name | Setup | Assertions |
|---|---|---|---|
| T-1 | `StreamingTextTurn_EmitsDeltasNotWholeMessage` | One turn: 3 updates each with `.Text = "hello"`, no FunctionCallContent. | Channel contains 3× `agent.message.delta` with `delta="hello"` and no `agent.message` event. `agent.turn.start` and `agent.turn.end` present. `run.completed` present. |
| T-2 | `ToolCallTurn_NoDeltasEmitted_ToolLoopRuns` | Turn 1: one update with FunctionCallContent(callId="c1", name="read_file", args={path="x.txt"}), no `.Text`. Turn 2: one update with `.Text = "Done."`. | No `agent.message.delta` or `agent.message` for turn 1. `tool.call` with callId="c1" present. Turn 2 emits `agent.message.delta`. `run.completed` present. |
| T-3 | `FallbackWholeMessage_WhenNoDeltaText` | One turn: one update with no `.Text` (`.Text` is null/empty), but after `ToChatResponse` the reconstructed `assistantMessage.Text` = "Summary" (simulated via a FunctionCallContent-only update plus second turn with text). | `agent.message` emitted in turn where streaming produced no text but assistant had text (requires fake that returns updates with FunctionCallContent only, then simulates second call returning text-only). |
| T-4 | `SplitFunctionCallArgs_ReconstructedCorrectly` | One turn: two updates — first has `FunctionCallContent(callId="c2", name="write_file", arguments={"path":` (incomplete JSON), second has the rest `"out.txt","content":"x"})` assembled by `ToChatResponse`. Turn 2 returns done text. | `tool.call` event has complete `arguments` dictionary with both `path` and `content` keys. Tool loop runs (mock tool returns "ok"). `tool.result` event present. |
| T-5 | `CancellationMidStream_EmitsRunFailed` | One turn: fake client throws `OperationCanceledException` after yielding one update. | `run.failed` event present. `agent.message.delta` for the first chunk present. No `agent.turn.end` (exception aborts before it). No crash without `run.failed`. |
| T-6 | `StreamingError_EmitsRunFailed` | One turn: fake client throws `InvalidOperationException("boom")` after yielding two updates. | `run.failed` event present. Exception propagates out of `ExecuteAsync`. |
| T-7 | `EmptyStreamingResponse_RunCompletes` | One turn: no updates yielded; no text, no tool calls. | `run.completed` present with empty summary. No `agent.message`. No crash. |
| T-8 | `DeltaMessageId_MatchesAcrossChunks` | One turn: 3 updates, first has `MessageId = "msg-1"`, rest have null. | All 3 `agent.message.delta` events carry `messageId = "msg-1"` (first non-null wins, pinned for turn). |
| T-9 | `MaxTurns_StillEnforced` | 31 turns of tool-call-only responses (never terminates normally). | `run.failed` event with `errorMessage = "Step limit reached."` after exactly 30 turns. |

### 8.2 Governance integration (existing `SandboxGovernanceTests.cs`)

No new test needed: `SandboxGovernanceTests` tests `SandboxGovernance.EvaluateToolCall`
directly, independent of the streaming/non-streaming call path.  The tool loop code path
that calls `governance.EvaluateToolCall` is structurally unchanged by this design.

### 8.3 Test helper: `ToAsyncEnumerable<T>`

```csharp
// If System.Linq.Async is not already available in the test project, add a local helper:
internal static class AsyncEnumerableHelper
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(
        this IEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in source)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
        await Task.CompletedTask;
    }
}
```

---

## 9. Unchanged Invariants

The following behaviours are **structurally unchanged** by this design:

- `agent.turn.start` / `agent.turn.end` — emitted in the same positions.
- `tool.call` / `tool.result` / `tool.error` payload shapes.
- Governance evaluation (`governance.EvaluateToolCall`) — called after
  `assistantMessage.Contents.OfType<FunctionCallContent>()` exactly as before.
- `SandboxedFileTools` invocation path.
- `MaxTurns` enforcement.
- Cancellation propagation via `CancellationToken`.
- `run.completed` with summary text.
- `run.failed` on step limit.
- Returned `string` from `ExecuteAsync` (accumulated in `sb` during deltas; unchanged in
  the fallback path).
- `FoundryClientFactory.CreateChatClient()` — returns `IChatClient`; no change needed.

---

## 10. Open Questions

1. **`ToChatResponse` behaviour with zero updates** — the SDK source is not checked here;
   reflection only confirms the method signature.  The implementation should add a defensive
   guard (test T-7 covers this empirically) or verify via the SDK source that an empty
   enumerable produces an empty `ChatResponse` rather than throwing.

2. **MessageId stability across the stream** — the design pins `turnMessageId` to the first
   non-null `MessageId` seen.  Azure OpenAI via `AzureOpenAIClient.AsIChatClient()` may
   populate `MessageId` on every update (all the same value) or only on the first.  Test T-8
   verifies the pinning logic but cannot confirm the provider's actual behaviour without a
   live model call.

3. **`FinishReason` on partial updates** — `ChatResponseUpdate.FinishReason` may be
   non-null on the final update only.  `ToChatResponse` is expected to propagate this to the
   `ChatResponse`.  If the `FinishReason` affects tool-loop termination in a future change,
   this assumption should be tested.

4. **Middleware pipeline fidelity** — `FoundryClientFactory` returns the raw
   `AzureOpenAIClient.GetChatClient(...).AsIChatClient()` without any MAF middleware.  If
   governance middleware (`WithGovernance`) is added to the Foundry pipeline in the future
   (as sketched in `design-sandbox-enforcement.md §2`), verify that the middleware's
   `GetStreamingResponseAsync` pass-through correctly forwards all `FunctionCallContent`
   fragments before implementing that change.

5. **Thread safety of `Emit`** — the current `Emit` closure uses `Interlocked.Increment`
   and `TryWrite`, which is safe for single-threaded call sites.  Streaming deltas arrive
   sequentially in the `await foreach` (no concurrency), so no lock is needed here —
   unlike the Copilot runner which has a concurrent permission handler on a separate thread.
   This remains true as long as no parallel streaming is introduced.

---

## Summary

Replace `GetResponseAsync` with `GetStreamingResponseAsync` in the Foundry turn loop.
Emit `agent.message.delta` per text chunk during iteration; collect all `ChatResponseUpdate`
objects; call `updates.ToChatResponse()` after the loop to reconstruct the full
`ChatMessage` including `FunctionCallContent`; continue the existing tool loop unchanged.
Emit `agent.message` only as a fallback when the stream produced no text deltas.
Both accumulation APIs (`GetStreamingResponseAsync` and `ToChatResponse`) are confirmed
present in the installed `Microsoft.Extensions.AI.Abstractions 10.5.1` assembly.
The delta event shape `{ delta, messageId }` is identical to the Copilot runner so the
frontend treats both providers uniformly.
