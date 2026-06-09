# Design: Run Watch Timeline UI Redesign

**Date**: 2026-06-08
**Author**: Trinity (Frontend Engineer)
**Status**: Design-complete — ready for implementation

---

## 1. Problem Statement

`RunWatcher` renders every SSE event as a flat table row:

```
[badge: agent.message.delta]  hello, I will now read…
[badge: tool.call            ]  read_file {"path":"src/index.ts"}
[badge: tool.result          ]  export function…
[badge: tool.call            ]  write_file {"path":"src/index.ts"}
[badge: tool.error           ]  sandbox-violation: …
```

Problems with the current design:
- Every event is equally prominent — no information hierarchy.
- `tool.call` and its paired `tool.result`/`tool.error` are separate rows with no visual link.
- `agent.turn.start` and `agent.turn.end` produce noisy rows rather than logical groupings.
- `agent.message.delta` events each add a row until the existing accumulation loop collapses them — but the collapse happens in render, meaning the list still re-renders on every incoming delta.
- No affordance for the run being actively in-progress vs. already-complete replay.
- No step status (completed / in-progress / error).
- No keyboard-accessible collapsing of verbose tool output.

The target is a modern agent timeline — think VS Code Copilot Chat or the GitHub Copilot coding-agent panel — where the information hierarchy is clear, verbose internals are collapsed by default, and active steps carry a visible in-progress indicator.

---

## 2. Component Tree

```
RunWatcher                              (existing orchestrator — trimmed but kept)
├── RunHeader                           (NEW)
│   ├── Text (run ID prefix)
│   ├── StreamStatusBadge               (NEW inline helper)
│   └── Spinner (conditional)
├── Timeline                            (NEW — role="log", aria-live="polite")
│   ├── TurnGroup[]                     (NEW — one per agent.turn.start/end pair)
│   │   ├── TurnDivider                 (NEW — subtle group header / step count)
│   │   └── TurnStepItem[]             (mixed)
│   │       ├── AgentMessageBubble      (NEW)
│   │       └── ToolCallCard            (NEW — Accordion-based)
│   │           ├── ToolCallHeader      (icon + human title + status icon)
│   │           ├── ArgsBlock           (monospace, collapsed by default)
│   │           └── ResultBlock | ErrorBlock (collapsed by default)
│   └── LifecycleEventCard[]            (NEW — run.completed/failed, review.*, merge.*)
│       └── DiffCard                    (NEW wrapper — reuses <DiffViewer>)
└── ReviewSection                       (existing — DiffViewer + ReviewPanel unchanged)
```

### Responsibilities

| Component | Props | Key behaviour |
|---|---|---|
| `RunHeader` | `runId`, `streamStatus`, `error?` | Shows short run ID, connecting/streaming spinner, done/error badge. |
| `Timeline` | `items: TimelineItem[]`, `streamStatus` | `role="log"` region. Maps items to sub-components. |
| `TurnGroup` | `item: TurnGroupItem` | Renders `TurnDivider` then iterates `item.steps`. Memoized. |
| `TurnDivider` | `turnIndex`, `stepCount`, `active` | Subtle horizontal rule + label ("Turn 1 · 3 steps"). Small `CircleFilled` or `RecordRegular` icon; spinner when `active`. |
| `AgentMessageBubble` | `content`, `streaming`, `isLiveRun` | Chat bubble. Shows blinking cursor only when `streaming && isLiveRun`. `aria-live="polite"` on streaming variant. Memoized. |
| `ToolCallCard` | `item: ToolCallItem` | Fluent `Accordion`. One card pairs the call + its result or error. Collapsed by default. `aria-label` describes tool + status. |
| `LifecycleEventCard` | `event: RunStreamEvent` | Compact card for terminal events (run.completed, run.failed, review events, merge events). |
| `DiffCard` | `diff`, `treeHash` | Wraps `<DiffViewer>`. File-header bar showing `+N / -N` counts parsed from the diff. |

`DiffViewer` and `ReviewPanel` are **reused unchanged**.

---

## 3. The Grouping / Reducer Model

### 3.1 Timeline Item Types

```typescript
// Discriminated union — the output of the grouping reducer
type TimelineItem =
  | TurnGroupItem
  | LifecycleItem;

interface TurnGroupItem {
  kind: 'turn-group';
  turnId: string;
  turnIndex: number;       // 1-based display counter
  steps: TurnStep[];
  active: boolean;         // true until agent.turn.end received
}

type TurnStep =
  | AgentMessageItem
  | ToolCallItem;

interface AgentMessageItem {
  kind: 'agent-message';
  messageId: unknown;
  content: string;
  streaming: boolean;      // true while deltas still arriving (no settled agent.message yet)
}

interface ToolCallItem {
  kind: 'tool-call';
  callId: unknown;
  toolName: string;
  humanTitle: string;      // derived: e.g. "Read file · src/index.ts"
  args: Record<string, unknown>;
  result: { content: string } | null;
  error: { errorCode: string; errorMessage: string; isSandboxViolation: boolean } | null;
  settled: boolean;        // false until tool.result or tool.error arrives
}

interface LifecycleItem {
  kind: 'lifecycle';
  event: RunStreamEvent;   // run.completed, run.failed, review.*, merge.*
}
```

Events that fall outside any turn (e.g. `review.requested` or `run.completed` which arrive after `agent.turn.end`) are appended directly to the top-level `TimelineItem[]` as `LifecycleItem`s.

### 3.2 Reducer State

```typescript
interface TimelineReducerState {
  items: TimelineItem[];                    // output list (immutable append-only)
  currentTurnIndex: number | null;          // index into items[] for the open TurnGroup
  pendingToolCalls: Map<unknown, number[]>; // callId → [turnIndex, stepIndex] in items
  streamingMessage: {
    turnIndex: number;
    stepIndex: number;
    messageId: unknown;
  } | null;
}
```

### 3.3 Reducer Logic (pseudocode)

```typescript
function timelineReducer(
  state: TimelineReducerState,
  event: RunStreamEvent
): TimelineReducerState {
  switch (event.type) {

    case 'agent.turn.start': {
      const newTurn: TurnGroupItem = {
        kind: 'turn-group',
        turnId: event.payload.turnId,
        turnIndex: (state.currentTurnIndex ?? -1) + 1 + 1,  // next 1-based turn number
        steps: [],
        active: true,
      };
      const items = [...state.items, newTurn];
      return { ...state, items, currentTurnIndex: items.length - 1 };
    }

    case 'agent.turn.end': {
      if (state.currentTurnIndex === null) return state;
      const items = state.items.map((item, i) =>
        i === state.currentTurnIndex && item.kind === 'turn-group'
          ? { ...item, active: false }
          : item
      );
      // Settle any streaming message in this turn
      const settled = settleStreamingMessage(items, state.streamingMessage);
      return { ...state, items: settled, currentTurnIndex: null, streamingMessage: null };
    }

    case 'agent.message.delta': {
      const { delta, messageId } = event.payload;
      if (state.streamingMessage &&
          state.streamingMessage.messageId === messageId) {
        // Append delta to existing streaming bubble
        return appendDeltaToMessage(state, String(delta ?? ''));
      }
      // New streaming message — start a new bubble
      return addStreamingMessage(state, messageId, String(delta ?? ''));
    }

    case 'agent.message': {
      // Settled message from the server (replaces or adds)
      const { messageId, content } = event.payload;
      if (state.streamingMessage?.messageId === messageId) {
        return settleMessage(state, String(content ?? ''));
      }
      // No prior streaming message for this id (e.g. replay) — add settled
      return addSettledMessage(state, messageId, String(content ?? ''));
    }

    case 'tool.call': {
      const { callId, toolName, arguments: args } = event.payload;
      const callItem: ToolCallItem = {
        kind: 'tool-call',
        callId,
        toolName: String(toolName ?? 'tool'),
        humanTitle: deriveHumanTitle(String(toolName ?? ''), args as Record<string, unknown>),
        args: (args as Record<string, unknown>) ?? {},
        result: null,
        error: null,
        settled: false,
      };
      return addStepToCurrentTurn(state, callItem);
      // Also register in pendingToolCalls map for O(1) lookup
    }

    case 'tool.result': {
      const { callId, content } = event.payload;
      return settleToolCall(state, callId, {
        result: { content: String(content ?? '') },
        error: null,
        settled: true,
      });
    }

    case 'tool.error': {
      const { callId, errorCode, errorMessage } = event.payload;
      const isSandboxViolation =
        String(errorCode ?? '').toLowerCase().includes('sandbox') ||
        String(errorCode ?? '').toLowerCase().includes('violation');
      return settleToolCall(state, callId, {
        result: null,
        error: {
          errorCode: String(errorCode ?? 'error'),
          errorMessage: String(errorMessage ?? ''),
          isSandboxViolation,
        },
        settled: true,
      });
    }

    case 'run.completed':
    case 'run.failed':
    case 'review.requested':
    case 'review.approved':
    case 'review.declined':
    case 'merge.completed':
    case 'merge.failed': {
      const lifecycleItem: LifecycleItem = { kind: 'lifecycle', event };
      return { ...state, items: [...state.items, lifecycleItem] };
    }

    default:
      return state;
  }
}
```

`deriveHumanTitle(toolName, args)` maps tool name + arguments to a short, readable label:
- `read_file` + `{ path: "src/index.ts" }` → `"Read file · src/index.ts"`
- `write_file` + `{ path: "src/index.ts" }` → `"Write file · src/index.ts"`
- `list_directory` + `{ path: "src/" }` → `"List directory · src/"`
- Fallback: capitalize tool name (replace `_` with space)

### 3.4 Incremental Dispatch (performance-critical)

`useRunStream` returns a new `events` array reference on every new event. A naive `useMemo([events])` re-runs the full O(n) fold on every delta.

Instead, use `useReducer` fed incrementally via `useEffect`:

```typescript
function useTimelineItems(
  events: RunStreamEvent[]
): TimelineReducerState {
  const [state, dispatch] = useReducer(timelineReducer, initialTimelineState);
  const processedCountRef = useRef(0);

  useEffect(() => {
    const newCount = events.length;
    if (newCount <= processedCountRef.current) return;
    // Process only the new tail
    for (let i = processedCountRef.current; i < newCount; i++) {
      dispatch({ type: 'event', event: events[i] });
    }
    processedCountRef.current = newCount;
  }, [events]);

  return state;
}
```

`dispatch` is stable across renders. Each new delta fires the reducer exactly once, updating only the affected streaming message item. `TurnGroup`, `ToolCallCard`, and `AgentMessageBubble` are all wrapped in `React.memo` — only the component whose props actually changed re-renders. A 500-event run produces at most one re-render of one `AgentMessageBubble` per delta.

---

## 4. Streaming / Cursor Approach

### 4.1 Live vs. Replay Distinction

| Scenario | `streamStatus` | `streaming` flag on message | Cursor shown? |
|---|---|---|---|
| Active run, delta arriving | `'streaming'` | `true` | Yes |
| Active run, message settled | `'streaming'` | `false` | No |
| Completed run replaying | `'done'` | `false` (all settled immediately) | No |
| Reconnected run (mid-stream) | `'streaming'` | `true` if last event was a delta | Yes |

The `isLiveRun` boolean is derived in `RunWatcher`:
```typescript
const isLiveRun = streamStatus === 'connecting' || streamStatus === 'streaming';
```

It is passed as a prop to `Timeline` and forwarded to `AgentMessageBubble`. **The cursor is only shown when both `streaming === true` AND `isLiveRun === true`.**

This eliminates the fake-typewriter risk: on a replayed completed run, all events are delivered near-simultaneously, `streamStatus` reaches `'done'` quickly, and no bubble ever has `streaming: true && isLiveRun: true`.

### 4.2 Cursor Implementation

```typescript
// In AgentMessageBubble's makeStyles
bubble: {
  // ... layout
},
cursorAfter: {
  '::after': {
    content: '""',
    display: 'inline-block',
    width: '2px',
    height: '1em',
    backgroundColor: tokens.colorBrandForeground1,
    marginLeft: tokens.spacingHorizontalXS,
    verticalAlign: 'text-bottom',
    animationName: {
      '0%, 100%': { opacity: 1 },
      '50%': { opacity: 0 },
    },
    animationDuration: '1s',
    animationIterationCount: 'infinite',
    animationTimingFunction: 'step-start',
  },
  '@media (prefers-reduced-motion: reduce)': {
    '::after': {
      animationName: 'none',   // static block — still visible, no blink
    },
  },
},
```

The `cursorAfter` class is applied to the bubble wrapper only when `streaming && isLiveRun`.

### 4.3 `agent.turn.end` as an Implicit Cursor Kill

When `agent.turn.end` arrives, the reducer sets `active: false` on the `TurnGroup` and clears `streamingMessage`. Any in-progress bubble has its `streaming` flag set to `false`. This removes the cursor without needing a separate signal.

---

## 5. Fluent Components and Icons

### 5.1 Fluent Components (all from `@fluentui/react-components@9.74.1`)

| Use | Component |
|---|---|
| Collapsible tool call | `Accordion`, `AccordionItem`, `AccordionHeader`, `AccordionPanel` |
| Step status / event badge | `Badge` |
| Active-step spinner | `Spinner` size="tiny" |
| Text throughout | `Text` |
| Turn divider | `Divider` |
| Review panel (unchanged) | `Button`, `MessageBar`, `MessageBarBody` |
| Card containers | `makeStyles` + `div` with token-based border/radius (no `Card` — avoids interaction model conflicts with `Accordion`) |

> **Note on `Card`**: `@fluentui/react-components` 9.x ships a `Card` component, but it has its own focusable/clickable interaction semantics that clash with the `Accordion`'s keyboard model inside `ToolCallCard`. Use styled `div`s with `tokens` for the card chrome instead.

### 5.2 Icons (all verified in `@fluentui/react-icons@2.0.328`)

| Purpose | Icon |
|---|---|
| Completed step | `CheckmarkCircleFilled` |
| Settled tool call (success) | `CheckmarkCircleFilled` |
| Tool error / run failed | `ErrorCircleFilled` |
| Sandbox violation (distinct) | `WarningFilled` (amber, not red) |
| Declined review | `DismissCircleFilled` |
| Collapse indicator (open) | `ChevronDownRegular` |
| Collapse indicator (closed) | `ChevronRightRegular` |
| Tool call | `WrenchRegular` |
| Agent message | `BotRegular` |
| File path argument | `DocumentRegular` |
| Args/code block label | `CodeRegular` |
| Turn start / active turn | `RecordRegular` |
| Run completed | `CheckmarkCircleFilled` |
| Merge / branch event | `BranchRegular` |
| Run in-progress (header) | `PlayRegular` (alongside `Spinner`) |

### 5.3 Styling Approach

All components use `makeStyles` + `tokens` — the same pattern as the existing `RunWatcher`, `DiffViewer`, and `ReviewPanel`:

```typescript
import { makeStyles, tokens } from '@fluentui/react-components';

const useStyles = makeStyles({
  toolCard: {
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    overflow: 'hidden',
  },
  toolCardError: {
    borderColor: tokens.colorPaletteRedBorderActive,
    backgroundColor: tokens.colorPaletteRedBackground1,
  },
  toolCardSandboxViolation: {
    borderColor: tokens.colorPaletteYellowBorderActive,
    backgroundColor: tokens.colorPaletteYellowBackground1,
  },
  completedStep: {
    opacity: '0.6',   // dim completed steps
  },
  argsBlock: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorNeutralBackground3,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-all',
  },
  messageBubble: {
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusLarge,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    maxWidth: '80%',
  },
  turnDivider: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
});
```

No new npm dependencies are required. `@fluentui/react-icons` is already present as a transitive dependency of `@fluentui/react-components` — it should be listed explicitly in `package.json` as a direct dev/prod dependency when its icons are imported in product code.

---

## 6. Accessibility Plan

### 6.1 Landmarks and Roles

- `Timeline` container: `role="log"` with `aria-label="Run timeline"` and `aria-live="polite"`. Screen readers announce new items as they are appended.
- `RunHeader`: `role="status"` with `aria-live="polite"` for the status badge/spinner so status changes are announced.
- `ReviewSection`: existing `Divider` landmark structure is preserved.

### 6.2 ToolCallCard Keyboard Interaction

`Accordion` / `AccordionItem` from Fluent 2 handles keyboard navigation natively:
- `Enter` / `Space` on `AccordionHeader` toggles the panel.
- `aria-expanded` is set automatically by `AccordionItem`.
- `aria-label` on the `AccordionHeader` button: `"Tool call: {humanTitle} — {status}"` where status is "pending", "succeeded", "failed", or "sandbox violation".

### 6.3 AgentMessageBubble

- `aria-label="Agent message"` on the bubble wrapper.
- When `streaming && isLiveRun`: add `aria-live="polite"` on the inner text span so screen readers announce appended content. Do **not** use `aria-live="assertive"` — it would interrupt every delta.
- The cursor `::after` pseudo-element is purely decorative — it does not need an ARIA label.

### 6.4 Status Icons

All status icons (`CheckmarkCircleFilled`, `ErrorCircleFilled`, etc.) are accompanied by visible text labels in `TurnDivider` and `LifecycleEventCard`. They carry `aria-hidden="true"` since the text is the accessible label.

### 6.5 Reduced Motion

```css
@media (prefers-reduced-motion: reduce) {
  /* cursor blink */
  ::after { animation: none; }
  /* spinner is Fluent's built-in Spinner — it respects prefers-reduced-motion internally */
}
```

Fluent 2's `Spinner` component already respects `prefers-reduced-motion`. The custom cursor animation is handled via the `makeStyles` `@media` block shown in §4.2.

---

## 7. Edge Cases

### 7.1 `tool.call` With No Matching Result

A `tool.call` event that never receives a `tool.result` or `tool.error` (e.g. stream cut mid-run):
- The `ToolCallItem` stays with `settled: false`, `result: null`, `error: null`.
- `ToolCallCard` renders a `Spinner` inside the header to indicate the result is pending.
- When `streamStatus === 'error'` and the card is still unsettled, switch the spinner to a `WarningFilled` icon with `aria-label="Result not received"`.

### 7.2 `tool.error` as Sandbox Violation

`isSandboxViolation` is derived from `errorCode` containing `"sandbox"` or `"violation"` (case-insensitive). The `ToolCallCard` receives distinct styling:
- Yellow/amber border (`colorPaletteYellowBorderActive`) rather than red.
- `WarningFilled` icon instead of `ErrorCircleFilled`.
- Expanded by default (unlike normal errors which are collapsed) — sandbox violations are security-relevant and merit immediate attention.

### 7.3 Empty / Whitespace-Only Messages

`AgentMessageBubble` suppresses render entirely when `content.trim() === ''` and `streaming === false`. While `streaming === true` with no content yet, it renders the cursor only (zero-width bubble placeholder), avoiding a layout jank when the first delta arrives.

### 7.4 Review / Merge Events Interleaved With Active Turn

`review.requested` can arrive while `currentTurnIndex !== null` (the turn end hasn't been emitted yet). The reducer appends a `LifecycleItem` to the top-level list regardless — it does **not** add it inside the current turn's steps. This is safe because the existing `RunWatcher` review logic only depends on `events.some(e => e.type === 'review.requested')`, which we preserve.

### 7.5 Reconnection and Dedup

`useRunStream` already deduplicates by `sequence` number (`prev.some(e => e.sequence === seq)`). The incremental dispatch in `useTimelineItems` uses `processedCountRef` to track how many events have been folded. On reconnection, `useRunStream` resets `events` to `[]`, which triggers a `useEffect` cleanup in `useTimelineItems` that must reset `processedCountRef.current = 0` and dispatch a `'reset'` action to clear the reducer state.

```typescript
useEffect(() => {
  processedCountRef.current = 0;
  dispatch({ type: 'reset' });
}, [runId]);   // runId changes on hard reset / new watch session
```

Reconnection to the **same** run (mid-stream disconnect) uses `Last-Event-ID` in `useRunStream` and replays only the missed events. The dedup prevents duplicates, so the same reducer path applies.

### 7.6 `run.failed` Inside a Turn

If the agent runner emits `run.failed` before `agent.turn.end` (a crash mid-turn), `currentTurnIndex` is not null. The reducer first closes the open turn (marks `active: false`) and then appends the `LifecycleItem`. This prevents an orphaned open-turn UI element.

### 7.7 Events of Unknown Type

The reducer's `default` branch returns `state` unchanged — unknown future event types are ignored gracefully. This preserves the "keep event contract intact" constraint.

---

## 8. Test Plan

No new test tooling. Tests go in the existing Vitest test suite (`apps/web`).

### 8.1 `timelineReducer` Unit Tests

The reducer is a pure function — it is the highest-value test target.

```
apps/web/src/__tests__/timelineReducer.test.ts
```

Test cases:

| # | Scenario | Assert |
|---|---|---|
| T-01 | `agent.turn.start` → `agent.turn.end` | `items[0].kind === 'turn-group'`, `active === false` |
| T-02 | Three deltas then `agent.message` | Single `AgentMessageItem` in steps, `streaming === false`, content is the settled content |
| T-03 | `tool.call` then `tool.result` | `ToolCallItem.settled === true`, `result` populated, `error === null` |
| T-04 | `tool.call` then `tool.error` (sandbox) | `isSandboxViolation === true` |
| T-05 | `tool.call` with no result | `settled === false`, `result === null`, `error === null` |
| T-06 | `review.requested` → `merge.completed` | Two `LifecycleItem`s at top level |
| T-07 | `run.failed` mid-turn | Open turn is closed (`active: false`), `LifecycleItem` appended |
| T-08 | Duplicate event sequence | State unchanged (handled by `useRunStream` dedup, but reducer test verifies idempotent `default` path) |
| T-09 | `agent.message` with unknown `messageId` (no prior delta) | Adds settled bubble without crashing |
| T-10 | `deriveHumanTitle` mapping | `read_file + {path}` → `"Read file · {path}"`, fallback for unknown tools |

### 8.2 `useTimelineItems` Hook Tests

Use `renderHook` from `@testing-library/react`:

```
apps/web/src/__tests__/useTimelineItems.test.ts
```

| # | Scenario | Assert |
|---|---|---|
| H-01 | Events array grows incrementally | `processedCountRef` advances; no items are duplicated |
| H-02 | `runId` changes (reset) | `items` clears to `[]` |
| H-03 | 200 delta events | All accumulated into one `AgentMessageItem`; no extra items |

### 8.3 `AgentMessageBubble` Snapshot / Interaction Tests

```
apps/web/src/__tests__/AgentMessageBubble.test.tsx
```

| # | Scenario | Assert |
|---|---|---|
| B-01 | `streaming=true, isLiveRun=true` | Cursor class applied |
| B-02 | `streaming=false` | No cursor class |
| B-03 | `streaming=true, isLiveRun=false` (replay) | No cursor class |
| B-04 | Empty content + streaming | Renders placeholder (no crash) |

### 8.4 `ToolCallCard` Accessibility Tests

```
apps/web/src/__tests__/ToolCallCard.test.tsx
```

| # | Scenario | Assert |
|---|---|---|
| C-01 | Render unsettled card | `aria-expanded="false"`, spinner present |
| C-02 | `Enter` on AccordionHeader | Panel opens, `aria-expanded="true"` |
| C-03 | `tool.error` (sandbox) | `WarningFilled` icon rendered, yellow border class applied |
| C-04 | `tool.error` (non-sandbox) | `ErrorCircleFilled` icon rendered |

---

## 9. Resolved Questions (Implementation Notes)

> All open questions from design were resolved during implementation on 2026-06-08.

### OQ-1 — No `agent.message.start` event (RESOLVED)
No `agent.message.start` event exists. Message boundaries are inferred from `messageId` changes on `agent.message.delta` events and from `agent.turn.end`. The reducer creates a new streaming bubble on the first delta for a new `messageId`. This is the permanent design.

### OQ-2 — `callId` guaranteed in tool events (RESOLVED)
Ground truth confirmed: `tool.call` = `{callId, toolName, arguments}`, `tool.result` = `{callId, content}`, `tool.error` = `{callId, errorMessage}`. All three carry `callId`. There is **no `errorCode` field** — `isSandboxViolation` is derived from `errorMessage` text (RD-B2).

### OQ-3 — `@fluentui/react-icons` explicit dependency (RESOLVED)
Added as a pinned direct dependency: `@fluentui/react-icons@2.0.328` in `apps/web/package.json` dependencies.

### OQ-4 — Virtualization (RESOLVED — deferred)
Shipped without virtualization. `React.memo` keeps per-delta re-renders minimal. Revisit if runs exceed ~300 timeline items.

### OQ-5 — DiffCard `+N / -N` counts (RESOLVED — not implemented)
`DiffCard` was not implemented as a separate component. The existing `DiffViewer` is used directly in `RunWatcher`'s review section, unchanged. If a summary bar is needed in future, it can be added to `DiffViewer` directly.

### Security (Y-series) — RESOLVED

- **Y-1 (Unbounded content)**: All text fields capped at 50,000 characters in the reducer (`cap()` function) and in display components. Expand/truncation note shown when content is capped.
- **Y-2 (Path prefix stripping)**: `stripPathPrefix()` strips `/home/xxx/`, `/Users/xxx/`, `C:\Users\xxx\` prefixes from tool card header titles. Full paths remain visible on expand.
- **Y-3 (Safe Markdown — scoped, sanitized, no raw HTML)**: Agent message bubbles (`AgentMessageBubble`) render **settled** messages (streaming=false OR isLiveRun=false) via `react-markdown` + `remark-gfm` + `rehype-sanitize` (defaultSchema). While a message is actively streaming (`streaming && isLiveRun`), plain escaped React text + cursor is used to avoid broken partial fences/lists. `rehype-raw` is intentionally excluded; raw HTML in agent text is neutralised by the sanitizer — `<script>` and `onerror` attributes are stripped before the React element tree is built. `react-markdown` never uses `dangerouslySetInnerHTML`. All other surfaces (ToolCallCard args/result/error, DiffViewer, LifecycleEventCard) remain plain React text nodes — they are the primary attack surface and are not changed. Links override the `a` renderer with `rel="noopener noreferrer" target="_blank"`. Security tests cover script-injection and onerror stripping.
- **Y-5 (Icons pinned)**: `@fluentui/react-icons@2.0.328` pinned as explicit direct dependency. All icon names verified against that version.

---

## 10. Summary of Changes to `RunWatcher`

After the redesign, `RunWatcher` is thin:

```typescript
export function RunWatcher({ runId }: RunWatcherProps) {
  const { events, status, error } = useRunStream(runId, API_KEY, API_URL);
  const { items } = useTimelineItems(events, runId);
  const isLiveRun = status === 'connecting' || status === 'streaming';
  // ... existing review logic unchanged ...

  return (
    <div className={styles.root}>
      <RunHeader runId={runId} streamStatus={status} error={error ?? undefined} />
      <Timeline items={items} streamStatus={status} isLiveRun={isLiveRun} />
      {hasReviewRequested && (
        <div className={styles.reviewSection}> {/* DiffViewer + ReviewPanel unchanged */} </div>
      )}
    </div>
  );
}
```

The `eventSummary()` and `badgeColor()` functions are deleted. The delta-accumulation loop is replaced by the reducer. `DiffViewer` and `ReviewPanel` are reused unchanged.
