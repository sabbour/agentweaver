# Coordinator Workflow Selection

A blueprint supplies a set of workflows, not one universal pipeline. For example,
Software Development supplies `software-delivery` and `bug-fix`. The coordinator
selects for the task's process and outputs, rather than name similarity.

## The selection flow

![Workflow selection: collect valid trigger-agnostic candidates, honor explicit and conversational overrides, handle zero or one candidate, then select with bounded retries and deterministic fallback](diagrams/canonical-workflow-selection.png)

<!-- Editable source: diagrams/src/canonical-workflow-selection.drawio.
     Export with pinned draw.io Desktop 31.4.5 using --spec canonical-workflow-selection.
     Review evidence: diagrams/reviews/canonical-workflow-selection/. -->

1. Collect available, valid definitions from the registry. Selection is trigger-agnostic.
1. Honor an available explicit request/backlog override, then an available conversational
   override from the orchestration input's `ReviseFeedback`.
1. Handle zero or one candidate without a model call. With multiple candidates, supply
   task, team roles and workflow descriptions/source tags to the selector.
1. Parse the chosen ID/name against available candidates. Parse failures or unknown
   choices receive up to two attempts; a model exception falls back immediately.
1. Persist/surface the selected workflow and rationale, then revalidate compatibility
   with the decomposition before execution.

The fallback prefers `default`/`standard`, then a non-code-review candidate, and only
then the first entry. It is not universally the project's first listed workflow.
Selection failure does not waive later binding or compatibility checks.

Sources: `CoordinatorOrchestratorExecutor.cs:271-372,407-488` and
`WorkflowSelector.cs:83-218`.

## User override

An explicit request/backlog override takes priority over conversational `use {id}`
selection. The traced orchestration path reads revision feedback; it does **not**
intercept every user message and switch any already-running workflow in place.

## Result and event contract

`WorkflowSelectionResult` contains `Selected`, `Rationale` and `WasAutoSelected`.
`coordinator.workflow_selected` carries the selected ID/name, rationale, available
choices and override hint where that path emits the selection. Explicit and
conversational overrides set `wasAutoSelected: false`; that value does not identify
only singleton projects.

| Condition | Outcome |
| --- | --- |
| Valid explicit/backlog override | Select it before conversational/model selection; not automatic |
| Valid conversational override | Select from available definitions; not automatic |
| One candidate | Return it without a model call |
| Parse failure or unknown model selection | Retry within the two-attempt bound, then fallback |
| Model throws | Immediate fallback |
| Valid automatic choice | Return the matched definition and rationale |

The root workflow library is the canonical
[blueprint mapping](workflow-library.md#blueprint-workflow-mappings).

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
