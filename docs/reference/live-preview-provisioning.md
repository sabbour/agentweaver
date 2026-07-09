# Live-preview provisioning — Reference

Reference for the coordinator Build & Test live-preview contract: the events, gate ordering, tool calls, and UI states that make preview provisioning an enforced outcome before human review.

For proxy routes and the `PortForwardSessionDto`, see [Sandbox browser preview — Reference](./sandbox-browser-preview.md). For the implementation flow, see the [deep dive](../deep-dive/live-preview-provisioning.md); for the review experience, see the [user guide](../experience/live-preview-provisioning.md).

## Coordinator contract

| Phase | Source | Contract |
| --- | --- | --- |
| Applicability | `CoordinatorAssemblyService.EnsurePreviewApplicabilityRecordedAsync` | Emits `sandbox.preview_applicability` before Build & Test. Documentation-only diffs are skipped; server/app-looking or ambiguous diffs require preview. |
| Build & Test instruction | `BuildTestTurnExecutor.CannedPrompt` | Build and test first. If previewable, call `start_preview_process`, `observe_bound_port`, then `start_preview(port=PORT)` with the observed port. |
| Preview approval | `AgentPreviewGate.RequestApprovalAsync` | Reuses the existing tool approval gate and auto-approve sources. No special preview bypass exists. |
| Preview provisioning | `SandboxEndpoints.StartPreviewForRunAsync` | On Gateway success, emits `sandbox.preview_ready`, `coordinator.preview_ready`, and a completed preview `workflow.step`. |
| Outcome guard | `CoordinatorAssemblyService.EnsureFinalPreviewOutcomeBeforeApprovalAsync` | Requires one final preview outcome before Build & Test approval is applied. If none exists, emits `sandbox.preview_failed` with `reason: "preview_outcome_missing"` and proceeds to human review. |

## PreviewRunner tools

These tools are contributed by the AgentHost PreviewRunner (`apps/Agentweaver.AgentHost/PreviewRunner.cs:70`).

| Tool | Arguments | Returns to agent | Notes |
| --- | --- | --- | --- |
| `start_preview_process` | `command`, optional `cwd`, optional `work_plan_id`, optional `tree_hash` | `preview_process_started: session_id=..., pid=..., cwd=...` | Starts a managed process under AgentHost supervision. |
| `observe_bound_port` | `session_id`, optional `timeout_seconds` | `bound_port_observed: ... port=..., healthy=True/False ...` | Parses `LISTENING ON PORT`, `Local: http://...`, `Now listening on: ...`; then falls back to socket diffing. |
| `health_check` | `session_id`, `port`, optional `path` | `preview_health: ... healthy=..., status=...` | Probes localhost inside the pod; status below 500 is healthy. |
| `stop_preview_process` | `session_id`, optional `reason` | `preview_process_stopped: ... stopped=...` | Stops the process group/tree. |

## Events

| Type | Final? | Required payload fields | Meaning |
| --- | --- | --- | --- |
| `sandbox.preview_applicability` | No | `run_id`, `work_plan_id`, `tree_hash`, `state`, `reason`, `evidence` | Applicability decision before Build & Test. `state` is `preview_required` or `preview_skipped_not_applicable`. |
| `sandbox.preview_skipped_not_applicable` | Yes | `run_id`, `work_plan_id`, `tree_hash`, `source`, `reason`, `evidence` | Final outcome for non-previewable work. Accepted by the guard. |
| `sandbox.preview_pending` | No | `run_id`, `work_plan_id`, `tree_hash`, `target_port`, `approval`, `request_id` | Existing `AgentPreviewGate` is waiting for HITL approval. Not accepted as final. |
| `sandbox.preview_ready` | Yes | `run_id`, `work_plan_id`, `tree_hash`, `target_port`, `pod_name`, `session_id`, `preview_url`, `keepalive_url`, `started_at` | Gateway preview was provisioned. Accepted by the guard when `preview_url` is non-empty. |
| `coordinator.preview_ready` | Mirror | Same as `sandbox.preview_ready` | Coordinator-scoped mirror used by consumers that prefer coordinator event families. |
| `sandbox.preview_failed` | Yes | `run_id`, `work_plan_id`, `tree_hash`, `source`, `reason`, `message`; optional `target_port` | Preview failed, was denied, timed out, or was missing at approval time. Accepted by the guard and surfaced as preview unavailable. |
| `workflow.step` | Stage state | `step: "preview"`, `status`, `label`, optional `message` | Drives the preview stage row in the workflow graph and run tree. |

## Preview failure reasons

| Reason | Emitter | Meaning |
| --- | --- | --- |
| `approval_denied` | `SandboxEndpoints` | The HITL approval was denied. |
| `approval_timed_out` | `SandboxEndpoints` | The HITL approval did not arrive before the approval timeout. |
| `capacity` | `SandboxEndpoints` | Preview session capacity was exceeded. |
| `no_bound_pod` | `SandboxEndpoints` / preview service error mapping | No bound sandbox pod was available for the run. |
| `port_out_of_range` | Endpoint validation | Requested port is outside the configured Gateway preview range. |
| `preview_probe_failed` | Preview service error mapping | The target port could not be reached through the preview path. |
| `gateway_failed` | `SandboxEndpoints` | Unexpected Gateway preview provisioning failure. |
| `preview_outcome_missing` | `CoordinatorAssemblyService` | Build & Test completed without any final preview outcome. |
| `docs_only` | `CoordinatorAssemblyService` | Not a failure; used as skip reason for documentation-only diffs. |
| `server_files_changed` / `ambiguous_default_required` | `CoordinatorAssemblyService` | Applicability reasons that make preview required. |

Consumers must treat unknown `reason` values as displayable text and continue.

## UI projection

`apps/web/src/pages/CoordinatorRunPage.tsx` reads the latest preview event from `GET /api/runs/{id}/events` and the live stream:

| Latest event | UI state |
| --- | --- |
| `sandbox.preview_ready` or `coordinator.preview_ready` with `preview_url` | Shows **Open preview** on the Build & Test row and in the human-review artifacts panel. |
| `sandbox.preview_pending` | Shows **Preview pending approval**. |
| `sandbox.preview_failed` | Shows **Preview unavailable** with the backend reason/message; human review remains actionable. |
| No preview events | Shows no Build & Test preview affordance. |

## Source

| Concern | File |
| --- | --- |
| PreviewRunner tool provider and process lifecycle | `apps/Agentweaver.AgentHost/PreviewRunner.cs` |
| Build & Test prompt | `packages/Agentweaver.AgentRuntime/Workflow/BuildTestTurnExecutor.cs` |
| Coordinator guard and applicability | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs` |
| Endpoint events and Gateway preview response | `apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs` |
| HITL/auto-approve policy | `apps/Agentweaver.Api/Sandbox/Preview/AgentPreviewGate.cs` |
| Event constants | `packages/Agentweaver.Domain/EventTypes.cs` |
| Web event projection | `apps/web/src/pages/CoordinatorRunPage.tsx` |

## See also

- [Events reference](./events.md#event-taxonomy)
- [Coordinator reference](./coordinator.md#phase-3-collective-assembly-and-terminal-status)
- [Sandbox browser preview — Reference](./sandbox-browser-preview.md)
