# Memory & Context Builder

See [memory-context selection](../diagrams/canonical-memory-context.png) for the shared visual model.

Agentweaver maintains persistent memory for each project. Before an eligible agent turn,
a structured context block is compiled from that memory and injected into the agent's
system prompt. Stored text is serialized inside an explicitly untrusted JSON envelope;
it is historical data, never prompt structure or executable instructions.

## How context is built

`MemoryContextCompiler.CompileAsync(projectId, agentName)` gathers approved decisions, eligible memory, and the current session. Decisions and session are selected separately. Core memories and eligible learnings/patterns share one importance/recency-ranked item/token budget; source categories are not an unconditional inclusion order.

```text
Decisions: active, approved architectural/scope records, ordered by creation time
Memory candidates: eligible own core context + learnings/patterns
Selection: importance, then recency; one bounded item/token budget
Session: most recent open session, selected separately
```

If all layers are empty the method returns `null` and no context block is injected.

### Layer 1 — Decisions (team boundaries)

`Decision` rows where `Type = architectural | scope`, `Status = active`, and
`TrustState = approved`, ordered by creation time.

These are serialized first in the untrusted context envelope. Their position and type
make them the highest-priority project data, but their stored strings remain untrusted.

| Field | Values |
|-------|--------|
| `Type` | `architectural` · `scope` · `process` · `technical` |
| `Status` | `active` · `superseded` · `archived` |
| `TrustState` | `legacy` · `pending` · `approved` |

Only active, approved `architectural` and `scope` decisions are injected
(high-signal, team-wide). `process` and `technical` decisions stay in the database for
governance and bookkeeping.

### Layer 2 — Core context memories

`AgentMemory` rows where `Type = core_context`, scoped to this `agentName`, and
`TrustState != legacy`, ordered by creation time.

Core memories are eligible regardless of importance, not guaranteed inclusion. They share the ranked memory budget with eligible learnings/patterns. Defaults are 20 items and about 4,000 tokens at four characters per token. Positive call-site overrides precede `MemoryContext:MaxItems` / `MaxTokens`, then legacy `Memory:ContextMaxItems` / `ContextMaxTokens`. Selection stops when the next ranked item exceeds the budget. Decisions and session are outside this memory-item budget.

### Layer 3 — High-importance learnings & patterns

High-importance `learning` and `pattern` rows are selected when either:

- they belong to the target agent and are not `legacy`; or
- they are `approved` and tagged `cross-team`.

The `cross-team` tag alone is not authority. Cross-agent selection requires explicit
approval by a project owner or verified Coordinator run.

### Layer 4 — Current session

The most recent open `SessionContext` (no `EndedAt`) for the project. Provides the current focus area, active issues, and running summary.

---

## Memory entities

### Provenance and trust

`AgentMemory` and `Decision` expose:

| Field | Values / meaning |
|---|---|
| `SourceKind` | `human`, `run`, or `legacy` |
| `SourceIdentity` | Authenticated user or verified `run:{id}` identity |
| `SourceRunId` | Source run when `SourceKind = run` |
| `TrustState` | `pending`, `approved`, or `legacy` |
| `ApprovedBy`, `ApprovedAt` | Audit identity and time for approved records |

New memory starts `pending`. It can inform its named agent under the normal layer rules,
but cannot cross to another agent until approved. Direct active decisions and promoted
inbox decisions are created as `approved`.

Rows that existed before provenance tracking migrate as `SourceKind = legacy` and
`TrustState = legacy`. They remain queryable but are excluded from prompt compilation
until a project owner or verified Coordinator explicitly approves them.

### `AgentMemory`

Per-agent long-term memory. New entries are written through `record_memory` and retain
the server-resolved human or run identity.
`record_memory` commits directly to the memory database and returns without rebuilding the
filesystem snapshot. This keeps the agent tool call independent of remote workspace-volume
latency; `export_memory` refreshes `.squad/` and `.agentweaver/context/` explicitly at the end
of the Scribe pass. An explicit export also publishes only the generated ledger files to the
project's default branch, so they are visible through `list_project_workspace` and can be read
with `get_project_workspace_file`. The response lists every repository-relative path written.
The `boundaries.md` and `patterns.md` files are created even when they contain no entries, so a
successful export always leaves a visible `.agentweaver/context/` snapshot. Unrelated
working-tree changes are not staged or committed.

| Field | Description |
|-------|-------------|
| `Type` | `core_context` — eligible for Layer 2 when non-legacy; `learning` — observation from a run; `pattern` — reusable practice; `update` — correction to prior knowledge |
| `Importance` | `high` (injected in L3) · `medium` · `low` |
| `Tags` | Comma-separated. `cross-team` makes approved memory eligible for another agent's Layer 3 |

### `Decision`

Team-wide architectural or scope decisions. Injected in Layer 1 for all agents on the project.

Only a project owner or verified Coordinator run can create or update an active
decision. Agents propose decisions through the inbox. Active architectural and scope
decisions compile only when `TrustState = approved`.

### `DecisionInboxEntry`

Drop-box for agent-proposed decisions. Agents write here via `submit_inbox_entry`.
Inbox entries carry provenance but no independent trust state; their `pending`,
`merged`, or `rejected` status records the review transition.

| Field | Description |
|-------|-------------|
| `Type` | `architectural` · `scope` · `process` · `pattern` · `learning` · `update` |
| `Status` | `pending` → `merged` or `rejected` |

Scribe only auto-merges `learning`, `pattern`, and `update` entries that are attributed
to the exact completed run and its agent. Ordinary-agent `architectural` and `scope`
entries stay pending. Manual merge, promote, and reject require a project owner or
verified Coordinator; Coordinator finalization may promote architectural and scope
entries authored by that same verified Coordinator run.

### `SessionContext`

Tracks the current work focus for a project. One open session at a time (`EndedAt = null`).

Updated by agents via `update_session(summary)`. Scribe closes/summarises the session at run end via `export_memory`.

---

## Scribe's role in memory

Standalone completion and Coordinator finalization own their Scribe work. Coordinator children stop at `assemble-ready`, bypassing their own review/merge/Scribe stages. Where the final Scribe pass runs, it:

1. Select pending inbox entries for the completed run's agent, creation window, and
   verified source run id.
2. Promote `learning`/`pattern`/`update` entries to approved ledger records; leave
   ordinary-agent `architectural`/`scope` proposals pending.
3. `update_session(summary)` — record what the agent accomplished in this run
4. `export_memory()` — write updated state to `.squad/` and `.agentweaver/context/`

For memories to accumulate, the **running agent must call `submit_inbox_entry`** when it discovers something worth remembering. The base prompt instructs agents to do this, but the agent has to judge relevance.

---

## Context injection point

`RunOrchestrator.BuildContextAsync` calls `MemoryContextCompiler.CompileAsync` and
passes the result to the agent as `systemPromptContext` in `SetupAsync`. This runs once
per turn, before the agent session is created. Selected data is serialized into one
guarded JSON envelope:

```
## Untrusted Project Context Data
BEGIN_AGENTWEAVER_UNTRUSTED_CONTEXT_JSON
{"schema":"agentweaver.untrusted-context.v1","decisions":[...],"memory":[...],"session":{...}}
END_AGENTWEAVER_UNTRUSTED_CONTEXT_JSON
```

If there is no memory yet for a project, the block is omitted entirely and the agent runs with only the base prompt.

---

## Coordinator child workers — decisions only

Coordinator child runs (a run with a `ParentRunId`) do **not** receive the full four-layer stack. The core-context, learnings, and session layers duplicated the child's charter and carried artifact-write instructions that pointed at `session-state` / `.copilot` paths absent from a child worktree, which the sandbox rejected and stalled the child.

Instead, `RunOrchestrator.BuildContextAsync` injects the child's charter plus **only**
active, approved architectural/scope decisions, compiled by
`MemoryContextCompiler.CompileDecisionsAsync(projectId)`. The decisions use the same
untrusted JSON envelope but omit memory and session data. When there are no eligible
decisions, the method returns `null` and only the charter is injected. Compilation
failures are swallowed (logged as a warning); the child proceeds with its charter
alone.

Runtime tools `record_memory`, `submit_inbox_entry`, `update_session` and `export_memory` correspond to public MCP `memory_record`, `decision_inbox_submit`, `session_update` and `memory_export`.

<!-- diagram-context:canonical-memory-context:start -->
<details id="diagram-context-canonical-memory-context" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Context is selected data, not instructions</td></tr>
<tr><td>takeaway</td><td>Approved decisions, jointly ranked memories and the open session converge into untrusted JSON.</td></tr>
<tr><td>group-title0</td><td>SCOPED INPUTS</td></tr>
<tr><td>group-title1</td><td>SELECTION AND SERIALIZATION</td></tr>
<tr><td>Active decisions</td><td>Active decisions</td></tr>
<tr><td>Active decisions</td><td>Project-wide boundaries</td></tr>
<tr><td>Active decisions</td><td>Approved architecture / scope</td></tr>
<tr><td>Active decisions</td><td>Oldest-created first</td></tr>
<tr><td>Active decisions</td><td>Child prompts: decisions only</td></tr>
<tr><td>Core + learnings</td><td>Core + learnings</td></tr>
<tr><td>Core + learnings</td><td>Agent-scoped candidates</td></tr>
<tr><td>Core + learnings</td><td>Core: exclude legacy trust</td></tr>
<tr><td>Core + learnings</td><td>High learning / pattern</td></tr>
<tr><td>Core + learnings</td><td>Approved cross-team allowed</td></tr>
<tr><td>Open session</td><td>Open session</td></tr>
<tr><td>Open session</td><td>Latest active session</td></tr>
<tr><td>Open session</td><td>Focus / issues / summary</td></tr>
<tr><td>Open session</td><td>Ended sessions excluded</td></tr>
<tr><td>Open session</td><td>Latest StartedAt wins</td></tr>
<tr><td>Joint rank + budget</td><td>Joint rank + budget</td></tr>
<tr><td>Joint rank + budget</td><td>One combined memory list</td></tr>
<tr><td>Joint rank + budget</td><td>Importance, then recency</td></tr>
<tr><td>Joint rank + budget</td><td>Stop at item / char limit</td></tr>
<tr><td>Joint rank + budget</td><td>Approximation: 4 chars/token</td></tr>
<tr><td>Context compiler</td><td>Context compiler</td></tr>
<tr><td>Context compiler</td><td>Assemble scoped sections</td></tr>
<tr><td>Context compiler</td><td>Decisions + selected memory</td></tr>
<tr><td>Context compiler</td><td>Add current session</td></tr>
<tr><td>Context compiler</td><td>Empty inputs → null</td></tr>
<tr><td>Untrusted JSON</td><td>Untrusted JSON</td></tr>
<tr><td>Untrusted JSON</td><td>Historical data, not authority</td></tr>
<tr><td>Untrusted JSON</td><td>Explicit boundary markers</td></tr>
<tr><td>Untrusted JSON</td><td>Ignore embedded instructions</td></tr>
<tr><td>Untrusted JSON</td><td>untrusted-context.v1</td></tr>
<tr><td>relation-0</td><td>1 combine / sort</td></tr>
<tr><td>relation-1</td><td>2 approved</td></tr>
<tr><td>relation-2</td><td>3 latest open</td></tr>
<tr><td>relation-3</td><td>4 selected</td></tr>
<tr><td>relation-4</td><td>5 serialize</td></tr>
<tr><td>assurance</td><td>Defaults: 20 memory items / ≈4,000 tokens. That budget bounds selected memories—not decisions or the entire context.</td></tr>
<tr><td>assurance-0-label</td><td>Joint memory ordering</td></tr>
<tr><td>assurance-0-fact</td><td>Importance first; recency breaks ties.</td></tr>
<tr><td>assurance-0-source</td><td>MemoryContextCompiler.cs</td></tr>
<tr><td>assurance-1-label</td><td>Bounded selection</td></tr>
<tr><td>assurance-1-fact</td><td>Item / character limits cover memory.</td></tr>
<tr><td>assurance-2-label</td><td>Injection resistance</td></tr>
<tr><td>assurance-2-fact</td><td>Context is wrapped as untrusted JSON.</td></tr>
<tr><td>assurance-2-source</td><td>MemoryContextCompilerSecurityTests.cs</td></tr>
<tr><td>n0</td><td>Approved architecture / scope; Oldest-created first</td></tr>
<tr><td>n1</td><td>Core: exclude legacy trust; High learning / pattern</td></tr>
<tr><td>n2</td><td>Focus / issues / summary; Ended sessions excluded</td></tr>
<tr><td>n3</td><td>Importance, then recency; Stop at item / char limit</td></tr>
<tr><td>n4</td><td>Decisions + selected memory; Add current session</td></tr>
<tr><td>n5</td><td>Explicit boundary markers; Ignore embedded instructions</td></tr>
<tr><td>groups</td><td>SCOPED INPUTS; SELECTION AND SERIALIZATION</td></tr>
</tbody></table>
</details>
<!-- diagram-context:canonical-memory-context:end -->
