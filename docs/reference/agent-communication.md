# Agent Communication — Reference

See [Execution transport is below team coordination](../diagrams/canonical-agent-communication-a2a.png) for the shared visual model.

See [Coordinator-mediated dispatch, observations and steering](../diagrams/canonical-agent-communication-handoff.png) for the shared visual model.

See [Decision inbox, ledger, memory and curation](../diagrams/canonical-agent-communication-shared.png) for the shared visual model.

This reference maps each of Agentweaver's three **agent communication channels**
to its concrete surfaces: MCP tools and HTTP API endpoints. For the conceptual
model and the reasoning behind it, read the
[Agent Communication deep dive](../deep-dive/agent-communication.md).

The three channels are:

1. **Indirect / shared-state coordination** — the decisions ledger and
   cross-agent memory (the team's shared brain).
2. **Coordinator-mediated handoffs** — the WorkPlan / subtask DAG and child-run
   dispatch.
3. **Direct transport (A2A)** — the worker↔sandbox-pod execution transport for a
   single agent turn.

> **Precision note.** Channels 1 and 2 are how the *team* coordinates. Channel 3
> is how a *single agent turn* is *executed*. A2A is not a way for two agents to
> chat. Do not map agent-to-agent coordination onto the A2A surfaces. The A2A turn
> surface is `RemoteAgentProxy → Authorization: Bearer {per-run token} →
> AgentHost message:stream`; each AgentHost pod accepts only its own run's token.

---

## Channel 1 — Shared-state coordination

Agents coordinate indirectly by reading the project's decisions and memory at
turn start (and mid-run) and by writing proposals back into the decision inbox.
The conceptual model is in the
[Memory & Decisions deep dive](../deep-dive/memory-decisions.md); the full tool
catalog is in the [MCP reference](./mcp.md) and the
[API reference](./api.md). The surfaces below are the ones that constitute
cross-agent communication.

### Decision inbox — propose, list, promote, reject

The inbox is the durable drop-box in front of the canonical ledger. Agents
**submit** proposals here; reviewers and the Scribe **merge** or **reject** them.

| MCP tool | API endpoint | Purpose |
| --- | --- | --- |
| `decision_inbox_submit` | `POST /api/projects/{id}/decisions/inbox` | Submit a decision or learning proposal (slug-keyed, idempotent) |
| `decision_inbox_list` | `GET /api/projects/{id}/decisions/inbox` | List entries (`?agent=`, `?type=`, `?status=`; default `pending`) |
| `decision_inbox_merge` | `POST /api/projects/{id}/decisions/inbox/{entryId}/merge` | Promote a pending entry into a canonical decision |
| (merge alias) | `POST /api/projects/{id}/decisions/inbox/{entryId}/promote` | Alias for merge/promote |
| `decision_inbox_reject` | `POST /api/projects/{id}/decisions/inbox/{entryId}/reject` | Reject a pending entry (retained for audit) |

`decision_inbox_submit` parameters: `project_id`, `agent_name`, `slug` (unique, for
idempotency), `type` (`learning` · `pattern` · `update` · `architectural` ·
`scope` · `process` · `technical`), `title`, `content`, optional `rationale`.
Re-submitting the same `agent_name` + `slug` while still pending updates the
entry in place; a different agent reusing the same slug is de-collided into a new
entry. See the slug de-collision logic in the
[Memory & Decisions deep dive](../deep-dive/memory-decisions.md).

### Decisions ledger — create, list, update

Promoted entries become canonical **decisions**. The coordinator and Scribe paths
may also create decisions directly, and decisions can be superseded or archived.

| MCP tool | API endpoint | Purpose |
| --- | --- | --- |
| `decision_create` | `POST /api/projects/{id}/decisions` | Create a decision directly (coordinator / Scribe path) |
| `decision_list` | `GET /api/projects/{id}/decisions` | List decisions (`?type=`, `?agent=`) |
| (get one) | `GET /api/projects/{id}/decisions/{decisionId}` | Get a single decision |
| `decision_update` | `PUT /api/projects/{id}/decisions/{decisionId}` | Update status/content; set `superseded_by_id` |

`decision_update` accepts `status` (active, superseded, archived), replacement `content`, and `superseded_by_id`.

Active, approved `architectural` and `scope` decisions are eligible for team-wide context compilation. They remain untrusted historical data, not executable prompt instructions. Coordinator children receive approved-decisions context without the full memory/session selection.

### Cross-agent memory — record, list, get, search

`memory_search` queries across project agents. Search visibility is not prompt eligibility: another agent's learning or pattern must be high importance, approved, and tagged `cross-team` to enter context selection. Eligible records remain subject to the compiler's item/token budget.

| MCP tool | API endpoint | Purpose |
| --- | --- | --- |
| `memory_record` | `POST /api/projects/{id}/agents/{name}/memory` | Add a memory entry for an agent |
| `memory_list` | `GET /api/projects/{id}/agents/{name}/memory` | List one agent's memories (`?type=`, `?importance=`) |
| `memory_get` | `GET /api/projects/{id}/agents/{name}/memory/{memId}` | Get a single memory entry |
| `memory_search` | `GET /api/projects/{id}/memory` | **Cross-agent** search across the whole project (`?type=`, `?tags=`) |

`memory_record` parameters: `project_id`, `agent_name`, `type` (`learning` ·
`pattern` · `core_context` · `update`), `content`, optional `importance`
(`low` · `medium` · `high`) and comma-separated `tags`. `memory_search`
parameters: `project_id`, optional `type`, optional `tags` (comma-separated, OR
semantics) — and it returns entries from **all** agents.

> **`GET /api/projects/{id}/memory` is the cross-agent search surface.** It is the
> endpoint behind `memory_search` and the one place a caller reads the team's
> accumulated memory without naming a specific agent.

### Curation — the Scribe and export

After a run, the **Scribe** lists pending inbox entries, merges low-risk
`learning` / `pattern` / `update` entries, updates the session, and exports DB
state to files. Architectural and scope entries are left for review. Export and
import bridge the DB to the `.squad/` and `.agentweaver/context/` mirrors:

| API endpoint | Purpose |
| --- | --- |
| `POST /api/projects/{id}/memory/export` | Export DB memory → `.squad/` + `.agentweaver/context/` |
| `POST /api/projects/{id}/memory/import` | Import `.squad/decisions/inbox/*.md` → DB |

The Scribe's role and the four-layer context build are documented in the
[Memory reference](./memory.md).

---

## Channel 2 — Coordinator-mediated handoffs

The coordinator decomposes a goal into a WorkPlan / subtask DAG, dispatches child
runs, observes them, and steers them. Children report results **up** to the
coordinator; they never message each other. These tools are thin proxies over the
Coordinator endpoints — see the [Coordinator reference](./coordinator.md), the
[MCP reference](./mcp.md), and the API table in the [API reference](./api.md).

### Start and intent

| MCP tool | API endpoint | Purpose |
| --- | --- | --- |
| `coordinator_start` | `POST /api/projects/{id}/orchestrations` | Start a coordinator orchestration from a plain-language `goal` |
| `coordinator_outcome_spec_get` | `GET /api/runs/{id}/outcome-spec` | Read the persisted OutcomeSpec (intent contract) |
| `coordinator_outcome_spec_confirm` | `POST /api/runs/{id}/outcome-spec/confirm` | Confirm the spec, resuming past the gate |
| `coordinator_outcome_spec_revise` | `POST /api/runs/{id}/outcome-spec/revise` | Re-draft the spec from `feedback` |

Interactive `defineOutcome` waits for confirmation. `direct` skips that drafted-outcome gate, while launch Autopilot can confirm `defineOutcome` unattended; dispatch/review/merge boundaries remain.

### Plan, children, and dispatch

| MCP tool | API endpoint | Purpose |
| --- | --- | --- |
| `coordinator_work_plan_get` | `GET /api/runs/{id}/work-plan` | The subtask DAG: `subtasks` (with `assignedAgent`, `phase`, `isolation`, `status`, `childRunId`) and `dependencies` edges |
| `coordinator_children_get` | `GET /api/runs/{id}/children` | Dispatched child runs, each with `subtaskId`, `childRunId`, `subtaskStatus`, `assignedAgent`, `childRunStatus`, `worktreeBranch` |
| `orchestration_topology` | `GET /api/runs/{id}/work-plan` + `GET /api/runs/{id}/children` | One-shot `{ coordinatorRunId, workPlan, children }` snapshot |

Each child run carries a `ParentRunId` and a `SubtaskId`. Children stop at the
**assemble-ready** boundary — they do not run review, merge, or Scribe; the
coordinator assembles. The full dispatch and assembly model is in
[Coordinator Internals](../deep-dive/coordinator-internals.md).

### Steering — coordinator-mediated, not peer-to-peer

Steering is how an operator redirects in-flight work. It always goes **through the
coordinator**, which relays to the targeted child — siblings never steer each
other.

| MCP tool | API endpoint | Purpose |
| --- | --- | --- |
| `coordinator_steer` | `POST /api/runs/{id}/steer` | Send `stop`, `redirect`, `amend`, or a recovery directive. Omit `target_child_run_id` to broadcast. |

`coordinator_steer` accepts `run_id`, `kind`, `instruction`, and optional
`target_child_run_id`. `kind` is `stop`, `redirect`, `amend`, or a recovery verb
such as `recover`. `instruction` is required for `redirect` and `amend`. It is
optional for `stop` and recovery directives. A `stop` cancels the targeted turn
immediately. Other directives apply at the next turn boundary. Pause is not
supported. Directive progress streams as `coordinator.steering`
(`pending → queued → relayed → applied`).

### Observing the topology

A coordinator run is an ordinary run, so there is no separate streaming tool —
point `run_watch` at the coordinator `run_id`.

| MCP tool | API endpoint | Purpose |
| --- | --- | --- |
| `run_watch` | `GET /api/runs/{id}/stream` | Live stream; carries `coordinator.work_plan`, `coordinator.topology`, `subtask.*`, and `coordinator.steering` events |

The `subtask.*` family (`subtask.dispatched`, `subtask.running`,
`subtask.assemble_ready`, `subtask.completed`, `subtask.failed`) is how results
and status flow **up** to the coordinator view. Child clarifying questions and
tool approvals are re-emitted on this stream and routed back to the originating
child — never sideways. See [Coordinator Internals](../deep-dive/coordinator-internals.md).

---

## Channel 3 — Direct transport (A2A)

A2A is a leaf-turn transport below team coordination. AgentHost exposes `A2ATurnBridgeAgent` through a purpose-routing runner for workflow execution or the Operator Assistant MCP loop. Its provider boundary may use a run-bound Copilot capability or BYOK; orchestration and checkpoint persistence stay outside the pod.

A2A has its own dedicated surfaces and is documented separately:

- [A2A bridge deep dive](../deep-dive/a2a-bridge.md) — the conceptual transport
  model (worker tier, sandbox-pod AgentHost, remote agent proxy).
- [A2A reference](../reference/a2a.md) — the concrete transport endpoints and
  wiring.
- [A2A turn and event transport](../deep-dive/a2a-bridge.md#turn-and-event-transport) —
  the rationale for A2A as the sole worker→AgentHost wire transport.

Because A2A operates **below** team coordination, none of the Channel 1 or
Channel 2 surfaces change when a turn runs in a remote pod versus locally. The
shared-state tools and coordinator tools are identical either way.

---

## Channel-to-surface summary

| Concern | Channel | MCP tools | Key endpoints |
| --- | --- | --- | --- |
| Propose / promote boundaries | 1 | `decision_inbox_submit`, `decision_inbox_list`, `decision_inbox_merge`, `decision_inbox_reject` | `/api/projects/{id}/decisions/inbox*` |
| Canonical decisions | 1 | `decision_create`, `decision_list`, `decision_update` | `/api/projects/{id}/decisions*` |
| Cross-agent memory | 1 | `memory_record`, `memory_list`, `memory_get`, `memory_search` | `/api/projects/{id}/agents/{name}/memory*`, `GET /api/projects/{id}/memory` |
| Decompose & dispatch | 2 | `coordinator_start`, `coordinator_work_plan_get`, `coordinator_children_get`, `orchestration_topology` | `/api/projects/{id}/orchestrations`, `/api/runs/{id}/work-plan`, `/api/runs/{id}/children` |
| Steer & observe | 2 | `coordinator_steer`, `run_watch` | `/api/runs/{id}/steer`, `/api/runs/{id}/stream` |
| Execute one agent turn | 3 | see [A2A reference](../reference/a2a.md) | see [A2A reference](../reference/a2a.md) |

## Related reading

- [Agent Communication deep dive](../deep-dive/agent-communication.md) — the
  conceptual model and why indirect coordination beats direct chat.
- [Agent Communication experience](../experience/agent-communication.md) — what
  these surfaces look like to a user.
- [Memory reference](./memory.md) and
  [Memory & Decisions deep dive](../deep-dive/memory-decisions.md).
- [Coordinator reference](./coordinator.md),
  [MCP reference](./mcp.md), and [API reference](./api.md).
- [A2A bridge deep dive](../deep-dive/a2a-bridge.md) and
  [A2A reference](../reference/a2a.md).

<!-- diagram-context:canonical-agent-communication-handoff:start -->
<details id="diagram-context-canonical-agent-communication-handoff" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Handoffs, not peer chat</td></tr>
<tr><td>subtitle</td><td>The Coordinator owns the dependency frontier and assembles child results.</td></tr>
<tr><td>group-title0</td><td>Intent → execution contract</td></tr>
<tr><td>group-title1</td><td>Children and assembly</td></tr>
<tr><td>Human goal</td><td>Human goal</td></tr>
<tr><td>Human goal</td><td>Define the desired outcome</td></tr>
<tr><td>Human goal</td><td>intent input</td></tr>
<tr><td>OutcomeSpec</td><td>OutcomeSpec</td></tr>
<tr><td>OutcomeSpec</td><td>Confirm before dispatch</td></tr>
<tr><td>OutcomeSpec</td><td>confirmation gate</td></tr>
<tr><td>WorkPlan DAG</td><td>WorkPlan DAG</td></tr>
<tr><td>WorkPlan DAG</td><td>Subtasks + dependencies</td></tr>
<tr><td>WorkPlan DAG</td><td>eligible frontier</td></tr>
<tr><td>Child run A</td><td>Child run A</td></tr>
<tr><td>Child run A</td><td>One assigned subtask</td></tr>
<tr><td>Child run A</td><td>isolated worktree</td></tr>
<tr><td>Child run B</td><td>Child run B</td></tr>
<tr><td>Child run B</td><td>Another eligible subtask</td></tr>
<tr><td>Collective assembly</td><td>Collective assembly</td></tr>
<tr><td>Collective assembly</td><td>Integrate settled work</td></tr>
<tr><td>Collective assembly</td><td>one reviewed integration</td></tr>
<tr><td>e1</td><td>draft</td></tr>
<tr><td>e2</td><td>confirm</td></tr>
<tr><td>e3</td><td>dispatch A</td></tr>
<tr><td>e4</td><td>dispatch B</td></tr>
<tr><td>e5</td><td>result A</td></tr>
<tr><td>e6</td><td>result B</td></tr>
<tr><td>assurance-title</td><td>DEPENDENCIES ARE CONTROL</td></tr>
<tr><td>assurance-line1</td><td>A dependency edge is scheduling, not a conversation channel.</td></tr>
<tr><td>assurance-line2</td><td>A2A transports one agent turn between worker and sandbox; it is not peer chat.</td></tr>
<tr><td>Human goal</td><td>Input</td></tr>
<tr><td>Human goal</td><td>Desired outcome</td></tr>
<tr><td>Human goal</td><td>Scope</td></tr>
<tr><td>Human goal</td><td>Human intent</td></tr>
<tr><td>Human goal</td><td>Gate</td></tr>
<tr><td>Human goal</td><td>Confirm or revise</td></tr>
<tr><td>Human goal</td><td>Owner</td></tr>
<tr><td>Human goal</td><td>Coordinator intake</td></tr>
<tr><td>OutcomeSpec</td><td>State</td></tr>
<tr><td>OutcomeSpec</td><td>Persisted contract</td></tr>
<tr><td>OutcomeSpec</td><td>Fields</td></tr>
<tr><td>OutcomeSpec</td><td>Scope / assumptions</td></tr>
<tr><td>OutcomeSpec</td><td>Human confirmation</td></tr>
<tr><td>OutcomeSpec</td><td>Next</td></tr>
<tr><td>OutcomeSpec</td><td>Workflow selection</td></tr>
<tr><td>WorkPlan DAG</td><td>Model</td></tr>
<tr><td>WorkPlan DAG</td><td>Subtasks + edges</td></tr>
<tr><td>WorkPlan DAG</td><td>Bounded assignee</td></tr>
<tr><td>WorkPlan DAG</td><td>Ready</td></tr>
<tr><td>WorkPlan DAG</td><td>Dependencies satisfied</td></tr>
<tr><td>WorkPlan DAG</td><td>Store</td></tr>
<tr><td>WorkPlan DAG</td><td>Persisted WorkPlan</td></tr>
<tr><td>Child run A</td><td>Binding</td></tr>
<tr><td>Child run A</td><td>ParentRunId / SubtaskId</td></tr>
<tr><td>Child run A</td><td>Files</td></tr>
<tr><td>Child run A</td><td>Per-child worktree</td></tr>
<tr><td>Child run A</td><td>Charter + decisions</td></tr>
<tr><td>Child run A</td><td>Output</td></tr>
<tr><td>Child run A</td><td>Result to parent</td></tr>
<tr><td>Child run B</td><td>Eligible frontier only</td></tr>
<tr><td>Child run B</td><td>Failure</td></tr>
<tr><td>Child run B</td><td>Blocks dependents</td></tr>
<tr><td>Child run B</td><td>Chat</td></tr>
<tr><td>Child run B</td><td>No sibling channel</td></tr>
<tr><td>Collective assembly</td><td>Settled child branches</td></tr>
<tr><td>Collective assembly</td><td>Action</td></tr>
<tr><td>Collective assembly</td><td>Integrate collective work</td></tr>
<tr><td>Collective assembly</td><td>Gates</td></tr>
<tr><td>Collective assembly</td><td>Configured checks</td></tr>
<tr><td>Collective assembly</td><td>Review</td></tr>
<tr><td>Collective assembly</td><td>One human decision</td></tr>
<tr><td>goal</td><td>Outcome, scope, assumptions; Coordinator drafts the contract</td></tr>
<tr><td>spec</td><td>Human confirms or revises; Persisted intent, not execution</td></tr>
<tr><td>plan</td><td>One owner per bounded subtask; Only satisfied dependencies run</td></tr>
<tr><td>a</td><td>Active decisions + charter; Result returned to Coordinator</td></tr>
<tr><td>b</td><td>Parallel only when eligible; No direct child-to-child chat</td></tr>
<tr><td>assembly</td><td>Child results flow upward; Failed / RAI child blocks dependents</td></tr>
<tr><td>notes</td><td>DEPENDENCIES ARE CONTROL; A dependency edge is scheduling, not a conversation channel.; A2A transports one agent turn between worker and sandbox; it is not peer chat.</td></tr>
<tr><td>groups</td><td>Intent → execution contract; Isolated work → collective assembly</td></tr>
</tbody></table>
</details>
<!-- diagram-context:canonical-agent-communication-handoff:end -->

<!-- diagram-context:canonical-agent-communication-a2a:start -->
<details id="diagram-context-canonical-agent-communication-a2a" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>A2A remotes a leaf turn, not the graph</td></tr>
<tr><td>takeaway</td><td>Setup and task cross to AgentHost; assistant output and structured events return.</td></tr>
<tr><td>Workflow graph</td><td>Workflow graph</td></tr>
<tr><td>Workflow graph</td><td>Host owns gates/checkpoints</td></tr>
<tr><td>Workflow graph</td><td>Five factory-created leaf types</td></tr>
<tr><td>RemoteAgentProxy</td><td>RemoteAgentProxy</td></tr>
<tr><td>RemoteAgentProxy</td><td>Build setup DataContent</td></tr>
<tr><td>RemoteAgentProxy</td><td>Task TextContent in same message</td></tr>
<tr><td>AgentHost bridge</td><td>AgentHost bridge</td></tr>
<tr><td>AgentHost bridge</td><td>message:stream over HTTP+JSON</td></tr>
<tr><td>AgentHost bridge</td><td>Apply per-turn context</td></tr>
<tr><td>Caller event pipeline</td><td>Caller event pipeline</td></tr>
<tr><td>Caller event pipeline</td><td>Decoded structured events</td></tr>
<tr><td>Caller event pipeline</td><td>Durable state outside pod</td></tr>
<tr><td>Proxy stream decoder</td><td>Proxy stream decoder</td></tr>
<tr><td>Proxy stream decoder</td><td>Output + RunEventDataPart</td></tr>
<tr><td>Proxy stream decoder</td><td>Check definitive turn end</td></tr>
<tr><td>Leaf runtime</td><td>Leaf runtime</td></tr>
<tr><td>Leaf runtime</td><td>Execute provider/tool loop</td></tr>
<tr><td>Leaf runtime</td><td>Stream updates and events</td></tr>
<tr><td>arrow-1</td><td>invoke</td></tr>
<tr><td>arrow-2</td><td>send</td></tr>
<tr><td>arrow-3</td><td>run</td></tr>
<tr><td>arrow-4</td><td>stream</td></tr>
<tr><td>arrow-5</td><td>append</td></tr>
<tr><td>note-0</td><td>Claim/configure is a separate lifecycle, completed before this exchange.</td></tr>
<tr><td>note-1</td><td>EOF alone is not successful completion; structured failures remain failures.</td></tr>
<tr><td>notes</td><td>Claim/configure is a separate lifecycle, completed before this exchange.; EOF alone is not successful completion; structured failures remain failures.</td></tr>
</tbody></table>
</details>
<!-- diagram-context:canonical-agent-communication-a2a:end -->

<!-- diagram-context:canonical-agent-communication-shared:start -->
<details id="diagram-context-canonical-agent-communication-shared" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Three channels, three different jobs</td></tr>
<tr><td>takeaway</td><td>Team context, coordinator handoff and A2A execution transport must not be conflated.</td></tr>
<tr><td>Project state API</td><td>Project state API</td></tr>
<tr><td>Project state API</td><td>Decisions and eligible memory</td></tr>
<tr><td>Project state API</td><td>Pending writes are not authority</td></tr>
<tr><td>Host compilation</td><td>Host compilation</td></tr>
<tr><td>Host compilation</td><td>Approved scope/architecture</td></tr>
<tr><td>Host compilation</td><td>Children: no full memory stack</td></tr>
<tr><td>Prepared child context</td><td>Prepared child context</td></tr>
<tr><td>Prepared child context</td><td>Charter + skills composed too</td></tr>
<tr><td>Prepared child context</td><td>Injection failure is logged</td></tr>
<tr><td>Coordinator</td><td>Coordinator</td></tr>
<tr><td>Coordinator</td><td>Owns subtask structure</td></tr>
<tr><td>Coordinator</td><td>No peer-chat protocol</td></tr>
<tr><td>Child run</td><td>Child run</td></tr>
<tr><td>Child run</td><td>Bounded assigned outcome</td></tr>
<tr><td>Child run</td><td>Results return upward</td></tr>
<tr><td>Assemble-ready result</td><td>Assemble-ready result</td></tr>
<tr><td>Assemble-ready result</td><td>Parent integrates fragments</td></tr>
<tr><td>Assemble-ready result</td><td>Not independent child merge</td></tr>
<tr><td>Worker proxy</td><td>Worker proxy</td></tr>
<tr><td>Worker proxy</td><td>One leaf execution request</td></tr>
<tr><td>Worker proxy</td><td>Setup data + task text</td></tr>
<tr><td>AgentHost pod</td><td>AgentHost pod</td></tr>
<tr><td>AgentHost pod</td><td>Provider session and tools</td></tr>
<tr><td>AgentHost pod</td><td>No prompt-compilation DB read</td></tr>
<tr><td>Returned stream</td><td>Returned stream</td></tr>
<tr><td>Returned stream</td><td>Output + structured RunEvents</td></tr>
<tr><td>Returned stream</td><td>Caller owns persistence</td></tr>
<tr><td>arrow-1</td><td>select</td></tr>
<tr><td>arrow-2</td><td>inject</td></tr>
<tr><td>arrow-3</td><td>dispatch</td></tr>
<tr><td>arrow-4</td><td>report</td></tr>
<tr><td>arrow-5</td><td>A2A</td></tr>
<tr><td>arrow-6</td><td>stream</td></tr>
<tr><td>note-0</td><td>Rows: shared context / coordinator handoff / execution transport.</td></tr>
<tr><td>note-1</td><td>Agents can submit inbox proposals or record pending memory through the API.</td></tr>
<tr><td>notes</td><td>Rows: shared context / coordinator handoff / execution transport.; Agents can submit inbox proposals or record pending memory through the API.</td></tr>
</tbody></table>
</details>
<!-- diagram-context:canonical-agent-communication-shared:end -->
