# Web UI reference

The Scaffolder web UI is a React 19 and Fluent 2 client over the backend API. It submits runs, streams live events, shows run details, and records your review decision before anything merges. The browser client keeps all run logic in the API layer.

## Configuration

Copy `.env.example` to `.env` in `apps/web`, then set the Vite variables:

| Variable | Default | Purpose |
| --- | --- | --- |
| `VITE_API_URL` | `http://localhost:5000` | API base URL |
| `VITE_API_KEY` | empty | Bearer key sent on every request |

## Develop and build

```powershell
cd apps/web
npm install
npm run dev
npm run build
npm run lint
```

`npm run build` type-checks the app and produces a production bundle. `npm run lint` runs ESLint.

## Routes

| Path | Screen | Purpose |
| --- | --- | --- |
| `/` | Submit a run | Form to start a run |
| `/watch/:runId` | Watch run | Live event stream, diff, and review panel for a run |

## Flows

### Submit a run

The home page collects the repository path, originating branch, task description, and model source. Submit stays disabled until the path, branch, and task are filled in. On success the app navigates to the watch screen for the new run.

### Watch a run

The watch screen streams events with `fetch`, not `EventSource`, so it can send the bearer key and `Last-Event-ID`. The stream reconnects after a drop and deduplicates by `sequence`. Reconnection replays from the in-memory buffer while the run's entry is retained on the server.

#### Run header

A header bar shows the shortened run ID alongside a status indicator: a spinner while connecting or streaming, a success badge when done, or an error badge on failure.

#### Timeline

Events are grouped into turns. Each turn opens with a divider that reads **Turn N · X steps** and shows a live spinner while the turn is in progress or a checkmark once it closes. A completed turn that received no steps is not shown — it produces no divider or content in the timeline.

Inside each turn, two kinds of steps appear:

**Agent message bubbles** — the agent's text output, rendered with a bot icon on the left. On a live in-progress run, text arrives token-by-token and a blinking cursor follows the end of the accumulated text. Once the server confirms the message is complete, the content is rendered as Markdown: headings, lists, inline code, fenced code blocks, block quotes, and tables. Headings use the Fluent type-ramp scale (h1 → Base500, h2 → Base400, h3/h4 → Base300) so they stay visually consistent with the rest of the UI. Links open in a new tab with `rel="noopener noreferrer"`. When opening a completed run after the fact, the full message content is replayed at once with no cursor — this is expected behaviour, not a bug.

Markdown is sanitized using rehype-sanitize with the default allowlist schema. `rehype-raw` is not included, so any raw HTML in agent output is neutralised rather than rendered. All text fields are React text nodes; `dangerouslySetInnerHTML` is not used anywhere in the rendering pipeline.

**Tool call cards** — each tool call renders as a collapsible accordion card with a wrench icon. The header shows a status indicator and a human-readable title derived from the tool name and key argument, for example **Read file · src/index.js** or **Run command · npm test**. Inside the card, the arguments are shown as formatted JSON, and the result or error appears once settled.

A tool call with no result yet shows a spinner in the header. A regular error shows a red error badge; a sandbox or path-restriction violation shows a yellow warning badge and the card **auto-expands** (so the error is visible without a click). Expanding a tool cluster shows only the first level (individual tool rows) collapsed — click a row to expand its detail pane. Tool clusters with no errors default to collapsed. Both the arguments and the output are plain escaped text — no HTML is interpreted.

**Inline approval cards** — when the agent calls a tool that requires human approval, an **approval card** appears inline in the current turn's timeline (not at the bottom). The card shows:
- The tool name as a badge
- The resource URL (scrollable, monospace)
- Four action buttons: **Allow once**, **Allow this run**, **Always allow (session)**, **Deny**
- An optional intention description

After taking action, the card collapses to a single line: `✓ Allowed (once) · web_fetch` or `✗ Denied · web_fetch`. On page reload, already-actioned cards remain collapsed.

**Lifecycle event cards** — events such as `run.completed`, `run.failed`, `review.requested`, and the merge outcome are shown as flat cards outside any turn group, with a colour-coded icon and badge. When the agent reports `run.outcome(achieved: false, ...)`, the `run.completed` lifecycle card renders with an amber warning indicator and the reason text instead of the normal green success badge.

#### Review gate

When the run reaches the review gate, a diff viewer and an inline review panel appear below the timeline. See [Review a run](#review-a-run) below.

### Review a run

The review panel is embedded in the watch screen. When the agent emits a `review.requested` event, the watch screen fetches the run, shows the diff, and renders a details table alongside the review panel. The panel shows the tree hash and two buttons: Approve and Decline.

**Approve** can have three outcomes:

- **Merge succeeds** — the run transitions to `merged` and a green success badge appears showing the commit hash. The transition happens live via the `merge.completed` event on the SSE stream; you do not need to refresh the page.
- **Retriable block** — the server returns a 409 with an error message (for example, because there are uncommitted local changes). The panel shows the server message as a warning bar and keeps Approve and Decline active so you can fix your working tree — commit or stash the changes — and approve again.
- **Terminal merge failure** — if the merge fails in a way that cannot be retried, a red `merge_failed` badge appears with the failure reason. The review panel re-appears so you can attempt another approve after resolving the conflict manually, or decline the run.

**Decline** records the decision and the run transitions to `declined`.

### Artifact browser

A resizable split-panel layout divides the watch screen: the timeline occupies the left panel and the artifact browser occupies the right panel. The browser is available whenever the run has a worktree — from the moment the run starts through any awaiting-review state.

The browser has two tabs:

**Files tab** — shows the worktree's file tree. Files modified by the agent appear with a colour-coded status badge (`A` added, `M` modified, `D` deleted). Clicking a file opens it in a read-only Monaco editor or CommonMark preview (for `.md` files). The editor shows a diff view highlighting agent changes against the originating branch baseline. The file list polls every few seconds on in-progress runs to pick up new changes as the agent writes them. On 409 (worktree unavailable), polling stops automatically.

**Changes tab** — shows a flat list of all changed files with `+N / -N` line counts. Clicking a file opens the same Monaco diff view.

## Structure

```text
src/
  api/
    types.ts            API shapes
    client.ts           fetch-based API client
    apiClient.ts        shared client built from config
    sse.ts              run-stream hook
  components/
    RunSubmitForm.tsx
    RunWatcher.tsx      orchestrates the watch screen
    RunHeader.tsx       run ID + stream status indicator
    Timeline.tsx        renders the ordered list of timeline items
    TurnGroup.tsx       one agent turn: divider + steps
    TurnDivider.tsx     "Turn N · X steps" header with active/done indicator
    AgentMessageBubble.tsx  streaming plain-text or settled Markdown bubble
    ToolCallCard.tsx    collapsible card: icon + title + args + result/error
    LifecycleEventCard.tsx  flat card for run/review/merge lifecycle events
    ReviewPanel.tsx
    RunDetail.tsx
    DiffViewer.tsx      syntax-highlighted unified diff component
    ArtifactBrowser.tsx resizable split-panel file tree + Monaco/markdown viewer
    FileViewerModal.tsx read-only Monaco diff viewer and CommonMark preview modal
  timeline/
    types.ts            discriminated union types for reducer state
    reducer.ts          pure grouping reducer (turns, steps, streaming state)
    useTimelineItems.ts hook that feeds the SSE event list into the reducer
  pages/
    HomePage.tsx
    WatchPage.tsx
  App.tsx               Fluent provider and routing
  main.tsx              entry point
  config.ts             reads VITE_API_URL and VITE_API_KEY
```

