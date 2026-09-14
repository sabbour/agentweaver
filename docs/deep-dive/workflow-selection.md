# Workflow selection — Deep Dive

When a project has more than one workflow, the coordinator must decide which one to apply before it decomposes a goal into a work plan. This page explains the selection algorithm, the override hierarchy, and how all of the above compose into a single deterministic-first decision that is always resilient to model failure.

For the user-facing controls see [Submitting and Watching Runs — Workflow selection](../guide/runs.md#workflow-selection). For the API reference and override precedence table see [Coordinator reference — Workflow selection](../reference/coordinator.md#workflow-selection).

## What it is

Workflow selection answers one question: **which process shape should this task follow?** The built-in default, conformance-checked catalog workflows, and valid project YAML workflows supply the candidate set. Blueprint restrictions apply to normal availability; the built-in `default` remains available.

**Automatic** selection is silent when at most one workflow is available: no LLM call or selection event. Available explicit overrides are checked earlier and emit an event even for a singleton. With multiple candidates, the model has bounded authority and a deterministic fallback.

## Selection logic

`WorkflowSelector.SelectAsync` (`apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93`) builds a process-fit prompt that includes:

- the task description (goal)
- the team's role titles
- each available workflow's id, name, description, and whether it is project/custom or built-in/library

The selection rules given to the model are explicit:

- **Process fit, not name similarity.** A closest-sounding built-in is a bad pick if its process does not fit.
- **Prefer project/custom workflows** over built-in/library workflows when a custom workflow can perform the requested process.
- **Prompt fallback.** The prompt asks for the first listed workflow (the project default) when no process fits. The runtime fallback has a more specific rule, below.

The requested response is `{ "selected": "<id>", "rationale": "<1-2 sentences>" }`. The parser also accepts supported string forms, normalizes ids/display names, strips thinking blocks, and can recover an unambiguous candidate named in prose. Malformed output or an unknown choice permits **one retry**; an exception falls back immediately. Every accepted choice still resolves to a supplied candidate (`WorkflowSelector.cs:83–174`, `:219–235`).

The selector's actual `ResolveDefault` prefers an available `default` or `standard`, then the first non-`code-review` candidate, then the first candidate (`WorkflowSelector.cs:193–215`). This can differ from the configured project default placed first by the coordinator. The coordinator's outer exception fallback, by contrast, returns its resolved project default (`CoordinatorOrchestratorExecutor.cs:389–403`). These are distinct fallback sites, not one universal “first candidate” rule.

## Override hierarchy

The algorithm runs overrides and availability checks before the LLM is ever invoked. In priority order (highest first):

### 1. Request-level dialog override

`StartOrchestrationRequest.WorkflowOverrideId` is set when the user selects a workflow from the **Workflow** dropdown in the **Start task** dialog. The endpoint passes it through `CoordinatorRunService.StartCoordinatorRunAsync` to `CoordinatorDraftInput.WorkflowOverrideId`.

In `SelectWorkflowAsync` (`apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:299`):

```csharp
var overrideId = input.WorkflowOverrideId
    ?? await ResolveWorkflowOverrideIdAsync(backlogStore, input.RunId, ct);
```

The dialog override is resolved first. If it is set, it wins over the backlog-task pin without a fallback to the pin.

### 2. Backlog-task pin

`BacklogTask.WorkflowOverrideId` is set on the task card via `PUT /api/projects/{id}/backlog/tasks/{taskId}/workflow-override`. When the heartbeat picks up the task and the dialog override is absent, `ResolveWorkflowOverrideIdAsync` (`apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:515–523`) reads the override from the backlog task.

Both the dialog override and the backlog-task pin are subject to the same availability check: the workflow must exist in the available set. An unavailable override is logged and ignored; selection continues with conversational override, candidate count, and then the LLM path as applicable—not an immediate default.

### 3. Conversational override (`use {workflow-id}`)

Typing `use {workflow-id}` as revision feedback before confirming the OutcomeSpec triggers `WorkflowSelector.TryParseOverride` (`apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:182`), which matches the pattern:

```
^\s*use\s+(?<id>[A-Za-z0-9._-]+)\s*$
```

This is checked before singleton handling (`apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:331–349`). An available conversational choice emits a selection event and wins unless an earlier available request/backlog override already returned.

### 4. LLM auto-select

The initial `WorkflowSelector.SelectAsync` call is reached when two or more available workflows exist and no available explicit override resolved. The choice and rationale are surfaced in a `coordinator.workflow_selected` event.

### 5. Deterministic fallback

The coordinator resolves the project default first (`WorkflowRegistry.ResolveDefault`) and orders it first. It does not pre-empt automatic selection. See the distinct selector and coordinator fallback rules above.

```
available dialog override > available backlog-task pin (when dialog value absent)
  > available conversational use {id} > singleton / model selection > deterministic fallback
```

### Post-decomposition compatibility

This is a separate check, not another override priority. If decomposition produces code but the selected workflow lacks `build_test`, automatic selection reselects among compatible candidates. If none exist, it tries the conformance-checked platform `software-delivery`, then `bug-fix`, outside the project's normal allowed-set filter; absence of a valid fallback fails instead of silently dropping the gate. An **explicit** choice remains selected with a missing-Build/Test warning (`CoordinatorOrchestratorExecutor.cs:407–494`; `WorkflowRegistry.cs:108–119`).

## Invocation context

`RunOrigin` records how work started; selection does not filter candidates by origin. There is no current `WorkflowInvocationKind` or `ResolveInvocationKindAsync` API in this path (`CoordinatorOrchestratorExecutor.cs:297–300`).

Event and schedule trigger producers are implemented. Subject to automation activation and durable invocation claims, they publish Ready backlog tasks pinned to the matching workflow; normal pickup then starts the coordinator. See [invocation context](workflow-engine.md#invocation-context). Upstream trigger conditions determine when work begins, not which workflows are valid.

## End-to-end flow

![Workflow selection: available request or backlog override, conversational override, singleton or bounded model selection, distinct fallback rules, then code-producing decomposition compatibility](../diagrams/canonical-workflow-selection.png)

<!-- Shared read-only canonical; editable source: ../diagrams/src/canonical-workflow-selection.drawio. -->

## The workflow selection event

An available explicit override emits `coordinator.workflow_selected` before the count check. The multi-candidate selector path also emits it, and post-decomposition reselection may emit a second event. The event carries:

| Field | Meaning |
|---|---|
| `selectedId` | The chosen workflow id |
| `selectedName` | The chosen workflow name |
| `rationale` | Why this workflow was selected (or why the default was used) |
| `wasAutoSelected` | `true` when the LLM (or fallback) picked; `false` for an explicit user override |
| `overrideHint` | `"Reply 'use {other-id}' to change..."` with the available list |
| `available` | The full list of available workflows at selection time |

Only the automatic singleton/empty-candidate shortcut is silent. The coordinator's outer exception fallback persists reasoning without emitting this selection event.

## Source

| File | Responsibility |
|---|---|
| `apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs` | Selector contracts, prompt construction, override parser, bounded retry, candidate matching and fallback |
| `apps/Agentweaver.Api/Coordinator/CopilotWorkflowSelectionModel.cs` | Production model-completion seam |
| `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–494` | Override hierarchy, candidate selection and post-decomposition compatibility |
| `apps/Agentweaver.Api/Coordinator/CoordinatorMessages.cs:15` | `CoordinatorDraftInput.WorkflowOverrideId` — carries the dialog override into the executor |
| `apps/Agentweaver.Api/Contracts/Dtos.cs` | `StartOrchestrationRequest.WorkflowOverrideId` — the request DTO field |
| `apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs` | `POST /api/projects/{id}/orchestrations` — passes `workflow_override_id` to `CoordinatorRunService` |
| `apps/web/src/api/client.ts` | `startOrchestration(projectId, goal, workflowOverrideId?)` — passes `workflow_override_id` in the request body |

<!-- diagram-context:canonical-workflow-selection:start -->
<details id="diagram-context-canonical-workflow-selection" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Workflow selection</td></tr>
<tr><td>subtitle</td><td>Trigger-agnostic • explicit choices precede singleton</td></tr>
<tr><td>returns-heading</td><td>SOURCE / RETURN</td></tr>
<tr><td>outcomes-heading</td><td>OUTCOMES</td></tr>
<tr><td>footer</td><td>Post-decomposition Build &amp; Test compatibility is a separate check (executor:407–494).</td></tr>
<tr><td>Load candidates</td><td>Load candidates</td></tr>
<tr><td>Load candidates</td><td>Project default ordered first</td></tr>
<tr><td>Load candidates</td><td>registry.Available</td></tr>
<tr><td>Explicit override?</td><td>Explicit override?</td></tr>
<tr><td>Explicit override?</td><td>Dialog value, else backlog pin</td></tr>
<tr><td>Explicit override?</td><td>must be available</td></tr>
<tr><td>Conversational choice?</td><td>Conversational choice?</td></tr>
<tr><td>Conversational choice?</td><td>Revision feedback: use {id}</td></tr>
<tr><td>Candidate count</td><td>Candidate count</td></tr>
<tr><td>Candidate count</td><td>Only automatic selection</td></tr>
<tr><td>Candidate count</td><td>0 / 1 / multiple</td></tr>
<tr><td>Ask selection model</td><td>Ask selection model</td></tr>
<tr><td>Ask selection model</td><td>Goal + roles + process fit</td></tr>
<tr><td>Ask selection model</td><td>maximum 2 attempts</td></tr>
<tr><td>Usable candidate?</td><td>Usable candidate?</td></tr>
<tr><td>Usable candidate?</td><td>Parse / normalize / prose match</td></tr>
<tr><td>Usable candidate?</td><td>reject unknown choices</td></tr>
<tr><td>Selected workflow</td><td>Selected workflow</td></tr>
<tr><td>Selected workflow</td><td>Emit selection + rationale</td></tr>
<tr><td>Selected workflow</td><td>workflow_selected</td></tr>
<tr><td>Explicit choice</td><td>Explicit choice</td></tr>
<tr><td>Explicit choice</td><td>Emit selection</td></tr>
<tr><td>Explicit choice</td><td>not auto-selected</td></tr>
<tr><td>Silent choice</td><td>Silent choice</td></tr>
<tr><td>Silent choice</td><td>One: candidate</td></tr>
<tr><td>Silent choice</td><td>Zero: project default</td></tr>
<tr><td>Model fallback</td><td>Model fallback</td></tr>
<tr><td>Model fallback</td><td>default / standard then non-code-review</td></tr>
<tr><td>Model fallback</td><td>else first candidate</td></tr>
<tr><td>Outer fallback</td><td>Outer fallback</td></tr>
<tr><td>Outer fallback</td><td>Project default</td></tr>
<tr><td>Outer fallback</td><td>when catch permits</td></tr>
<tr><td>edge-02-label</td><td>available</td></tr>
<tr><td>edge-03-label</td><td>absent / invalid</td></tr>
<tr><td>edge-06-label</td><td>0 or 1</td></tr>
<tr><td>edge-07-label</td><td>2+</td></tr>
<tr><td>edge-08-label</td><td>response</td></tr>
<tr><td>edge-09-label</td><td>exception</td></tr>
<tr><td>edge-10-label</td><td>accepted</td></tr>
<tr><td>edge-11-label</td><td>retry once</td></tr>
<tr><td>edge-12-label</td><td>2 unusable</td></tr>
<tr><td>edge-13-label</td><td>emit choice</td></tr>
<tr><td>edge-14-label</td><td>outer catch</td></tr>
<tr><td>fallback</td><td>default / standard</td></tr>
</tbody></table>
</details>
<!-- diagram-context:canonical-workflow-selection:end -->
