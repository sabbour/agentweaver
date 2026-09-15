# Decoupled live-preview provisioning — Deep Dive

Decoupled live preview makes publication a platform-owned step rather than a
Build & Test tool side effect. `PreviewStep` resolves a command, starts the app in
the retained AgentHost pod, observes its real port, obtains approval, creates
preview resources and **validates the exact generated HTTPS URL before reporting
ready**. The API never directly probes the sandbox preview port. Command
discovery uses heuristics first and a bounded model fallback if unresolved.

For proxy internals, see [Sandbox browser preview](./sandbox-browser-preview.md). For event and endpoint contracts, see the [reference](../reference/live-preview-provisioning.md). For the review workflow, see the [user guide](../experience/live-preview-provisioning.md).

## End-to-end flow

The invocation point is in the coordinator assembly Build & Test gate. `CoordinatorAssemblyService` records preview applicability, runs Build & Test, then calls `PreviewStep.RunAsync` before applying the authored gate decision (`apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:710`, `:753`). `ShouldRunDeterministicPreviewStep` means the step runs for `APPROVED` and `REQUEST_CHANGES` verdicts, and skips only `DECLINED` verdicts or missing service wiring (`CoordinatorAssemblyService.cs:180`).

There is no feature flag. If the service is wired and the verdict is not declined, the preview step runs. Infrastructure that cannot produce a reachable Gateway preview self-skips by emitting `sandbox.preview_skipped_not_applicable` with reason `preview_infra_unavailable` (`apps/Agentweaver.Api/Coordinator/Preview/PreviewStep.cs:83`).

## Deterministic command and port discovery

`PreviewStep` does not ask the Build & Test model to call preview tools. It drives AgentHost directly:

1. Resolve a command from the detached worktree with `PreviewCommandResolver`. The resolver first tries the worktree root, then probes a bounded ordered list of conventional web-app subdirectories — `client`, `app/client`, `frontend`, `web`, `app`, `src/client` (depth ≤ 2) — and uses the first directory that yields a runnable command. Server, API, and backend directories are not probed; only frontend/UI candidates are considered. This allows client/server and monorepo-style layouts to produce a live preview even when the app is nested in a subdirectory. It checks `package.json`, `.csproj`, Dockerfile, Makefile, Python, Go, and simple Node entry points, forcing all-interface binds where known (`apps/Agentweaver.Api/Sandbox/Preview/PreviewCommandResolver.cs:25`).
2. Call `POST /preview-runner/processes` on the run-bound AgentHost pod origin, not the A2A path (`apps/Agentweaver.Api/Sandbox/Preview/PreviewRunnerHttpClient.cs:35`, `:78`).
3. Call `observe-bound-port`; AgentHost parses stdout/stderr log hints, then diffs the namespace-local kernel socket tables `/proc/net/tcp` and `/proc/net/tcp6` to find a new listening port without depending on the `ss` binary. It verifies HTTP health on the app's actual port, starts `TcpPortForwarder`, then verifies HTTP health again through the forwarder's public port (`apps/Agentweaver.AgentHost/PreviewRunner.cs:246`, `:610`, `:315`).
4. Register the preview with the existing `AgentPreviewGate` and `SandboxPreviewService`. The platform passes the forwarder public port — scanned from `3000-9000` — so the Gateway always targets a pod-IP-reachable listener instead of assuming a fixed port (`PreviewStep.cs:146`, `:166`, `apps/Agentweaver.AgentHost/TcpPortForwarder.cs:75`). `SandboxPreviewService` then creates the Service + HTTPRoute without an API-pod TCP preflight, because NetworkPolicy permits preview-port ingress only from the Gateway (`SandboxPreviewService.cs:134`, `k8s/base/networkpolicy-sandbox.yaml`).

`PreviewStep` is the single terminal outcome emitter for this stage: `sandbox.preview_ready`, `sandbox.preview_failed`, or `sandbox.preview_skipped_not_applicable` (`PreviewStep.cs:229`, `:258`, `:272`). Observe failures now stay legible end-to-end instead of collapsing into an opaque HTTP 500: `no_listening_port_discovered` means the timeout expired without a healthy listening port, `process_exited:exit={code}` means the app exited before readiness, and `observe_error` means the AgentHost observe endpoint hit an unexpected error but returned a structured unhealthy result. The forwarder adds two more distinct reasons: `bound_unreachable` when the public port fails the through-forwarder health check, and `no_public_port_available` when the allowed public range has no free port (`PreviewRunner.cs:262`, `apps/Agentweaver.AgentHost/Program.cs:347`, `PreviewRunner.cs:321`, `:346`).

The command resolver no longer pins `PORT=3000` or appends `--port`. It may add host-bind hints for known frameworks, but the app otherwise uses its framework default or `process.env.PORT`; port selection belongs to the platform observation/forwarder path (`apps/Agentweaver.Api/Sandbox/Preview/PreviewCommandResolver.cs:25`).

Readiness is split by trust boundary: AgentHost checks app and forwarder health
inside the pod; the API then validates the generated Gateway hostname. Failed or
cancelled publication rolls back the Service/HTTPRoute and reconciles retention;
resource creation alone never yields `preview_ready`. The preview ingress rule
admits Gateway pods, not a hostname; port 8088 also has separate control allows.

## Publication lease

Publication can spend the configured Gateway-convergence window before its terminal
`sandbox.preview_ready` batch commits while the run row is still active. Without a lease, an agent
that finished its work inside that window cancelled its own preview: the run's completion token
tore down the publication, and the preview process stopped with reason `preview_not_published`.

Every publication path therefore claims a run-level lease before its slow work
(`IRunStore.TryBeginPreviewPublicationAsync`). The lease is a column on the run row, so it is
visible to all API replicas. While it is held, `PreviewPublicationLeaseRunStore` defers every
transition that can make the run terminal, and `preview_ready` keeps its ordering before the
terminal event. While DNS and Gateway programming converge, the publication loop renews the short
lease and the coordinator treats a current lease as durable evidence that the otherwise-silent child
is still doing live work. A refused renewal means the run is already terminal, and publication
aborts with the same conflict it reported before.

Each lease extension is only three minutes, so a replica that crashes mid-publication stops renewing
and cannot park a run for the full convergence budget. Explicit cancellation completes the run
stream and clears the lease before terminalizing, so it interrupts publication rather than waiting
for convergence. `PreviewStep` also releases the lease around the preview approval wait, so a run is
never held open while an operator decides.

`PreviewPublicationLeaseRunStore` deliberately wraps `RunActiveClaimGuardedRunStore` from the
outside. That store's per-run claim is also taken by the conditional `preview_ready` append, so
waiting while holding it would deadlock against the publication being waited for. Callers that need
the guarded store in the chain use `RunStoreChain.Find<T>` instead of a direct cast
(`apps/Agentweaver.Api/Infrastructure/PreviewPublicationLeaseRunStore.cs`,
`IRunStoreDecorator.cs`).

## Gateway and lifetime alignment

On success, registration creates the same Gateway HTTPRoute / Service chain described in [Sandbox browser preview](./sandbox-browser-preview.md), but it also records `preview_runner_session_id` on both the `preview_ready` payload and the HTTPRoute annotations (`PreviewStep.cs:249`, `apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:670`). That id is distinct from `session_id`, which remains the Gateway capability token. Listing active sessions uses a label-selector pod-existence check under the same isolation rule, not an API-to-sandbox TCP liveness check (`SandboxPreviewService.cs:399`, `:768`).

Keepalive now touches both lifetimes. `SandboxPreviewService.KeepAliveAsync` patches the HTTPRoute idle expiry, then best-effort calls `/preview-runner/processes/{sessionId}/health-check` using the annotated PreviewRunner session so the supervised app process is not reaped while the Gateway route stays alive (`SandboxPreviewService.cs:236`, `:271`).

The sandbox itself has one explicit run-level lifecycle derived from those durable routes:
`Previewable` when no unexpired route exists and `PreviewActive` while at least one does.
Entering or reasserting `PreviewActive` best-effort extends the backing claim TTL
using configured `LifetimeMinutes * 60 + 600` and sets
`cluster-autoscaler.kubernetes.io/safe-to-evict=false`. This is not a per-project
hard-maximum calculation or an unconditional retention guarantee.
Turn-end release, the orphan reaper, preview start, and active-use keepalive all invoke this
same idempotent transition instead of independently remembering those side effects. When the
final route stops or expires, reconciliation returns the run to `Previewable`, restores the
configured normal claim TTL, and sets `safe-to-evict=true`. If several routes exist for one
run, stopping one keeps the run `PreviewActive` until the last route is gone.

## Per-run preview-runner credential

AgentHost `/preview-runner/*` endpoints are protected by a per-run credential:

- API mints a fresh credential on AgentHost launch and sends it only in the `/configure` body; it is not a pod environment variable, file, or config value (`apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:699`).
- AgentHost stores it in `AgentHostRuntimeState.PreviewRunnerCredential`, in memory only (`apps/Agentweaver.AgentHost/AgentHostRuntimeState.cs:40`).
- The server side separately persists the value in `ISecretStore` under the
  deterministic run key. A failed write is logged but still delivers pod memory
  state, degrading cross-replica recovery. Without vault configuration the server
  store is in memory.
- `/preview-runner/*` accepts either the turn bearer token or the preview-runner credential and fails closed when either credential is configured (`apps/Agentweaver.AgentHost/Program.cs:450`).
- The credential key is deterministic per run, but every launch mints a new random value (`apps/Agentweaver.Api/Sandbox/Preview/PreviewRunnerCredential.cs:32`, `:39`).
- Pod release and crash/stall orphan reaping delete the durable credential best-effort (`KubernetesSandboxExecutor.cs:486`, `apps/Agentweaver.Api/Sandbox/AgentHostReaperService.cs:189`).

The AgentHost process launcher also scrubs secret-bearing environment variables before spawning the untrusted preview app, so the app cannot inherit the preview-runner credential (`apps/Agentweaver.AgentHost/PreviewRunner.cs:408`).

## Failure behavior

Preview outcomes return to authored assembly-gate processing:

- Preview failure never changes an `APPROVED` verdict into request-changes.
- Existing `REQUEST_CHANGES` can be promoted to approval only when preview failed
  and feedback is positively classified as preview-only; missing/failed
  classification preserves request-changes.
- The UI shows **Preview unavailable** with the recorded reason. Do not assume
  every return takes the human-review branch.
- `DECLINED` Build & Test skips the preview step because that gate is already terminal.

Denial and non-retryable failures stop the process. Approval expiry keeps a
healthy process **private**, with retry context for a fresh approval; it never
publishes a route. A successful approval still requires an active run, healthy
process and validated HTTPS publication.

The older approval-time outcome guard remains as a safety net. If no terminal preview outcome exists, the coordinator emits `sandbox.preview_failed` with reason `preview_outcome_missing`, but still proceeds with the authored Build & Test decision (`CoordinatorAssemblyService.cs:2455`).

## Source

| Concern | File |
| --- | --- |
| Deterministic preview step | `apps/Agentweaver.Api/Coordinator/Preview/PreviewStep.cs` |
| Build & Test invocation point | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs` |
| Command resolution | `apps/Agentweaver.Api/Sandbox/Preview/PreviewCommandResolver.cs` |
| AgentHost preview-runner HTTP client | `apps/Agentweaver.Api/Sandbox/Preview/PreviewRunnerHttpClient.cs` |
| AgentHost preview-runner endpoints and auth | `apps/Agentweaver.AgentHost/Program.cs` |
| Supervised process runner and `/proc/net/tcp{,6}` port discovery | `apps/Agentweaver.AgentHost/PreviewRunner.cs` |
| Pod-local TCP forwarder | `apps/Agentweaver.AgentHost/TcpPortForwarder.cs` |
| Per-run credential helper | `apps/Agentweaver.Api/Sandbox/Preview/PreviewRunnerCredential.cs` |
| Gateway preview + dual keepalive | `apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs` |
| Credential cleanup on pod release/reap | `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs`, `apps/Agentweaver.Api/Sandbox/AgentHostReaperService.cs` |

## See also

- [Decoupled live-preview provisioning — Reference](../reference/live-preview-provisioning.md)
- [Decoupled live-preview provisioning — User Guide](../experience/live-preview-provisioning.md)
- [Sandbox browser preview](./sandbox-browser-preview.md)
- [Coordinator internals](./coordinator-internals.md)

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
