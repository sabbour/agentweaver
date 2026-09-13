---
title: Submitting and Watching Runs
---

# Submitting and Watching Runs

A **Run** is a unit of work that Agentweaver executes on your behalf. You describe what you want in plain language; the coordinator agent scopes it, confirms it with you, and then drives the team of agents to produce the result — all inside isolated sandboxes.

## Starting a run

### Model provider context

Supported AI actions show three provider states:

- **Expected provider** shows the provider selected before submission.
- **Using** shows the provider for active execution.
- **Used** shows the provider recorded for completed execution.

Every execution surface uses the same compact provider indicator. It stays on one line,
truncates before it can displace primary controls, and wraps beside its action only when
the surrounding layout is narrow. The visible indicator contains the phase and provider
kind. Its tooltip and accessible description contain the full model and scope details.
Multi-action groups show one visible indicator while each AI action retains the complete
accessible provider description.

Screen readers announce changes, including replacement by another provider of the same kind.
The UI and API do not expose credentials, account names, or provider-binding identities.

A pending readiness check says **Checking AI provider readiness**. A failed readiness
request says that the check could not complete and offers **Refresh provider**; it does
not claim that the provider itself is unavailable. Provider setup guidance appears only
when the API returns `effective_model_provider.state: "unavailable"`. If a completed run
has no recorded provider event, the UI says **Provider details not recorded** or omits the
provider footer instead of inferring which provider ran.

For failed runs, the header shows the provider recorded in the run once. The retry action
keeps its newly prepared provider in its accessible label. A second visible **Expected
provider** label appears only when it differs from the recorded run provider, so operators
can review the change before retrying.

Agentweaver records an immutable provider and capability snapshot when a run starts. Provider
enablement, disablement, or configuration changes apply to future runs only; they do not switch
or cancel an in-flight run during assembly, revision, recovery, or replay. The UI continues to
show the provider accepted for that run.

The platform still revalidates that the accepted provider credential remains usable immediately
before each covered model call. A revoked or expired credential fails closed with
`409 model_provider_changed` before model invocation and includes a redacted replacement context.
The UI shows the replacement as new **Expected provider** context.

The API binds an execution key to the caller, operation, project, and provider configuration.
The key expires after five minutes.
Missing or expired keys require new context.

Coordinator outcome drafting and Preview analysis use the effective model provider, including
a configured BYOK provider. Other coordinator classifier actions can still require GitHub
Copilot when their execution path is Copilot-specific.
Queued work retains its accepted provider fingerprint and stops if the provider changes before pickup.

Custom API clients prepare context through `POST /api/ai/execution-context`.
The request contains `operation` and the applicable `project_id` or `run_id`.
The response contains `phase: "prepared"`, `execution_key`, `expires_at`, and `effective_model_provider`.
The provider object contains redacted kind, scope, type, model, availability, and comparison data.

Send `execution_key` in `If-Model-Provider-Key` for the corresponding action.
Do not display or send the public `provider_key` as authority.
It is an opaque comparison fingerprint, not an execution key.
If preparation or a guarded invocation returns `409 model_provider_changed`, stop the operation,
use the replacement context, and prepare a new key. If the provider is unavailable, configure an
eligible BYOK provider or the operation's required GitHub Copilot capability, then retry.

### Coordinator orchestration

From inside a project, open the **Board** page and click **Start task** (or use the **Start task** button from the runs list or Flow page).

Enter your task as a natural-language goal in the **Goal** field:

> "Refactor the authentication module to use JWT and add integration tests."

The action buttons use the concise **Goal required** indicator until the required Goal
field contains text; their accessible description remains **Enter a goal to continue**.
While provider preparation is still in progress, the compact indicator says **Checking
provider** and its accessible description says **Checking AI provider readiness**. This
is not a provider failure. Provider setup guidance appears only when the resolved provider
is actually unavailable.

Click **Start task**. The coordinator orchestration begins and you're taken to the topology view.

![Start orchestration dialog](/guide/images/start-orchestration.png)

::: tip Be specific about outcomes
Describe what success looks like, not just what to do. The coordinator uses your description to draft an OutcomeSpec — the more concrete your goal, the sharper the spec.
:::

#### Workflow selection

When you click **Start**, the coordinator automatically selects the best-fit workflow for your goal using an LLM pass over your team's available workflows, their descriptions, and team roles. The selection and its rationale are shown in the coordinator conversation.

To override: open the **Workflow** dropdown in the **Start task** dialog and choose a specific workflow. The dropdown shows only workflows with a manual trigger; it is hidden when only one workflow is available. Leave it on **Auto** to let the coordinator choose.

You can also override mid-conversation by typing `use {workflow-id}` before confirming the OutcomeSpec.

### Preview your work

Runnable outputs are most useful when reviewers can open them live. Software delivery and bug-fix workflows include a platform `build_test` gate that runs after RAI and before human review. It builds, tests, starts web/service artifacts when applicable, verifies the actual bound port,
and registers a sandbox preview with `start_preview(port=PORT, session_id=SESSION_ID)`. The
`session_id` is the value returned by `observe_bound_port`; it lets the API confirm that the
healthy preview process is still alive before publication.

For a custom workflow without that gate, ask the coordinator to have an agent build and start
the app in its sandbox. The agent can call `start_preview(port=PORT)` and optionally include
the observed session ID. If registration times out, check `run_status` and retry only after
confirming that the sandbox is still running. On non-Kubernetes backends, it provides local
run instructions instead.

The supervised preview process accepts either a worktree-relative working directory or the canonical absolute path of the worktree (or one of its subdirectories). Paths outside the run worktree, traversal escapes, and symlink or junction escapes remain blocked by the sandbox policy.

## The OutcomeSpec confirmation

Before any agent work starts, the coordinator:

1. Reads the team's existing memories and decisions
2. Selects the best-fit workflow for your task
3. Drafts an **OutcomeSpec** — a short, structured statement of:
   - **Goal** — what you're asking for
   - **Desired outcome** — what success looks like
   - **Scope** — what is and isn't in scope
   - **Assumptions** — what the coordinator is assuming
4. Presents the spec for your review (and may ask targeted clarifying questions)
5. Waits for your confirmation

You review the OutcomeSpec in the conversation panel. If it looks right, confirm. If you need to adjust scope or correct an assumption, say so in the chat — the coordinator revises and re-presents.

::: warning No work dispatched until you confirm
The coordinator will not start any agent work until you explicitly confirm the OutcomeSpec. This gate is enforced by the platform.
:::

## The WorkPlan and topology view

Once you confirm the spec, the coordinator:

1. Decomposes the OutcomeSpec into a **WorkPlan** — a dependency graph of subtasks
2. Assigns each subtask to the best-fit agent and selects a model — an explicit run `modelId` (or the project's GitHub Copilot default) pins every subtask; otherwise each subtask uses its role's default model
3. Dispatches independent subtasks in parallel; dependent ones run in series

There is a short transition while the WorkPlan and integration branch are being created. During
that transition, the coordinator's ordinary changed-files endpoint returns an empty list, and
the collective assembly-files endpoint also returns an empty list. `GET /api/runs/{id}/work-plan`
returns the typed `404 work_plan_not_found` response until the plan is persisted; this means
"not ready yet" for an existing coordinator run, not that the run itself is missing. Collective
changed files appear through `GET /api/runs/{id}/assembly/files` once assembly has started.

You see the **topology view** — a live graph of the entire orchestration.

![Run topology](/guide/images/run-topology.png)

The graph shows:

- **Coordinator node** at the center
- **Agent nodes** for each dispatched subtask, labeled with the agent's name and role
- **Edge status** — running, completed, failed, awaiting
- **Coordinator status badge** in the header (Dispatching → Awaiting assembly → Assembling → In review → Complete)

### Comparing topology layouts

The **Topology layout** control is available on the live run graph. **Balanced grid
(current)** is the default layout engine. Choose **Legacy staircase (comparison)** only
to compare card placement while diagnosing a rollout; it does not alter the run,
its nodes, dependencies, edge direction, or status data. The selection is remembered
locally and is also available in workflow graph viewer and editor canvases.

Click any agent node to open its individual **execution view** and watch that agent's work in detail.

## Steering mid-run

While a coordinator orchestration is active, you can intervene from the topology view:

| Action | Effect |
|---|---|
| **Send directive** | Give the coordinator new direction; it relays to affected agents |
| **Redirect child** | Change a running child agent's focus at its next turn boundary |
| **Amend the plan** | Ask the coordinator to update the WorkPlan |
| **Stop run** | Immediately stop the orchestration; takes effect on running agents right away |

::: tip Stop is immediate; redirect is at the next turn
Stopping a run takes effect immediately on all running agents. Redirecting or amending takes effect at the next agent turn boundary — the current turn completes first.
:::

After sending guidance, the Messages pane records a durable acknowledgement with its
**queued** or **applied** outcome and its target/scope. This acknowledgement means the
coordinator accepted the direction; it is not evidence that a child advanced. A child
waiting for its own approval remains blocked until that approval is resolved. When live
updates are reconnecting or disconnected, the pane marks the displayed state as possibly
stale until an explicit progress event arrives.

## Watching an execution live

Click any agent node in the topology view to open its **execution view**. This streams every event from that agent's run in real time.

![Execution live view](/guide/images/execution-watch.png)

### The workflow pipeline

Each agent run passes through a pipeline shown as a left-to-right node graph. For coordinator child runs (subtasks), the pipeline is:

```
Agent → Assemble-ready
```

RAI, Build & Test, Human Review, Merge, and Scribe run once on the **combined** output of all child agents — not per subtask. In the built-in software workflows, Build & Test runs after RAI and before Human Review.

Loopback edges appear when RAI or a reviewer requests changes and the agent needs to revise.

### Event timeline

The event timeline lists every event the agent emitted:

| Event type | What it shows |
|---|---|
| **Agent message** | The agent's text output — reasoning, summaries, responses |
| **Tool call** | A tool the agent invoked (file read, write, shell command, search, etc.) |
| **Tool result** | The output returned from that tool call |
| **Question** | A clarifying question the agent is asking you |
| **System event** | Pipeline transitions (stage started, stage completed, RAI verdict) |

Events stream live over SSE and are persisted before fan-out. If you open the page after the run completes, all events load from the persisted log.

When you expand a tool call, its arguments are shown as labeled fields. Long values, such as file contents, can be expanded individually without obscuring the other arguments.

### Question gate

When an agent asks a question, the run **pauses** at a question gate until you answer. The question appears in the event timeline with an answer input. Type your answer and submit — the agent continues.

### Tool approval

If the run's sandbox policy requires approval before executing certain tool calls, an **approval banner** appears at the top of the page. Click **Jump to approval** to scroll to the pending tool call, then approve or deny it.

Enable **Auto-approve tools** in the run header to skip per-call approval prompts for the remainder of that run.

Preview exposure approvals also remain visible in the notification bell, a persistent toast, and
the timeline until resolved. Their project-configurable window defaults to 30 minutes. If one
expires, choose **Retry approval** to create a fresh approval attempt while keeping the run and
healthy preview process in place.

Selecting **Review now** from an approval notification opens that exact run. If the notification
does not include a valid run target, Agentweaver explains that the approval cannot be opened
instead of sending you to a different run.

## RAI check

Each agent run passes a **Responsible AI (RAI)** check before its output proceeds. If the check flags the output, the run automatically loops back — the agent revises and the check re-runs. This loopback is visible as a "Revise" edge in the pipeline graph. If the check passes, the run proceeds to the next stage (human review or assembly).

## Run states

| Status | Meaning |
|---|---|
| **Running** | The run is actively executing |
| **Awaiting assembly** | All subtasks have finished; coordinator is collecting results |
| **Assembling** | Coordinator is assembling the combined output |
| **In review** | Awaiting your approval |
| **Completed / Merged** | Merged successfully |
| **No Changes** | The agent finished but made no file changes |
| **Failed** | Unrecoverable error |
| **Declined** | You rejected the changes |
| **Merge Failed** | The merge step failed (e.g., a conflict on the target branch) |

### Agent turn infrastructure failures

The run timeline reports `agent_turn_internal_error` when Agentweaver must supply a
structured fallback: the pod bridge's turn throws without first emitting a structured
`run.failed`, the worker receives an unstructured `run.failed`, or the A2A stream ends
on an unsupported or unset event. This is an execution-infrastructure failure, not a
model request for changes. The fallback is marked `retryable: true` because the
surrounding workflow may retry or redispatch the turn; it does not mean that the
interrupted turn completed successfully.

Agentweaver does not replace more specific outcomes with this fallback:

- a cancellation requested by the caller remains a cancellation;
- an existing typed timeout or failure keeps its own error code and retryability;
- an unavailable project or platform Copilot connection remains
  `model_provider_connection_required` and stops before workflow fallback validation;
- other A2A exceptions become `a2a_transport_failure`, with retryability determined by
  the transport failure;
- a clean A2A stream end without `agent.turn.end` becomes the retryable
  `agent_host_turn_incomplete`.

Before a remote A2A failure reaches the durable event stream, Agentweaver keeps only a
bounded allowlisted error code and retryability. It derives the one-line diagnostic
message from those fields; it never uses remote `message`, `detail`, prompt, tool-input,
or tool-output text. Invalid, secret-bearing, path-like, stack-like, nested, and
unrecognized A2A fields are replaced; they are never retained for later redaction. See the
[Operations Guide](./operations#diagnosing-agent-turn-infrastructure-failures) for
operator guidance.

### Failed-run diagnostics

For a failed run, the Coordinator page shows the terminal diagnostic when one was
persisted. API and MCP clients can read the same projection through
`GET /api/runs/{id}/terminal-diagnostic` and `run_failure_diagnostic`.
The project's **Observability → Traces** page shows the same diagnostic beside failed
traces. Use **Show failed only** to focus investigation. Correlation IDs are links back
to that trace's focused view; they are navigation handles, not raw telemetry payloads.
The Coordinator diagnostic includes a **View trace** action and tells you whether retry is
available without repeating the provider or error code in separate status fragments.

### Execution bottleneck evidence

Select a span and open **Attributes** to inspect **Execution diagnostics**. Agentweaver
records safe process start/end times plus host-process CPU time and memory working-set
snapshots when the host makes them available. The panel also reserves queue and dispatch
timestamps for environments that emit them.

This evidence is correlated with the selected agent or tool span, but it is deliberately
not a bottleneck verdict. Disk and network I/O, sandbox-process resource usage, capacity
pressure, and unrecorded queue phases display **Not recorded**. When evidence is missing
or incomplete, the panel says that no bottleneck is inferred rather than attributing a
delay to CPU, memory, I/O, network, or capacity. No commands, command output, prompts,
credentials, paths, or arbitrary dependency payloads are added to trace telemetry.

Provider snapshot failures are separate from provider health and authorization:

- `model_provider_snapshot_unavailable` means Agentweaver could not load the immutable
  provider snapshot saved for the run.
- `github_copilot_capability_snapshot_unavailable` means the run-bound Copilot capability
  snapshot was missing, expired, or could not be redeemed.

Both diagnostics recommend retrying to create a new run snapshot. They do not claim that
the configured provider changed, became unavailable, or requires reconnection. Reconnect
GitHub only when a new run reports `github_copilot_auth_required`.

The projection contains only a bounded error code, safe message, component,
timestamp, retryability, allowlisted correlation IDs, and sanitized cause types.
AgentHost-generated internal failures and pre-launch provider failures include a
server-generated correlation ID, the active trace ID when available, and a bounded
exception-type chain. If an earlier
best-effort agent operation failed but the Coordinator later terminalized for another
reason, the projection uses the latest terminal failure instead of the earlier recovered
failure.
It does not expose raw pod logs, stack traces, prompts, tool payloads, HTTP headers,
credentials, tokens, or keys. Project Viewers can read diagnostics for their project.
Projectless runs remain visible only to their submitting owner. Unauthorized and
unknown run IDs both return not found from the diagnostic endpoint.

The persisted event timeline and SSE replay apply the same terminal-failure projection
to legacy `run.failed` rows. Their sequence, event type, cursor, timestamp, and access
rules are preserved, but the payload is limited to the safe message, allowlisted code,
and retryability fields.

## Runs list

The project page shows all runs in reverse chronological order. Each row shows:

- Run status badge
- Task description
- Start time
- **Topology** button

From the runs list you can also **Abandon** an in-flight run (discards pending changes) or **Delete** a completed run from the history.

## Sandboxed execution

Each agent runs inside a **dedicated git worktree** branched from the project's working directory. Agents cannot reach outside their worktree unless the sandbox policy explicitly allows it. The originating branch is never modified during a run — only after you approve and the merge step completes.

While a child is running, its **Changes** and **Files** views refresh automatically. If its worktree is still provisioning, the views show that state instead of an empty result and continue polling until current artifacts are available.

![Sandboxed execution: Project working directory, Agent worktrees, Changes in worktrees, Assembled combined diff, Merge to branch, Worktrees discarded](../diagrams/canonical-sandbox-experience.png)

<!-- Rendered from ../diagrams/src/canonical-sandbox-experience.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

## See also

- [Workflow selection — Deep Dive](/deep-dive/workflow-selection) — full algorithm, override hierarchy, and trigger filtering
- [Coordinator reference — Workflow selection](/reference/coordinator#workflow-selection-how-the-coordinator-picks-the-process-to-run) — precedence table and API details