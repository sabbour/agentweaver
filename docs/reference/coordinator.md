# Coordinator reference

The Coordinator is a built-in agent (codename Squad) that every team gains automatically. It adds a single new capability on top of the existing single-agent platform: an **orchestration layer**. The coordinator turns a user goal into a confirmed, memory-informed **outcome spec** before any work begins.

The coordinator is itself an observable, streamed, human-accountable run (`agent_name: "Coordinator"`, no parent run). It does not perform domain work itself — it only orchestrates and persists artifacts into the existing memory store.

This page documents the Phase 1 outcome-spec flow. Decomposition, child-run dispatch, steering, bubble-up, and the collective review/merge are later phases and are out of scope here.

## What it is (and is not)

The coordinator is orchestration-only. It MUST NOT reimplement any platform capability. The following capabilities stay owned by their existing features; the coordinator reuses them and never duplicates them:

| Capability | Owned by | Coordinator does |
| --- | --- | --- |
| RAI gate | RAI reviewer in the run graph | Reuses it per run; never re-specifies RAI checks |
| Casting / roster / per-role model | Casting service | Selects agent + model per subtask (later phase) |
| Human review / merge | Run graph executors | Reuses them; never runs a parallel review or merge |
| Scribe / session logging | Scribe executor | Reuses it; never re-logs sessions itself |
| Memory and decisions | Memory store | Reads context; persists the outcome spec (and later the work plan) |

Because of this non-redundancy contract, the coordinator's charter describes only orchestration behavior — read memories and decisions for context, draft and confirm an outcome spec, and (in later phases) decompose, dispatch, observe, and hand off. It does not re-specify RAI, casting, memory governance, sandboxing, review, merge, or scribe. The provider is fixed to GitHub Copilot; only the model id varies within Copilot.

## The Phase 1 outcome-spec flow

A coordinator run drafts a confirmable restatement of the goal and blocks all dispatch until a human confirms it.

1. **Start.** A goal is submitted for a project. The coordinator run begins and emits `coordinator.started` carrying the `goal`. The project's working directory, default branch, and the authenticated caller become the run's repository path, originating branch, and submitting user.
2. **Draft.** The coordinator reads the project's existing memories and decision-inbox entries as grounding context, then drafts an **outcome spec**: a desired outcome, scope, assumptions, and any scoped clarifying questions.
3. **Suspend at the gate.** The outcome spec is persisted with status `awaiting_confirmation`, and the run emits `coordinator.outcome_spec` and suspends at the confirmation gate. No decomposition or child dispatch occurs here — the run blocks until the human confirms or revises.
4. **Confirm or revise.**
   - **Confirm** advances the spec to status `confirmed`, emits `coordinator.outcome_spec.confirmed`, and resumes the run. In Phase 1 the run then terminates (decomposition and dispatch are later phases), followed by `run.completed`.
   - **Revise** re-drafts the spec using human feedback and re-suspends at the gate, emitting a fresh `coordinator.outcome_spec`.

### Outcome spec fields

| Field | Notes |
| --- | --- |
| `goal` | The submitted goal. |
| `desiredOutcome` | The drafted desired outcome. |
| `scope` | Drafted scope. |
| `assumptions` | Drafted assumptions. |
| `clarifyingQuestions` | Optional; omitted when none were drafted. |
| `status` | `drafting`, `awaiting_confirmation`, `confirmed`, or `declined`. |
| `confirmedBy` | Set once confirmed; omitted otherwise. |

## The human confirmation gate

The gate is the safety property of the flow: **no subagent work is dispatched before a human confirms the outcome spec.** A named human stays accountable for the run. The gate is reachable from both mandated clients at parity:

- **Web UI** — the coordinator run page renders the outcome-spec panel with Confirm and Request-changes actions and an explicit "no work is dispatched until you confirm" notice. See the [Web UI reference](./web.md#coordinator-run-and-outcome-spec-gate).
- **MCP server** — the `coordinator_*` tools start, read, confirm, and revise the spec; `run_watch` on the coordinator run id streams the live drafting. See the [MCP server reference](./mcp.md#coordinator).

Both clients are thin: all orchestration logic lives in the API's coordinator service, and clients hold no spec logic.

## Related references

- [API reference — Coordinator endpoints](./api.md#coordinator-endpoints)
- [Events reference — `coordinator.*` events](./events.md)
- [MCP server reference — Coordinator tools](./mcp.md#coordinator)
- [Web UI reference — Coordinator run and outcome-spec gate](./web.md#coordinator-run-and-outcome-spec-gate)
