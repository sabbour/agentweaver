# Runs, Board, and Live Watch

A run is the visible unit of work in Agentweaver: you submit an outcome, watch the coordinator and agents execute it, review the result, and keep the project board clean as work moves from idea to done. The web UI and MCP tools expose the same experience at different distances: the UI gives you a live workspace and Kanban board, while MCP lets another assistant submit, inspect, stream, retry, and archive runs without leaving its conversation. This guide follows the user journey from submission to board tracking to live watch.

Scope: this page describes the run submission, board, and live watch experience; detailed review, workspace browsing, and merge decisions live in [Review, workspace & merge](./review-workspace-merge.md).

## The mental model

Agentweaver separates **work intake** from **run execution**.

- A **task** is an item on the project board. It may sit in **Backlog** or **Ready** before the coordinator claims it.
- A **run** is active execution. It has a run id, status, event stream, timeline, artifacts, review state, and lifecycle history.
- The **board** is the shared operational view. It shows tasks that have not started and run cards that are moving through execution, review, and completion.
- The **embedded live timeline** shows the event stream as turn groups, agent messages, tool calls, lifecycle cards, approvals, and terminal status inside the orchestration/session experience.
- **MCP tools** provide parity for assistants and automations: start with `coordinator_start`, inspect with `run_status`, stream with `run_watch`, manage intake with `backlog_*`, and clean up with `run_archive` or `backlog_archive_task`.

The product shape is intentionally simple: capture work, rank it, let the coordinator claim Ready work, watch live execution, review the result, then archive what no longer needs attention.

## Submitting work

Submitting work answers one question: **what outcome should Agentweaver produce, and where should it work?**

All web-started work creates a coordinator orchestration. On the project **Board**, select **Start task**. Enter a **Goal** and, if needed, select a **Workflow**.

Select **Define Outcome** to review an OutcomeSpec before child work starts. Select **Direct** to start a coordinator run from the goal without that confirmation step.

### Project-board submission

Most day-to-day work starts on the project page. The page has the project title, a **Start task** action for starting coordinator work, the Kanban board, and a runs list below the board.

The board's capture bar says **Capture a task into Backlog**. Type a short title and select **Add** or press Enter. The task appears in **Backlog** as a draggable task card. You can then edit the card to add a longer **Description**, choose a workflow override, move it to **Ready**, or archive it if it no longer matters.

The board also supports quick add from each intake column:

- **Add to Backlog** captures a task directly into Backlog.
- **Add to Ready** captures a task and immediately promotes it to Ready.
- **Send all to Ready** bulk-promotes every Backlog task while preserving their relative order.

Use Backlog when work is still being shaped. Use Ready when the item is committed enough for the coordinator to claim.

### MCP submission

MCP starts the supported run flow with `coordinator_start`. Use backlog tools when work must wait in the queue.

## The board experience

The board has **six logical buckets**, rendered as four main lanes — **Backlog**, **Ready**, **Active**, **Done** — and a separate **Needs attention / review** section for **Human Review** and **Problems**.

![Cropped project board capture showing the four main flow lanes, without the separate attention and review section](/screenshots/project-board.png)

This existing capture is cropped to the main flow. It does not show the separate Human Review/Problems area and is not evidence of six side-by-side columns.

| Logical bucket | What the user sees | Who moves cards there |
| --- | --- | --- |
| **Backlog** | Captured work that is not yet committed. | The user. |
| **Ready** | Ranked work the coordinator may pick up next. | The user, then the heartbeat claims from it. |
| **Problems** | Failed, blocked, declined, or otherwise attention-needed runs. | The coordinator and run lifecycle. |
| **Human Review** | Runs waiting for a person to approve or request changes. | The coordinator and review flow. |
| **Active** | Runs currently moving through coordinator workflow. | The coordinator and heartbeat. |
| **Done** | Completed or merged work. | The coordinator, review, and merge flow. |

The bucket definitions are independent of their main-flow or attention-section placement:

- **Backlog** — "Captured but not yet committed to. Things you're considering."
- **Ready** — "Committed work that the coordinator and Ralph monitor may pick up next."
- **Problems** — "Blocked, failed, declined, or otherwise needs attention."
- **Human Review** — "Work waiting for a person to review or approve."
- **Active** — "Work currently moving through the coordinator workflow."
- **Done** — "Completed or merged work."

The board can be zoomed with the board controls or Ctrl+scroll so all workflow columns fit on screen. The Done column can hide older terminal history; **Show older** reveals collapsed completed cards, and **Show less** returns to the shorter view.

### Task cards in Backlog and Ready

Task cards show the work before it becomes a run. A card includes:

- the task title,
- optional description,
- the identity that captured it,
- a workflow menu,
- an edit action,
- an archive action.

The workflow menu lets you pin the task to a specific valid workflow or return it to **Use project default**. This is product-level steering before execution starts.

Users can drag task cards:

- within **Backlog** to rank work,
- within **Ready** to rank queued work,
- from **Backlog** to **Ready**,
- from **Ready** back to **Backlog**.

Dragging to **Problems**, **Human Review**, **Active**, or **Done** is rejected with **Only the coordinator moves work into the workflow.** Workflow columns reflect execution state, not manual placement.

### Run cards in workflow columns

Run cards appear once work is claimed or submitted as execution. They are read-only from a workflow-position perspective: the coordinator owns their movement across Problems, Human Review, Active, and Done.

A run card shows:

- the task or run title,
- a status badge,
- the current stage, assembly stage, or work-plan status,
- the agent name when a specific agent is attached, otherwise **Coordinator**,
- **Approval needed** when a pending tool approval exists,
- **Retry** when the run is failed or merge-failed,
- an archive button,
- a **Retried from** link when the run was created by retry.

Selecting a run card opens the coordinator run detail page for that run. From there, the user can inspect the topology, child progress, live timeline, and review path.

### Moving, reordering, and archiving

The board supports two kinds of user movement:

1. intake movement for task cards, and
2. cleanup movement through archive actions.

Intake movement is rank-sensitive. Reordering a task within Backlog or Ready changes its zero-based position in that column. Moving a task between Backlog and Ready can place it at a target position or append it to the end. Ready order matters because heartbeat pickup reads from the Ready queue.

Archive removes a task or run from active board projections. Archive is not review, rejection, or deletion; it is a visibility decision. Use it when the item should no longer occupy the board.

### Retry from a card

Failed and merge-failed run cards show **Retry**. Eligible coordinators can resume **in place**, retaining their run id and history; other retries create a **fresh linked run**. The card navigates to the returned `run_id`, which may therefore be the same id. Only a fresh retry adds a new run and **Retried from** relationship.

### MCP board tools

MCP exposes the board with the same operational shape:

| Tool | What it does |
| --- | --- |
| `backlog_get_board` | Returns Backlog, Ready, Problems, Human Review, Active, and Done for a project. It can include terminal history when requested. |
| `backlog_capture_task` | Captures a new task into Backlog with a required title and optional description. |
| `backlog_edit_task` | Edits a task title and description before it is claimed. |
| `backlog_move_to_ready` | Moves a task from Backlog to Ready, optionally at a zero-based target position. |
| `backlog_move_to_backlog` | Moves a task from Ready back to Backlog, optionally at a zero-based target position. |
| `backlog_reorder_task` | Reorders a task within its current Backlog or Ready bucket. |
| `send_all_backlog_to_ready` | Bulk-promotes all Backlog tasks to Ready, preserving order and safely doing nothing when Backlog is empty. |
| `backlog_archive_task` | Archives a task off the active board; if the task is already claimed, its linked coordinator run card is archived too. |

Use `backlog_get_board` as the MCP snapshot equivalent of opening the web board. Use movement tools only for Backlog and Ready. For active runs, use run tools such as `run_status`, `run_watch`, `run_retry`, and `run_archive`.

## Inspecting a run live

The live inspection experience answers: **what is the agent doing right now, what has it already done, and what needs my attention?** Standalone workflow and execution run pages have been retired; run details now appear inside the coordinator orchestration page, embedded task/session panels, the artifact browser, and MCP tools such as `run_watch`.

Embedded run inspection surfaces show:

- the owning coordinator or task context,
- a stream status such as **Connecting**, **Streaming**, **done**, or **error**,
- artifact panels for run workspaces when available,
- timelines that update as events arrive,
- review or merge resolution badges after terminal review events.

Timeline surfaces auto-scroll while the user stays near the bottom; if the user scrolls up, new events continue arriving without forcing the viewport down.

### Browsing artifacts

Run artifacts are shown through a shared **Artifact Browser** used everywhere a workspace is
inspected — the per-run panel, each per-agent session panel, and the coordinator run view. It has two
views: a compact **Changes** list that flattens the run's changed files for a quick scan, and a
**Files** tab that renders the workspace as a real folder tree you can expand and drill into.
Selecting a file opens its diff or content. Because the coordinator run now uses the same browser,
its assembled collective artifacts are inspectable in the same way as any child run's, rather than
only through the timeline.

For an available sandbox preview, follow the run's preview controls. The API manages preview registration and lifecycle; browser application traffic uses **Gateway routing** to the sandbox, not an API data-plane proxy. See [Sandbox browser preview](./sandbox-browser-preview.md). The former preview-dialog image was a placeholder and is omitted.

Stream status is not the entire lifecycle. A run can be **awaiting_review** while the stream is done; lifecycle cards provide the domain meaning.

### The timeline

The timeline is a projection of the run event stream. It is not a chat transcript and not a raw log dump. It groups related events into readable units:

- **turn groups** for agent turns,
- **agent message bubbles** for content the agent emits,
- **tool-call cards** for tools the agent uses,
- **lifecycle cards** for run, review, merge, sandbox, coordinator, and subtask events,
- **workflow step cards** for pipeline step transitions.

The timeline announces itself as **Run timeline** and behaves like a live log while the run is active. If the local buffer drops older entries during a very long run, the page shows how many older events are not currently shown.

Open an orchestration, then select a task or session to read this timeline. No standalone Watch/Execution page or placeholder timeline capture is needed to explain that navigation.

### Turn groups

A turn begins when the agent starts a turn and ends when the turn-end event arrives. While a turn is active, tool calls appear inline and expanded so the user can see live progress. Once the turn completes, consecutive tool calls collapse into a compact **Used N tools** row unless errors make them important enough to expand by default.

This gives the page two useful modes: verbose while work is moving, compact when reviewing after completion.

Agent messages can stream as deltas before becoming a settled message. The UI shows the message bubble as streaming while deltas arrive, then settles it when the full message arrives. Intent annotations, such as report-intent style summaries, render as compact muted lines so tool clusters have a human-readable reason without taking over the page.

### Tool-call cards

A tool-call card answers what tool was called, whether it is pending or settled, what arguments were passed, and what result or error came back.

Each row is a single compact line: an action-specific FluentUI icon, a human-readable title, and
muted secondary metadata. The leading icon reflects the kind of action — for example a document icon
for reads, a magnifier for searches, a folder for listings, an edit glyph for edits, and a trash
glyph for deletes — so a cluster of calls is scannable at a glance. The status icon settles once the
call resolves: only genuinely pending calls show a spinner, while completed calls show a check (or an
error or warning state). Completed tool calls no longer show a stuck clock or a perpetual spinner,
because still-pending calls are force-settled when the turn closes. Sandbox violations show a warning
style and a **sandbox** badge, and a `run_command` result with a non-zero exit code also shows a
warning even when the tool call itself returned a result.

Selecting an expandable card reveals **args**, result output when it is more than a plain `ok`, and error or violation text. Large blocks are truncated with the total character count so the page stays responsive.

### Lifecycle cards

Lifecycle cards translate domain events into scannable milestones. Common cards include:

| Event family | What the user sees |
| --- | --- |
| Run completion | **run.completed** with a summary, or a warning if the agent reported the outcome was not achieved. |
| Run failure | **run.failed** with the failure message or summary. |
| Review | **review.requested**, **review.approved**, **review.declined**, **review.changes_requested**. |
| Revision | **revision.started** when requested changes send work back to the agent. |
| Merge | **merge.started**, **merge.completed**, **merge.failed**. |
| Sandbox | **sandbox.selected** and sandbox warnings. |
| Coordinator | outcome spec, work plan, subtask dispatch, children complete, assembly, RAI, review, merge, scribe, completion, failure, block, or decline. |

Tool approvals render as prominent cards labeled **Tool Approval Required**. They show the tool name, URL when relevant, intention text when provided, and actions such as **Allow once**, **Allow this run**, **Allow tool**, **Always allow (session)**, and deny. Once resolved, the approval collapses into a concise resolved line.

### Status transitions the user sees

Agentweaver uses both stream statuses and run lifecycle statuses. Users mostly see these run states:

| State | Meaning | Typical board location |
| --- | --- | --- |
| **Backlog** | A captured task is not yet ready for pickup. | Backlog |
| **Ready** | A task is ranked and available for coordinator pickup. | Ready |
| **Running** or **in_progress** | The run is actively executing. | Active |
| **Dispatching** | A coordinator run is selecting and starting child work. | Active |
| **Awaiting assembly** | Child runs are done and the coordinator is collecting results. | Active |
| **Assembling** | The coordinator is building the combined output. | Active |
| **Awaiting Review** or **In review** | Work is ready for human review. | Human Review |
| **Merging** | Approved work is being integrated. | Active or Human Review while it resolves |
| **Completed** | The run finished successfully. | Done |
| **Merged** | The reviewed result merged successfully. | Done |
| **No Changes** | The run completed but produced no file changes. | Done |
| **Failed** | Execution hit an unrecoverable failure. | Problems |
| **Merge Failed** | Execution completed, but integration failed. | Problems |
| **Declined** | Review rejected the candidate. | Problems |
| **Blocked** | The coordinator cannot proceed without intervention. | Problems |
| **Archived** | The task or run is hidden from active board projections. | Not shown on the active board |

The [shared lifecycle above](#the-mental-model) is the single state diagram. The server projects persisted run state first, then work-plan state/stage: failed, declined, and merge-failed runs go to **Problems**; completed, merged, or assemble-ready runs go to **Done**; awaiting-review runs go to **Human Review**. A still-active coordinator's plan can also place it in review or Problems. **Merging** is not a separate bucket and can remain in Human Review while its plan still says review; otherwise it is Active. Archive is a visibility flag, not an execution transition.

## The event stream behind live inspection

Live inspection is powered by the run event stream over SSE. Conceptually, every important run fact becomes an ordered event:

```text
sequence
type
payload
```

The SSE endpoint emits frames with the event sequence as `id`, the event type as `event`, and the JSON payload as `data`. When the stream is complete, it emits a final `done` event. The frontend uses a fetch-based SSE reader so it can attach authorization headers and send reconnect cursors.

The stream has two user-visible guarantees:

1. **Replay**: opening or refreshing a run can rebuild the timeline from persisted events.
2. **Tail**: while the run is active, new events arrive live without polling.

The backend writes events durably before live fan-out. Live channel delivery is a low-latency path, not the source of truth. If a browser disconnects, reconnects, or opens the run after completion, it can resume from the last sequence it saw.

For the implementation-backed replay/tail model, see the shared [durable event stream](../run-event-stream.md). Replaying persisted events restores the explanation of a run; it does **not** guarantee transparent re-execution of an interrupted remote A2A model turn.

### Reconnect and replay

When the UI reconnects, it sends `Last-Event-ID` with the highest sequence processed. The backend replays persisted events after that sequence, then tails live events. The frontend also ignores duplicates with old sequence values.

The user experience is:

- a temporary header message such as **Stream disconnected; reconnecting in 4s.**,
- automatic reconnect with increasing delays,
- missed events replayed into the same timeline,
- terminal events stopping the reconnect loop.

If the stream reconnects too many times without success, the header changes to **error**. The run history is still durable; a refresh or later reopen can replay persisted events when the API is reachable.

### MCP watch with `run_watch`

`run_watch` is the MCP equivalent of keeping the live watch page open. It connects to the same run stream and reports progress notifications:

- agent messages and message deltas become progress text,
- `tool.call` becomes **Tool call: \<name\>**,
- `tool.result` reports that a tool result was received,
- `run.completed` reports **Run completed**,
- `review.requested` reports **Run awaiting review**.

When streaming ends, `run_watch` fetches and returns the final run state. Use it when an assistant should stay attached until completion or review. Use `run_status` when you only need a point-in-time snapshot.

### Snapshot with `run_status`

`run_status` returns the current run detail. It is the right tool for:

- checking whether a known run is still active,
- inspecting the same or new run returned by retry,
- reading final state after `run_watch`,
- deciding whether to hand off to review, retry, or archive.

In the UI, the equivalent snapshot appears as run badges, board projection, and run detail state. In MCP, `run_status` is the compact source of truth for automation decisions.

## Retry and archive

Retry and archive are the two main run-management actions after something has happened.

### Retry

Use retry when the original intent is still valid but the attempt failed. `run_retry` and web **Retry** share the server's recovery choice for eligible failed or merge-failed runs.

Retry behavior:

- eligible in-place coordinator recovery retains the run id and its history;
- fresh retry creates a new id and event stream linked to the failed source;
- the original history remains inspectable;
- the UI follows the returned id, rather than assuming a new orchestration;
- server eligibility, provider authorization, and retry-chain limits still apply.

Retry is especially useful for transient failures, stale branches, merge failures after the target moved, or execution issues that are fixed by updated context.

### Archive

Archive removes a run from active board and list projections. The MCP tool is `run_archive`; task-level cleanup uses `backlog_archive_task`; task cards and run cards expose archive buttons.

Archive behavior:

- keeps the distinction between completed history and active board attention,
- hides items that no longer need action,
- can be applied to run cards directly,
- can archive a linked run card when archiving a claimed backlog task.

Archive is not retry, review, cancellation, or deletion. It hides the item without stopping active execution. Use Stop/cancel when work must halt, and Delete only when the run and workspace should be removed.

## Edge cases and attention states

### Failed runs

Failed runs move to **Problems**. The card shows a danger-colored status and may expose **Retry**. Open the run before retrying when the failure reason matters; the watch timeline shows the failed turn, the tool call or lifecycle card that explains the failure, and any coordinator assembly failure detail.

For MCP, call `run_status` for the snapshot and `run_watch` only if the run is still streaming. If the failure is terminal and the intent is still valid, use `run_retry`.

### Runs awaiting review

Runs awaiting review move to **Human Review** and show review lifecycle cards in the timeline. The run is no longer just "working"; it needs a person to inspect the output, approve it, decline it, or request changes.

Hand off detailed review behavior to [Review, workspace & merge](./review-workspace-merge.md). The important board rule is that **Human Review** is a queue for people, not an error column.

### Pending tool approvals

A run can pause while waiting for a tool approval. The board card can show **Approval needed**, and the watch timeline shows **Tool Approval Required** with allow and deny actions. Until approval resolves, the agent may not progress past that tool call.

Use the approval scope carefully: **Allow once** is narrowest, **Allow this run** applies within the current run, **Allow tool** covers that tool for the rest of the run, and **Always allow (session)** lasts for the current server session.

### Reconnects, refreshes, and late opens

Refreshing the watch page does not erase the run's explanation. The page reconnects to the SSE stream and replays persisted events after the last known sequence. Opening a completed run replays its persisted history and then completes cleanly.

If the browser was disconnected during a long run, the durable stream fills the gap on reconnect. If the local page buffer has dropped older items because the run produced many events, the UI tells the user how many older events are not shown in the current timeline.

### Empty or unavailable workflow columns

If workflow columns are unavailable, the board shows a warning that workflow buckets may be empty until the API recovers. Backlog and Ready remain the user's intake model; workflow columns become trustworthy again when the API can project run state.

### Long outputs

Tool-call details can be large. The watch page truncates very large argument or result blocks and shows the total character count. This protects the live page while still exposing enough context to understand the call.

## Related reading

- [Overview](./00-overview.md)
- [Coordinator & orchestration](./coordinator-orchestration.md)
- [Workflows & backlog](./workflows-backlog.md)
- [Review, workspace & merge](./review-workspace-merge.md)
- [Submitting and Watching Runs](../guide/runs.md)
- [Board and Backlog](../guide/board.md)
- [Run event stream](../run-event-stream.md)
- [Events & Observability](../deep-dive/events-observability.md)
- [Sandbox browser preview](./sandbox-browser-preview.md) — open a live HTTPS preview of a server an agent started inside its run's sandbox pod, from the run/watch view.
- [Token usage monitoring](./token-usage-monitoring.md) — current run/graph and selected-range telemetry surfaces, including how to read AI Credit values.

<details id="diagram-context-canonical-board-lifecycle" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>The board is a projection</td></tr>
<tr><td>subtitle</td><td>Columns reflect persisted task and run state—not a separate workflow engine.</td></tr>
<tr><td>group-title0</td><td>Before and during execution</td></tr>
<tr><td>group-title1</td><td>Review / terminal outcomes</td></tr>
<tr><td>Backlog</td><td>Backlog</td></tr>
<tr><td>Backlog</td><td>Captured task; not queued</td></tr>
<tr><td>Backlog</td><td>task: backlog</td></tr>
<tr><td>Ready</td><td>Ready</td></tr>
<tr><td>Ready</td><td>Queued task</td></tr>
<tr><td>Ready</td><td>task: ready</td></tr>
<tr><td>Active</td><td>Active</td></tr>
<tr><td>Active</td><td>Work is in progress</td></tr>
<tr><td>Active</td><td>default non-review bucket</td></tr>
<tr><td>Problems</td><td>Problems</td></tr>
<tr><td>Problems</td><td>Failed / declined / merge failed</td></tr>
<tr><td>Problems</td><td>or assembly blocked / failed</td></tr>
<tr><td>Human Review</td><td>Human Review</td></tr>
<tr><td>Human Review</td><td>AwaitingReview / InReview</td></tr>
<tr><td>Human Review</td><td>or assembly stage: Review</td></tr>
<tr><td>Done</td><td>Done</td></tr>
<tr><td>Done</td><td>Completed / Merged / AssembleReady</td></tr>
<tr><td>Done</td><td>or plan Complete / stage Done</td></tr>
<tr><td>e1</td><td>queue</td></tr>
<tr><td>e2</td><td>claim + start</td></tr>
<tr><td>e3</td><td>await review</td></tr>
<tr><td>e4</td><td>finish</td></tr>
<tr><td>e5</td><td>problem</td></tr>
<tr><td>assurance-title</td><td>DEFAULT BUCKETS · NOT A NEW STATE MACHINE</td></tr>
<tr><td>assurance-line1</td><td>Configured workflow stages may replace the default run columns. Arrows summarize typical changes, not every path.</td></tr>
<tr><td>assurance-line2</td><td>The board polls persisted state. Ready tasks with unmet dependencies stay Ready; blocked is a flag.</td></tr>
<tr><td>Backlog</td><td>Entity</td></tr>
<tr><td>Backlog</td><td>BacklogTask</td></tr>
<tr><td>Backlog</td><td>State</td></tr>
<tr><td>Backlog</td><td>Run</td></tr>
<tr><td>Backlog</td><td>Not required</td></tr>
<tr><td>Backlog</td><td>Action</td></tr>
<tr><td>Backlog</td><td>Move to Ready</td></tr>
<tr><td>Ready</td><td>Blocked</td></tr>
<tr><td>Ready</td><td>Dependency metadata</td></tr>
<tr><td>Ready</td><td>Pickup</td></tr>
<tr><td>Ready</td><td>Atomic claim</td></tr>
<tr><td>Active</td><td>Input</td></tr>
<tr><td>Active</td><td>Coordinator run</td></tr>
<tr><td>Active</td><td>Status</td></tr>
<tr><td>Active</td><td>Non-review default</td></tr>
<tr><td>Active</td><td>Plan</td></tr>
<tr><td>Active</td><td>Dispatch / assembly</td></tr>
<tr><td>Active</td><td>Approval</td></tr>
<tr><td>Active</td><td>Separate pending flag</td></tr>
<tr><td>Problems</td><td>Failed / Declined</td></tr>
<tr><td>Problems</td><td>Merge</td></tr>
<tr><td>Problems</td><td>MergeFailed</td></tr>
<tr><td>Problems</td><td>Assembly</td></tr>
<tr><td>Problems</td><td>Blocked / Failed</td></tr>
<tr><td>Problems</td><td>Also</td></tr>
<tr><td>Problems</td><td>AssemblyDeclined</td></tr>
<tr><td>Human Review</td><td>AwaitingReview</td></tr>
<tr><td>Human Review</td><td>InReview</td></tr>
<tr><td>Human Review</td><td>Review stage</td></tr>
<tr><td>Human Review</td><td>Approve</td></tr>
<tr><td>Human Review</td><td>Resumes execution</td></tr>
<tr><td>Done</td><td>Completed / Merged</td></tr>
<tr><td>Done</td><td>AssembleReady</td></tr>
<tr><td>Done</td><td>Complete</td></tr>
<tr><td>Done</td><td>Done stage</td></tr>
<tr><td>backlog</td><td>No run is required yet; Move to Ready to queue work</td></tr>
<tr><td>ready</td><td>Unresolved dependencies stay here; Blocked is a flag, not a column</td></tr>
<tr><td>progress</td><td>Claimed tasks link to their run; Pending approval is a separate flag</td></tr>
<tr><td>failed</td><td>Blocked assembly is recoverable; Not every problem is terminal</td></tr>
<tr><td>review</td><td>Approval resumes the workflow; Approval alone is not Done</td></tr>
<tr><td>done</td><td>Persisted-state mapping; Not merely a clicked approval</td></tr>
<tr><td>notes</td><td>DEFAULT BUCKETS · NOT A NEW STATE MACHINE; Configured workflow stages may replace the default run columns. Arrows summarize typical changes, not every path.; The board polls persisted state. Ready tasks with unmet dependencies stay Ready; blocked is a flag.</td></tr>
<tr><td>groups</td><td>Before and during execution; Review and terminal outcomes</td></tr>
</tbody></table>
</details>
