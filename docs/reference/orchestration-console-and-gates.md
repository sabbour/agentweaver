# Orchestration console and gates — Reference

Reference for the v0.7.12 orchestration console, Outcome plan phase, Build & Test gate, workflow-editor gate authoring, blueprint gate awareness, and the two backend fixes called out for this wave.

## Routes and client calls

| Surface | Route / call | Notes |
|---|---|---|
| Coordinator run page | `/projects/:projectId/orchestrations/:runId` | Renders the four-zone console. The route is listed as **Coordinator run** in the web reference (`docs/reference/web.md`). |
| Outcome plan snapshot | `GET /api/runs/{id}/outcome-spec` | Returns `OutcomeSpecResponse`; `404` before drafting is expected and the panel keeps polling (`CoordinatorEndpoints.cs:22`, `OutcomePlanPanel.tsx:244`). |
| Outcome plan confirm | `POST /api/runs/{id}/outcome-spec/confirm` | Returns the updated spec, `409 run_not_active`, or `409 no_pending_gate` (`CoordinatorEndpoints.cs:47`, `:77`). |
| Outcome plan revise | `POST /api/runs/{id}/outcome-spec/revise` | Requires `{ feedback }`; re-drafts and re-suspends at the same gate (`CoordinatorEndpoints.cs:86`, `:98`). |
| Work plan snapshot | `GET /api/runs/{coordinatorRunId}/work-plan` | Seeds subtasks and dependencies; returns `404` when no plan exists yet (`CoordinatorEndpoints.cs:145`, `:168`). |
| Children snapshot | `GET /api/runs/{coordinatorRunId}/children` | Returns dispatched child runs paired with subtask status, or an empty array before dispatch (`CoordinatorEndpoints.cs:172`). |
| Coordinator message | `POST /api/runs/{coordinatorRunId}/steer` | Used by the single **Message coordinator** composer; child context passes `target_child_run_id` (`CoordinatorEndpoints.cs:198`, `AgentSessionPanel.tsx:1580`). |
| Workflow save | `PUT /api/projects/{projectId}/workflows/{workflowId}` | After writing YAML, the endpoint adds a new workflow id to `AllowedWorkflowIds`, syncs the registry, and returns the reloaded definition or a precise reload error (`WorkflowDefinitionEndpoints.cs:214`, `:299`, `:314`, `:331`). |

## OutcomeSpec DTO

`OutcomeSpecResponse` fields come from `apps/Agentweaver.Api/Contracts/Dtos.cs:889`; the web interface mirrors them in `apps/web/src/api/types.ts:526`.

| Field | Type | Notes |
|---|---|---|
| `goal` | string | Original request. |
| `desiredOutcome` | string | Coordinator-authored target outcome. |
| `scope` | string or string[] in web | Server sends a string; web renders string or list defensively. |
| `assumptions` | string or string[] in web | Server sends a string; web renders string or list defensively. |
| `clarifyingQuestions` | string or string[] | Optional; omitted when no questions exist. |
| `status` | `drafting` \| `awaiting_confirmation` \| `confirmed` \| `declined` | UI badge values are mapped in `OutcomePlanPanel.tsx:164`. |
| `confirmedBy` | string | Optional; set after confirmation. |

## WorkPlan DTO

The work-plan client contract lives in `apps/web/src/api/types.ts:568` and `:648`.

| Field | Type | Notes |
|---|---|---|
| `workPlanId` | number | Persisted plan id. |
| `coordinatorRunId` | string | Parent coordinator run id. |
| `outcomeSpecId` | number | Confirmed outcome spec id this plan implements. |
| `status` | string | Server-authored work-plan status. |
| `isolationSummary` | string | Optional summary. |
| `subtasks` | `WorkPlanSubtaskResponse[]` | Includes subtask id, title, scope, assigned agent, selected model, phase, isolation, status, and optional child run id. |
| `dependencies` | `WorkPlanDependencyResponse[]` | Edges from prerequisite subtask to dependent subtask. |

## Event contract

| Event | Used by | Notes |
|---|---|---|
| `coordinator.outcome_spec` | Outcome plan node and panel | Latest event wins by `sequence`; carries desired outcome, scope, assumptions, questions, and status (`OutcomePlanPanel.tsx:280`). |
| `coordinator.outcome_spec.confirmed` | Outcome plan status | Forces `confirmed` and captures `confirmedBy` (`OutcomePlanPanel.tsx:285`, `:305`). |
| `coordinator.work_plan` | Work plan node, run tree, activity line | Marks work-plan availability and seeds subtask narratives (`CoordinatorRunPage.tsx:1266`; `AgentSessionPanel.tsx:1135`). |
| `coordinator.graph` | Graph shape | Highest `seq` wins over REST graph seed (`CoordinatorRunPage.tsx:1228`). |
| `subtask.*` | Run tree and coordinator activity | `dispatched`, `running`, `assemble_ready`, `rai_flagged`, `completed`, `failed`, and `pending_capacity` are rendered as session activity (`AgentSessionPanel.tsx:1170`). |
| `coordinator.child_approval_resolved` | Session pane | Renders child approval outcome as `approved`, `denied`, or `expired` (`AgentSessionPanel.tsx:1220`). |

The global event taxonomy page lists the same coordinator and subtask event names in `docs/reference/events.md`.

## Build & Test workflow node

| Property | Value |
|---|---|
| YAML type | `build_test` (`WorkflowDefinitionLoader.cs:213`) |
| Graph role / node type | `review` / `gate` (`WorkflowDtos.cs:181`, `:194`) |
| Default agent | `qa-engineer` when omitted in generator guidance (`CopilotWorkflowGenerator.cs:137`) |
| Runtime executor | `BuildTestTurnExecutor` (`BuildTestTurnExecutor.cs:10`) |
| Prompt ownership | Platform-owned `CannedPrompt`; authors do not set a prompt (`BuildTestTurnExecutor.cs:12`, `CopilotWorkflowGenerator.cs:137`) |
| Verdicts | `approved`, `request-changes`, `declined` (`BuildTestTurnExecutor.cs:169`) |
| Preview activation | After tests pass for web apps/services, discover actual bound port, verify the server, then call `start_preview(port=PORT)` (`BuildTestTurnExecutor.cs:17`, `:20`) |

### YAML example

```yaml
- id: build-test
  type: build_test
  label: Build & Test
  role: review
  agent: qa-engineer
```

Route `approved` to human review, `request-changes` back to implementation, and `declined` to a terminal node. The built-in `bug_fix` and `software_delivery` workflows follow that pattern (`bug_fix.yaml:103`; `software_delivery.yaml:144`).

## Workflow editor authoring

The visual editor exposes special gate palette entries for RAI Check, Rubberduck Review, Human Review, and Build & Test (`VisualWorkflowEditor.tsx:127`, `:137`, `:148`, `:158`). The generic node-type dropdown uses `AUTHORABLE_WORKFLOW_NODE_TYPES`, which filters out `merge` and `scribe` (`workflowYaml.ts:33`). Existing Merge and Scribe nodes can still load, but the inspector marks them read-only because the platform owns the tail (`VisualWorkflowEditor.tsx:123`, `:654`).

## Blueprint gate awareness

`CopilotBlueprintGenerator` includes both `WorkflowGatePromptGuidance.BlueprintGateAwareness` and `SoftwareBuildTestRequirement` in the prompt (`CopilotBlueprintGenerator.cs:127`, `:129`). The shared guidance says blueprints must preserve or trigger specialized gates, must include `build_test` immediately before human review for buildable/runnable software, should include RAI/Rubberduck/Human Review when warranted, and must not ask for Merge or Scribe nodes (`WorkflowGatePromptGuidance.cs:27`).

## Backend fix notes

### Approval already resolved or expired

`POST /api/runs/{id}/tool-approvals` and `/tool-denials` now return a resolved `200` payload when the request id is already approved, denied, or expired instead of only surfacing a failing conflict. The response includes `resolved: true`, `expired`, and `state` (`RunEndpoints.cs:1544`, `:1553`, `:1592`, `:1602`). Unknown request ids still return `404` with a message that tells operators to post to the child subtask run id when applicable (`RunEndpoints.cs:1562`, `:1610`). `FormatApprovalState` maps terminal states to `approved`, `denied`, `expired`, `pending`, or `unknown` (`RunEndpoints.cs:2488`).

### Workflow save reload

After saving workflow YAML, the endpoint ensures a new workflow id is added to the project's allowed workflow ids before syncing the registry (`WorkflowDefinitionEndpoints.cs:299`, `:307`, `:314`). If reload still fails, it logs the allowed ids and invalid-entry error, then returns a specific failure for validation versus discovery (`WorkflowDefinitionEndpoints.cs:331`, `:341`, `:347`).

## See also

- [Orchestration console and gates — User Guide](../experience/orchestration-console-and-gates.md)
- [Orchestration console and gates — Deep Dive](../deep-dive/orchestration-console-and-gates.md)
- [Coordinator reference](./coordinator.md)
- [Events reference](./events.md)
- [Web UI reference](./web.md)
- [Sandbox browser preview reference](./sandbox-browser-preview.md)
