# Coordinator Workflow Selection (Feature 015 US5)

A project instantiated from a Blueprint carries a **set of functionally-distinct
workflows** (e.g. a Software Development project carries `software-delivery`,
`bug-fix`, and `code-review`). When the coordinator picks up a task it selects the
**best-fit** workflow for *that* task instead of unconditionally running the
project default — a quick fix routes to `bug-fix`, a net-new feature to
`software-delivery`, a pure review request to `code-review`.

## When selection runs

| Project workflows | Behavior |
|-------------------|----------|
| Exactly one | Selection is **skipped silently** — no LLM call, no event. That workflow (the project default) is used. |
| More than one | The coordinator runs an LLM selection and surfaces the result with a rationale and an override hint. |

Selection is an **optimization over the default**, never a hard gate: it always
resolves to a workflow, and any failure falls back to the project default.

## The selection flow

1. **Collect candidates.** The coordinator gathers the project's available
   workflows from the `WorkflowRegistry` (built-in default + catalog library
   workflows + the project's `.agentweaver/workflows/` files). The project
   default is ordered first so it is the deterministic fallback.
2. **Build context.** The task/goal description, the team roles (the project's
   active roster), and each candidate's `id`/`name`/`description` plus a
   `[built-in/library]` vs `[project/custom]` source tag.
3. **LLM call.** `IWorkflowSelector.SelectAsync` issues one completion via
   `IWorkflowSelectionModel` (production: a grounded `CopilotAIAgent` turn) with
   this prompt shape:

   ```
   You are selecting the most appropriate workflow for a task.

   Task: {taskDescription}
   Team roles: {roles}

   Available workflows:
   - {id} [built-in/library | project/custom]: {name} — {description}
   ...

   Selection rules:
   - Match on PROCESS FIT: what steps the workflow runs and what outputs it produces.
   - Do NOT select by name similarity or domain-word overlap. A closest-sounding built-in
     is a bad choice if its process does not fit.
   - Prefer project/custom workflows over generic built-in/library workflows when a custom
     workflow can perform the requested process.
   - If no workflow is a good process fit, select the first listed workflow (the project
     default) instead of guessing.

   Reply with JSON: {"selected": "<workflow-id>", "rationale": "<1-2 sentences why>"}
   Select the workflow whose process best matches the task and team.
   ```

   Each candidate is tagged `[built-in/library]` or `[project/custom]` so the model can prefer a
   project-authored workflow over a generic one. Selection is keyed on **process fit** — the steps a
   workflow runs and the outputs it produces — never on name or domain-word similarity.

4. **Parse + validate.** The first balanced JSON object is extracted; the
   `selected` id must be one of the available candidates. On a parse failure or an
   unknown id the selector falls back to the project default and logs a warning.
5. **Surface.** A `coordinator.workflow_selected` event is emitted on the
   coordinator run stream:

   ```json
   {
     "selectedId": "bug-fix",
     "selectedName": "Bug Fix",
     "rationale": "A one-line null check is a fast, contained defect fix.",
     "wasAutoSelected": true,
     "overrideHint": "Reply 'use {other-id}' to change (available: software-delivery, bug-fix, code-review).",
     "available": [ { "id": "software-delivery", "name": "Software Delivery" }, ... ]
   }
   ```

   The same selection is surfaced identically via the MCP server and the Web UI.

## User override

The override is a conversational command. Each incoming user message is checked
for the pattern `use {workflow-id}` (case-insensitive) **before** routing to the
normal task handler. If the id is one of the available workflows, the run switches
to it and the coordinator acknowledges the change. An explicit user override
always wins over the coordinator's pick (consistent with Feature 010's per-task
override).

## Result contract

`WorkflowSelectionResult` carries:

- `Selected` — the chosen `WorkflowDefinition`.
- `Rationale` — a 1–2 sentence explanation (or a fallback explanation).
- `WasAutoSelected` — `false` only for a single-workflow project (pure
  pass-through); `true` whenever the multi-workflow path ran, including when it
  fell back to the default after a model failure.

## Fallback summary

| Condition | Outcome |
|-----------|---------|
| Single workflow | Default returned, `WasAutoSelected = false`, no LLM call |
| Model unavailable / throws | Project default, warning logged |
| Malformed JSON | Project default, warning logged |
| Unknown selected id | Project default, warning logged |
| Valid selection | The chosen workflow + its rationale |
