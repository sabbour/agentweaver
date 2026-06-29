---
title: Submitting and Watching Runs
---

# Submitting and Watching Runs

A **Run** is a unit of work that Agentweaver executes on your behalf. You describe what you want in plain language; the coordinator agent scopes it, confirms it with you, and then drives the team of agents to produce the result — all inside isolated sandboxes.

## Starting a run

### Coordinator orchestration

From inside a project, open the **Board** page and click **Start task** (or use the **Start task** button from the runs list or Flow page).

Enter your task as a natural-language goal in the **Goal** field:

> "Refactor the authentication module to use JWT and add integration tests."

Click **Start task**. The coordinator orchestration begins and you're taken to the topology view.

![Start orchestration dialog](/guide/images/start-orchestration.png)

::: tip Be specific about outcomes
Describe what success looks like, not just what to do. The coordinator uses your description to draft an OutcomeSpec — the more concrete your goal, the sharper the spec.
:::

#### Workflow selection

When you click **Start**, the coordinator automatically selects the best-fit workflow for your goal using an LLM pass over your team's available workflows, their descriptions, and team roles. The selection and its rationale are shown in the coordinator conversation.

To override: open the **Workflow** dropdown in the **Start task** dialog and choose a specific workflow. The dropdown shows only workflows with a manual trigger; it is hidden when only one workflow is available. Leave it on **Auto** to let the coordinator choose.

You can also override mid-conversation by typing `use {workflow-id}` before confirming the OutcomeSpec.

### Single-agent run

For simpler tasks that don't need a full team, start a single-agent run by selecting a specific agent and submitting a task directly to them. The agent works in its own isolated worktree and the same review pipeline applies.

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
2. Assigns each subtask to the best-fit agent and selects a model per task based on complexity
3. Dispatches independent subtasks in parallel; dependent ones run in series

You see the **topology view** — a live graph of the entire orchestration.

![Run topology](/guide/images/run-topology.png)

The graph shows:

- **Coordinator node** at the center
- **Agent nodes** for each dispatched subtask, labeled with the agent's name and role
- **Edge status** — running, completed, failed, awaiting
- **Coordinator status badge** in the header (Dispatching → Awaiting assembly → Assembling → In review → Complete)

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

## Watching an execution live

Click any agent node in the topology view to open its **execution view**. This streams every event from that agent's run in real time.

![Execution live view](/guide/images/execution-watch.png)

### The workflow pipeline

Each agent run passes through a pipeline shown as a left-to-right node graph. For coordinator child runs (subtasks), the pipeline is:

```
Agent → RAI → Assemble-ready
```

For standalone single-agent runs, the full pipeline is:

```
Agent → RAI → Human Review → Merge → Scribe
```

Human Review, Merge, and Scribe run once on the **combined** output of all child agents — not per subtask.

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

### Question gate

When an agent asks a question, the run **pauses** at a question gate until you answer. The question appears in the event timeline with an answer input. Type your answer and submit — the agent continues.

### Tool approval

If the run's sandbox policy requires approval before executing certain tool calls, an **approval banner** appears at the top of the page. Click **Jump to approval** to scroll to the pending tool call, then approve or deny it.

Enable **Auto-approve tools** in the run header to skip per-call approval prompts for the remainder of that run.

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

## Runs list

The project page shows all runs in reverse chronological order. Each row shows:

- Run status badge
- Task description
- Start time
- **Topology** (coordinator runs) or **Workflow** (single-agent runs) button

From the runs list you can also **Abandon** an in-flight run (discards pending changes) or **Delete** a completed run from the history.

## Sandboxed execution

Each agent runs inside a **dedicated git worktree** branched from the project's working directory. Agents cannot reach outside their worktree unless the sandbox policy explicitly allows it. The originating branch is never modified during a run — only after you approve and the merge step completes.

```mermaid
flowchart LR
    A[Project working directory] -->|branch per agent| B[Agent worktrees]
    B -->|agents make changes| C[Changes in worktrees]
    C -->|all agents done| D[Assembled combined diff]
    D -->|you approve| E[Merge to branch]
    D -->|you decline| F[Worktrees discarded]
```