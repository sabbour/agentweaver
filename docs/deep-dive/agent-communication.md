# Agent Communication — Conceptual Deep Dive

## Purpose and mental model

A team in Agentweaver is many agents working toward one outcome, but the agents
never sit in a chat room talking to each other. There is no free-form
agent-to-agent conversation, no message bus where one specialist pings another,
no negotiation loop. Coordination is deliberately **indirect and structured**.

The mental model is a **shared workshop**, not a group chat:

- There is a **shared brain** every agent reads from and writes to — the
  decisions ledger and the cross-agent memory.
- There is a **foreman** — the coordinator — who breaks a goal into bounded jobs,
  hands each to one agent, and assembles the pieces back together.
- There is a **delivery mechanism** that carries a single agent's turn to wherever
  it physically runs — the A2A transport between the worker and a sandbox pod.

These are three different channels with three different jobs. The first two are
how the *team* coordinates. The third is how a *single* agent turn is *executed*.
Keeping them distinct is the most important idea in this document: **A2A is
execution transport, not a way for two agents to talk.**

## The three channels

| Channel | What it coordinates | Direction | Carrier |
| --- | --- | --- | --- |
| Indirect / shared-state | Accepted boundaries and eligible context | Read during preparation and mid-run; inbox proposals or pending memory records | Decisions ledger + cross-agent memory |
| Coordinator-mediated handoff | One goal decomposed into bounded subtasks | Coordinator → children; results flow up | WorkPlan / subtask DAG |
| Direct transport (A2A) | A single agent turn's execution | Worker ↔ sandbox pod | claim warm AgentHost pod, one-time `/configure`, then A2A `message:stream` with per-run bearer auth |

The rest of this document explains each channel, then explains **why** the team
coordinates through a shared blackboard instead of direct chat.

---

## Channel A — Indirect coordination through shared state

This is the primary way agents influence each other, and it borrows the classic
**blackboard** pattern: contributors never address one another directly; they
read from and write to a shared, durable surface, and a curator keeps it
coherent. In Agentweaver that surface is the
[decisions ledger and cross-agent memory](./memory-decisions.md), and the curator
is the **Scribe**.

### The shared brain has two parts

- **Decisions** are accepted team boundaries — architectural and scope rules the
  whole team must respect. They are the highest-authority artifact.
- **Memory** is reusable context — core facts, learnings, and patterns. Memory
  *informs* an agent; decisions *constrain* the team. Memory never overrides a
  decision.

Both are scoped to a project, and both are described in depth in the
[Memory & Decisions deep dive](./memory-decisions.md) and the
[Memory reference](../reference/memory.md).

### Reading: agents start from, and stay synced with, the shared state

During agent preparation, orchestration compiles eligible, budgeted context:
approved active architectural/scope decisions, core context, high-importance
learnings/patterns, approved cross-team contributions, and the current session.
This is not an unconditional copy of every layer before every turn. Coordinator
children use the decisions-only exception below. The structured context remains
untrusted input, not an instruction-priority override. The full layering logic
lives in the [Memory reference](../reference/memory.md).

Reads are not limited to spawn time. Agents can also pull the latest decisions
and memory **mid-run**, so a long-running agent picks up boundaries that were
promoted after it started rather than working from a stale snapshot. This keeps
the blackboard live: a constraint accepted while an agent is mid-flight becomes
visible to it on its next read.

### Writing: agents propose, they do not publish

Agents do not write team law directly. When an agent discovers something worth
keeping — a learning, a reusable pattern, a correction, or a candidate boundary —
it **drops a proposal into the decision inbox**. The inbox is a durable,
reviewable drop-box in front of the canonical ledger. Proposing is not the same
as deciding. The `record_memory` tool also writes a **Pending** memory record
directly through the API; not every memory write passes through the decision
inbox, and a pending record is not approved team authority.

### Curating: the Scribe merges, conflict-free

After a run completes, the **Scribe** step reviews the inbox and **promotes**
accepted entries into the ledger or memory, leaving an audit trail behind. Lower
-risk learnings, patterns, and updates can auto-merge; higher-impact architectural
and scope proposals are left for coordinator or human review. Rejected entries
are retained, not deleted, so the record explains not just what the team accepted
but what it declined.

Because merges **add facts and change status** rather than rewriting history,
independent agents can contribute concurrently without clobbering each other.
Two agents proposing under the same natural name are de-collided into two
distinct entries; a rejection is a status transition, not a delete; a promotion
links the source proposal to the decision it created. The
[Memory & Decisions deep dive](./memory-decisions.md) calls this the
**conflict-free merge model**, and it is exactly what makes indirect coordination
safe at scale: no agent has to lock the blackboard to write to it.

### Coordinator children read decisions only

A coordinator child run is a focused worker with a tight charter. It receives the
team's **approved active architectural and scope decisions** through
`CompileDecisionsAsync`, not the full compiled memory/session stack. Charter,
skills and capabilities are composed separately, so decisions are not the entire
child prompt. Injection failures are logged and can leave a child without the
compiled decision context; selection is not an unconditional delivery guarantee.
This carve-out is detailed in the [Memory reference](../reference/memory.md).

---

<a id="channel-b-coordinator-mediated-handoffs"></a>

## Channel B — Coordinator-mediated handoffs

The second channel is how a single goal becomes parallel work without any agent
having to coordinate with a peer. The [coordinator](./coordinator-internals.md)
sits between the human goal and the workers and owns all cross-agent structure.

### Decompose: goal → OutcomeSpec → WorkPlan DAG

The coordinator first drafts an **OutcomeSpec** — the intent contract — capturing
the goal, desired outcome, scope, and assumptions, and suspends at a confirmation
gate. Nothing is dispatched until the spec is confirmed. Once confirmed, it
produces a **WorkPlan**: the execution contract. The WorkPlan decomposes the goal
into an **outcome-complete** set of independently dispatchable **subtasks** — one for
every lifecycle stage the outcome implies, not the fewest that compile — each owned by one
agent, each bounded, ordered by explicit **dependency edges** that form a DAG. The
full decomposition logic is in the
[Orchestration deep dive](./orchestration.md) and
[Coordinator Internals](./coordinator-internals.md).

### Dispatch: children run independently, in parallel where safe

For each subtask whose dependencies are satisfied — the **ready frontier** — the
coordinator dispatches a **child run**, tagged with a `ParentRunId` and its
`SubtaskId`. Independent subtasks run in parallel; dependent ones are serialized
behind their prerequisites. The DAG makes this parallelism deterministic: the same
plan advances the same way every time.

### Handoff: assemble-ready, not merge-independent

A child run is intentionally trimmed. It does its agent work and child-level
safety checks, then **stops at the assemble-ready boundary**. It does *not* run
human review, merge, or Scribe — those are the parent's job. "Assemble-ready"
means: *my fragment is finished and ready for the coordinator to integrate*, not
*my work is done and shipped*. Children are fragments of the parent outcome, so
they must not merge independently. The coordinator collects the assemble-ready
pieces, integrates them in dependency order, runs collective review, and records
the combined result.

### Children report up, never sideways

This is the structural rule that replaces peer chat. A child never messages
another child. When a child needs a clarifying answer or a tool approval, the
request is **re-emitted on the coordinator's stream**; the human (or, under
Autopilot, the coordinator) answers, and the answer is routed back to the
requesting child. The coordinator's view is the inbox; the child remains the owner
of its own request. Information flows **up to the coordinator and back down to the
originating child** — never laterally between siblings. **Steering** works through
the coordinator. It supports `stop`, `redirect`, `amend`, and recovery verbs such
as `recover`. `redirect` and `amend` require an instruction. Other verbs can omit
it. Omitting the child run ID broadcasts the directive to active children. Pause is
not supported.
The full handoff and steering model is in the
[Coordinator Internals deep dive](./coordinator-internals.md).

---

## Channel C — Direct transport (A2A)

The third channel is the one most easily confused with "agents talking," so be
precise: **A2A (Agent2Agent) is the wire transport that remotes a single agent
turn**. It is execution plumbing, not a coordination protocol.

When an agent turn runs in a distributed deployment, the **worker** keeps the
entire orchestration graph — the workflow, the human-in-the-loop gates, the
resume logic — in process. Only the **leaf agent turn** is sent over A2A to an
**AgentHost** running inside a **sandbox pod**, which executes the model turn and
its tools, then streams the turn's output back. On the worker side the leaf is a
`RemoteAgentProxy` (an `A2AAgent` over the A2A **HTTP+JSON** transport); the pod
hosts an `A2ATurnBridgeAgent` (MAF name `agentweaver-pod`) wrapping its singleton
`CopilotAIAgent`. `RemoteWorkflowAgentFactory` remotes five workflow agents this
way: worker, RAI, Rubberduck, Build/Test, and Scribe. The Operator Assistant also
uses `RemoteAgentProxy` outside that factory. The orchestration graph never crosses the boundary; A2A carries one
turn's setup, assistant output, and structured run events. A2A is the sole worker→AgentHost wire
transport for that seam.

Why this is **not** Channel A or B:

- It moves **one turn of one agent** to where it physically executes. It does not
  let two agents converse.
- The thing on each end of the A2A link is a worker and a pod, **not two
  collaborating agents**.
- Team coordination — shared decisions, memory, handoffs — happens entirely in the
  worker tier, above this transport, regardless of whether a turn runs locally or
  in a remote pod.

In other words, A2A could carry every agent turn in the system and the *team*
would still coordinate the same way, through the blackboard and the coordinator.
The conceptual model of the transport lives in the
[A2A bridge deep dive](./a2a-bridge.md); its surfaces are in the
[A2A reference](../reference/a2a.md). For the distributed execution rationale,
see the [distributed-execution deep dive](./distributed-execution-scaling.md).

---

## Why indirect coordination instead of direct chat

The team channels deliberately avoid agent-to-agent conversation. The reasons are
the heart of the design.

### Auditability

Every coordination event is a durable artifact. A proposal sits in the inbox; a
promotion creates a linked decision; a rejection is retained; a handoff is a
WorkPlan edge; an assembled outcome carries a collective review. You can answer
"why did the team do this?" by reading state, not by replaying a transcript.
Free-form chat leaves only an unstructured log that is hard to audit and easy to
contradict.

### Determinism

A subtask DAG advances the same way every time: ready subtasks dispatch, results
flow up, the coordinator assembles. There is no emergent, order-dependent
back-and-forth between agents whose outcome depends on who spoke first.
Determinism is what makes orchestration reproducible and recoverable from
persisted state rather than from chat history.

### Conflict-free merge

The blackboard merges by **adding facts and changing status**, never by rewriting
history. Independent agents contribute concurrently — de-collided proposals,
status-transition rejections, link-preserving promotions — without locking or
overwriting each other. A chat model has no equivalent: two agents asserting
conflicting things in a conversation produce a contradiction someone must resolve
by hand.

### No chat-loop nondeterminism

Direct agent-to-agent chat invites loops: A asks B, B asks A, neither converges,
tokens burn, and the outcome depends on arbitrary turn ordering. Routing all
cross-agent questions **up to the coordinator** removes the loop entirely. There
is exactly one place a question can be answered, exactly one owner per request,
and a bounded, observable resolution path.

### One authority layer

Memory informs; decisions govern; only promoted decisions bind the team. Because
agents *propose* rather than *publish*, no single agent's transient opinion
becomes team policy by being asserted loudly in a conversation. The review buffer
is the price of keeping policy deliberate.

---

## Putting it together

Refer back to the shared communication overview
and the [coordinator handoff](#channel-b-coordinator-mediated-handoffs), rather than
introducing a second recap diagram.

The three channels never blur:

- The **shared brain** (Channel A) is how knowledge and boundaries reach every
  agent and how agents feed knowledge back — indirectly, durably, conflict-free.
- The **coordinator** (Channel B) is how one goal becomes many bounded jobs and
  how their results come back together — top-down handoff, bottom-up results.
- **A2A** (Channel C) is how a single agent turn is physically executed somewhere
  else — transport, not conversation.

If you remember one distinction, remember this: **agent coordination is the
team-level blackboard plus coordinator handoffs; A2A is a single agent turn
remoted to a pod.** They solve different problems and must not be conflated.

## Related reading

- [Memory & Decisions deep dive](./memory-decisions.md) — the shared ledger,
  inbox→promotion, and conflict-free merge.
- [Memory reference](../reference/memory.md) — four-layer context build and the
  Scribe's role.
- [Orchestration deep dive](./orchestration.md) and
  [Coordinator Internals](./coordinator-internals.md) — WorkPlan/subtask DAG,
  dispatch, and assemble-ready handoff.
- [A2A bridge deep dive](./a2a-bridge.md) and [A2A reference](../reference/a2a.md)
  — the worker↔pod execution transport.
- [Agent Communication reference](../reference/agent-communication.md) — the
  concrete MCP tools and API endpoints behind each channel.
- [Agent Communication experience](../experience/agent-communication.md) — what
  coordination looks like to a user watching a team work.

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
