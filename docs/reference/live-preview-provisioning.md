# Decoupled live-preview provisioning — Reference

See Resolve command, supervise process, observe port, approve and publish for the shared visual model.

Reference for the platform-owned preview step that runs after Build & Test. It starts a supervised app process, discovers the actual port, registers a Gateway preview URL, and records a durable preview outcome without changing the Build & Test verdict.

For the Gateway routes and `PortForwardSessionDto`, see [Sandbox browser preview — Reference](./sandbox-browser-preview.md). For implementation details, see the [deep dive](../deep-dive/live-preview-provisioning.md). For the user workflow, see the [experience guide](../experience/live-preview-provisioning.md).

## Runtime contract

| Contract | Shipped behavior | Source |
| --- | --- | --- |
| Feature flag | None. The step runs whenever `PreviewStep` is wired and Build & Test is not declined. | `CoordinatorAssemblyService.ShouldRunDeterministicPreviewStep` |
| Command resolution | Two tiers: the fast/free/deterministic `PreviewCommandResolver` heuristics run first; only when they return `Unresolved` does an LLM fallback (`IPreviewCommandModel`, issue #541) get a bounded worktree view and propose a command. The model-chosen command runs through the identical start/observe/approval path — only the command string's origin differs. If neither tier resolves, the terminal `preview_command_unresolved` outcome is preserved. | `PreviewStep.cs`; `CopilotPreviewCommandModel.cs` |
| Build & Test coupling | Runs after Build & Test for `APPROVED` and `REQUEST_CHANGES`; skipped on `DECLINED`. | `CoordinatorAssemblyService.cs:753` |
| Port choice | Platform observes the app port inside the sandbox pod using log hints plus `/proc/net/tcp` and `/proc/net/tcp6`, then registers a forwarder public port from `3000-9000`; no configured fixed app port is used. | `PreviewStep.cs:129`, `:166`; `PreviewRunner.cs:262`, `:610`, `:315` |
| Registration readiness | In-pod AgentHost observe verifies app + forwarder readiness; the API never probes `podIP:{target_port}` before creating Service/HTTPRoute. | `PreviewRunner.cs:315`; `SandboxPreviewService.cs:134` |
| End-to-end reachability | Confirmed by immediately using the returned Gateway hostname (`preview_url`), because NetworkPolicy admits preview-port ingress only from the Gateway. App Routing owns the managed DNS zone and creates each preview record; DNS name-resolution failures retry with bounded backoff until the configured convergence deadline (ten minutes by default), while an existing record succeeds immediately. | `k8s/base/networkpolicy-sandbox.yaml`; `SandboxPreviewService.cs` |
| Infra unavailable | Emits `sandbox.preview_skipped_not_applicable` with reason `preview_infra_unavailable`. | `PreviewStep.cs:83` |
| Preview failure | Emits `sandbox.preview_failed`; never blocks human review and never forces changes. | `PreviewStep.cs:31`, `CoordinatorAssemblyService.cs:772` |
| Approval | Uses existing `AgentPreviewGate`; no preview-specific bypass. | `PreviewStep.cs:157` |

## Sandbox pod retention while a preview is active (issue #542)

A live preview resolves through: Gateway → per-preview `HTTPRoute` → per-run ClusterIP `Service`
→ the run's **sandbox pod** (selected by pod label). The `HTTPRoute`/`Service` are reaped on their
own annotation-driven schedule, but they are useless once the pod behind them is gone. Historically
the sandbox pod's `SandboxClaim` was deleted **unconditionally** the moment the originating subtask's
turn ended (`KubernetesSandboxExecutor.ReleaseAgentHostPodAsync`), and a completed subtask's claim
also became an "orphan" to `AgentHostReaperService` immediately — so a preview URL handed to a human
reviewer would `404` within minutes, before the review gate could open it.

Release, orphan reaping and preview activity use `ReconcilePreviewLifecycleAsync(runId)`. Durable HTTPRoute annotations determine run-level state and idempotent retention changes.

| State | Durable evidence | Sandbox effects |
|---|---|---|
| `PreviewActive` | At least one route has both idle and maximum expiry in the future. | Extend backing claim TTL and set pod `safe-to-evict=false`; release/reaping defer. |
| `Previewable` | No qualifying route, unavailable client/run identity, or lookup failure. | Restore normal TTL and `safe-to-evict=true`; normal cleanup can proceed. |

Cleanup reads cluster state even if creation is disabled in this process. The backing pod need not exist for a retention decision. Protection patches are best-effort, not a guarantee against infrastructure loss.

Both supported claim-name candidates are merge-patched without removing sibling fields. Active retention uses service-level `LifetimeMinutes * 60 + 600`; inactive state restores `Sandbox:Kubernetes:TimeoutSeconds` (default 600). Stop/expiry reconcile after route deletion: another live route retains the pod; removing the final one reverses protection.

## AgentHost preview-runner endpoints

These are platform-facing AgentHost endpoints. They are root-mounted on the AgentHost origin, not under the A2A path (`apps/Agentweaver.AgentHost/Program.cs:291`).

| Method & path | Body | Returns | Notes |
| --- | --- | --- | --- |
| `POST /preview-runner/processes` | `command`, `cwd`, optional `runId`, `workPlanId`, `treeHash` | `session_id`, `pid`, `started_at`, `working_directory` | Starts a supervised process. |
| `POST /preview-runner/processes/{sessionId}/observe-bound-port` | `timeoutSeconds`, `healthPath` | `session_id`, `port`, `evidence`, `healthy`, `health_evidence`, `app_port`, optional `reason` | Runs in the sandbox pod: discovers the app port from logs or `/proc/net/tcp{,6}`, starts the pod-local forwarder on `0.0.0.0`, verifies HTTP health through the forwarder public port, and returns that public port as `port`. On observe failure it still returns `200` with `healthy=false` and a closed-set `reason`. |
| `POST /preview-runner/processes/{sessionId}/health-check` | `port`, optional `path` | `session_id`, `port`, `path`, `healthy`, `status_code`, `evidence` | Used directly and by Gateway keepalive dual-touch. |
| `DELETE /preview-runner/processes/{sessionId}` | optional `reason` query | `session_id`, `stopped`, `reason` | Stops the process tree. |

Auth accepts either the per-run turn bearer token or the per-run preview-runner credential. If either is configured, missing or invalid auth returns `401` (`apps/Agentweaver.AgentHost/Program.cs:450`).

## Preview events

| Event | Final? | Payload fields | Meaning |
| --- | --- | --- | --- |
| `sandbox.preview_applicability` | No | `run_id`, `work_plan_id`, `tree_hash`, `state`, `reason`, `evidence` | Applicability recorded before Build & Test. |
| `sandbox.preview_start_requested` | No | `run_id`, `work_plan_id`, `tree_hash`, `source`, `command_source` | `PreviewStep` resolved a command and is starting the app. `command_source` distinguishes the tier that resolved it: a heuristic source (e.g. `package.json:dev`, `csproj`, `dockerfile`) or `llm` for the model fallback (issue #541). |
| `sandbox.preview_pending` | No | `run_id`, `work_plan_id`, `tree_hash`, `target_port`, `approval`, `request_id`, `expires_at`, `timeout_minutes`, optional `retry_of_request_id` | Preview approval gate is waiting, including a fresh retry attempt. |
| `sandbox.preview_ready` | Yes | `run_id`, `work_plan_id`, `tree_hash`, `target_port`, `pod_name`, `session_id`, `preview_runner_session_id`, `preview_url`, `keepalive_url`, `started_at` | Gateway preview is ready. `session_id` is the Gateway token; `preview_runner_session_id` is the supervised process id. |
| `coordinator.preview_ready` | Mirror | Same as `sandbox.preview_ready` | Coordinator-family mirror for the ready outcome. |
| `sandbox.preview_failed` | Yes | `run_id`, `work_plan_id`, `tree_hash`, `source`, `reason`, `message`; timeout additionally includes `approval_request_id`, `retry_available`, `expired_at`, `preview_runner_session_id` | Preview did not produce a URL; review can continue. An approval timeout retains the process and can be retried. |
| `sandbox.preview_skipped_not_applicable` | Yes | `run_id`, `work_plan_id`, `tree_hash`, `source`, `reason`, `message` or `evidence` | Preview intentionally skipped, including infra unavailable. |
| `workflow.step` | Stage state | `step: "preview"`, `status`, `label`, `message`, `timestamp_utc` | Drives graph/run-tree preview status. |

## Failure and skip reasons

| Reason | Meaning |
| --- | --- |
| `preview_infra_unavailable` | Pod-per-run or Gateway preview infrastructure cannot produce a reachable URL. |
| `preview_command_unresolved` | Neither resolution tier could determine how to run the app: the deterministic resolver found no match (it tries the worktree root first, then probes conventional subdirectories — `client`, `app/client`, `frontend`, `web`, `app`, `src/client` — in that order; server/API/backend directories are not probed) AND the LLM fallback (issue #541) either declined, was unavailable, or proposed a command that failed defensive validation (empty command, or a working directory outside the worktree). |
| `preview_runner_unauthorized` | AgentHost rejected the preview-runner credential. |
| `process_exited` | Preview process could not start. |
| `process_exited:exit={code}` | Preview process started but exited before a healthy port was observed. |
| `no_listening_port_discovered` | Observe timed out without finding a healthy listening port in logs or `/proc/net/tcp{,6}`. |
| `observe_error` | Unexpected AgentHost observe-endpoint error; surfaced as a structured unhealthy result, not an opaque HTTP 500. |
| `health_check_failed` | A port was found but did not pass the HTTP health check. |
| `bound_unreachable` | The app's loopback health check passed, but the forwarder public port did not pass the through-forwarder health check. |
| `no_public_port_available` | AgentHost could not bind any free forwarder public port in the allowed `3000-9000` range. |
| `approval_denied` | Operator denied preview exposure. |
| `approval_timed_out` | Operator did not approve before the project timeout (1440 minutes / 24 hours by default). The latest expired attempt can be retried with a fresh request id while reusing the retained process. |
| `port_not_allowed` | Observed port is outside the allowed Gateway preview range. |
| `registration_failed` | Gateway preview registration failed. |
| `preview_outcome_missing` | Safety-net guard found no terminal outcome. |

Consumers should display unknown reasons as text and continue.

## Credential lifecycle

| Step | Behavior | Source |
| --- | --- | --- |
| Mint | Fresh random value per AgentHost launch. | `PreviewRunnerCredential.Mint` |
| Delivery | Sent in the `/configure` request body; not env/file/config. | `KubernetesSandboxExecutor.CallAgentHostConfigureAsync` |
| Storage | Persisted in the run secret store under a deterministic key for cross-replica reconcile. | `PreviewRunnerCredential.SecretKey` |
| AgentHost memory | Stored in `AgentHostRuntimeState.PreviewRunnerCredential`. | `AgentHostRuntimeState.cs` |
| Cleanup | Deleted on pod release and orphan reaper sweep. | `KubernetesSandboxExecutor.ReleaseAgentHostPodAsync`, `AgentHostReaperService.TryDeleteOrphanCredentialAsync` |

## Web projection

The coordinator run page reads the latest preview event:

| Latest event | UI state |
| --- | --- |
| `sandbox.preview_ready` or `coordinator.preview_ready` | **Open preview** on Build & Test and human review. |
| `sandbox.preview_pending` | **Preview pending approval**. |
| `sandbox.preview_failed` | **Preview unavailable** with reason/message; review remains actionable. |
| `sandbox.preview_skipped_not_applicable` | No unavailable error; preview was intentionally skipped. |

## See also

- [Events reference](./events.md)
- [Coordinator reference](./coordinator.md)
- [Sandbox browser preview — Reference](./sandbox-browser-preview.md)

<details id="diagram-context-live-preview-provisioning-fig1" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Preview ready means validated publication</td></tr>
<tr><td>takeaway</td><td>Platform-owned orchestration still has explicit skip, denial, expiry and failure outcomes.</td></tr>
<tr><td>Build/Test verdict</td><td>Build/Test verdict</td></tr>
<tr><td>Build/Test verdict</td><td>Approved or request-changes</td></tr>
<tr><td>Build/Test verdict</td><td>Declined: no preview stage</td></tr>
<tr><td>Command resolution</td><td>Command resolution</td></tr>
<tr><td>Command resolution</td><td>Heuristic then bounded model</td></tr>
<tr><td>Command resolution</td><td>Unavailable infra: skipped</td></tr>
<tr><td>AgentHost runner</td><td>AgentHost runner</td></tr>
<tr><td>AgentHost runner</td><td>Map effective workspace</td></tr>
<tr><td>AgentHost runner</td><td>Start authenticated process</td></tr>
<tr><td>App + forwarder health</td><td>App + forwarder health</td></tr>
<tr><td>App + forwarder health</td><td>Observe actual app port</td></tr>
<tr><td>App + forwarder health</td><td>Bind reachable public port</td></tr>
<tr><td>Preview approval</td><td>Preview approval</td></tr>
<tr><td>Preview approval</td><td>Grant / deny / expire</td></tr>
<tr><td>Preview approval</td><td>Policy auto-approval is explicit</td></tr>
<tr><td>Approved publication</td><td>Approved publication</td></tr>
<tr><td>Approved publication</td><td>Active run + process recheck</td></tr>
<tr><td>Approved publication</td><td>Create Service and HTTPRoute</td></tr>
<tr><td>No publication</td><td>No publication</td></tr>
<tr><td>No publication</td><td>Deny: stop; expire: private retry</td></tr>
<tr><td>No publication</td><td>Never emit ready on rejection</td></tr>
<tr><td>Generated HTTPS URL</td><td>Generated HTTPS URL</td></tr>
<tr><td>Generated HTTPS URL</td><td>Bounded DNS/readiness checks</td></tr>
<tr><td>Generated HTTPS URL</td><td>Failure: rollback publication</td></tr>
<tr><td>preview_ready</td><td>preview_ready</td></tr>
<tr><td>preview_ready</td><td>Exact URL validated</td></tr>
<tr><td>preview_ready</td><td>Return to authored gate handling</td></tr>
<tr><td>arrow-1</td><td>prepare</td></tr>
<tr><td>arrow-2</td><td>start</td></tr>
<tr><td>arrow-3</td><td>observe</td></tr>
<tr><td>arrow-4</td><td>request</td></tr>
<tr><td>arrow-5</td><td>grant</td></tr>
<tr><td>arrow-6</td><td>reject</td></tr>
<tr><td>arrow-7</td><td>probe</td></tr>
<tr><td>arrow-8</td><td>healthy</td></tr>
<tr><td>note-0</td><td>Denial/expiry branch stays private; unresolved commands fail explicitly.</td></tr>
<tr><td>note-1</td><td>Rows summarize stages; the page retains detailed failure and retry rules.</td></tr>
<tr><td>note-2</td><td>Resource creation alone is not readiness; API does not probe pod preview ports.</td></tr>
<tr><td>notes</td><td>Denial/expiry branch stays private; unresolved commands fail explicitly.; Rows summarize stages; the page retains detailed failure and retry rules.; Resource creation alone is not readiness; API does not probe pod preview ports.</td></tr>
</tbody></table>
</details>
