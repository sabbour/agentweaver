# Memory & Context Builder

Agentweaver maintains persistent memory for each project. Before every agent turn, a structured context block is compiled from that memory and injected into the agent's system prompt — giving agents continuity across runs without requiring the user to repeat themselves.

## How context is built

`MemoryContextCompiler.CompileAsync(projectId, agentName)` assembles context from four layers, applied in strict priority order:

```
Layer 1 (highest priority): Decisions — non-negotiable team boundaries
Layer 2: Core context memories — agent-specific background knowledge
Layer 3: High-importance learnings & patterns (own + cross-team)
Layer 4 (lowest priority): Current open session focus
```

If all layers are empty the method returns `null` and no context block is injected.

### Layer 1 — Decisions (team boundaries)

`Decision` rows where `Type = architectural | scope` and `Status = active`, ordered by creation time.

Rendered as `## Boundaries and Decisions` with a note that these **take precedence over all other context**. Agents treat them as hard constraints.

| Field | Values |
|-------|--------|
| `Type` | `architectural` · `scope` · `process` · `pattern` |
| `Status` | `active` · `superseded` · `rejected` |

Only `architectural` and `scope` decisions are injected (high-signal, team-wide). `process` and `pattern` decisions stay in the DB for Scribe bookkeeping.

### Layer 2 — Core context memories

`AgentMemory` rows where `Type = core_context` scoped to this `agentName`, ordered by creation time.

These are stable, always-relevant facts about the agent's domain: "this project uses EF Core", "the API base URL is X", etc. They are always included regardless of importance level.

### Layer 3 — High-importance learnings & patterns

Top 5 `AgentMemory` rows where `Importance = high` AND (`AgentName = agentName` OR `Tags` contains `cross-team`), restricted to `Type = learning | pattern`, ordered newest-first.

The `cross-team` tag allows one agent's learnings to surface in another agent's context — useful for shared constraints discovered during a run (e.g. "the sandbox blocks writes outside the worktree").

### Layer 4 — Current session

The most recent open `SessionContext` (no `EndedAt`) for the project. Provides the current focus area, active issues, and running summary.

---

## Memory entities

### `AgentMemory`

Per-agent long-term memory. Written by agents during runs via `record_memory` / `submit_inbox_entry`, merged by Scribe.

| Field | Description |
|-------|-------------|
| `Type` | `core_context` — always injected; `learning` — observation from a run; `pattern` — reusable practice; `update` — correction to prior knowledge |
| `Importance` | `high` (injected in L3) · `medium` · `low` |
| `Tags` | Comma-separated. `cross-team` makes a memory visible in other agents' Layer 3 |

### `Decision`

Team-wide architectural or scope decisions. Injected in Layer 1 for all agents on the project.

Written by agents via `submit_decision`. Scribe merges from the inbox and manages the lifecycle (`active → superseded | rejected`).

### `DecisionInboxEntry`

Drop-box for agent-proposed decisions. Agents write here via `submit_inbox_entry`; Scribe reviews and promotes to `Decision` (merge) or marks `rejected`.

| Field | Description |
|-------|-------------|
| `Type` | `architectural` · `scope` · `process` · `pattern` |
| `Status` | `pending` → `merged` or `rejected` |

Scribe only auto-merges `learning`, `pattern`, and `update` types. `architectural` and `scope` entries are left for coordinator review.

### `SessionContext`

Tracks the current work focus for a project. One open session at a time (`EndedAt = null`).

Updated by agents via `update_session(summary)`. Scribe closes/summarises the session at run end via `export_memory`.

---

## Scribe's role in memory

After every completed project run, the **Scribe** step runs automatically:

1. `list_inbox(forAgent)` — list pending inbox entries for the run's agent
2. `merge_inbox_entry(id)` — promote `learning`/`pattern`/`update` entries to `AgentMemory`; skip `architectural`/`scope` (coordinator review)
3. `update_session(summary)` — record what the agent accomplished in this run
4. `export_memory()` — write updated state to `.squad/` and `.agentweaver/context/`

For memories to accumulate, the **running agent must call `submit_inbox_entry`** when it discovers something worth remembering. The base prompt instructs agents to do this, but the agent has to judge relevance.

---

## Context injection point

`RunOrchestrator.BuildContextAsync` calls `MemoryContextCompiler.CompileAsync` and passes the result to the agent as `systemPromptContext` in `SetupAsync`. This runs once per turn, before the agent session is created. The context block is appended to the agent's base system prompt.

```
Agent base prompt
   +
## Boundaries and Decisions    ← Layer 1
## Memory                      ← Layers 2 + 3
## Current Session             ← Layer 4
```

If there is no memory yet for a project, the block is omitted entirely and the agent runs with only the base prompt.
