# Memory & Context Builder

Agentweaver maintains persistent memory for each project. Before an eligible agent turn,
a structured context block is compiled from that memory and injected into the agent's
system prompt. Stored text is serialized inside an explicitly untrusted JSON envelope;
it is historical data, never prompt structure or executable instructions.

## How context is built

`MemoryContextCompiler.CompileAsync(projectId, agentName)` assembles context from four layers, applied in strict priority order:

```
Layer 1 (highest priority): Approved decisions — non-negotiable team boundaries
Layer 2: Non-legacy core context — agent-specific background knowledge
Layer 3: Non-legacy own learnings + approved cross-team learnings
Layer 4 (lowest priority): Current open session focus
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

These are stable, always-relevant facts about the agent's domain: "this project uses EF Core", "the API base URL is X", etc. They are always included regardless of importance level.

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
of the Scribe pass.

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

After every completed project run, the **Scribe** step runs automatically:

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
