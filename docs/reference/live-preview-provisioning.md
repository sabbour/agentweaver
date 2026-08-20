# Decoupled live-preview provisioning — Reference

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
| End-to-end reachability | Confirmed by using the returned Gateway hostname (`preview_url`), because NetworkPolicy admits preview-port ingress only from the Gateway. | `k8s/base/networkpolicy-sandbox.yaml`; `SandboxPreviewService.cs:147` |
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

Both teardown paths now consult `ISandboxPreviewService.HasActivePreviewAsync(runId)` and **defer**
while a preview is still alive:

| Teardown path | Behavior with an active preview | Source |
| --- | --- | --- |
| Turn-end release | `ReleaseAgentHostPodAsync` skips the claim delete and returns; the pod stays up. | `KubernetesSandboxExecutor.cs` |
| Orphan reaper sweep | `SweepOrphanedPodsAsync` skips (continues past) a claim whose run has a live preview. | `AgentHostReaperService.cs` |

`HasActivePreviewAsync` returns `true` only when the run has an `HTTPRoute` whose **idle** expiry
(`preview-expires-at`, bumped by keepalive) *and* **hard-max** expiry (`preview-max-until`) are both
still in the future (`PreviewReaper.Decide(...) == Alive`). It deliberately does **not** require the
pod to still exist (the pod is present at the teardown boundary), and it is leak-safe: preview
disabled, no un-expired route, or any lookup failure all return `false`, so the caller performs its
normal teardown. Eventual teardown is therefore bounded and cannot leak: with no keepalive the preview
idle-expires (`Sandbox:Preview:IdleTimeoutMinutes`, default 30) or hits its hard max
(`Sandbox:Preview:MaxLifetimeHours`, default 8); the `SandboxPreviewReaperService` then deletes the
route, and the next `AgentHostReaperService` sweep — now seeing no active preview — reaps the pod.

### Direct-backed execution subtasks and worker reaper parity

Coordinator-dispatched execution subtasks may report `sandbox.backend=direct` because their tool
traffic goes directly to the per-run AgentHost. Their preview process is nevertheless backed by the
same `agent-*` `SandboxClaim` and pod lifecycle described above. Both API and worker roles run
heartbeat/reaper paths, so both must read the shared `HTTPRoute` state before treating a terminal
child claim as orphaned.

The worker deployment therefore mirrors the API's `Sandbox__Preview__*` cluster configuration.
Its sandbox Role has read-only `httproutes` access plus `sandboxclaims: patch,update` and
`pods: patch`. This lets the worker reaper see an active route, renew its backing claim TTL, and
re-assert `safe-to-evict=false` instead of deleting the claim. The worker still cannot create,
modify, or delete preview routes. When the route idle/max lifetime ends, the positive preview check
becomes false and the next orphan sweep deletes the terminal claim and credential, preserving
bounded cleanup.

### Cluster-side claim TTL renewal (issue #560)

Deferring the **API-side** deletes above is necessary but not sufficient. Every `SandboxClaim` is
created with a cluster-side lifecycle TTL
(`spec.lifecycle.ttlSecondsAfterFinished = Sandbox:TimeoutSeconds`, default **600s**, with
`shutdownPolicy: Delete`). The sandbox controller enforces this TTL **independently of the API**: once
a run's pod workload *finishes* — which happens within seconds for a coordinator-dispatched child
execution subtask when its turn ends — the controller reaps the pod ~`TimeoutSeconds` later. That is
why a preview backed by a **terminal** run still went `NXDOMAIN` ~8–10 min after the turn ended even
with the #542/#551 API-side deferral in place: the #551 deferral cannot stop the controller. (The
coordinator run in the original A/B test survived only because it stayed *active* — its workload never
"finished", so its claim TTL never fired — which is a different code path from the terminal-run
deferral it was meant to prove.)

While a preview is active, the API now **renews the backing claim's cluster TTL** so the controller
keeps the pod alive for exactly as long as a preview may live:

| Renewal trigger | Behavior | Source |
| --- | --- | --- |
| Turn-end release deferral | After deferring the delete, `RenewBackingClaimTtlAsync(runId)` patches the claim TTL. | `KubernetesSandboxExecutor.ReleaseAgentHostPodAsync` |
| Orphan reaper deferral | After deferring the reap, the sweep renews the claim TTL. | `AgentHostReaperService.SweepOrphanedPodsAsync` |
| Keepalive | Each keepalive renews the backing claim TTL for the route's run (read from the durable `preview-run-id` annotation). | `SandboxPreviewService.KeepAliveAsync` |

`RenewBackingClaimTtlAsync` JSON-merge-patches `spec.lifecycle.ttlSecondsAfterFinished` up to
`MaxLifetimeHours × 3600 + 600s` on both the agent-host (`agent-*`) and run-command (`run-*`) claim
names for the run (whichever exists is patched; a missing candidate 404s and is ignored). MergePatch
preserves the sibling `shutdownPolicy`. It is **leak-safe / best-effort**: a no-op when preview is
disabled and never throws. Bounded teardown is preserved because the extended TTL is only a *backstop*
— the API-side preview reaper and `AgentHostReaperService` still delete the claim promptly on
idle/max expiry, which supersedes the TTL. It is only invoked while a preview is demonstrably active
(both deferral sites gate on `HasActivePreviewAsync`; keepalive only runs for a live route).

> **Controller behaviour (verified against `kubernetes-sigs/agent-sandbox` v0.5.3, the pinned
> `SANDBOX_CONTROLLER_VERSION`):** this renewal is effective because the controller recomputes the
> deletion deadline from the **live** claim field on every reconcile — it does **not** snapshot an
> absolute deadline at finish time. `SandboxClaimReconciler.checkExpiration` calls
> `lifecycle.TimeLeft(time.Now(), claim.Spec.Lifecycle.ShutdownTime, claim.Spec.Lifecycle.TTLSecondsAfterFinished, finishedCondition)`,
> and `lifecycle.ExpireAt` returns `finishedAt + ttlSecondsAfterFinished` (the earlier of that and the
> optional spec `ShutdownTime`, which Agentweaver does not set). `finishedAt` is the fixed
> `Finished` condition timestamp; the TTL is read live, so patching `spec.lifecycle.ttlSecondsAfterFinished`
> upward moves the deadline forward, and a spec patch triggers an immediate reconcile
> (`RequeueAfter = timeLeft`). The claim's TTL is the *sole* TTL-driven expiry: the controller never
> copies `spec.lifecycle` onto the underlying `Sandbox` object, so there is no independent
> Sandbox-level TTL that could reap the pod behind the claim. The only requirement is that a renewal
> lands within `TimeoutSeconds` (default 600s) of the workload finishing — satisfied by the turn-end
> release, the ~2-min reaper deferral, and per-request keepalive. **If the pinned controller version
> changes, re-verify this reconcile behaviour**, as the fix depends on it.

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
| `approval_timed_out` | Operator did not approve before the project timeout (30 minutes by default). The latest expired attempt can be retried with a fresh request id while reusing the retained process. |
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
