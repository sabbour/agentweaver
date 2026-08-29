# Changelog

## 0.21.3

### Patch Changes

- 74a9b0a: Restore sandbox pod execution on Kata. The AKS node-image upgrade on 2026-08-27 brought Kata 3.32.0, which flipped `disable_guest_empty_dir` to `true` and turned the executor IPC `emptyDir` into a per-container virtio-fs share, so the AgentHost↔executor Unix socket started failing every connection with `ECONNREFUSED` and AgentHost refused to start. Pinning that volume to `medium: Memory` keeps it on a guest-owned tmpfs, and the sidecar now fails at startup with the remediation instead of crash-looping silently.

## 0.21.2

### Patch Changes

- d9ac7f0: Keep the run artifact browser available while coordinator planning or sandbox worktree setup is still in progress, instead of treating not-yet-created artifacts as request failures.
- 416054a: Keep Outcome plan clarification controls synchronized while a revised plan is prepared, including when live updates arrive before the acknowledgement.
- cdcf581: Require explicit Entra redirect and frontend URLs, and derive both public production URLs from the deployment host instead of falling back to localhost.
- b2e6495: Keep the selected workflow node or edge open in the visual editor while its properties and workflow metadata are edited.
- 1531361: PostgreSQL migration containers now use their injected runtime database configuration instead of an image-embedded local database address.
- 9b8defb: Restore the project-scoped GitHub Copilot account picker so project owners can connect or switch the verified account used by the current GitHub App capability flow.
- 3412759: Require an explicitly connected project Copilot capability before AgentHost launches and show coordinator retry progress or an actionable failure.

## 0.21.1

### Patch Changes

- 1531361: PostgreSQL migration containers now use their injected runtime database configuration instead of an image-embedded local database address.

## 0.21.0

### Minor Changes

- 15ef610: Add immutable, purpose-specific GitHub identity snapshots and fail-closed credential fencing for brokered capabilities.
- 97027a3: Preserve fenced automation activation snapshots so repository automation safely resumes after restarts without replaying stale activation work.
- 0a972d4: Add an authenticated GitHub repository browse handoff that issues short-lived, single-use selection codes for safe project creation.
- daf7152: Add explicit Repo App and project Copilot App browser handoffs to MCP, with redacted capability readiness and subject-bound authorization status polling.
- 7e3d446: Add project-scoped GitHub Copilot App bindings with Owner authorization, safe reconnect state, and a separate disconnect path.
- b7df33f: Add purpose-bound GitHub capability fencing with immutable run snapshots for root, child, retry, and recovery launches. Root snapshots are now selected and captured directly from live authorization, repository grant, and Copilot binding sources at launch; the finite v1 legacy table is migration-only and is never consulted for new runs. Only a project whose persisted origin is explicitly blank may launch with zero snapshots; a GitHub-origin project that currently resolves none of the four purposes is denied rather than silently launched without capability protection. GitHub App history rows (installations, grants, bindings) are no longer used as the blank-project signal, since a GitHub-origin project can legitimately have none of those rows yet.
- 7bbe99c: Browser sign-in now uses Microsoft Entra ID only. Repository selection and Copilot access continue through their separate Repo App and Copilot App authorization handoffs, and Azure deployment setup no longer provisions legacy browser OAuth credentials.
- 08e02f8: Add Repo App installation-token downscoping and a bounded, replay-safe App webhook receiver for repository automation.
- 9431463: Add explicit, Entra-user-bound GitHub Repo App authorization with PKCE, safe callback handling, refresh, and revocation.
- c014960: Retire legacy GitHub OAuth, device-flow, account-link, and MCP OAuth-server paths. Agentweaver now requires Microsoft Entra sign-in and server-side two-App GitHub capabilities.
- 2c55e89: Show a safe Project Gallery confirmation after connecting a project's Copilot App capability.
- 217577d: Add durable, redacted identity, authorization, repository-grant, automation, run-snapshot, and audit records for the two-GitHub-App model.
- 44e0fc8: Add a redacted unattended automation readiness view to Project Settings. The view verifies the
  live Copilot App registration, uses fixed remediation codes, and avoids exposing GitHub
  credentials or provider details. Remove legacy per-project GitHub identity, webhook provisioning,
  and webhook-secret settings controls in favor of the Repo App's App-level webhook.

### Patch Changes

- f0a6139: Safely recover abandoned GitHub App webhook deliveries without allowing concurrent retries to process one delivery twice.
- 7bbe99c: Restore secure GitHub Copilot-backed backlog decomposition and show the project connection action when Copilot must be connected.
- cccfa29: Prevent workflow schedule and event activations from being claimed before their trusted authorization binding is durable, while allowing a safely failed publication to retry.
- b7df33f: Fix an authorization bypass in the GitHub capability snapshot lifecycle: a missing/unparseable
  `Run.ProjectId` could previously let root, child, retry, and resume launches succeed with zero
  GitHub capability snapshots, since only an explicitly blank-origin project may skip capture. Root
  construction and inherited child/retry snapshots now both fail closed (`github_capability_unavailable`)
  whenever the project id is missing, instead of treating an absent project id as an automatic pass.
- 5074e00: Fix demo capture UX: SlidePanel sticky footer, OutcomePlanPanel footer hoist, CoordinatorRunPage wiring, TeamPage Promise.all cold-start race. Capture plan: beat 1.3 heading wait + 60s timeout, beat 2.2 plan panel open via chip click + 120s Confirm timeout, beat 2.5 followNewPage 60s timeout.
- cccfa29: Prevent contributor backlog promotion from publishing a workflow trigger task before its trusted invocation binding completes.
- 12bda78: Keep Foundry sandbox commands observable and safely bounded while Kata virtual machines finish terminating timed-out processes.
- 5915f62: Harden two-GitHub-App persistence with externally safe authorization handles and durable webhook replay claims.
- cccfa29: Recover interrupted schedule and event trigger tasks after upgrades without publishing duplicate runs or exposing provisional tasks to contributors.
- 7bbe99c: Let connected project owners retry marketplace classification with a short-lived, single-use Copilot capability, reclaim unused capability records, and report the currently required API Key Vault secret in cluster diagnostics.
- 41d9322: Gate `gen_ai.tool.call.result` OTel span tag on JSON-shaped output to prevent plain-text file contents and shell output from leaking into App Insights traces. JSON objects and arrays are tagged (and redacted via the existing `RedactJsonStringIfApplicable` pipeline); all other result formats are silently omitted.
- c1d55a2: Remove static project-level GitHub identity override from CallerTokenScopeProvider. All agent runs now use the submitting user's own linked GitHub identity, eliminating agenthost Copilot auth failures caused by stale or missing project-level tokens.
- 9b9ee1c: Keep 'Allow for session' approvals within the current orchestration, and keep eligible 'Always
  allow' approvals for future runs in the same project without applying them to other projects.
- 7bbe99c: Reclaim expired marketplace Copilot capabilities whose broker request was interrupted after claiming them, while keeping active redemptions lease-fenced and fail-closed.
- 7bbe99c: Copilot-backed classification now redeems only the active run's purpose-bound capability, and AKS deployments no longer provision or export retired MCP OAuth signing configuration.
- 7db37f1: Derive Repo App repository permissions and display metadata from GitHub before unattended automation is configured.
- e9fe264: Route AgentRuntime and AgentHost GitHub credentials exclusively through immutable, run-bound capability snapshots, removing ambient Host token-store fallbacks.
- 70b653f: Raise agentweaver-exec container memory limit from 2Gi to 4Gi (request from 1Gi to 2Gi) to prevent kernel OOM kills when an agent runs a preview server alongside its own process. Scheduling density is unchanged — CPU (1000m/pod) remains the binding constraint at ~3 pods/node; only the per-container memory limit (a cgroup ceiling, not a scheduling input) was raised.
- 4cc0cc3: Harden sandbox repository credential delivery by starting validated GitHub CLI commands directly and retrying failed credential revocation during run cleanup.
- 3c59232: Stop terminal coordinator runs from being repeatedly recovered after a service restart.
- 4a1daf9: Ensure approval notifications open the run that is waiting for review.

## 0.20.0

### Minor Changes

- 41d98a1: Align the remaining Microsoft Agent Framework packages onto the 1.19.x line:
  `Microsoft.Agents.AI.Workflows` 1.11.1 → 1.19.0 and `Microsoft.Agents.AI.A2A`
  1.11.1-preview.260625.1 → 1.19.0-preview.260822.1.
  
  This completes the dependency alignment started when `GitHub.Copilot.SDK` was
  bumped to 1.0.11 alongside `Microsoft.Agents.AI.GitHub.Copilot` 1.19.0 (the SDK
  became strong-named in 1.0.4, so the adapter had to be rebuilt against the
  signed assembly). That change moved `Microsoft.Agents.AI.Abstractions` to 1.19.0
  while `Workflows` and `A2A` stayed on 1.11.1, leaving the framework split across
  two release lines; this brings them back onto a single line.
  
  `Microsoft.Agents.AI.Workflows` moves off the prerelease-adjacent 1.11.1 build
  onto stable 1.19.0. The public surface is additive across the range — no types
  or members used by Agentweaver were removed — so no source changes were needed.
  Transitively this also advances `Microsoft.Agents.AI` to 1.19.0,
  `Microsoft.Extensions.AI.Evaluation` to 10.9.0 and
  `Microsoft.Extensions.VectorData.Abstractions` to 10.7.0; none of the APIs
  dropped in those packages are referenced by Agentweaver.
- 3fdaa24: Deployment scripts now default to GHCR (`--image-source ghcr`) instead of ACR-build. This is faster for release deployments since GHCR images are pre-built by CI. ACR-build remains available as an explicit option.

### Patch Changes

- bbb9396: Fix agent-host-maintenance workflow to push to GHCR instead of ACR. ACR login via azure/login OIDC is not available in this workflow context.
- 0cc6ff3: Cache Playwright browsers and bubblewrap in CI to avoid re-downloading on every run. Draft-gate expensive jobs so stacked PRs don't burn full CI on upper frames. Remove the redundant `web-lint` echo job and the `diagrams-in-sync` job.
- 16bc62b: Reorder Kata runtime gate before the full .NET test suite so failures surface in ~90s instead of ~8min. Add max 2 retries on the gate step only. Fix 4 flaky Kata/sandbox timing tests.
- 5bfae29: Scope the CI `changes` path filter to `.github/workflows/ci.yml` instead of `.github/workflows/**`, so editing an unrelated workflow (agent-host-maintenance, docs-drift, publish-images, squad-*) no longer runs the entire .NET, web, Node toolchain, docs and diagram matrix. Applies the same scoping to `areasForPaths` in `scripts/ci/validate.mjs` so local and CI classification stay in sync. Also removes the `Web lint` job, an echo-only stub that could never fail and billed a full minute on every web change; lint still runs in `Web tests`.
- 15ae5b9: Bump `GitHub.Copilot.SDK` from 1.0.2 to 1.0.11, together with
  `Microsoft.Agents.AI.GitHub.Copilot` from 1.11.1-rc1 to 1.19.0.
  
  The two must move together: `GitHub.Copilot.SDK` became strong-named in 1.0.4
  (`PublicKeyToken` went from `null` to `cc7b13ffcd2ddd51`), while
  `Microsoft.Agents.AI.GitHub.Copilot` 1.11.1-rc1 was compiled against the
  unsigned SDK and records an assembly reference of
  `GitHub.Copilot.SDK, Version=1.0.0.0, PublicKeyToken=null`. A weakly-named
  assembly reference can never bind to a strong-named definition, so bumping the
  SDK on its own produced `error CS0012: The type 'CopilotClient' is defined in an
  assembly that is not referenced` in `Agentweaver.AgentRuntime`. Version drift
  alone was not the problem — SDK 1.0.3 still builds fine against the old adapter.
  
  `Microsoft.Agents.AI.GitHub.Copilot` 1.19.0 is built against the signed SDK and
  is the first stable (non-prerelease) line of the adapter, so this also moves the
  package off an `-rc1` prerelease.
- 4acfc9a: Set rebase-strategy: disabled on all Dependabot update configs to stop rebase storms. Dependabot PRs will no longer re-run CI on every push to dev.
- 36f230b: Gate docs-drift workflow on relevant paths (was running 624 times/month on unrelated changes). Scope publish-images matrix to only build images whose sources changed (except on main/release).
- 16bc62b: Fix intermittent Kata test failure caused by setsid race in bwrap process group detection

## 0.19.2

### Patch Changes

- b4a1eaa: Fix `deploy-render.test.mjs` CPU resource expectations to match the AgentHost/exec
  resource rebalance from #886 (agent-host 400m/1000m -> 300m/800m, exec
  600m/1000m -> 700m/1200m). The Node toolchain tests job is path-conditional and
  didn't run for that k8s-only change, so the stale expectations went undetected
  until the v0.19.1 release PR touched a triggering path.
- 96ad613: Fix the `tool.approval_context` SSE event not being handled by the frontend
  timeline reducer, so approval context is now correctly applied to the
  coordinator run model instead of being silently dropped.

## 0.19.1

### Patch Changes

- 501d756: Bump actions/cache from 4 to 6
- 9643764: Bump the fluent-ui group in /apps/web with 2 updates
- d4418dd: Bump Microsoft.AspNetCore.Mvc.Testing and 9 others
- b0d1307: Bump the azure-sdk group with 5 updates
- 367b0ac: Bump the opentelemetry group with 2 updates
- ffcd870: Fix a regression where the "Outcome plan confirmed by ..." banner and lifecycle event still showed a raw Entra object ID (GUID) instead of a display name. PR #854 only covered the interactive human confirmation path (`ConfirmOutcomeSpecAsync`); Direct-mode auto-confirmation and autopilot/unattended outcome-spec confirmation (fresh runs, retried backlog-pickup runs, and run retries) still attributed `confirmedBy` to the raw `SubmittingUser` identity. These paths now carry a resolved human-readable display name (falling back to the raw identity only when no display name is known) so the GUID no longer leaks into the confirmation message.
- 453675a: Fix issue #850 follow-up: custom tools registered by an `IAgentRuntimeToolProvider` (`start_preview`, `start_preview_process`, `observe_bound_port`, `health_check`, `stop_preview_process`) never recorded `tool.call`/`tool.result`/`tool.error` RunEvents or an `execute_tool` OTel span, since they go through the SDK's `ExternalToolRequestedEvent`/`ExternalToolCompletedEvent` pairing rather than the native `ToolExecutionStartEvent`/`ToolExecutionCompleteEvent` lifecycle PR #853 instrumented. The Execute Tool detail panel showed "No arguments/output recorded for this call" for these tools even though they executed successfully. Provider tools are now wrapped so their arguments and output are recorded (redacted) directly around the real invocation, sharing one callId across the span and RunEvents.
- 28d34d3: Fix the agent host maintenance scan so Trivy SARIF uploads still reach GitHub Security while the workflow continues to enforce HIGH and CRITICAL vulnerability failures, and refresh the agent host image toolchain to pick up current Trivy fixes.
- 7c8a89a: fix(sandbox): raise handshake timeout to exceed writable-root wait; address bwrap CPU throttling
  
  Preview kept failing with repeated 30s `observe_bound_port` timeouts even after the v0.19.0
  `observe_bound_port` handshake fix landed. Root cause: `PodExecSandboxClient`'s handshake
  timeout (30s) was shorter than `KataBwrapExecutor`'s own internal wait (120s) for the
  per-run writable-system-root "hold" helper to report `READY`, so the client gave up and
  abandoned the connection before the sidecar could ever report success or failure, which the
  sidecar then observed as a broken pipe.
  
  - Raised `PodExecSandboxClient.HandshakeTimeout` from 30s to 150s (safely above the
    writable-root wait) and documented the dependency between the two timeouts.
  - Rebalanced the `agentweaver-agent-host` sandbox pod's CPU split so the CPU-heavy
    `agentweaver-exec` container (which runs the bwrap writable-root setup) gets more
    request/limit headroom (600m/1000m -> 700m/1200m), taken from the thin relay-only
    `agentweaver-agent-host` container (400m/1000m -> 300m/800m). Combined pod-level totals
    (1000m request / 2000m limit) are unchanged, so katapool scheduling density is unaffected.
- 13d25a1: Fix the Trace Summary "Latest" stat to compute MAX(started_at) across all candidates instead
  of trusting list order, reverse "Recent coordinator runs" back to newest-first (matching the
  `/runs` API's deterministic newest-first order), and add a "View trace" button on the
  orchestration run detail page that jumps directly to that run's trace.

## 0.19.0

### Minor Changes

- acb84b5: feat: built-in workflows support edit, schedule, and event triggers via copy-on-write

  Editing, scheduling, or adding event triggers to a built-in workflow now automatically
  creates a local project copy (with the same name) instead of failing silently. Built-in
  entries are hidden from the list when a local copy exists, eliminating duplicate rows.

### Patch Changes

- 71869ee: The Human review approval card now includes a Change option that expands an inline feedback textarea, letting reviewers request specific agent revisions without declining the run outright.
- a8d1924: Fix blueprint demo beat hold times (2.4, 4.2, 4.3) to meet output budget minimums
- bc9c903: Replace hardcoded staging project/orchestration IDs in blueprint capture plan with env-var placeholders

  Beats 2.3–2.8 had hardcoded project ID (71cdf9d6) and orchestration ID (38cdd5a3) in their startUrls.
  These break after clean-staging removes the old project.

  Fix: replace with {{AGENTWEAVER_DEMO_PROJECT_URL}}/board and {{AGENTWEAVER_DEMO_ORCHESTRATION_URL}}.
  Add AGENTWEAVER_DEMO_ORCHESTRATION_URL prerequisite to all affected beats.

- 1c3c608: feat(auth): add adopt-session-token endpoint for GitHubLegacy mode

  Adds POST /api/auth/github/adopt-session-token so that callers already
  authenticated with a GitHub bearer token can promote that token into the
  IGitHubTokenStore without requiring a separate device-flow sign-in.
  Only available in GitHubLegacy auth mode.

- c27c4fd: Fail Blueprint demo triage capture early when its current issue, PR, or assistant-route prerequisites are unavailable.
- 3556ffe: fix(demo): beat capture fixes, narration scripts, and clean-staging improvements

  - Beat 4.1: add 30s pause after Enter so SPA navigates to /assistant?runId before beat 4.2 starts
  - Beats 4.2+4.3: remove startUrl for cross-beat URL continuity; use transcript text waitFor
  - clean-staging: accept continuation beats (no startUrl) and unresolved env-var placeholders
  - clean-staging: fix mojibake em dash in Blueprint fixture projectName
  - Add narration scripts for AKS, Blueprint, and sizzle reel scenarios
  - Add sizzle reel direction manifest (93.8s draft, 13/15 beats assembled)

- 7627e63: Bump @types/node from 26.1.1 to 26.2.0
- f06ddae: Bump actions/github-script from 7 to 9
- 07e669d: Bump @testing-library/user-event from 14.6.1 to 14.6.4 in /apps/web
- 237fd73: Bump coverlet.collector from 6.0.2 to 10.0.1
- 2583d3c: Bump postcss from 8.5.17 to 8.5.26 in /apps/web
- f7790f5: Bump the npm_and_yarn group across 3 directories with 5 updates
- 795a1ba: fix: persist terminal WorkPlan status when coordinator run already stopped

  CoordinatorDispatchService detected a terminal coordinator run but returned early without calling SetWorkPlanStatusAsync, leaving WorkPlans permanently stuck in dispatching. This caused the reconciler to re-arm every ~10 s forever (infinite loop). Fix calls SetWorkPlanStatusAsync before the early return and adds a regression test.

  Fixes #808.

- 2ec55b6: fix(ux): auto-switch from outcome-plan to approval session on real-time approval event

  When a coordinator.child_approval_required event arrives via SSE while the
  outcome-plan panel is visible, automatically switch to the child agent's session
  panel so the session-approval-gate is immediately visible without a manual tree
  click. Previously users (and recordings) had to click a tree item to expose the
  approval gate, which was non-obvious and caused beat 2.5 recording failures.

- 78e8890: The Outcome plan confirmation banner and assembly review now show the user's display name or GitHub login instead of a raw identity GUID.
- 9054667: fix(deploy): auto-load params.<username>.json for deploy-from-local

  Prevents AUTH_MODE from resetting to GitHubLegacy on every deploy-from-local run.

- 8e482b3: fix(auth): return inconclusive instead of false on Copilot probe 401/403
- 8dfbc88: fix(deploy): guard against empty Entra config in buildRuntimeConfigLiterals to prevent 503 errors
- 89e964a: Install GitHub CLI in the API runtime image and split the `github_cli` health diagnostic into separate installation and authentication checks so the health endpoint reports each concern independently.
- 23e0732: fix: null-guard legacy history.json deserialization

  Guards against NullReferenceException when creating projects from repositories
  that contain legacy history.json files with null entries, preventing a crash
  on project creation for affected repositories.

- a415cac: Fix `observe_bound_port`/`health_check` silently reporting a preview process as started
  when the sidecar's sandboxed spawn actually failed or was still resolving. The relay now
  emits a start handshake (ready/error) from the sidecar's `Started`/`Error` frame, and
  `StartSupervisedProcessAsync` blocks on it (30s timeout) before returning, so a failed
  spawn now throws immediately instead of yielding a real-but-useless local PID that would
  forever report `no_listening_port_discovered` with empty logs.
- fc47339: Fix preview runner pod OOM kills by setting explicit resource requests/limits and Node.js heap cap

  Sets 1Gi memory request and 2Gi memory limit on AgentHost and agentweaver-exec containers in the
  SandboxTemplate. Adds NODE_OPTIONS=--max-old-space-size=1024 to prevent V8 heap growth from
  triggering cgroup OOM kills during Next.js/Vite preview server startup. Adds deploy-render test
  assertions to lock these values into the rendered deployment contract.

  Fixes #845.

- 0575d91: fix(preview): keep preview startup and forwarding responsive while DNS warms

  Preview startup now tolerates Kata writable-root setup latency and cold forwarder
  health checks. The interface also reports DNS warm-up while a new preview hostname
  becomes reachable, instead of implying the URL is immediately ready.

- 295f0c1: fix(sandbox): increase shell watchdog grace and default timeout for Kata VM environments

  In Kata hardware-isolation, SIGTERM relay through the Kata agent can take tens of seconds
  on a cold or loaded node. The previous 60-second watchdog grace was being consumed before
  the process fully exited, causing the watchdog to fatally abort agent turns with
  "Shell execution exceeded its hard deadline of ~2 minutes" even when the executor had
  already sent the cancellation signal.

  Changes:

  - `WatchdogTimeoutGrace`: 60 s → 5 min — gives Kata processes enough time to die after
    the executor's `CancelAfter` fires, preventing false-positive `shell_execution_timeout` failures.
  - `DefaultTimeoutMs`: 30 s → 5 min for non-Build/Test agent contexts — prevents premature
    cancellation of legitimate long-running commands (npm install, git clone, cargo build, etc.)
    when the model doesn't supply an explicit `timeout_ms`.

- 4cc099d: Surface GitHub token expiry prominently and improve renewal resilience.

  - Show a warning banner and Re-link CTA in the UI when a GitHub OAuth token has expired or been revoked, so users know why coordinator runs fail
  - Fix entitlement probe endpoint (switched from `copilot_internal/v2/token` to `GET /models`) so Copilot entitlement status displays correctly for all linked accounts
  - Distinguish transient (network, 5xx) vs permanent (expired token, bad credentials) refresh failures — transient failures no longer sign the user out
  - Add proactive background refresh service that renews expiring tokens up to 2 hours before expiry, preventing mid-run token failures
  - Fix AgentHost sandbox executor to survive concurrent claim deletion during coordinator runs

- cff3d6b: Show model latency percentile checkpoints in readable seconds and minutes.
- b8ab020: feat(auth): add adopt-session-token endpoint for GitHubLegacy mode

  Adds `POST /api/auth/github/adopt-session-token` so that callers already
  authenticated with a GitHub bearer token in GitHubLegacy mode can promote
  that token into the `IGitHubTokenStore` without requiring a separate
  device-flow sign-in. This unblocks GitHub-origin project operations
  (clone, webhook provisioning) for GitHubLegacy deployments.

- 1d83edf: fix(projects): create GitHub projects from a shallow clone so large repositories open promptly while skill imports retain history for pinned tags.
- 1eec73b: Add an isolated, unauthenticated recording mode for capturing the safe Entra sign-in handoff without restoring saved browser storage.
- 38ce83f: fix(skills): shallow-clone GitHub skill repositories (depth=1) for faster import; guard against NullReferenceException when repo.Head is detached.
- ef2de84: Transaction traces now show tool call arguments and output in the Execute Tool detail panel, display AIC cost at every span level (turn, agent invocation, and run total), and redact sensitive values in tool payloads before persistence and API delivery.
- aaf4900: Reject workflow drafts that start with verdict-producing review or build/test nodes, and request a corrected generated workflow before it is returned.

## 0.18.2

### Patch Changes

- f0c53da: Keep the Create-from-GitHub dialog open when toggling the linked-account filter, and default the repository list to the currently selected GitHub account (including the gallery account switcher) instead of silently clearing the filter after repos load.
- 7c6e24c: fix(web): shell "Allow once" approval now works from coordinator view

  Root causes and fixes:

  - **isShell detection**: `coordinator.child_approval_required` events with a `commandHash` field are shell approvals bubbled from child runs. Detection was extended so `InThreadApprovalGate` correctly identifies them as shell approvals and calls `approveShell`/`denyShell` instead of `approveTool`/`denyTool`.
  - **Wrong run ID**: Shell approval API calls now target the child run (`childRunId` from event payload) instead of the coordinator run, preventing 404s from the backend.
  - **Resolution tracking**: `buildCoordinatorTurns` now uses `commandHash` as a fallback key when `requestId` is absent, so resolved shell approvals display correctly.
  - **Disabled state**: `ApprovalGate` now accepts a `disabled` prop that is passed through to both "Allow once" and "Deny" buttons; the gate disables while a request is in flight.
  - **UX**: Added a "Review" button in the "Needs input" MessageBar that scrolls to the pending approval gate, reducing the chance of it being missed.

## 0.18.1

### Patch Changes

- 3cae2ee: Restore Kata AgentHost readiness by moving model-controlled command execution into a hardened executor sidecar container, replacing a bubblewrap PID/procfs namespace that the kernel cannot create inside any Kubernetes container. Sandboxed process groups are now resolved and terminated without `/proc/<pid>/task/<pid>/children`, which the Kata guest kernel does not provide, so preview processes start reliably and no command can leak daemonised processes into the executor container.
- 6568b87: Give sandboxed runs a real developer toolchain and publish what the sandbox can actually do. A run
  that needs system packages now gets a per-run writable system root — `/usr` and `/var` overlaid onto
  a size-bounded tmpfs inside its own user namespace, `/etc` copied — so `apt-get install` works
  without adding a single pod privilege, and everything installed is discarded with the run and is
  invisible to other runs, to AgentHost, and to the node. The executor also answers a new
  `capabilities` request describing every supported workload (npm, NuGet, apt, preview port binding)
  and, for the ones it cannot perform, why and what would change that: container image builds require
  a builder sidecar, and `winget` is reported as unsupported on Linux with the Windows
  executor backend named as the remediation rather than being silently omitted.

  Image builds are now actually available where they are wanted, and the builder is scoped to the run.
  `k8s/optional/sandbox-buildkit-sidecar.yaml` adds an opt-in BuildKit sidecar to the sandbox pod,
  reached over a pod-local unix socket through the `awx-docker` shim — so a run can `docker build`
  without the sandbox container gaining a single capability (measured `CapEff: 0000000000000000`).
  Because a sandbox pod is a Kata VM, the builder's cache, history and content store are scoped to
  that one run: a second run sees an empty `debug histories` and an empty cache. That closes the
  cross-run channel a shared broker would have opened, where any run holding a client certificate
  could read another run's build logs and blobs through BuildKit's debug APIs.

  The trade-off is stated rather than hidden: the sidecar must be rootful with `CAP_SYS_ADMIN`,
  `CAP_NET_ADMIN` and `CAP_SYS_PTRACE`, because rootless BuildKit cannot work under Kata. Those
  capabilities are confined
  to the run's guest kernel — measured in-guest `NET_ADMIN` cannot defeat NetworkPolicy, IMDS stays
  blocked, build steps stay runc-confined, and the daemon refuses the `security.insecure` entitlement
  — but a `buildkitd` vulnerability would be a root-in-guest compromise, so the sidecar is off by
  default and requires a namespace whose PodSecurity level admits those three capabilities.

  Build steps run in their own empty network namespace (loopback only), so a Dockerfile line that
  needs the network — `RUN apt-get install`, `RUN npm install` — does **not** work; install
  dependencies in the sandbox shell, which keeps normal pod networking, and `COPY` the result in.
  Base images still pull, because the daemon fetches them itself. The capability contract declares
  this limit, so an agent discovers it before starting rather than halfway through a build.

  `awx-docker` also validates `--output` by parsing it as CSV field by field, so `type=image,push=true`,
  `type=registry`, a quoted `"push=true"` field and a second `type=` in the same value are all refused.
  That refusal is ergonomics, not a boundary — `buildctl` is on `PATH` and `BUILDKIT_HOST` is
  exported, so a caller can reach the daemon directly. What actually prevents publishing is that the
  builder holds no registry credential, no ServiceAccount token and no workload identity.

  Availability of the build capability is measured, not assumed: the executor connects to the socket
  and speaks the HTTP/2 preface, reporting `image_build` as supported only when a real builder answers.
  A crashed daemon that left its socket file behind, or a sidecar still starting up, is reported as
  `RequiresExternalService` rather than advertised as a builder whose every build fails at connect time.

## 0.18.0

### Minor Changes

- adbd83f: Make preview approval timeouts configurable per project with a 30-minute default, keep
  pending approvals persistently visible, and allow owners to retry expired approvals
  without restarting the run or executing the preview process twice.
- d47df1c: Add a UI-harness pointer drag command with stable workflow-editor node and handle targets, safe element-relative coordinates, configurable movement steps, and failure evidence so canvas repositioning and drag-to-connect regressions can be reproduced.

### Patch Changes

- 615266f: Allow Entra project members to open and operate project runs according to their Agentweaver project role, even when the run records a linked GitHub login as its submitting identity.
- a476cc9: Fix switching a project between linked GitHub accounts so the selected identity remains active for request and background operations instead of reverting to the default account.
- 0d6aa2c: Confine Kata AgentHost shell and preview child processes to run-scoped mount namespaces, preventing absolute, obfuscated, traversal, and symlink paths from reaching sibling projects on the shared workspace volume.
- a0bfd98: Allow one workflow to keep a recurring schedule and a GitHub event trigger at the same time, with
  independent editing, API round-trips, and runtime dispatch for both. The event editor now also makes
  GitHub Issues actions explicit, so label-driven workflows can select `labeled` instead of silently
  remaining scoped to issue creation.
- bbb6dac: Keep live-preview sandboxes available coherently by applying claim TTL renewal, reaper deferral, autoscaler eviction protection, active-use keepalive, and final cleanup through one idempotent run lifecycle.
- 85f76da: Rotate persisted per-package dependency-cache generations during explicit invalidation.
- 7db4f17: Make workflow schedules discoverable and editable inside the visual workflow editor without overwriting unsaved workflow changes.
- cac2e20: Add safe npm download-cache reuse and timed layer/full validation profiles for concurrent worktrees, with physical local dependency trees and worktree-local .NET outputs.
- 47f6405: Keep each UI harness scenario on one persistent browser page across separate CLI actions, with crash recovery, isolated concurrent sessions, and explicit cleanup on finish.
- 857910e: Align workflow node authoring across the API and visual editor: add Open pull request and Publish actions to the picker, preserve them through YAML edits, and enforce the shared node-type contract in frontend and server tests.

## 0.17.0

### Minor Changes

- c832c0e: Add `--image-source ghcr` to `azure:deploy-from-release`, so an already-published release can be redeployed by importing its existing GHCR images instead of rebuilding them from source. This skips a full container rebuild and never touches cluster, ACR, Postgres, identity, or monitoring infrastructure.

### Patch Changes

- 5962680: Recover assembly AgentHost credential rotation and reaped-pod races with one bounded, reason-specific retry, active linked-account token propagation, and structured diagnostics that stop on persistent authorization or lifecycle failures.
- d6d5c7c: Let browser Assistant sessions use MCP tools with the current signed-in identity in Microsoft Entra deployments, while keeping the linked GitHub/Copilot credential separate and refreshing the platform token on each message.
- 75a2acb: Make the project settings “Create webhook automatically” action provision or refresh the connected repository's signed GitHub webhook instead of returning a local placeholder error.
- 0bf2e14: Keep Azure provisioning resilient when the AKS region does not support Log Analytics or Application Insights by selecting a nearby supported monitoring region, with an explicit override when needed.
- 75d5b9c: Keep the signed-in account name and avatar readable by placing the sidebar version badge on its own footer row, while retaining the full build version in the badge tooltip.
- 41975fa: Recover soft-deleted Key Vault preview-runner credential keys before retrying a launch. Recovery is bounded, safe under concurrent creators, preserves purge protection, and rotates to a fresh credential without logging secret values.
- 04c8cfb: Allow `release:publish` to run from a normal built checkout containing standard dependency, build, test, and harness outputs, while continuing to reject untracked source and unexpected ignored files.
- 6493f7b: Allow supervised preview processes to use canonical absolute working directories when they resolve to the run worktree or one of its subdirectories, while continuing to reject sandbox escapes.
- 75314ba: Prevent agent-authored memory and decisions from becoming trusted cross-team prompt instructions until an authorized coordinator or project owner approves them.
- 9ac9a97: Normalize aborted A2A turns to a single structured `agent_turn_internal_error` across general, Responsible AI, and Build & Test agents, while retaining bounded redacted diagnostics instead of exposing raw unsupported-event reasons.
- 082b216: Make UI harness captures wait for the authenticated application shell or a caller-declared semantic target, preserve failed commands through finish, and report persistent sign-in loading states as expired authentication instead of producing false-green evidence.

## 0.16.2

### Patch Changes

- 3a202ae: Fix `azure:provision-infra` silently ignoring `AUTH_MODE`/`ENTRA_CLIENT_ID`/`ENTRA_TENANT_ID`: these variables were readable via `variables.mjs` but were never wired into `provision-infra.mjs`'s own config schema or its `resolveVariables()` env override, so `--params-file`, no `--auth-mode`/`--entra-client-id`/`--entra-tenant-id` flags existed either, and only a raw exported environment variable actually took effect. A redeploy without that env var set would silently reset a live environment's sign-in mode back to `GitHubLegacy`. Adds `--auth-mode`, `--entra-client-id`, and `--entra-tenant-id` CLI flags plus matching params-file fields, validates `AUTH_MODE` against the exact `GitHubLegacy`/`Entra` values `AuthModeResolver.Parse()` accepts, and requires `ENTRA_CLIENT_ID`/`ENTRA_TENANT_ID` when `AUTH_MODE=Entra`.
- 9c2eecb: Fix multi-GitHub-account sign-in in Entra mode: linking an additional GitHub account now forces GitHub's account picker (`prompt=select_account`) instead of silently re-authorizing the account already linked, and the active (default) linked account's token is now what the rest of the platform resolves. Legacy per-user token scopes are transparently rewritten onto the caller's active linked identity — restoring Copilot entitlement, session starts, and generation for Entra users — and the AgentHost pod is handed the active identity's Key Vault secret.

## 0.16.1

### Patch Changes

- 073d9f9: fix(auth): repair linked-GitHub-account route/verb mismatches and the dead "link account" flow

  The Entra multi-account GitHub linking feature never actually worked end-to-end:

  - `client.ts`'s `listLinkedGitHubAccounts`, `setDefaultLinkedGitHubAccount`,
    `unlinkLinkedGitHubAccount`, and `listAccessibleGitHubRepos` called routes that never
    existed server-side (`/auth/github/linked-accounts*`, `/github/repos/accessible`), and one
    used the wrong HTTP verb (`POST` instead of `PUT`). Every one of these operations 404'd.
  - "Add account" / "Link another GitHub account" built a URL to `/auth/github/authorize` with
    an `intent=link` query param the server never reads — that endpoint always runs a plain
    sign-in exchange, never the dedicated link flow. Added `apiClient.beginLinkGitHubAccount()`
    calling the correct, pre-existing `POST /auth/github-accounts/link` endpoint and rewired
    both call sites to use it.
  - The accessible-repos response used inconsistent JSON casing versus what the frontend
    expects and was missing the source account's avatar/default-flag fields; fixed end to end.
  - `LinkedGitHubAccountResponse` was missing `name`/`type` fields the frontend type requires;
    populated with `type: "user"` (GitHub identity links are always personal accounts, never
    orgs) and `name: null` (not fetched at link time).

- 1295cfb: Fixed the PostgreSQL region/SKU pre-flight check rejecting every region during `provision-infra`. `az postgres flexible-server list-skus` returns a JSON array of capability sets, but the check read the capability fields off the array itself, so it always concluded that no server editions were supported and aborted before creating the Flexible Server — even in regions where the SKU was perfectly available. The failure also reported a fabricated reason (`Azure reported no supported server editions for this subscription/region.`) that hid Azure's real explanation, turning an actionable message such as "Subscriptions are restricted from provisioning in this region ... open a support request with Issue type of 'Service and subscription limits'" into a dead end. Provisioning now succeeds in supported regions, and genuinely restricted regions surface Azure's own wording so you can pick another `--postgres-location` or request an exception.
- 07c3598: fix: add missing Postgres migrations for notifications/GitHub-linking, polish sidebar GitHub sign-in UX

  - Added the Postgres counterparts of two migrations that only ever existed in the
    SQLite dev-migrations project (`apps/Agentweaver.Api/Migrations`), so the live
    production Postgres provider (which resolves migrations from the separate
    `Agentweaver.Api.Migrations.Postgres` assembly) never created their tables:
    - `dismissed_notifications` — caused `GET /api/notifications` to 500.
    - `github_account_link_states` / `project_github_identity_overrides` — caused
      "Link another GitHub account" to 500 with
      `42P01: relation "github_account_link_states" does not exist`.
  - `GitHubSignIn` (sidebar popover): wrapped the trigger in a tooltip so it's
    discoverable as the GitHub account switcher, added a persistent "Entra ID"
    badge + popover banner when signed in via Microsoft Entra ID, fixed the
    collapsed-rail (64px) layout so the trigger and status/version badge no
    longer squish together, and truncated long account name/login text in the
    popover's account lists.
  - `SettingsPage`: added a confirmation toast when landing on
    `?auth=github_linked&login=...` (the redirect from the GitHub account-link
    flow), then strips those query params so a refresh doesn't re-fire it.

- 6314349: fix(auth): make `/api/server/info` genuinely anonymous and return the configured auth mode

  `GET /api/server/info` is what the web app calls before sign-in to decide whether to show
  the Entra or GitHub sign-in button, but it was unreachable and incomplete:

  - Despite `.AllowAnonymous()`, the custom bearer-token (`GitHubTokenAuthMiddleware`) and
    GitHub-org authorization middlewares keep their own hardcoded anonymous-path allowlists
    and never consulted endpoint metadata, so every unauthenticated call got a 401.
  - The response body omitted `auth_mode` / `auth_mode_label` / `auth_mode_recommended`
    entirely, so even a successful call could not report the deployment's auth mode.

  The frontend defaults to `github-legacy` whenever the field is missing or the call fails,
  so Entra deployments (`AUTH_MODE=Entra`) silently showed "Sign in with GitHub". The
  endpoint is now exempt in all auth middlewares (and marked public in the OpenAPI document)
  and returns the auth mode resolved through the existing `AuthModeResolver`.

- 7797a39: Fixed public-access Postgres (`--postgres-access-mode public`) deployments where the generated Kubernetes egress policies still allowlisted the private delegated-subnet CIDR, so API and worker pods could never reach the Flexible Server (`Npgsql.NpgsqlException: Failed to connect ... TimeoutException`) even with the Azure-side firewall and public network access configured correctly. Public mode now emits FQDN-based `CiliumNetworkPolicy` objects (`allow-api-postgres-egress-fqdn` / `allow-worker-postgres-egress-fqdn`) that allow port 5432 to `<PG_SERVER_NAME>.postgres.database.azure.com` via Cilium `toFQDNs`, which stays correct when Azure changes the server's public IP. Private mode keeps the existing ipBlock `NetworkPolicy` objects unchanged.

## 0.16.0

### Minor Changes

- bfeb12d: Added `azure:provision-infra` support for provisioning PostgreSQL Flexible Server in a different Azure region from the AKS cluster. The installer now exposes `--postgres-location` / `PG_LOCATION` and `--postgres-access-mode` / `PG_ACCESS_MODE`, fails closed when a cross-region server is requested without switching to public access, creates the Azure-services-only firewall rule needed for public-access Flexible Server deployments, and performs a fail-fast SKU/region capability pre-flight so unsupported subscription+region combinations error immediately instead of hanging in Azure provisioning.
- e7e2a4a: Add `--node-vm-size` / `NODE_VM_SIZE` to `azure:provision-infra` so new AKS clusters can override the node-pool VM SKU when a subscription or region disallows the default. The default new-cluster SKU is now `Standard_D4s_v6` (up from `Standard_D4s_v3`); existing clusters are unaffected because the installer skips cluster and node-pool creation when those resources already exist.
- e3fdb53: Add a `custom` Azure installer image-source mode so deployments can import four operator-specified fully-qualified container image references instead of rebuilding in ACR or using only the repo-derived GHCR owner flow.

### Patch Changes

- b7bcfb3: Allow overriding the Postgres Flexible Server HA mode via `--postgres-ha-mode`/`PG_HA_MODE` between `ZoneRedundant` and `Disabled` to support regions and environments where zone-redundant HA is unavailable, including early-access/canary regions such as `eastus2euap`. Also fix Postgres server-name validation to reject names shorter than 3 characters.
- d0d6b99: Allow overriding the Postgres Flexible Server name via `--postgres-server-name`/`PG_SERVER_NAME` to route around the rare case where the default `agentweaver-pg` name is already reserved elsewhere in Azure's global namespace.

## 0.15.0

### Minor Changes

- 3ec039f: Publish Agentweaver container images to GitHub's container/artifact registry. A new `Publish images` workflow builds `agentweaver-api`, `agentweaver-frontend`, `agentweaver-mcp`, and `agentweaver-agent-host` and pushes them to `ghcr.io`, with tags that map to each stage of the `dev → release/vX.Y.Z → main` topology: `dev` pushes, release-candidate branches, `main`, published releases (`X.Y.Z`/`vX.Y.Z`/`latest`), and manual runs of an arbitrary commit. Every build also publishes an immutable `sha-<short>` tag. The build matrix is derived from the existing `image-spec.mjs` source of truth rather than restated in YAML.
- 10087fe: `npm run azure:provision-infra` can now reuse the four container images already published to GHCR instead of always rebuilding them into ACR. Operators opt in with `--image-source ghcr --ghcr-ref <ref>`, where `<ref>` must be an immutable published release tag (`vX.Y.Z`) or `sha-<hex>` tag; the importer preflights all four images together, captures the destination ACR digests for provenance verification, redacts optional GHCR credentials, and refuses conflicting tag overwrites unless `--force` is passed.

  Provisioning an existing AKS cluster now also reconciles legacy App Routing state only when needed. If the cluster predates the Gateway API / `nginx=None` policy, `10-create-cluster` detects the mismatch, enables the Istio-backed Gateway API path, and disables the managed nginx controller/default-domain drift with targeted idempotent updates; already-correct clusters remain untouched.

### Patch Changes

- 520b6ea: Make Azure installer image work easier to diagnose with elapsed lifecycle progress, remove unused recording and worktree content from ACR build contexts, and accept the documented GitHub organization allowlist rules including the global `*` wildcard.
- 0e52eb0: Allow Entra browser sign-in to redeem authorization codes with PKCE only when no client secret is configured, while keeping secret-based redemption available for tenants that allow it.
- b4c3fc1: Wire `Auth:Mode` and `Auth:Entra:*` config through the AKS deploy pipeline (`AUTH_MODE`/`ENTRA_CLIENT_ID`/`ENTRA_TENANT_ID` deploy-time environment variables) so Entra sign-in mode can actually be enabled on deployed environments.

## 0.14.0

### Minor Changes

- 21e1f4a: Add structured GitHub event-trigger predicates for workflows, including label, branch, review-state, ref, category, and fixed-regex comment matching, plus REST trigger-config endpoints for UI-driven editing.
- e576c7b: Add a DOM-based demo-recording capture pipeline with semantic cue capture, take analysis, stable topology and trace markup, and a reusable creative-direction skill for future demo videos.
- 1bbcfa6: Add the missing interactive Entra ID browser sign-in flow: `GET /auth/entra/authorize` and `GET /auth/entra/callback` endpoints implementing the Microsoft identity platform v2.0 authorization-code-with-PKCE flow, with CSRF-protected state, server-side PKCE code_verifier storage, and one-time session-exchange codes (no tokens ever placed in redirect URLs). Also exempts `/api/auth/session/exchange` from platform role authorization so anonymous session bootstrap works in Entra mode.
- 60699a3: Add Entra ID as a permanent, deployment-level dual-mode authentication and authorization option alongside GitHub-org login: platform App Roles (Tier-1), per-project Owner/Contributor/Viewer RBAC (Tier-2) with an atomic last-owner safeguard, fail-closed backfill for legacy pre-RBAC projects, linked GitHub account management with repo enumeration and Copilot entitlement display, and epoch-based auth-mode invalidation for safe rolling restarts.
- dffbe9e: Add web UI editing for workflow event triggers with curated GitHub events, typed condition rows, and OR-within-a-field matching. The project webhook settings page now also shows an automatic webhook creation entry point with a clear coming-soon fallback alongside the existing manual setup steps.
- f32abde: Generalize GitHub authorization from bare-org-only checks to mixed allow rules in
  `Auth:GitHub:AllowedOrg`, supporting `org`, `org/*`, and `org/team-slug` entries
  with OR semantics across the configured list.

  Also harden the legacy `Auth:GitHub:AllowedTeam` compatibility shim: when it overlaps
  with a bare-org rule for the same org, keep the effective rules org-wide, emit a
  prominent warning that the old AND-style restriction is not preserved, and show the
  resolved allow-list so operators can migrate to explicit `org/team-slug` rules.

- dffbe9e: Teach workflow generation to emit validated schedule and GitHub event triggers, including structured event predicates and correction-pass recovery for malformed trigger drafts.
- 6f74bcd: Add a direction-aware demo compositor that renders approved per-beat edits from cue-anchored segments, preserves narration timing, and lets final scenario assembly consume the new rendered beat outputs.

### Patch Changes

- 154c532: Observability now keeps retired Squad members' role titles in the Agent token breakdown so historical usage rows show each agent's real project role instead of a generic AI Assistant label.
- 91a2466: Stop the in-thread "Tool Approval Required" card from overlapping the agent activity feed on run/orchestration detail views. The run timeline could collapse to zero height under flex pressure inside its scroll container, letting its accordion content overflow visibly and the following approval card render on top of it; the timeline root now reserves its full content height (`flex-shrink: 0`) so sibling content always flows below it.
- 351dcc8: Fix child/subtask chat timelines collapsing under a single "Step 1" when the run stream only includes raw `report_intent` tool calls by treating those calls as step boundaries in the frontend timeline builder.
- 6b1cb0e: Fix Cluster diagnostics by removing the false `github_installation_token` alarm, measuring `agent_pod_quota` from the real pod and SandboxClaim object quotas instead of the removed CPU cap, and enabling the Cluster page's 30-second auto-refresh by default.
- 6c91732: Fix demo-recording approval-banner auto-approve watcher to catch all three approval-card UI surfaces (including ShellApprovalCard) and handle concurrent approval cards independently.
- 91e06d9: Contain the sidebar footer's alpha version badge so long dev build strings ellipsize inside the pill instead of spilling past it, while preserving enough room to keep short GitHub usernames visible.
- 1e3f210: Focus the assistant composer after choosing a suggested prompt so Enter can send it immediately.
- 391afc6: Stop the worker heartbeat reaper from deleting live preview-backed `SandboxClaim`s (#578; supersedes the refuted TTL-renewal hypotheses in #560/#564/#570/#571/#574). Root cause (confirmed via kube-audit attribution to `system:serviceaccount:agentweaver:agentweaver-worker`): `AgentHostReaperService` runs from **worker** pods, but the worker deployment carried no `Sandbox__Preview__*` config, so its `SandboxPreviewService` had a null cluster client and `HasActivePreviewAsync()` permanently false-negated — every orphan sweep deleted the backing claim of a completed/`AssembleReady` child that still had a live preview, killing the preview URL.

  Fix (both angles, complementary):

  - **Config parity** — `k8s/base/worker-deployment.yaml` now mirrors the API deployment's `Sandbox__Preview__Enabled=true` + gateway env, so the worker's DI actually builds an in-cluster client and the reaper can read durable cluster preview state.
  - **Fail-safe cluster reads** — `SandboxPreviewService.HasActivePreviewAsync`, `RenewBackingClaimTtlAsync`, and `SetBackingPodSafeToEvictAsync` now gate on the presence of a cluster client (`_client is null`) rather than the local `Enabled` provisioning flag, so a live route in cluster state stays authoritative for any process that can see it even if that process is not the one that provisions preview routes.
  - **RBAC** — the `agentweaver-worker-sandbox` Role now grants read on `httproutes` and `patch` on `sandboxclaims`/`pods` so the worker reaper's preview probe (list HTTPRoutes) and its defer-branch TTL renewal (#560) and safe-to-evict pin (#574) succeed instead of silently 403-ing back into the delete path. The worker still never creates or deletes preview routes — that stays with the API.

  Live verification against staging is pending (the shared staging environment was torn down by the subscription's routine 3-day GC and is being re-provisioned); validated locally via the .NET unit suite.

- a100e95: Materialize the selected workflow YAML into each run worktree so agents can inspect custom workflows inside their sandbox without committing those files into repository history.
- 4454699: Keep coordinator stories inline when task promotion would otherwise delegate the entire confirmed plan to backlog, so normal coordinator runs still dispatch live subtasks instead of completing with an empty delegated work plan.
- e9d701a: Raise the default approval timeout for agent-initiated `start_preview` requests from 5 minutes to 15 minutes, and let operators override it with `Sandbox:Preview:ApprovalTimeoutMinutes` or `SANDBOX_PREVIEW_APPROVAL_TIMEOUT_MINUTES`.
- a325ea9: Fix terminal merge conflicts on Squad bookkeeping files across concurrent coordinator runs (#621). A new project-level `SquadStateConsolidationService` is now the sole writer of the canonical decision ledger — it idempotently drains `.squad/decisions/inbox/*.md` into `.squad/decisions.md` on the project's default branch, decoupled from any run's branch-merge lifecycle. Per-run branch merges now resolve the canonical Squad ledgers (`.squad/decisions.md`, `.squad/agents/*/history.md`, `.squad/identity/now.md`) path-level "ours", so a run's racing copy can no longer produce a human-resolution-required conflict or clobber consolidated content, while genuine conflicts on every other path are still detected.
- 2eb6ea8: Fix Cluster page diagnostics: the Sandbox claims "Warm pool used" column now reads the live v1beta1 `warmPoolRef.name` field, the permanently empty Sandbox objects section is removed, and Pending capacity now explains that zero means runs are getting a sandbox immediately.
- b1d2119: Exclude Copilot-created worktree directories from Azure Container Registry build contexts so local deploys do not upload huge accidental tarballs from sibling repo checkouts.
- fbf2ae1: Fix MCP `project_create` so GitHub-backed project creation can pass `source_repository` instead of failing and falling back to a blank-origin project.
- 0b93925: Fix denied native Copilot shell attempts showing up in the run activity feed as raw shell tools like `bash` instead of the sandboxed `run_command` label, and make repeated native-shell denials within the same run more explicit so the model stops retrying the disabled tool.
- 7b9aba5: Allow the UI harness login flow to follow both GitHub OAuth and Microsoft Entra sign-in redirects while keeping automated browser actions same-origin only.
- c7e8c26: Fix the ObservabilityPages test fixture after the Project type gained three nullable model fields, restoring `apps/web` builds on `dev`.
- 9e84663: Improve generated skill drafts so they include concrete trigger guidance, step-by-step instructions, and richer examples instead of bland generic boilerplate.
- 1aed605: Surface active run previews near the top-level run actions so reviewers can open them without digging into the Build & Test step.
- 9d3b8de: Let dismissed human-review notifications reappear when the same run later requests review again.
- 7591d9b: Keep the sidebar footer readable by shortening long dev build version badges and preventing them from pushing the signed-in user out of view.
- 7f1d7ed: Allow GitHub org and repo avatars to load in the Create project from GitHub picker by permitting `https://avatars.githubusercontent.com` in the web app content security policy.
- e9868bf: Reduce wasted tool-calling round-trips where the model tried the SDK's native shell tool first (always denied) before falling back to the sandboxed `run_command` tool, by adding explicit guidance to the shared agent base prompt to use `run_command` directly.
- 145b1ae: Fix run timeline steps all rendering as "Step 1" instead of incrementing (Step 1, Step 2, Step 3...) by fixing an off-by-one in the continuation-narration collapse logic that let every size cap be overshot by one merge, allowing a whole run of small continuation-narrated steps ("Now let's...", "Next, I'll...") to fold into a single step.
- d2a7554: Keep the assistant view's New session action working even when it is clicked from an already-open conversation.
- 136d7a9: Fix observability trace and agent usage panels so named project agents show their assigned team role titles instead of a generic “AI Assistant” subtitle.
- 4775ce9: Fix run timeline tool categorization so preview/review-style tool names no longer fall into the generic file-view card.

## 0.13.0

### Minor Changes

- f32abde: Generalize GitHub authorization from bare-org-only checks to mixed allow rules in
  `Auth:GitHub:AllowedOrg`, supporting `org`, `org/*`, and `org/team-slug` entries
  with OR semantics across the configured list.

  Also harden the legacy `Auth:GitHub:AllowedTeam` compatibility shim: when it overlaps
  with a bare-org rule for the same org, keep the effective rules org-wide, emit a
  prominent warning that the old AND-style restriction is not preserved, and show the
  resolved allow-list so operators can migrate to explicit `org/team-slug` rules.

### Patch Changes

- 154c532: Observability now keeps retired Squad members' role titles in the Agent token breakdown so historical usage rows show each agent's real project role instead of a generic AI Assistant label.
- 91a2466: Stop the in-thread "Tool Approval Required" card from overlapping the agent activity feed on run/orchestration detail views. The run timeline could collapse to zero height under flex pressure inside its scroll container, letting its accordion content overflow visibly and the following approval card render on top of it; the timeline root now reserves its full content height (`flex-shrink: 0`) so sibling content always flows below it.
- 351dcc8: Fix child/subtask chat timelines collapsing under a single "Step 1" when the run stream only includes raw `report_intent` tool calls by treating those calls as step boundaries in the frontend timeline builder.
- 6b1cb0e: Fix Cluster diagnostics by removing the false `github_installation_token` alarm, measuring `agent_pod_quota` from the real pod and SandboxClaim object quotas instead of the removed CPU cap, and enabling the Cluster page's 30-second auto-refresh by default.
- 6c91732: Fix demo-recording approval-banner auto-approve watcher to catch all three approval-card UI surfaces (including ShellApprovalCard) and handle concurrent approval cards independently.
- 91e06d9: Contain the sidebar footer's alpha version badge so long dev build strings ellipsize inside the pill instead of spilling past it, while preserving enough room to keep short GitHub usernames visible.
- 1e3f210: Focus the assistant composer after choosing a suggested prompt so Enter can send it immediately.
- 391afc6: Stop the worker heartbeat reaper from deleting live preview-backed `SandboxClaim`s (#578; supersedes the refuted TTL-renewal hypotheses in #560/#564/#570/#571/#574). Root cause (confirmed via kube-audit attribution to `system:serviceaccount:agentweaver:agentweaver-worker`): `AgentHostReaperService` runs from **worker** pods, but the worker deployment carried no `Sandbox__Preview__*` config, so its `SandboxPreviewService` had a null cluster client and `HasActivePreviewAsync()` permanently false-negated — every orphan sweep deleted the backing claim of a completed/`AssembleReady` child that still had a live preview, killing the preview URL.

  Fix (both angles, complementary):

  - **Config parity** — `k8s/base/worker-deployment.yaml` now mirrors the API deployment's `Sandbox__Preview__Enabled=true` + gateway env, so the worker's DI actually builds an in-cluster client and the reaper can read durable cluster preview state.
  - **Fail-safe cluster reads** — `SandboxPreviewService.HasActivePreviewAsync`, `RenewBackingClaimTtlAsync`, and `SetBackingPodSafeToEvictAsync` now gate on the presence of a cluster client (`_client is null`) rather than the local `Enabled` provisioning flag, so a live route in cluster state stays authoritative for any process that can see it even if that process is not the one that provisions preview routes.
  - **RBAC** — the `agentweaver-worker-sandbox` Role now grants read on `httproutes` and `patch` on `sandboxclaims`/`pods` so the worker reaper's preview probe (list HTTPRoutes) and its defer-branch TTL renewal (#560) and safe-to-evict pin (#574) succeed instead of silently 403-ing back into the delete path. The worker still never creates or deletes preview routes — that stays with the API.

  Live verification against staging is pending (the shared staging environment was torn down by the subscription's routine 3-day GC and is being re-provisioned); validated locally via the .NET unit suite.

- a100e95: Materialize the selected workflow YAML into each run worktree so agents can inspect custom workflows inside their sandbox without committing those files into repository history.
- 4454699: Keep coordinator stories inline when task promotion would otherwise delegate the entire confirmed plan to backlog, so normal coordinator runs still dispatch live subtasks instead of completing with an empty delegated work plan.
- e9d701a: Raise the default approval timeout for agent-initiated `start_preview` requests from 5 minutes to 15 minutes, and let operators override it with `Sandbox:Preview:ApprovalTimeoutMinutes` or `SANDBOX_PREVIEW_APPROVAL_TIMEOUT_MINUTES`.
- a325ea9: Fix terminal merge conflicts on Squad bookkeeping files across concurrent coordinator runs (#621). A new project-level `SquadStateConsolidationService` is now the sole writer of the canonical decision ledger — it idempotently drains `.squad/decisions/inbox/*.md` into `.squad/decisions.md` on the project's default branch, decoupled from any run's branch-merge lifecycle. Per-run branch merges now resolve the canonical Squad ledgers (`.squad/decisions.md`, `.squad/agents/*/history.md`, `.squad/identity/now.md`) path-level "ours", so a run's racing copy can no longer produce a human-resolution-required conflict or clobber consolidated content, while genuine conflicts on every other path are still detected.
- 2eb6ea8: Fix Cluster page diagnostics: the Sandbox claims "Warm pool used" column now reads the live v1beta1 `warmPoolRef.name` field, the permanently empty Sandbox objects section is removed, and Pending capacity now explains that zero means runs are getting a sandbox immediately.
- b1d2119: Exclude Copilot-created worktree directories from Azure Container Registry build contexts so local deploys do not upload huge accidental tarballs from sibling repo checkouts.
- fbf2ae1: Fix MCP `project_create` so GitHub-backed project creation can pass `source_repository` instead of failing and falling back to a blank-origin project.
- 0b93925: Fix denied native Copilot shell attempts showing up in the run activity feed as raw shell tools like `bash` instead of the sandboxed `run_command` label, and make repeated native-shell denials within the same run more explicit so the model stops retrying the disabled tool.
- c7e8c26: Fix the ObservabilityPages test fixture after the Project type gained three nullable model fields, restoring `apps/web` builds on `dev`.
- 9e84663: Improve generated skill drafts so they include concrete trigger guidance, step-by-step instructions, and richer examples instead of bland generic boilerplate.
- 1aed605: Surface active run previews near the top-level run actions so reviewers can open them without digging into the Build & Test step.
- 9d3b8de: Let dismissed human-review notifications reappear when the same run later requests review again.
- 7591d9b: Keep the sidebar footer readable by shortening long dev build version badges and preventing them from pushing the signed-in user out of view.
- 7f1d7ed: Allow GitHub org and repo avatars to load in the Create project from GitHub picker by permitting `https://avatars.githubusercontent.com` in the web app content security policy.
- e9868bf: Reduce wasted tool-calling round-trips where the model tried the SDK's native shell tool first (always denied) before falling back to the sandboxed `run_command` tool, by adding explicit guidance to the shared agent base prompt to use `run_command` directly.
- 145b1ae: Fix run timeline steps all rendering as "Step 1" instead of incrementing (Step 1, Step 2, Step 3...) by fixing an off-by-one in the continuation-narration collapse logic that let every size cap be overshot by one merge, allowing a whole run of small continuation-narrated steps ("Now let's...", "Next, I'll...") to fold into a single step.
- d2a7554: Keep the assistant view's New session action working even when it is clicked from an already-open conversation.
- 136d7a9: Fix observability trace and agent usage panels so named project agents show their assigned team role titles instead of a generic “AI Assistant” subtitle.
- 4775ce9: Fix run timeline tool categorization so preview/review-style tool names no longer fall into the generic file-view card.

## 0.12.2

### Patch Changes

- 607992b: Stop AKS cluster-autoscaler from evicting live AgentHost **preview** pods during kata-node scale-down (#574; chains #560/#564/#570/#571). Root cause: the agent-sandbox v0.5.3 controller defaults sandbox pods to `cluster-autoscaler.kubernetes.io/safe-to-evict: "true"` and the kata pool runs the cluster-autoscaler (`--min-count 1 --max-count 5`) with no agent-host PodDisruptionBudget, so the autoscaler drained kata nodes and killed serving preview pods in ~6 minutes — entirely independent of the `SandboxClaim` `ttlSecondsAfterFinished` reaper that #560/#564/#570/#571 all targeted (which is why those RBAC-correct, source-correct TTL fixes never stopped the deaths). `shutdownPolicy: Delete` then removed the workload-less claim, so the claim appeared to "vanish" instantly and faster than any 600s TTL.

  Fix (Option B — dynamic, symmetric with the existing `RenewBackingClaimTtlAsync` calls): `SandboxPreviewService.SetBackingPodSafeToEvictAsync` merge-patches the backing pod's `safe-to-evict` annotation to `"false"` whenever a live preview is asserted (`StartPreviewAsync`, `KeepAliveAsync`, `KubernetesSandboxExecutor.ReleaseAgentHostPodAsync` defer branch, `AgentHostReaperService` defer branch) and back to `"true"` on teardown (`StopPreviewAsync`, also reached by expiry via `ReapAsync`). Best-effort/no-throw; no-ops when preview is disabled or no bound pod resolves; ignores 404. No RBAC change needed — the `agentweaver-api-sandbox` Role already grants `pods: patch`.

  Empirically validated on live staging before merge: a pinned pod (`safe-to-evict:"false"`) kept its underutilized kata node out of scale-down (`NoCandidates`) for ~14 min; flipping the same pod to `"true"` made the autoscaler mark that node `candidates: 1` within ~3.5 min — proving the annotation is the deciding factor. An agent-host PodDisruptionBudget (Option C) was intentionally skipped (noted as a follow-up): agent-host pods are ephemeral pod-per-run, a PDB risks blocking legitimate node drains/warm-pool recreation, and it would not stop a `safe-to-evict:true` scale-down anyway.

## 0.12.1

### Patch Changes

- ab5133b: Grant the `agentweaver-api-sandbox` Role `patch`/`update` on `sandboxclaims.extensions.agents.x-k8s.io` so the backing-claim TTL renewal added for #560 (#564) actually works (#570). Verified via a live A/B on staging: `SandboxPreviewService.RenewBackingClaimTtlAsync`/`KeepAliveAsync` JSON-merge-patch `spec.lifecycle.ttlSecondsAfterFinished` on the run's `SandboxClaim`, but the Role in `k8s/base/rbac-api.yaml` only ever granted `get, list, create, delete` on that resource — every renewal attempt returned HTTP 403 Forbidden, so #564 shipped a silent no-op and the sandbox controller kept reaping preview pods on their original TTL. Audited the other `SandboxPreviewService`/`KubernetesSandboxExecutor`/`AgentHostReaperService` patch paths (pods, HTTPRoutes) — both already carry the verbs they need, so `sandboxclaims` was the only gap. Added `KubernetesRemoteApiManifestTests.ApiSandboxRole_GrantsPatchAndUpdateOnSandboxClaims` to pin the verb list and catch this class of RBAC/code-permission mismatch in CI going forward.

## 0.12.0

### Minor Changes

- 6bd3967: Add suggested prompt buttons to the Assistant Run page's empty state so a first-time user or someone doing a quick smoke test doesn't have to think of a prompt themselves. Five ready-to-use example requests (list projects and run status, start a smoke-test run, check the latest run's status, list available MCP tools/skills, and create-a-test-project-then-run) are shown as chips before a conversation exists; clicking one populates the composer for review/edit rather than auto-submitting, matching the existing edit-then-send flow.

### Patch Changes

- 5ff4fc8: Fix the Visual Workflow Editor so nodes can be connected by dragging (#555). The editor reuses the shared read-only `WorkflowNode` component, whose connection handles were hard-coded to `{ opacity: 0, pointerEvents: 'none' }` — correct for read-only graph renders (CoordinatorRunPage, WorkflowGraphPanel, LandingWorkflowDemo, all `nodesConnectable={false}`), but it meant React Flow never received the `pointerdown` needed to _start_ a connection drag on the editable canvas, so the wired-up `onConnect` handler could never fire and edges could not be authored. Handle interactivity is now gated on a new `connectable` flag in `WorkflowNodeData`: read-only surfaces keep the invisible, non-interactive edge anchors, while `VisualWorkflowEditor` renders visible, `pointer-events: all`, `isConnectable` handles so drag-to-connect works.
- a066e0a: Fix the Visual Workflow Editor "Add node" palette (#558). "Build & Test" appeared twice with identical labels — once as the preconfigured `SPECIAL_GATES` preset and once as the raw `build_test` node-type from `AUTHORABLE_WORKFLOW_NODE_TYPES` — and the flat, mostly icon-less list mixed reviewer/gate roles, agent steps, and control-flow primitives with no separators or descriptions, making it hard to scan. The palette is now organized into three groups using the existing Fluent `MenuGroup`/`MenuGroupHeader`/`MenuDivider` pattern (as in `WorkflowsPage`): **Reviewers & gates** (RAI, Rubberduck, Human Review, Build & Test presets + Peer review, Check/gate primitives), **Agent steps** (Prompt), and **Flow control** (Fan-out, Fan-in, Coordinator-composed, Serial, Terminal). Every row now has an icon and a one-line description, and the duplicate is removed — the raw `build_test` primitive is dropped from the palette (fully represented by the "Build & Test" preset), so it appears exactly once.
- ab06270: Keep live previews reachable for terminal (coordinator-dispatched) runs by renewing the backing SandboxClaim's cluster-side TTL while a preview is active (#560). PR #551 (#542) only deferred the **API-side** claim delete/orphan-reap, but each `SandboxClaim` is created with `spec.lifecycle.ttlSecondsAfterFinished` (default 600s, `shutdownPolicy: Delete`) which the sandbox controller enforces independently: once a child subtask's pod workload finishes, the controller reaps the pod ~10 min later regardless of the API deferral, so the preview URL still went `NXDOMAIN` ~8 min after the turn ended. `ISandboxPreviewService` now exposes `RenewBackingClaimTtlAsync(runId)`, which JSON-merge-patches the backing claim TTL up to cover the preview's hard-max lifetime (`MaxLifetimeHours × 3600 + margin`) on both the `agent-*` and `run-*` claim names. It is called from the turn-end release deferral (`KubernetesSandboxExecutor.ReleaseAgentHostPodAsync`), the orphan-reaper deferral (`AgentHostReaperService.SweepOrphanedPodsAsync`), and each keepalive (`SandboxPreviewService.KeepAliveAsync`). Best-effort and leak-safe: a no-op when preview is disabled, ignores 404s, never throws, and remains bounded because the API-side reaper still deletes the claim promptly on idle/max expiry (which supersedes the TTL).
- 78fac61: Fix GitHub event-triggered workflows never firing for projects created via the "import from GitHub" flow. The webhook receiver matched `project.Origin.SourceRepository` against the delivery payload's `repository.full_name` ("owner/repo"), but `CreateFromGitHubAsync` stores the full HTTPS clone URL, so real deliveries returned 204 and fired nothing. Both sides are now normalized to canonical `owner/repo` before comparison, fixing both the import (URL) and connect (owner/repo) creation paths. Verified end-to-end against staging with a real repo and live webhook delivery.

## 0.11.6

### Patch Changes

- 9a06ea2: Persist the team decision/memory ledger to the repository and report export failures honestly (#539). The DB-backed ledger (`.squad/decisions.md` and `.agentweaver/context/*`) is now mirrored into each run's git worktree at commit time, so it rides the same commit/push flow as the run's other changes and actually lands in the user's repo (previously it was only written to the base checkout, which is never committed). `POST /memory/export` (and the shared exporter) now return an actionable error instead of unconditionally reporting `{exported: true}` when the on-disk write fails.
- 5c80d8a: Fix the Workflow visual editor silently placing newly-added nodes outside the visible canvas viewport (#540). `<ReactFlow fitView>` only auto-fits on initial mount, so a node added via "Add node" (or a special gate) that the DAG layout positions outside the current viewport rendered behind the canvas pane's `overflow: hidden`, making the click look like it did nothing even though the node was added correctly. `VisualWorkflowEditor` now imperatively re-fits the viewport (via `useReactFlow().fitView`) whenever the node count grows, while leaving pan/zoom untouched for unrelated edits like renaming a node.
- 31402e8: Add an LLM-powered fallback for live-preview command discovery (#541). The deterministic `PreviewCommandResolver` heuristics still run first as a fast/free/instant pass, but when they can't tell how to run a project (for example a plain static HTML/CSS site with no build tooling — the case that silently failed during a demo), `PreviewStep` now gives a model a bounded, token-capped view of the worktree and asks it to propose the exact `0.0.0.0`-bound, OS-assigned-port command and working directory. The model-chosen command still runs through the same sandboxed AgentHost start → port-observation → approval-gate pipeline — no new trust boundary — and its working directory is validated to stay inside the worktree. If the model also declines or can't produce a viable command, preview still ends in the terminal `preview_command_unresolved` outcome (this tier is additive, never a forced success). The resolving tier is observable via the existing `command_source` telemetry field (`heuristic:<source>` vs `llm`).
- e74a8dd: Keep a run's sandbox pod alive while a live preview is active so `start_preview` URLs stay reachable long enough to be viewed (#542). Previously the sandbox pod backing a preview was torn down the instant the originating subtask's turn ended — `KubernetesSandboxExecutor.ReleaseAgentHostPodAsync` deleted the `SandboxClaim` unconditionally, and `AgentHostReaperService` treated the just-completed run's claim as an orphan immediately — so a `preview_url` handed to a human-review gate `404`ed within minutes (verified: the exact URL returned istio-envoy 404 ~8–9 min later, and re-invoking the endpoint returned `409 no bound sandbox pod`). Both teardown paths now consult a new `ISandboxPreviewService.HasActivePreviewAsync(runId)` and defer while a preview's idle (`Sandbox:Preview:IdleTimeoutMinutes`, default 30) and hard-max (`Sandbox:Preview:MaxLifetimeHours`, default 8) expiries are both still in the future. The check is leak-safe (disabled preview, no un-expired route, or any lookup failure all fall back to normal teardown), so eventual teardown stays bounded: with no keepalive the preview idle-expires, the preview reaper deletes the route, and the next orphan sweep — now seeing no active preview — reaps the pod.
- 6a4a97b: Fix `execute_tool` telemetry spans reporting an inflated, identical duration for every tool in a parallel batch when one sibling blocks. When a `web_fetch` call waits out its 5-minute HITL approval deadline, the GitHub Copilot SDK's sequential dispatch stalls delivery of the other tools' lifecycle events; because the span was bounded by when our consumer loop observed those events, near-instant tools (e.g. `list_decisions`, `get_memory`, `list_inbox`) were reported at the same ~5-minute duration. Spans are now bounded by the SDK event's own `Timestamp`, so each tool's recorded duration reflects its real execution window rather than consumer-loop back-pressure.
- 7a87fe6: Fix the "Problems" panel (and other coordinator-run cards) on the project
  board rendering the entire raw task prompt as the card title. Long,
  multi-paragraph prompts previously rendered in full with no truncation,
  making individual cards enormous and breaking the board's compact card
  layout. The task text is now clamped to 3 lines with an ellipsis, and the
  full text remains available via a native `title` tooltip on hover/focus.

## 0.11.5

### Patch Changes

- 9ffe0e0: Fix `coordinator.assembly_merge_failed` ("the working tree cannot be safely reconciled
  with the merge result because uncommitted content diverges") firing after an already
  fully-approved coordinator run's human review, when a subtask's own sandboxed coding
  agent appends new entries directly to already-tracked Squad bookkeeping files (for
  example `.squad/decisions.md`, `.squad/agents/*/history.md`) without committing them.
  `WorktreeManager` now auto-commits dirty content on already-tracked, modified paths in
  the checked-out originating-branch working tree immediately before computing merge
  safety, so this uncommitted-but-legitimate content becomes an ordinary extra parent
  commit instead of blocking the merge. This also fixes the reported symptom where the
  `conflictingFiles` list grew across repeated retries: every merge attempt now sweeps
  whatever is currently dirty, so retries can no longer compound into an ever-larger,
  unresolvable conflict set. A genuine textual collision between the auto-committed
  content and the child branch's own change to the same file still correctly fails the
  merge for human resolution — auto-committing never hides a real conflict.
- 6f299ae: Log durable, redacted telemetry for `start_preview` tool-call failures in
  AgentHost. AgentHost sandbox pods are ephemeral and recycled shortly after a
  run completes, so a non-success HTTP response (e.g. a 403) or an unhandled
  exception from the `start_preview` tool's callback previously left no
  durable evidence to investigate after the fact. `PreviewPublishTool` now
  logs a structured event (tool name, run id, port, HTTP status code,
  redacted+truncated response body or exception message) via the existing
  `SandboxToolContext.Logger`, which already flows through to Application
  Insights wherever `APPLICATIONINSIGHTS_CONNECTION_STRING` is configured.
  Anything token/secret-shaped is redacted via
  `Agentweaver.SandboxExec.SandboxOutputRedactor` before being logged.
- 47d7496: Fix `start_preview` (agent-initiated preview registration) returning HTTP 403
  for the run's own agent in every real deployment: `IsOwnerOrServiceCaller`
  only recognized the internal service caller via a configured `Auth:User`
  setting that no deployment ever sets (only `Auth:ApiKey` is injected). The
  shared service key actually resolves to the hardcoded
  `agentweaver-internal` identity, which is now checked directly, matching the
  authorization already used for memory/decision/casting callbacks.

## 0.11.4

### Patch Changes

- c8ed32c: Fix an intermittent `GitHubCopilotUnauthorizedException` at the build-test assembly
  gate: `KubernetesSandboxExecutor` now resolves the GitHub access token shipped in the
  AgentHost `/configure` request through the refresh-aware `IGitHubAccessTokenProvider`
  (falling back to the raw token store only when the provider is unavailable), instead
  of reading a potentially stale/expired token directly. This closes a race where the
  build-test gate's freshly-launched AgentHost pod could receive a token that expired
  during earlier subtask stages of the same run.

  Also improve `assembly_merge_failed` diagnostics: a working-tree-divergence merge
  `Blocked` outcome (uncommitted local content that cannot be safely reconciled with the
  merge result) now reports the affected relative file path(s) via `conflictingFiles`,
  instead of always showing an empty list alongside the "cannot be safely reconciled"
  message. This is a diagnostics-only change — the merge-safety refusal decision itself
  is unchanged.

- c49fc95: Fix sandbox preview creation for Python apps ("app.py"/"main.py" entrypoints):
  the resolved preview command invoked a bare `python` binary, which does not
  exist on the agent sandbox image (only `python3` is installed). Every preview
  attempt for a Python-only app failed with `process_exited: exitCode=127
... python: not found`. The resolver now emits `python3 ...` for both
  entrypoints.
- 3234ada: Remove the unsafe hardcoded `KEYVAULT_NAME` default (`agentweaver-kv`) from the
  Azure deploy tooling (`scripts/azure/variables.mjs`). That generic default was
  never a real Key Vault in any provisioned subscription, and deploy commands
  silently fell back to it (or to a manually-typed-but-wrong vault name) whenever
  an operator forgot to set `KEYVAULT_NAME` explicitly -- corrupting the rendered
  `agentweaver-runtime-config` ConfigMap and the `agentweaver-secrets`/
  `agentweaver-user-tokens` SecretProviderClasses' `keyvaultName`/Key Vault URI
  fields and silently breaking GitHub OAuth sign-in.

  `KEYVAULT_NAME` is now REQUIRED with no generic default: `resolveVariables()`
  fails fast with an actionable error if it is unset. `steps/30-deploy.mjs`
  additionally verifies (`az keyvault show`) that the named vault actually
  exists BEFORE rendering or applying any manifest, catching typos that happen
  to name a real-but-wrong vault too (not just a made-up name). This is internal
  deploy-tooling reliability hardening; there is no user-facing application
  behavior change.

- 143aea4: Bump the `agent-sandbox` controller pin (kubernetes-sigs/agent-sandbox) from `v0.5.0` to
  `v0.5.3` in `scripts/azure/steps/10-create-cluster.mjs`. v0.5.2 renamed the core install
  asset from `manifest.yaml` to `sandbox.yaml`, so the script's default manifest URL is
  updated to match; the `SANDBOX_CONTROLLER_MANIFEST_URL` override remains available for
  anyone pinning an older controller version. No user-facing behavior change is expected.

## 0.11.3

### Patch Changes

- bc50c1c: Clarify the board's Ready column so dependency-blocked tasks no longer appear as pickup-ready queued work.
- 1cd2078: Surface unhandled exceptions from the AgentHost `/configure` endpoint instead of
  letting them escape as an opaque, empty-body HTTP 500. The endpoint now logs the
  real exception (still attributable to the specific run/pod before it recycles) and
  returns a structured `agenthost_configure_unexpected_exception` JSON body, making the
  recurring `agenthost_configure_failed` failure diagnosable.
- 6d7d9aa: Fix a cross-pod race that could cause assembly to fail with
  `agenthost_configure_failed` right as a run entered human review. The
  work plan's status was flipped to `InReview` before the durable
  `AssemblyReviews` row backing that gate was persisted, leaving a short
  window where a peer pod's reconciler sweep could observe `InReview` with
  no pending review row, conclude the run was orphaned, and re-arm
  assembly — colliding with the still-live owner on the same AgentHost
  claim mid-`/configure`. The review row is now persisted before the
  status flip, closing the window.
- 27ea216: Fix `start_preview` (and other `IAgentRuntimeToolProvider`-built tools) failing with
  an opaque "Tool execution failed" on warm-pool AgentHost pods. The per-turn API
  base URL/key resolved by `CopilotAIAgent.BuildSessionConfigTools` was never
  forwarded to tool providers, so `PreviewRunnerToolProvider` always fell back to the
  unreachable `http://localhost:5000` default (#335 P1 follow-up).

## 0.11.2

### Patch Changes

- 0c2debd: Fix AgentHost client mTLS: `agentweaver-api` and `agentweaver-worker` now present a
  client certificate and validate AgentHost's server certificate against the pinned CA
  when calling the AgentHost A2A endpoint over HTTPS, and their
  `Sandbox__AgentHost__RequireMtls` setting is kept in sync with AgentHost's own Kestrel
  mTLS listener via a dedicated overlay patch, so a redeploy can no longer silently
  revert the client side to plain HTTP while the server side still requires mTLS.
- 866ec1f: Fix a Postgres foreign-key violation that could silently drop a whole decomposed
  work plan. `BacklogPromotionService` now saves task rows in their own
  `SaveChangesAsync` call before adding and saving their dependency rows, so EF
  Core/Npgsql's batched insert ordering can no longer race the dependency rows
  ahead of the tasks they reference (`FK_backlog_task_dependencies_backlog_tasks_depends_on_task_id`).
- 6843b4a: Fail fast when the UI harness reuses an empty Playwright storage state so staging dry-runs report AUTH_EXPIRED instead of proceeding with a broken session.
- dd9f01f: Fix tool-approval "AgentHost approval endpoint is unreachable" (503) during the
  coordinator draft/decompose/orchestrate phases. `ResolveApprovalOwningRunIdAsync` did
  not know about the synthetic `-coordinator-draft`/`-coordinator-decompose`/
  `-coordinator-orchestrate` run-id suffixes used to key approval-gate context for those
  LLM turns, so an operator's "Allow once" click on a grounding tool call (e.g.
  `web_fetch`) raised during outcome-spec drafting always failed with `no_context`.
- ca08eb0: Fix AgentHost mTLS startup so loading the mounted CA certificate no longer
  attempts to parse a private key from the public-only `ca.crt` PEM.
- dd9f01f: Fix a 500 error when approving or denying the very first tool call of a run
  (e.g. a `web_fetch` during coordinator spec drafting). The approval-gate
  owning-run resolution could return a synthetic coordinator-phase key (e.g.
  `{runId}-coordinator-draft`) that is not a real run id, which then crashed in
  `RunId.Parse`. That synthetic key is now recognized and treated as the posted
  coordinator run for ownership/status checks, while still using it to look up
  the approval-gate request.
- fcdfcc4: Fix UI harness auth replay for staging: Agentweaver's session token lives in
  `sessionStorage`, which Playwright's `context.storageState()` does not capture
  (only cookies and `localStorage` are persisted). Headless dry-runs replaying a
  saved storage state always landed back on the GitHub sign-in page even with a
  freshly captured, non-empty state. The `login` command now also captures a
  companion `sessionStorage` seed file, and headless sessions re-hydrate it via
  `context.addInitScript` before any page script runs.
- 06007b7: Fix a bug where GitHub org-membership checks could intermittently return a
  hard "authorize SSO" 403 (`OrgAuthResult.OrgAccessNotGranted`) even for
  callers whose org membership is genuinely public. When the primary
  authenticated membership check hit SAML-enforcement (403), the fallback
  unauthenticated `public_members` check's own rate-limit responses were
  silently treated as a confirmed "not a public member" instead of a
  retryable inconclusive result. The fallback's rate-limited/inconclusive
  result is now correctly surfaced as `Inconclusive` so the caller retries
  instead of hard-denying access.
- 0b544d1: Restore the production AgentHost A2A listener so hardened deployments bind the
  expected mTLS endpoint on port 8088 and reject clients whose certificates are
  not signed by the mounted Agentweaver CA.

## 0.11.1

### Patch Changes

- 6843b4a: Fail fast when the UI harness reuses an empty Playwright storage state so staging dry-runs report AUTH_EXPIRED instead of proceeding with a broken session.
- ca08eb0: Fix AgentHost mTLS startup so loading the mounted CA certificate no longer
  attempts to parse a private key from the public-only `ca.crt` PEM.
- fcdfcc4: Fix UI harness auth replay for staging: Agentweaver's session token lives in
  `sessionStorage`, which Playwright's `context.storageState()` does not capture
  (only cookies and `localStorage` are persisted). Headless dry-runs replaying a
  saved storage state always landed back on the GitHub sign-in page even with a
  freshly captured, non-empty state. The `login` command now also captures a
  companion `sessionStorage` seed file, and headless sessions re-hydrate it via
  `context.addInitScript` before any page script runs.
- 0b544d1: Restore the production AgentHost A2A listener so hardened deployments bind the
  expected mTLS endpoint on port 8088 and reject clients whose certificates are
  not signed by the mounted Agentweaver CA.
- f241d0c: Fix notification requests failing on PostgreSQL deployments after a
  notification is dismissed.

## 0.11.0

### Minor Changes

- 9e55ed8: Add LLM-assisted skill marketplace catalog parsing (step-1b): a project can now add a curated marketplace source by GitHub repo URL. A new catalog indexer auto-detects skills from the repo tree (deterministic `SKILL.md` heuristic with a bounded, fail-closed Copilot classifier fallback), caches the parsed index per repo revision, and paginates browse from it (anonymous-first, page-lazy descriptions). Project sources are persisted per project (SQLite + Postgres) with add/list/remove endpoints; existing config marketplaces are unchanged.
- a5925f5: Fixed a High-severity security-assessment finding: stdio MCP clients (e.g. the
  CLI, editor integrations) previously authenticated backend calls with the
  shared `AGENTWEAVER_API_KEY`, which the API maps to the trusted
  `agentweaver-internal` identity and exempts from project-ownership checks —
  letting any stdio client reach every project on the backend, not just the
  operator's own.

  Stdio clients should now set `AGENTWEAVER_TOKEN` to a per-user bearer token
  (an Agentweaver-minted OAuth access token, or a GitHub token such as `gh auth
token`) so the backend attributes calls to the real user and enforces
  project ownership. Credential precedence is: inbound per-request token (HTTP
  transports) → `AGENTWEAVER_TOKEN` → `AGENTWEAVER_API_KEY` (last-resort
  fallback).

  **Breaking change for stdio deployments still relying on the shared key**:
  if `AGENTWEAVER_TOKEN` is not set and `AGENTWEAVER_API_KEY` is, the MCP
  server now refuses to start in stdio mode by default. Set
  `AGENTWEAVER_ALLOW_SHARED_KEY=true` to explicitly opt back into the
  insecure fallback (e.g. for first-party service-to-service callers that
  intentionally use the shared identity). See `docs/guide/mcp-cli.md` for
  migration guidance.

### Patch Changes

- 01d6699: Fixed a Critical security-assessment finding: AgentHost sandbox pods (which
  execute untrusted agent/tool shell commands) previously federated to the same
  Key Vault identity as the API (`agentweaver-api-identity`), granted Key Vault
  Secrets User/Officer roles. Untrusted code running in a sandbox could exchange
  its projected workload-identity token for a Key Vault access token and read
  every user's secrets.

  AgentHost now federates to a dedicated, least-privilege managed identity
  (`agentweaver-agenthost-identity`) with no Key Vault role assignments. This is
  a functional no-op for legitimate use: the run owner's GitHub token is already
  brokered per-run by the API through the `/configure` call rather than fetched
  directly from Key Vault by the sandbox. Deploying this change to an existing
  cluster also removes the legacy `agentweaver-agenthost-fedcred` federated
  credential from the API identity so older deployments can't retain the
  vault-privileged mapping.

- faaff4c: Render fully-promoted ("delegated to backlog") coordinator runs as complete instead of
  leaving RAI, Human Review, Merge, and Scribe stuck as "Pending forever", and notify the
  user when subtasks are promoted to the Board.

  - The coordinator graph descriptor now marks the skipped assembly stages of a delegated
    run with an authoritative `delegated` status (single source of truth); the run tree and
    workflow graph render those nodes as a terminal "Delegated to backlog" state and the
    coordinator/work-plan nodes as Completed.
  - A poll-derived "N subtasks created" notification (linking to the project Board) is
    emitted for delegated runs, reusing the existing notification center with board-specific
    toast/badge copy.

- 6681a50: Enforce the per-run filesystem policy at the Kata command boundary (security, #476):

  - **Cross-run workspace escape**: every Kata AgentHost pod mounts the _shared_ RWX
    `/workspace` PVC, and the Kata-mode `PassthroughExecutor` previously ignored the per-run
    filesystem policy entirely. A prompt-injected command could keep its declared working
    directory inside its own tree yet read/write a sibling project via an absolute path
    (`cat /workspace/<other-project>/secrets`, `git -C /workspace/<other-project> …`).
  - **New guard**: `SharedWorkspacePathGuard` scans a command's _text_ for absolute paths
    that resolve under a protected shared-mount root (default `/workspace`, override via
    `AGENTWEAVER_PROTECTED_SHARED_ROOTS`) but outside the run's own allowed roots, and rejects
    them before the shell starts. It is wired into both `ShellCommandValidator` (the
    `run_command` tool) and `PassthroughExecutor` (the executor boundary, consuming
    `SandboxCommand.FilesystemPolicy`), collapsing `.`/`..` traversal and handling quoting,
    `--flag=` assignment, and colon path-lists.

  This is defense-in-depth, not a substitute for true per-run volume isolation (the shared
  RWX PVC follow-up remains tracked architectural work); a command-text filter cannot catch
  every obfuscation, but it closes the direct cross-project read/write path described in #476.

- 9b43fdb: Fix the "Browse curated marketplaces" dialog freezing when selecting a source: browsing a marketplace now fetches only the source's subtree via the GitHub Trees API (bounded by a hard timeout) instead of a full, untimed repository clone, so failures surface as a clear error and a loading state is shown while browsing. Browsing also now falls back to an anonymous request when a user's token is refused with any non-success status (public marketplaces in SAML-enforced orgs such as `microsoft/skills`, whose Trees API returns 403 and whose raw blobs return 404 for an un-SSO'd token, no longer come back empty). Browse is now a paginated index: it enumerates every candidate skill + location from the Git Trees metadata (one call, zero blob downloads), then fetches each `SKILL.md` frontmatter definition ONLY for the requested page (default 25, cap 50) — concurrently and anonymously (curated marketplaces are public, so browse attaches no user token, avoiding a slow token round-trip) — so even large marketplaces like `github/awesome-copilot` (~400 skills) return one fully-described page in a few seconds; a skill's full content is downloaded only at import time. Browse's throwaway placeholder tree is written to local ephemeral scratch instead of the data directory, which in production is a CIFS/Azure Files SMB mount whose per-file latency made browsing large marketplaces take tens of seconds. The browse request accepts `page`/`pageSize` and the response returns `total`, `page`, `page_size`, and `has_more`; the Skills page wires this to a "Load more" control. Skill descriptions written as YAML block scalars (`description: |` / `>`) are parsed correctly. The "Azure Skills" marketplace subpath is also corrected to a plugin path that actually exists (`.github/plugins/azure-sdk-dotnet/skills`).
- 3d2fbc9: Simplify the Account settings MCP clients section to show only the MCP server URL, removing the per-client (Claude Desktop, VS Code, GitHub Copilot CLI) config snippets and copy buttons. Update the page description to cover both the MCP connection and the repository sandbox policy.
- de4b433: Fixed the release preparation ignored-file guard so it no longer rejects standard dependency/build/output directories (node_modules, dist, bin, obj, test output, harness artifacts), which had made `release:prepare`/`release:publish` unrunnable from a normal checkout, while still flagging unexpected ignored files in source/config locations.
- 45f2d3e: Added a copy-to-clipboard button to the install/quick-start command blocks on the documentation landing page.
- ecc5a8f: GitHub org allowlist now accepts multiple orgs via config
  (`Auth:GitHub:AllowedOrg` / `GITHUB_ALLOWED_ORG`).

  The GitHub organization authorization gate previously enforced membership of a
  single, exact-match org. It now parses `Auth:GitHub:AllowedOrg` as a delimited
  LIST (split on `,` and `;`, trimmed, empty entries dropped, de-duplicated
  case-insensitively, order preserved) and authorizes a caller who is a member of
  **any** listed org. For each allowed org the existing two-step check is applied
  verbatim (authenticated `/orgs/{org}/members/{login}`, then the unauthenticated
  `/orgs/{org}/public_members/{login}` SAML fallback). Fail-closed behavior is
  unchanged: empty/whitespace config yields an empty list and blocks every
  non-exempt request. When no org confirms membership but at least one org's
  primary authenticated check was inconclusive (expired token / 5xx / network),
  the result is `Inconclusive` rather than a hard denial, preserving the
  refresh-time re-check semantics. The single-org list parser is shared by the
  authorization service, the org-authorization middleware, and the API-key
  middleware.

  The value is now config-driven and non-committed: it flows from the deploy-time
  `GITHUB_ALLOWED_ORG` environment variable through the `agentweaver-runtime-config`
  ConfigMap into the API and worker deployments (mirroring `GITHUB_CALLBACK_URL`).
  Committed defaults remain `microsoft`.

- 20f6dea: `azure:provision-infra` interactive installer now supports arrow-key selection (with the numbered prompt as a fallback when raw-mode TTY is unavailable), walks you through creating a GitHub OAuth App (with link and callback-URL guidance) before asking for the client ID/secret, and prompts for the GitHub org(s) allowed to sign in (`GITHUB_ALLOWED_ORG`, also available as `--github-allowed-org`). Prompts now validate and reprompt on invalid input, and az-backed discovery (subscription/resource group/location) degrades to a manual prompt instead of crashing on transient failures.
- aa6b6ff: Hardened sandbox RBAC (High-severity security-assessment finding): split the
  combined API/worker sandbox permissions into distinct least-privilege Roles
  (`agentweaver-api-sandbox`, `agentweaver-worker-sandbox`) each bound to its own
  ServiceAccount, added a namespace-wide default-deny `NetworkPolicy` with
  explicit compensating allows for DNS, Postgres, and AgentHost orchestration
  traffic, and restricted `pods/exec` — which cannot be scoped via RBAC
  `resourceNames` because sandbox pod names are dynamic — with a
  `ValidatingAdmissionPolicy` (`k8s/base/vap-sandbox-exec.yaml`) that permits
  exec only from the `agentweaver-api`/`agentweaver-worker` ServiceAccounts
  against pods named `agentweaver-agent-host-*`, closing the lateral-movement
  path where either identity could previously exec into any pod in the
  namespace (including each other or Postgres).
- 16dcabd: Hardened the release pipeline (`azure:release-publish`, changeset prepare/sync
  scripts) to reject untracked AND unexpectedly git-ignored files in the working
  tree before publishing or syncing a release. Previously the check only ran
  `git status --porcelain --untracked-files=all`, which does not surface files
  that match a `.gitignore` pattern — an attacker-planted file under a path like
  `node_modules/` or `dist/` could have been silently bundled into a release
  artifact. The check now also flags unexpected ignored files, with a narrow
  allowlist limited to genuinely safe, never-shipped editor/local-tooling paths
  (`.vscode/`, `.idea/`, `.squad/`, etc.). Requires running these scripts from a
  truly clean checkout, per the existing `RELEASING.md` guidance.
- 0b5374e: Prevent workflow and skill discovery from reading through repository symlinks.

## 0.10.1

### Patch Changes

- b732690: Added a Content-Security-Policy and defense-in-depth security headers
  (`X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
  `Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy`) to
  the `Agentweaver.Web` static host response pipeline, addressing a Low-severity
  security-assessment finding (missing security headers/CSP). The CSP is
  same-origin (`default-src 'self'`) with a strict `script-src 'self'` (no
  `unsafe-inline`/`unsafe-eval`) and `style-src 'self' 'unsafe-inline'` (required
  by @fluentui/react-components' runtime style injection).

  Also documented the accepted residual risk for the companion Low-severity
  finding (OAuth session token stored in `sessionStorage`, JS-readable) with a
  code comment in `apps/web/src/config.ts` — the token is not duplicated across
  storage locations or logged today, and a full migration to an HttpOnly/Secure
  session cookie (which also requires adding CSRF protection) is tracked as a
  separate, larger follow-up rather than attempted in this pass.

- bc99875: Enforce project ownership on all project-scoped memory, decision, session, and casting
  endpoints. Previously these routes verified only that a project existed, so any
  authenticated organization member who learned another project's UUID could read or modify
  its memory, sessions, and decisions or hijack its agent-team casting. Because active
  decisions are compiled verbatim into future agent system prompts, this also closed a
  stored cross-project prompt-injection (XPIA) vector. A centralized `ProjectAuthorization`
  guard now authorizes the caller against the project owner (the trusted internal
  service identity used for a run's own agent callbacks remains exempt), covering both the
  direct API and the MCP tools that proxy to these same routes.
- 73e3026: Harden agent-runtime tool gating against three security-review findings. (1) Native Copilot shell now fails closed for **every** run: the SDK's in-process native shell (which bypasses `ISandboxExecutor`/bubblewrap and was only working-directory-checked) is rejected in both `CopilotAIAgent` and `GitHubCopilotAgentRunner`, and shell is exposed solely through the sandboxed `run_command` tool. (2) The "tool-less" LLM classifiers (`CopilotWorkflowSelectionModel`, `OutcomeSpecReplyClassifier`, `AssemblyGateCodeClassifier`, `StoryIndependenceClassifier`, `PreviewClassifier`) now set `AvailableTools = []` and install a deny-by-default `OnPermissionRequest` handler, since `Tools = []` alone does not disable the SDK's built-in native tools against prompt-injected input. (3) `OperatorToolApprovalPolicy` is now fail-closed: only an explicit allow-list of read/low-consequence tools runs without an operator prompt, so consequential mutators (including `sandbox_policy_set`, `memory_import`, `skill_import`, `skill_assign`, `workflow_save`) and any unrecognized/new MCP tool require approval by default, with a reflection-based coverage test guarding against classification drift.
- 2289ccf: Bump the vendored `@github/copilot-linux-x64` CLI binary in the AgentHost image from 1.0.67 to 1.0.71-3, self-update npm before installing global tooling, bump `yq` from 4.44.3 to 4.53.3 (dominant source of the remaining HIGH/CRITICAL findings), and add a cache-busting `GH_CLI_CACHE_BUST` ARG to the GitHub CLI apt-install layer so it stops silently reusing a stale, CVE-carrying cached `gh` build across CI runs. Add a narrowly-scoped `.trivyignore` for the handful of CVEs confirmed to have no fix in the newest upstream `yq`/`gh` releases (transitive Go stdlib/grpc-go/x-net/x-text baked into third-party compiled binaries we cannot patch). Also fix the actual Trivy CVE gate in `agent-host-maintenance.yml` to scan with `format: table` instead of `format: sarif` — Trivy has a known bug where `.trivyignore` isn't reliably honored before the exit-code check runs in SARIF mode (aquasecurity/trivy#9487), so the table-format scan is now the real HIGH/CRITICAL gate and the SARIF step is upload-only (non-gating) for the Security tab. Severity/exit-code/ignore-unfixed settings on the gate itself are unchanged, so any CVE not explicitly listed in `.trivyignore` still fails the build.
- 3b66282: Fix `extractChangelogSection` to accept an optional `v` prefix inside bracketed changelog headings (e.g. `## [v0.9.70]`), so `release:sync-dev`'s pre-flight version check no longer fails on historical hand-authored CHANGELOG.md entries.
- 52f8818: Harden K8s/Kata sandbox isolation (security):

  - **Sandbox egress**: `sandbox-egress-allowlist` no longer permits `0.0.0.0/0` on
    all ports. It is now scoped to public TCP/443 only and denies RFC1918, CGNAT/link-local,
    and IPv6 ULA/link-local ranges — blocking lateral movement to in-cluster
    Services/nodes/VNet and IMDS SSRF, matching the proven `agenthost-egress-allowlist`.
  - **Public MCP identity**: the internet-exposed MCP now runs as a dedicated,
    least-privilege `agentweaver-mcp` ServiceAccount (no binding to the pod-create/exec
    sandbox Role, default token automount disabled) instead of sharing `agentweaver-api`,
    removing a namespace privilege-escalation path.
  - **AgentHost A2A mTLS**: the production overlay enables mutual TLS + hostname
    verification for the `/configure` credential channel (encrypts the GitHub/turn tokens
    that previously crossed the pod network over plain HTTP).

  Shared RWX workspace per-run isolation (Alert 2) is documented as follow-up
  architectural work; the compounding egress and identity controls are hardened here.

- 71a4509: Harden the supply chain across CI, container builds, and provisioning scripts: pin the `agent-host-maintenance` workflow's Trivy scan action to a reviewed full commit SHA (was `@master`) and add a Sigstore-backed build provenance attestation instead of relying solely on a mutable ACR tag; verify checksums for every tool downloaded during Dockerfile builds (Copilot CLI, Node.js, yq) and replace the mutable NodeSource `curl | bash` install with a checksum-verified tarball and exact-pinned npm globals (also bumping pnpm 10.34.5 -> 11.5.3 to close a HIGH CVE, CVE-2026-55697, caught by the Trivy CVE gate); pin previously-floating NuGet package versions and commit `packages.lock.json` for every project, with `RestorePackagesWithLockFile`/`RestoreLockedMode` enabled so CI restores fail loudly instead of silently resolving a new dependency version; and route Key Vault secret writes in the Azure provisioning scripts through short-lived, mode-0600 scratch files (`scripts/azure/lib/secret.mjs`) instead of passing secret values as CLI arguments, closing a `ps`/`/proc` process-listing exposure window.
- fcb6b3e: Resolve symlinks and reparse points before workspace file-access containment checks so a
  repository-planted symlink can no longer escape the workspace root. Previously the checks were
  lexical only (`Path.GetFullPath` + string prefix), which validated the pathname but still
  followed a symlink on read or write — allowing a malicious repo to disclose or overwrite files
  outside its worktree (e.g. a mounted secrets store). Workspace read and write endpoints now share
  a single `WorkspacePathGuard` that resolves the real target and rejects any path landing outside
  the workspace root.
- e1600d3: Harden the GitHub webhook trust boundary with regression tests that lock in existing
  security properties: reject a delivery signed with a different project's secret (proving
  per-project secret scoping, not a shared global secret), and prove prompt-injection text
  smuggled in issue/comment payload fields never reaches the fired backlog task. Also correct
  a stale doc comment that claimed no webhook receiver was wired.
- 5676f49: Harden GitHub OAuth refresh-token and web sign-in `state` handling (security):

  - **Fail closed on refresh org re-check.** `/oauth/token` refresh now denies (403) when the
    brokered GitHub token is missing/expired or the org-membership re-check is inconclusive, instead
    of silently falling back to the issuance-time org claim. A user removed from the required org can
    no longer keep minting access tokens through the refresh chain by revoking/expiring their GitHub
    token. The membership check runs on a non-consuming peek, so a transient (inconclusive) denial
    leaves the refresh token usable once membership can be confirmed again; a definitive
    non-membership revokes the whole refresh chain.
  - **Atomic single-use refresh-token consumption.** Refresh rotation now claims the presented token
    with a single conditional compare-and-swap (`ConsumedAt IS NULL`), so a concurrent replay of the
    same refresh token can no longer fork two independent live refresh branches; the loser triggers
    reuse detection and the chain is revoked.
  - **No SAML bypass via public membership.** A SAML-enforcement (`403`) response on the authenticated
    private org-membership check is now treated as "SSO required" and is no longer overridden by the
    unauthenticated public-membership fallback.
  - **Browser-bound OAuth `state`.** The web sign-in `state` is bound to the initiating browser via a
    Secure, HttpOnly, SameSite=Lax cookie (double-submit) and validated on callback, mitigating
    login-CSRF where an attacker grafts their pre-authorized `state`/`code` onto a victim's browser.

## 0.10.0

### Minor Changes

- 6d95e74: Add settings and documentation that provide copyable MCP client configurations for
  Claude Desktop, VS Code, and GitHub Copilot CLI.
- f50ce19: Projects with no connected GitHub repository can now create and connect one instead of
  being stuck. A new "Connect a GitHub repository" flow (Project Settings, and a dismissible
  banner on the project dashboard) lets you pick an owner (yourself or an org), choose a
  repo name and visibility, and creates the repo then pushes the project's existing local
  history to it.

  The push-PR execution step's "no connected repository" case now emits a `skipped` step
  event (with a message pointing to Project Settings) instead of `failed`, since a missing
  GitHub connection is not a run failure.

- 5c292e3: Separate repository release publication, published-release deployment, local-checkout deployment, arbitrary-commit deployment, and Azure infrastructure provisioning into explicit commands.
- 016f97d: Browse curated skill marketplaces and add selected skills to project catalogs.
- 7662468: Enable MCP persona harness runs to export normalized evidence and a shared Judge prompt
  for agent-native verdicts, instead of depending on a configured judge subprocess.
- a61b0c9: Migrate the AKS deployment manifests under `k8s/` from flat, envsubst-rendered YAML to a Kustomize-based `base/` + `overlays/production/` layout.

  `scripts/azure/steps/30-deploy.mjs` now builds the full production overlay via `kubectl kustomize` (kubectl's built-in Kustomize support -- no separate `kustomize` binary required) instead of the old hand-rolled `lib/render.mjs` envsubst renderer, then re-groups the combined build back into the same staged apply order (identity/RBAC/quota/PVCs, network policies, services/gateway/routes, sandbox template, deployments, worker) it has always used. Dynamic values (image tags, the public HOST-derived URLs, workload-identity IDs, Key Vault/Tenant IDs, hostnames) are now injected via Kustomize's `images:` transformer, a `configMapGenerator` (`agentweaver-runtime-config`), and `replacements:` patches instead of textual placeholder substitution.

  Manifests not part of the automated deploy (one-off migration Jobs, example-only Secrets, the app-code `SandboxClaim` template) moved to `k8s/reference/` and are excluded from the Kustomize base. No new tool prerequisite is required: `kubectl apply -k` / `kubectl kustomize` cover this migration's needs.

- 5912d2e: Add project-specific GitHub webhook settings with independently generated, rotatable secrets and workflow event trigger documentation.
- f50ce19: Wire the existing `open_pull_request` workflow node into the built-in default
  workflow (`merge` → `push-pr` → `scribe`) and into the `RunWorkflowGraphBinder`
  so any code-producing workflow with a platform-appended merge/scribe step now
  publishes or updates a GitHub pull request automatically. `GitHubPullRequestClient`
  is now idempotent: if GitHub reports the pull request already exists (422), the
  existing open PR is looked up and returned as success instead of failing the run.
- 79164db: Made the `Changeset advisory` CI check a required, blocking status check instead
  of an advisory-only warning: a PR touching release-relevant paths (`apps/`,
  `packages/`, `scripts/azure/`, `k8s/`) with no changeset and no
  `changeset:not-required` exemption now fails CI instead of only printing a
  warning. Test-only diffs under those paths no longer trigger the requirement.
  Also made every path-scoped CI job (`.NET tests`, `Node toolchain tests`, `Web
tests`, `Web lint`, `Docs build`) run only when its relevant paths actually
  changed, skipping unrelated suites (e.g. docs-only PRs no longer run the full
  .NET/web suites) while always running everything when the CI workflow itself
  changes.
- 4a33119: Add structured contributor-authored release notes and a prepared, reviewable release workflow for Agentweaver maintainers.
- 5192547: Run and schedule project workflows from the workflow library, and duplicate built-in templates into the visual editor.

### Patch Changes

- 47dd188: Add a runnable CLI-to-MCP smoke test that verifies authentication, project setup,
  run completion, artifact retrieval, and cleanup against local or staging servers.
- b47bdc5: Add an interactive landing-page preview for declarative workflows, including gates and scheduled runs.
- 3185e2d: Users can now dismiss individual notifications directly from the notification bell dropdown.
- 3b77d94: Use Copilot-based semantic classification to decide preview applicability and preserve non-preview review feedback.
- a8d74d8: Prevent `record_memory` from timing out when project workspace storage is slow by returning
  after the durable database write and leaving filesystem snapshot generation to the explicit
  end-of-run memory export.
- dc54bbb: Fixed a coordinator merge failure that blocked runs whose kept-alive preview left
  untracked build artifacts (e.g. a `node_modules/` directory in a demo app with no
  `.gitignore`) in the working tree. When the merge took the working-tree
  reconciliation path, harmless untracked files that are absent from the merge result
  tree were incorrectly treated as unreconcilable and failed the merge with
  "uncommitted content diverges from the merge result". Untracked paths the result
  tree does not reference are now correctly left untouched (a hard reset never touches
  them), matching the reconciler's documented contract, while genuinely divergent
  edits to tracked files still correctly block the merge.
- 87fb201: Bound xUnit test collection parallelism to two workers and made tool-approval
  gate terminal-state resolution atomic (guarding replacement cleanup so a
  gate can't be resolved twice under concurrent access). Also gave
  approval-expiration tests more scheduler headroom to reduce CI flakiness.
- bc61049: Fixed the Operator Assistant chat composer losing focus after every send and
  feeling frozen while a message was in flight. Both were caused by the
  composer's `disabled` prop being tied to the `busy` send state, which
  disabled the textarea for the duration of the request — React blurs disabled
  form elements (stealing focus) and made the whole composer unresponsive even
  though the send itself is already optimistic (input clears immediately, a
  pending message bubble shows). The textarea now stays enabled and focused
  during a send; only the send affordance is gated via `disableSend`, so users
  can keep typing their next message right away.
- cec9bfb: Show the complete GitHub Awesome Copilot skill catalog by browsing its `skills` directory instead of scanning only the repository root.
- f46c163: Fix the built-in "Azure Skills" marketplace subpath so browsing it finds skills. The `microsoft/skills` repo nests `SKILL.md` files one directory deeper (`.github/plugins/azure-skills/skills/<name>/SKILL.md`) than the previously configured subpath.
- fbe8887: Fix Build & Test gate coordinator behavior (#386, #387). The gate now renders as a `planned` node in
  the run tree from the start — the `GET /api/runs/{id}/graph` endpoint and the topology-shape
  `coordinator.graph` emissions resolve the actual assembly gates from the selected workflow instead of
  falling back to the RAI + Human Review defaults that omitted `build_test` until execution reached it.
  The coordinator also drops the platform Build & Test gate for non-code-producing work plans (all
  subtasks are planning-phase deliverables such as research, PRDs, or design docs) so those runs no
  longer loop indefinitely at a gate that has no code to build or test.
- 87fb201: Fixed a Coordinator race where a deferred decision applied only on the next
  heartbeat instead of immediately when the approval gate armed, and switched
  run-plan tests to poll for the ordered `coordinator.work_plan` stream event
  rather than treating the earlier database commit as completion.
- 8304dca: Fixed edge routing in the architecture-diagram renderer so lines no longer
  overlap each other or cut straight through unrelated node cards. The renderer
  was drawing every edge as a `getSmoothStepPath` between fixed top/bottom
  handles, which ignores dagre's own edge routing -- so an edge from one rank to
  a distant rank sliced right through any card in between, and several
  near-parallel edges collapsed onto the same path.

  The shared renderer (`docs/diagram-renderer/`) now draws each edge along the
  poly-line dagre actually routes for it (`dagre` performs real layered edge
  routing, threading each line through the gaps between ranked nodes), rendered
  with rounded corners so it still reads like the product's smoothstep edges.
  Labelled edges hand dagre their footprint up front so it reserves
  non-overlapping label slots along the route, and the existing label
  collision-avoidance pass now seeds from those reserved anchors. `nodesep`,
  `edgesep`, and `ranksep` were widened for extra breathing room. This is a
  general fix in the pipeline -- every current and future graph-spec benefits,
  with no per-diagram tuning. The three AKS diagrams were re-rendered.

- 6a2d926: Fix stale `k8s/` manifest paths in `KubernetesRemoteApiManifestTests` after the Kustomize `base`/`overlays` migration (#375). The test helper still pointed at the old flat `k8s/*.yaml` layout and was raising `FileNotFoundException` for every run against `dev`; it now resolves manifests under `k8s/base/`, matching the current directory structure.
- a1c11f1: Fix `agentweaver-runtime-config` ConfigMap deploying to the wrong Kubernetes namespace (`default` instead of `agentweaver`), which caused `azure:deploy-from-local`/`azure:upgrade` to fail with `CreateContainerConfigError: configmap "agentweaver-runtime-config" not found` on the API, MCP, and worker deployments and the AgentHost SandboxTemplate.

  The production Kustomize overlay (`k8s/overlays/production/kustomization.yaml`) generates this ConfigMap directly via `configMapGenerator`, but had no top-level `namespace:` transformer of its own. `k8s/base/kustomization.yaml`'s `namespace: agentweaver` transformer only applies to resources pulled in via `resources: - ../../base`, not to generators declared in the overlay itself, so the generated ConfigMap silently fell back to whatever namespace `kubectl apply` defaults to. Added `namespace: agentweaver` to the overlay's kustomization.yaml, and a regression test asserting every namespace-scoped resource in the built manifest set carries `namespace: agentweaver`.

  Also fixes a second, pre-existing (not caused by the Kustomize migration) live-deploy blocker discovered during the same `azure:deploy-from-local` validation: AgentHost warm-pool pods crash-looped with `AgentHost:McpEndpoint must be configured` because `AgentHost__McpEndpoint` was never set anywhere (not in the old flat manifests, not in the new Kustomize base) even though `Program.cs` requires it unconditionally at pod startup for every AgentHost pod (not just ones adopted for the operator-assistant purpose, per #346/#347's narrow AgentHost cutover). Added the env var (`http://agentweaver-mcp:8080/mcp`, a constant in-cluster Service URL) to `sandbox-template-agenthost.yaml`, plus the matching ingress `NetworkPolicy` (`allow-agenthost-to-mcp` in `networkpolicy-mcp.yaml`) so the MCP pod actually accepts the connection -- egress from AgentHost pods was already permitted by the existing `sandbox-egress-allowlist`.

- 8725a32: Fix `project_create` MCP tool: the optional `blueprint` argument had no C# default value, so the
  SDK's reflection-based argument binding treated it as required and rejected any call that omitted
  it (the normal/documented case) with an opaque "An error occurred invoking 'project_create'." error
  before the tool body ever ran. `blueprint` now defaults to `null` like the other optional
  create-project fields.
- f5df97f: Fix a live-deploy-blocking MCP server startup crash: `project_create`'s optional `blueprint`
  parameter changed from `JsonElement?` to `string?` (a JSON-encoded string), fixing a regression of
  the 7605b692/#419 landmine. `Microsoft.Extensions.AI`'s reflection-based schema exporter cannot
  serialize the default/uninitialized state of a `Nullable<JsonElement>` parameter into the tool's
  JSON schema, which crashed the whole MCP server at boot (`AIJsonUtilities.CreateFunctionJsonSchema`
  -> `InvalidOperationException` during `MapMcp`). Using `string?` keeps the parameter optional (so
  `WithToolsFromAssembly` still binds calls that omit it, without a required-parameter binding
  rejection) while remaining safely serializable as a schema default. Added a regression test that
  launches the real compiled Agentweaver.Mcp process and asserts clean startup, since this bug only
  reproduces with the exact dependency versions Agentweaver.Mcp resolves at runtime.
- 87fb201: Fix a React ref-write-during-render bug in the landing page workflow demo, and remove the hard dependency on a PATH-available `openssl` binary for RSA key/random-byte generation in the Azure provisioning scripts (now uses Node's built-in `crypto` module).
- 2867811: Fixed Operator Assistant turns failing 100% of the time on AgentHost pods.
  `MapA2AHttpJson`'s session store calls `CreateSessionAsync` on every new A2A
  message regardless of `AgentHostPurpose`, and `A2ATurnBridgeAgent` (a
  `DelegatingAIAgent`) forwarded this unconditionally to the singleton
  `CopilotAIAgent`. For the `OperatorAssistant` purpose, `AgentHostStartupService`
  deliberately never calls `CopilotAIAgent.SetupAsync` (this purpose never drives
  `CopilotAIAgent` — turns are routed to `IOperatorAssistantAgent` instead), so
  `CopilotAIAgent.CreateSessionCoreAsync` threw
  `InvalidOperationException("SetupAsync must be called before
CreateSessionAsync.")` before the turn ever executed. `A2ATurnBridgeAgent` now
  overrides session creation to bypass `CopilotAIAgent` for the
  `OperatorAssistant` purpose, matching how turn execution already routes around
  it; all other purposes are unaffected.
- 2776953: Fixed a bug (#388) where a reviewer (Build & Test gate, RAI, rubber-duck, or
  any steering-driven revision) sending a review/revision request to a target
  agent WIPED that agent's run-tree message stream instead of appending to it.
  The shared in-place-resume/revision-injection mechanism (used by
  `CoordinatorAssemblyService.ExecuteInPlaceSteerAsync`,
  `CoordinatorDispatchService.TryInjectSteeringRevisionAsync`, and
  `CoordinatorSteeringService`'s recovery path) removed and recreated the
  child/coordinator run's `RunStreamStore` entry to clear the completed flag
  before resuming, which discarded every event recorded before the review.
  `RunStreamStore`/`RunStreamEntry` now expose a `Reopen()` operation that
  clears the completed/awaiting-review flags in place while preserving the
  recorded history, so the new review/revision turn is appended after the
  target agent's prior messages instead of replacing them.
- fb95e09: Fix independent task promotion so story components are classified concurrently with a
  runtime-aligned timeout and one bounded retry. Classification degradation remains
  fail-closed but is now surfaced on the work-plan timeline instead of silently producing
  an unexplained empty board.
- 4f57729: Fix the "Alpha vX.Y.Z" badge (top-left of the app shell) always showing the last `VERSION`-file bump, even when the running deployment was produced by `azure:upgrade`/`azure:deploy-from-local` (which tag images by git SHA and never touch `VERSION`) — making the badge completely uninformative about what's actually running.

  Root cause: `AppVersionProvider` only ever read the static `VERSION` file baked into the image, and while every Dockerfile already declares `ARG IMAGE_TAG`/`ARG GIT_SHA` (passed by `scripts/azure/image-spec.mjs` for every `az acr build`), those were only ever set as OCI `LABEL`s (image metadata), never as container `ENV` vars, so the running .NET process had no way to read them.

  Fixed by:

  - Adding `ENV IMAGE_TAG=${IMAGE_TAG}` / `ENV GIT_SHA=${GIT_SHA}` right after the existing `ARG`/`LABEL` declarations in all four Dockerfiles (API, MCP, web, AgentHost), so the build provenance is readable at runtime via `Environment.GetEnvironmentVariable`.
  - `AppVersionProvider` now prefers these runtime env vars: when `IMAGE_TAG` looks like a real semver release tag (`^v?\d+\.\d+\.\d+$`), it's a real `azure:release` build and that tag is the authoritative version. Otherwise (local `dotnet run`, or a git-SHA-tagged `azure:upgrade`/`azure:deploy-from-local` build) it falls back to the `VERSION` file for the base semver and surfaces the git SHA separately.
  - `GET /api/version` now returns `{ version, gitSha, isRelease }` instead of a single opaque string.
  - The frontend badge now reads: `Alpha v0.9.70` for a real release, `Alpha v0.9.71-dev+a1c11f1` for a SHA-tagged local/upgrade build — clearly distinguishing the two instead of showing the same stale string for both.

- 87fb201: Fixed Web lint and Web test CI breaks introduced after the Changesets
  integration landed: extracted non-component exports out of
  `CostChip.tsx`/`BlueprintPicker.tsx`/`LandingWorkflowDemo.tsx` into sibling
  modules to satisfy `react-refresh/only-export-components`, removed a dead
  reassignment flagged by `no-useless-assignment`, and fixed a real
  `CoordinatorRunPage` test flake caused by a missing global `afterEach`
  cleanup between test files (added `apps/web/src/test/setup.ts` and made
  dialog-button role queries more resilient to CPU-contention timing).
- 87fb201: Fix `azure:deploy-from-local` and other provisioning commands failing on Windows when `openssl` isn't on `PATH`: the mTLS certificate generation step now falls back to the `openssl` binary bundled with Git for Windows.
- d78caed: Fixed the expanded workflow-definition graph so branching and reconverging
  paths use the same routed staircase layout as coordinator run graphs.
- bbd4689: Migrated the `docs/deep-dive/*.md` Mermaid **flowcharts** onto the Fluent-styled
  `@xyflow/react` + `dagre` diagram pipeline (the same one used for the AKS
  architecture diagrams), so they render as on-brand node/card diagrams with the
  overlap-free edge routing shipped previously, instead of raw ```mermaid fences.

  Adds a reusable converter (`scripts/docs/mermaid-to-graphspec.mjs`) and a
  migration CLI (`scripts/docs/migrate-mermaid.mjs`) that lift the semantics the
  Mermaid sources already carry — `class` category assignments, node shapes, and
  nested `subgraph` clusters — into graph-spec card icons/badges and groups. 104
  flowcharts across 36 deep-dive docs were converted to `docs/diagrams/src/*.json`
  specs and pre-rendered to PNG. Non-flowchart Mermaid (`sequenceDiagram`,
  `stateDiagram`, `classDiagram`, `erDiagram`) is intentionally left as-is — it is
  not representable by the node/edge/group graph-spec and keeps rendering via
  `vitepress-plugin-mermaid` (tracked as follow-up).

- b7eeb61: Migrated the `docs/experience/*.md` Mermaid **flowcharts** onto the Fluent-styled
  `@xyflow/react` + `dagre` diagram pipeline (Phase 2, Batch B), so they render as
  on-brand node/card diagrams with the overlap-free edge routing instead of raw

  ```mermaid fences. 18 flowcharts across 14 experience docs were converted to
  `docs/diagrams/src/*.json` specs and pre-rendered to PNG. Non-flowchart Mermaid
  (`sequenceDiagram`, `stateDiagram`) is intentionally left as-is and keeps
  rendering via `vitepress-plugin-mermaid`.

  Hardens the shared converter/CLI in the process:

  * `mermaid-to-graphspec.mjs` no longer splits node labels on `&` when it begins
    an HTML entity (`&gt;`, `&amp;`, `&#39;`, …); previously a label like
    `allow replicas &gt; 1` was torn apart and spawned a stray `gt` node.
  * `migrate-mermaid.mjs` now names specs directory-scoped (`<dir>-<doc>-figN`) so
    same-named docs in different folders no longer collide on a shared basename
    (the initial `docs/deep-dive` batch keeps its bare `<doc>-figN` names).
  ```

- 06fede5: Migrated the Mermaid **flowcharts** in `docs/guide/*.md`, `docs/reference/*.md`
  and `docs/run-event-stream.md` onto the Fluent-styled `@xyflow/react` + `dagre`
  diagram pipeline (Phase 2, Batch C), replacing raw ```mermaid fences with
pre-rendered PNG embeds. 19 flowcharts across those docs were converted to
`docs/diagrams/src/\*.json`specs and pre-rendered to PNG (the 3 hand-authored
AKS architecture PNGs already embedded in`docs/guide/architecture-aks.md` are
untouched). Non-flowchart Mermaid (`sequenceDiagram`) is intentionally left as-is
and keeps rendering via `vitepress-plugin-mermaid`.

  Fixes `migrate-mermaid.mjs` to compute the diagram embed path relative to each
  doc's directory, so a doc at the `docs/` root (like `run-event-stream.md`)
  correctly references `diagrams/…` instead of `../diagrams/…`.

- 8578a4f: Re-rendered the AKS architecture diagrams (README.md's "Block diagram" and
  architecture-aks.md's "Component diagram", simplified + detailed) as
  Fluent-styled node/card diagrams instead of generic flowchart output. GitHub's
  built-in Mermaid renderer was clipping long subgraph/node labels on these
  diagrams, and a static pre-render was the fix -- but the pre-rendered result
  (first via `@mermaid-js/mermaid-cli`, then via a plain React Flow SVG export)
  still looked nothing like the product's own polished, on-brand node/edge
  diagrams (`apps/web/src/components/CoordinatorTopologyGraph.tsx`).

  Diagrams are now driven by plain JSON graph-specs (`docs/diagrams/src/*.json`)
  rendered through a small standalone app (`docs/diagram-renderer/`) that mounts
  a real `@xyflow/react` graph with `dagre` compound-cluster auto-layout and a
  custom node-card component matching `CoordinatorTopologyGraph`'s Fluent UI v9
  card styling (rounded card, icon + title + subtitle, pill category badges,
  tiered group containers) using the app's actual resolved color palette.
  Playwright captures each diagram as a static PNG
  (`scripts/docs/capture-diagrams.mjs`). `npm run docs:render-diagrams`
  regenerates the PNGs from the JSON specs; `npm run docs:check-diagrams` (CI)
  is now a fast, browser-free drift check comparing each spec's content hash
  against a committed `.hash.txt`, rather than re-rendering and diffing
  geometry (which broke across OSes due to host font-metric differences).

- ab6f28b: Remove the "Define your deterministic workflows" panel and its embedded editor
  demo from the marketing landing page. After several rounds of feedback and
  rewrites, the panel was decided to be cut rather than iterated on further.
- 87d3f2d: Remove the redundant "Break into tasks" button and dialog from the Outcome Plan panel. The same decompose-into-backlog-items capability remains available from the Kanban board and Workspace page.
- 1e07ca0: Removed a redundant clarifying note from the landing page's Deploy to Azure card (already explained in the getting started guide).
- ffd6b57: Rename the misleading `azure:dev` npm script to `dev:open` (opens a browser after `npm run dev` starts). It made zero Azure calls, so the `azure:` prefix implied a nonexistent cloud dependency.
- ab0e83f: Tool call arguments in the run timeline now display as labeled fields, with long values expandable independently for easier review.
- 03c0d1e: Replace the coordinator's keyword and file-extension heuristics for Build & Test gate
  applicability with a small, tool-less LLM classification that fails safely by retaining
  the gate when the model is unavailable, times out, or returns an ambiguous response.
- 6974479: Restore distributed traces and AI usage metrics from pod-per-run AgentHost workers, and add run/parent-run correlation dimensions to lifecycle metrics.
- 1f5b509: Prevent coordinator learnings from being lost when a terminal run recycles its AgentHost pod
  before the final Scribe turn. Terminal cleanup now waits for the bounded Scribe pass to finish,
  then releases the per-run pod and assembly worktree.
- a178c3f: Fix native agent tool calls (submit_decision, memory, inbox, …) silently timing out (~100s)
  from inside sandbox agent-host pods on the AKS/Cilium staging cluster.

  Agent-host pods could not reach the in-cluster `agentweaver-api` Service on TCP 8080. Under
  Cilium, an in-cluster ClusterIP resolves to the destination pod's security identity, and only
  an identity-based (`podSelector`) egress rule authorizes it — a CIDR `ipBlock` allow (even the
  `0.0.0.0/0` rule in `sandbox-egress-allowlist`) matches only the "world"/CIDR entity, never a
  cluster-managed pod identity. The MCP dependency already had such a rule; the API did not.

  `agenthost-egress-allowlist` now adds an explicit, tightly-scoped `podSelector` egress allow
  from agent-host pods to `agentweaver-api` on TCP 8080 (mirroring the existing MCP rule), so
  API-backed native tools connect east-west instead of black-holing against the RFC1918 egress
  exclusions of the SandboxTemplate-owned network policy. RFC1918 egress is not otherwise widened.

- 3464bb5: Shorten the git SHA shown in the version badge to 7 characters, matching the short-SHA convention already used for `IMAGE_TAG` (`AppVersionProvider` now truncates the full `GIT_SHA` env var instead of passing it through as-is).
- 09a69fc: Fix the live staging Operator Assistant outage where AgentHost pods could not reach the in-cluster
  MCP service on port 8080 and every first turn timed out with `agenthost_unavailable`.

  `agenthost-egress-allowlist` now includes an explicit, tightly-scoped egress allow from
  AgentHost pods to `agentweaver-mcp` on TCP 8080, matching the live fix that restored
  AgentHost -> MCP connectivity without broadening RFC1918 egress.

- 7ca12be: Skip the coordinator Build & Test gate for documentation-only work even when decomposition labels
  its single subtask as execution work.
- 4a2bb82: Prevent automatically selected document-only workflows from bypassing Build & Test when decomposition reveals code-producing work, while preserving explicit workflow overrides with a clear warning.

All notable changes to Agentweaver are documented in this file, generated from the repository's git tag/commit history (`v0.7.0` through `v0.9.60`).

Format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/). Entries are grouped by release tag (newest first) and bucketed by commit-message prefix (`fix`, `feat`, `refactor`/`chore`, `docs`, `test`); merge commits and routine `chore(squad)` state-sync commits are omitted for readability. Regenerate with `python scripts/gen-changelog.py` if the history needs to be rebuilt.

## [v0.9.70] - 2026-07-16

### Fixed

- chore(release): rebuild `agentweaver-frontend` image, which had gone stale in staging — the `merge-docs-landing-main` branch (docs landing page redesign, `LandingWorkflowDemo.tsx`) was merged into `main` after the v0.9.69 images were already built/deployed, so the running frontend image no longer matched watched source paths at `HEAD`. No application code changes in this release beyond the image rebuild; v0.9.69's assistant hotfix is unaffected and confirmed still deployed

## [v0.9.69] - 2026-07-16

### Fixed

- fix(assistant): revert v0.9.68's `EnableSessionStore`/`InfiniteSessions` re-enable for `OperatorAssistantAgent` — live staging immediately hit `Error: database is locked` on every new operator run. Root cause: `OperatorAssistantAgent.RunTurnAsync` creates a brand-new Copilot SDK session on every single turn (never resumes one), so with the store enabled every turn across every concurrent conversation in the pod hammered the same pod-local SQLite session file. Durable rehydration from persisted `RunEvents` (the other half of the v0.9.68 recall fix) is unaffected and remains correct

## [v0.9.68] - 2026-07-16

### Fixed

- fix(assistant): operator assistant conversations can now be resumed after an idle-timeout closure, a pod restart, or a follow-up landing on the other API replica — `AssistantRunService.RunTurnAsync` rehydrates the in-memory run state from durable `RunEvents` on a cache-miss (with ownership/agent-type checks preserved) instead of permanently 404ing, and flips a `Completed` run back to `InProgress` when resumed
- fix(assistant): re-enable the Copilot SDK's native session store (`EnableSessionStore`, `InfiniteSessions`) for `OperatorAssistantAgent` only — the prior disable was copy-pasted from the one-shot sandboxed agents citing `copilot-sdk#1814`, which is documented as a one-shot/ephemeral-container issue and doesn't apply to the long-lived in-process assistant; sandboxed one-shot agents keep the disable

### Added

- feat(web): add a delete action to each row on the Sessions page, with a confirm dialog, using the existing generic run-delete endpoint

## [v0.9.67] - 2026-07-16

### Fixed

- fix(assistant): root-cause fix for "operator assistant chat frequently terminates mid-turn" — `k8s/api-deployment.yaml` had no `terminationGracePeriodSeconds`/`preStop` hook, so every rolling deploy (multiple/day in this repo) sent SIGTERM and the Generic Host's default 30s `ShutdownTimeout` cancelled `RequestAborted` well before legitimate long assistant turns (60-100+s across multiple MCP tool calls) could finish. Added `terminationGracePeriodSeconds: 120` + a `preStop: sleep 5` hook to the API deployment, and set `ConfigureHostOptions(o => o.ShutdownTimeout = TimeSpan.FromSeconds(100))` in `Program.cs` to pair with it, so in-flight assistant turns now drain instead of being forcibly cancelled during deploys

## [v0.9.66] - 2026-07-16

### Added

- feat(web): promote Sessions to a global top-level nav item with its own collapsible section and a New Session button, no longer scoped to a project or gated behind a feature flag; adds the `/sessions` route (#346 follow-up)
- feat(workflows): wire a real GitHub webhook receiver (`POST /api/webhooks/github`, HMAC-SHA256 signature verified) as the first live external event source for the scheduled/event workflow triggers feature (#53 follow-up)
- test(aks): add Pester regression coverage for the two release-script bugs found during the v0.9.65 ship — job-state misdetection in image builds and provenance verifier pod-selector scope (#351)

### Fixed

- fix(api-harness): don't crash schema validation on null `adapterVersion`/`personaCoreVersion` for structural (non-persona) seam scenarios

### Changed

- chore: retire the dead legacy Console/Operator-dock backend (`ConsoleEndpoints`, `ConsoleTurnService`, `CopilotConsoleFacadeAgent`) — zero live callers remained after #346
- chore(release): bump version to v0.9.66

## [v0.9.65] - 2026-07-16

### Fixed

- Fix #350: tear down AgentHost pods on every cancel/fail transition, including watch-loop failures, steering stop, and cancel/delete endpoints
- Fix #348: reconcile dirty/stale-index checked-out branches after worktree merges instead of surfacing false staged deletions or silently corrupting state
- Fix #342: make provenance verification tolerate variable live pod counts and exclude Pending/Terminating pods
- fix(release): port `release.sh` to `release.ps1` and delegate image builds to `20-build-push-images.ps1` for correct provenance stamping (#340)
- fix(auth): remove the unnecessary `read:org` OAuth scope from GitHub login flows and rely on the existing public-members org fallback
- Fix #336: force inline assigned skill instructions for coordinator-dispatched pod-per-run implementation children instead of dangling materialize pointers
- chore(web): remove the legacy Operator dock and redirect its orphaned route to `/assistant` (#346)

### Added

- feat(workflows): add the `open_pull_request` workflow node with templated title/body support and draft PR creation (#49)
- feat(workflows): add `daily`/`weekly`/`monthly` schedule triggers, named events, a scheduler service, and a manual event-fire endpoint (#53)
- feat(prd): add opt-in PRD story promotion to independent backlog tasks with tracked `BacklogTaskDependency` edges (#285)

### Changed

- chore(release): bump version to v0.9.65

## [v0.9.60] - 2026-07-15

### Fixed

- fix(mcp-harness): pass raw target URL to StreamableHTTPClientTransport, not assertTargetAllowed's void return
- fix: distinguish team-workspace 404 from project-not-found in MCP error mapping
- fix(mcp): make team_cast goal/confirm_proposal_id optional in inputSchema (Fixes #344)

### Changed

- chore(release): bump version to v0.9.60

## [v0.9.59] - 2026-07-15

### Fixed

- fix(mcp): emit proper object schema for run_task's `run` property (Fixes #341)
- fix(aks): don't treat accumulated prov tags on an unchanged digest as ambiguous

### Changed

- chore(release): bump version to v0.9.59

## [v0.9.58] - 2026-07-15

### Fixed

- Fix MCP run-workflow tool schemas and error surfacing

### Changed

- chore(release): bump version to v0.9.58
- chore: append MCP stress-test harness learnings

### Other

- MCP harness: dynamic persona parity with the API harness

## [v0.9.57] - 2026-07-15

### Fixed

- Fix #336: deliver per-turn skills/memory/identity to pod-per-run agents
- fix(coordinator): bound the reply-classifier model turn and default it to a fast model (#272)
- fix(skills): make assigned-skill delivery observable in agent system prompt (#336)
- Fix agent memory tool injection for warm-pool orchestration runs (Fixes #335)
- fix(coordinator): recognize multi-clause affirmations at the outcome-spec gate (#272)
- fix(coordinator): drain orphaned outcome-spec confirm/revise deferrals (#272)

### Changed

- chore(release): bump version to v0.9.57
- chore: persist squad state and harness transcript updates
- refactor(coordinator): classify outcome-spec chat replies with the LLM, not a regex (#272)
- chore: persist harness transcripts and squad state updates

## [v0.9.56] - 2026-07-14

### Fixed

- fix(sandbox): give agents scratch space outside the worktree (#224)

### Added

- feat(coordinator): allow confirming/revising outcome spec via chat message (#272)

### Changed

- chore(release): bump version to v0.9.56
- chore(mcp): harden driver instructions, actionable errors, run_task tool (#128, #129, #130)

### Other

- Add agent memory and session list views

## [v0.9.55] - 2026-07-14

### Fixed

- fix(preview): register start_preview so observe_bound_port's hint is reachable (#334)
- fix: collapse chatty timeline micro-steps
- Fix build-subtask terminal-emission gap: recover verified child work instead of failing (#331)
- fix(coordinator): retry resumes from failure point instead of restarting lifecycle (#332)
- fix(projects): make working_directory optional when workspace provider auto-assigns paths (#333)
- fix: live-refresh orchestration artifacts and md preview default
- fix(aks): enforce provenance stamp failures
- fix(k8s): route OpenAPI through staging gateway

### Changed

- chore(release): bump version to v0.9.55

### Other

- persona-actor: cap response.body at ~1.5KB, move reasoning into thought
- harness: fix live tail being invisible -- background output must be polled and relayed
- harness: add timing-only performance summary derived from transcript ts field
- harness: reformat live tail as parsed TURN/THOUGHT lines, not raw JSONL
- harness: auto-start a live tail of the transcript for operator visibility

## [v0.9.54] - 2026-07-14

### Fixed

- fix(orchestration): surface + durably persist assembly_blocked ineligible-subtask detail (#97)
- fix(notifications): emit reserved tool_approval notification type (#321)
- fix(#319): add notification type badge to notification center dropdown
- fix(#251): wire post-deploy image provenance verification into deploy pipeline
- fix(ui): coordinator timeline UI bugs - varied step labels, outcome-spec markdown, work-plan topology thumbnail
- fix: stamp server-side UTC timestamp on every RunEvent
- fix(rai): stop raw JSON responses from leaking into the RAI verdict rationale
- fix(mcp-harness): stop applying target-guard URL validation to stdio transport; document quickstart contract
- fix(harness): route execution through discoverable skills first
- fix(harness): use canonical 'execute' tool alias instead of non-canonical 'bash'
- fix(ui-harness): enforce scoped approval execution

### Added

- feat(api-harness): drop fixed persona scenarios and curated subcommands for a dynamic, curl+OpenAPI-guided driver
- feat(harness): add persistent learnings + persona catalog memory
- feat(harness): add selectable orchestration agent
- feat(api-harness): add Copilot CLI skill
- feat: add MCP protocol test harness
- feat(ui-harness): add Playwright persona evidence driver
- feat(api-harness): support request-changes gate decisions

### Changed

- chore(release): bump version to v0.9.54

### Docs

- docs(mcp-harness): add /mcp endpoint suffix and OAuth token requirement to quickstart
- docs(harness): add target resolution + usage examples; scribe: merge fleet-mode wave decisions
- docs(harness): sharpen skill triggers
- docs(ui-harness): add Copilot CLI skill contract
- docs(mcp-harness): add Copilot CLI skill

### Other

- harness: delete orphaned approval-gate library (approvals.mjs/approval-judge.mjs)
- harness: generalize goal-statement resolution/injection out of persona-core files
- harness: record learning for drive.mjs deletion pivot
- harness: delete drive.mjs, replace with a documented curl+YAML-spec contract
- Refine Oracle core tone
- Generalize Oracle core brief
- Remove Oracle journey hints
- Thin Oracle adapter guidance
- Remove Oracle adapter tool references
- Make Oracle adapter spec-driven
- harness: drive.mjs spec prefers YAML OpenAPI by default; reinforce spec-first resolution in PersonaActor
- Add Oracle persona brief
- Enrich API OpenAPI metadata
- harness: dispatch persona driving to a fresh PersonaActor sub-agent
- harness: verify drive.mjs against Tank's live /openapi/v1.json, document operationId gap
- harness: add spec-resolved operationId dynamic client to drive.mjs call
- Add OpenAPI spec generation to Agentweaver.Api for api-harness
- harness-judge: agent-native default judge via Judge subagent (tools: [])
- Add generate-blueprint and validate-blueprint tools to API harness driver
- Add combined harness launcher skill
- Migrate persona harness to API harness

## [v0.9.53] - 2026-07-14

### Fixed

- fix(release): build image when retag source tag is absent from ACR
- fix(a2a): emit structured terminal on pod turn abort to avoid bare "Received: None" (#267)
- Fix #240: adopt durably-completed children on coordinator recovery instead of re-running them
- Fix #317: re-check durable event log before declaring agent_stall_timeout
- Fix false-positive stall: require agent.turn.end before A2A turn success
- fix(tests): derive DataMigratorTests fixture schema from real SqliteDb

### Added

- feat(harness): add shared judge package
- feat(personas): add shared persona briefs package

### Changed

- chore(release): bump version to v0.9.53

### Docs

- docs(ui-harness): add Evidence integrity & governance to Harness Agent (Seraph 4 & 5)
- docs(api-harness): clarify Finding 1 scope — allowlist is target-deployment, not in-sandbox action denial
- docs(mcp-harness): clarify Finding 1 is a host/environment allowlist, not a sandboxed-action denier
- docs(api-harness): fold Seraph Pre-Implementation security review into spec
- docs(mcp-harness): fold Seraph blocking security findings (target-host allowlist + prompt-injection threat model)
- docs(ui-harness): fold in Seraph blocking security findings
- docs(mcp-harness): mark request-changes as a hard blocking prerequisite for deep gate-review
- docs(shared): align Harness spec with canonical join-key and reproManifest
- docs(api-harness): distinguish frustration not_assessed from none
- docs(api-harness): close 5 blocking gaps from rubber-duck review
- docs(mcp-harness): split frustration 'none' from 'not_assessed' to fix aggregate math
- docs(mcp-harness): add required-capabilities contract as smoke/acceptance regression tripwire
- docs(shared): add free-text Harness invocation mode, clarify sync dispatch, fix frustration schema
- docs(shared): correct Harness Agent division of labor
- docs(shared): add Harness Agent top-level orchestrator spec
- docs(mcp-harness): driver discovers tool surface via live tools/list, never hardcodes tool names
- docs(mcp-harness): reconcile shared-package naming with API/UI specs
- docs(ui-harness): fix remaining stale scripts/persona-harness refs missed by earlier rename pass
- docs(shared): add Combined Launcher Skill spec subsection
- docs(api-harness): spec Copilot CLI skill (two-file discoverable design)
- docs(api-harness): apply three amendments to API test harness plan
- docs(mcp-harness): add GitHub Copilot CLI Skill spec section
- docs(ui-harness): add GitHub Copilot CLI Skill spec section
- docs: apply harness rename convention ({surface}-persona-harness -> {surface}-harness)
- docs(ui-harness): persona reviews/approves gates like a real operator (functional, not quality-grading)
- docs: add API test harness plan as sibling to UI/MCP harness specs
- docs(mcp): add persona-realistic gate review (validate before approving, request-changes) with scope boundary
- docs(ui-harness): state driver-acts-as-persona-only boundary (no diagnosis/interpretation)
- docs(ui-harness): parallel/autonomous driver model + explicit 4-source judge evidence
- docs(mcp): state driver-vs-judge boundary explicitly — driver simulates persona, never diagnoses
- docs(ui-harness): self-improvement loop, LLM-generated personas, frustration verdict dimension
- docs(mcp): add parallel/headless driver model + broaden judge evidence sources (AppInsights/kubectl)
- docs(mcp): bake in self-improvement loop framing, LLM-generated personas, frustration verdict dimension
- docs(ui-harness): add Cross-Harness Shared Layer (shared personas + one judge core)
- docs(mcp): add MCP test harness design spec (epic #295)
- docs: add parallel Playwright UI test harness design spec (#1 UI track)

### Tests

- test(coordinator): regression coverage for stale ineligible_subtasks redirect re-arm

### Other

- Preserve established requirements across outcome-spec revisions
- tank: history entry for persona-harness judge-gated approval driving (#1)
- persona-harness: drive approval gates via the API after judging (#1)

## [v0.9.52] - 2026-07-14

### Fixed

- fix: preserve coordinator assembly files after completion
- fix(release): push tags before GitHub release
- fix: skip malformed verdict findings
- fix: judge-automation round 2 - full transcript evidence + verdict schema validation

### Added

- feat: assemble dynamic persona brief prompts

### Changed

- chore(release): bump version to v0.9.52
- chore: ignore .worktrees/ (git worktree checkouts, not repo content)
- chore(harness): WIP safety checkpoint for persona-harness (untracked -> git-recoverable)

## [v0.9.51] - 2026-07-14

### Fixed

- fix(ui): declutter Human Review gate, add warning-tinted background
- fix: v0.9.50-rc1 batch - path-traversal hardening, pagination, notifications, backlog metrics (#261 #108 #312 #313 #208 #247 #200 #310 #302 #246 #282 #311)
- fix: batch v0.9.49-rc1 candidate - steering scope, assembly recovery, edge occlusion, scratch dirs, approval scoping (#227 #309 #308 #306 #224 #216 #278 #303)
- fix(k8s): right-size agent-host pod requests to stop MemoryPressure eviction churn (#307)
- fix(preview): retain private-session port attribution
- fix: Kata-aware bwrap passthrough (#269), steering revision-child branch mismatch (#305)
- Fix gate-scoped activity UX
- fix(coordinator-ui): UI polish for #249/#262/#277/#279
- Fix coordinator message timeline seeding
- fix(agent-runtime): only advertise team-coordination tools when registered (#268)
- fix(coordinator-ui): stale coordinator_status badge on terminal/cancelled runs (#304)
- fix(#269): install bubblewrap in AgentHost image
- fix(#256): default Sandbox:PodLocalWorkspace:ImplementationEnabled to false
- fix: fence timed-out shell and drain A2A turns (#254)
- fix: harden A2A transport recovery (#259 #267 #219)
- fix(coordinator): recover assembly RAI gates (#232 #209)
- fix: enable pod-local implementation execution (#243 #252 #253 #255 #300)
- fix(runtime): batch execution agent tool turns
- fix(web): bound session cache and stop child polling
- fix: scope tool denials to owning run (#281)
- fix(web): live-update Changes/Files, session switch cache, coordinator narration, tool status (#280, #287, #286, #299)
- fix(ui): restore approval and coordinator indicators (#274 #275 #276)
- fix: scope tool approvals to owning run (#281)
- fix(web): correct AppCard as-prop typing in TileGrid
- fix(aks): run frontend dist prebuild synchronously before parallel image builds
- fix(aks): remove frontend node_modules before ACR build
- fix(aks): harden frontend auth and image waits
- fix(frontend): prebuild dist before ACR build
- fix(frontend): use ACR secret build arg for npm auth
- fix(web): enable BuildKit Dockerfile frontend (#265)
- Fix Copilot permission rejection decisions
- Fix FlowPage agent cards rendering as unbounded raw text dumps
- Fix preview timeout cancellation handling
- Fix #257: structured declared_output_paths for coordinator conflict detection
- Fix #260: bounded auto-retry with backoff for retryable infra failures
- Fix #258: allow preview lifecycle tools through sandbox policy backend
- Fix topology node click closing panel instead of zooming
- fix(RunTimeline): stop clamping long activity step headers to one line
- fix(web): use onToggle not onOpenChange for FluentUI Accordion; support 1JS npm auth via BuildKit secret
- fix(deploy): use --legacy-peer-deps in frontend Dockerfile npm ci
- fix(timeline): open activity steps as they stream in, not just at first mount
- fix(#255): collapse package caches into sandbox home
- fix(#253): preserve repos in pruned paths (Seraph 5th re-review)
- fix(#253): bound nested-repo scan with cancellation + ignored-path pruning (Seraph 4th re-review)
- fix(#253): discover nested-repo gitlinks from filesystem, flatten deepest-first, reject residual gitlinks (Seraph 2nd re-review)
- fix(#254): worker idle backstop looser than in-pod 15m idle + eliminate per-update timer leak (Seraph re-review)
- fix: harden agent turn resiliency
- fix: make agent turns resilient to long shell commands
- Fix pod-local cache and preview path propagation
- Fix assembly gates on SMB workspaces
- fix(tests): update 13 failing tests for UI coherence migration

### Added

- feat: migrate workflow editor/graph components off copilot-fluent-system kit
- feat: migrate chat/agent thread components off copilot-fluent-system kit
- feat(board): migrate BOARD cluster off copilot-fluent-system
- feat(ui): migrate project-core pages to shared UI kit
- feat: migrate ops/system pages to shared UI kit
- feat: migrate shell cluster pages to shared UI kit
- feat: nestable AgentStep children + Composer readOnly mode
- feat: rebuild copilot surface mirroring @1js component anatomy natively
- feat: replace @1js copilot with native FluentUI chat surface
- feat: agentic progress components and @1js/fluentai copilot wiring

### Changed

- chore(release): bump version to v0.9.51
- chore(release): normalize VERSION to strict semver (0.9.50-rc1 -> 0.9.50)
- chore: bump VERSION to 0.9.45-rc1
- chore: bump VERSION to 0.9.44-rc1
- chore: bump version to v0.9.43-rc1 for Wave 2 runtime-resilience deploy
- chore: bump version to v0.9.42-rc1 for Wave 1 coordinator-ui/runtime-resilience deploy
- chore: bump VERSION to 0.9.41-rc1
- refactor(frontend): switch AKS npm auth to credprovider
- chore: bump VERSION to 0.9.40-rc1
- chore(release): bump VERSION to 0.9.37-rc1

### Docs

- docs: add hard rule - never approve preview/review gate without live-testing preview URL first
- docs: update e2e harness plan with v0.9.50-rc1 release milestone
- docs: add staging environment recovery/recreation authority to E2E plan
- docs: add operating rules to E2E harness plan
- docs: add continuous E2E harness + triage plan
- docs: explain pod-local write-back and caches (#253, #255)

### Tests

- test(e2e): make Playwright baseURL overridable via AKS_BASE_URL
- Test #264 reject wire payload serialization
- test(#255): restore npm sandbox E2E after Seraph review

### Other

- Bump version to 0.9.50-rc1
- Bump version to 0.9.49-rc1
- Bump version to 0.9.47-rc1
- Bump version to 0.9.46-rc1
- Surface failed-tool warning in collapsed clusters and failure reason/retryability on failed runs
- Polish Board intake toolbar and Dashboard stat tiles
- Replace Changes/Files modal with full-width split-view slide-in
- Add tile-grid views for Projects/Team and fix Start-task overlap
- Topology: cinematic zoom, auto-orientation, content-driven node sizing, tighter staircase spacing
- Implement pod-local implementation writeback (#253)
- Generalize pod-local execution workspaces
- Document pod-local assembly execution
- CoordinatorRun: redesign topology graph — compact pills, staircase layout, toolbar
- CoordinatorRun: flatten run tree into an aligned single-level list
- CoordinatorRun: run-wide chips, single thread, shared AI-credits, tree + header polish
- Ship v0.9.36-rc1: model catalog (#238), nested-app preview (#244), observability traces + token-breakdown (#245, #248)
- CoordinatorRun: enlarge run-summary chips and clarify the Plan chip count
- CoordinatorRun: collapse "Used N tools" groups by default in the Timeline
- CoordinatorRun: unified Messages surface (task-first tree, interleaved CLI-style activity, pinned composer)
- CoordinatorRun round 2: real native chat + agentic, rich tree, live minimap, declutter
- Complete cross-page coherence: shared UI kit + CoordinatorRun/Console reworks
- Migrate RUN/STEER/COORDINATOR panels off copilot-fluent-system
- Migrate dashboard/runs/badge components off copilot-fluent-system
- Migrate FILE/ARTIFACT/VIEWER components off copilot-fluent-system
- migrate FlowPage, ObservabilityRedirectPage, SignInPage onto shared UI kit
- migrate squad pages to shared UI kit
- Migrate web app to native FluentUI with Copilot (Day) theme

## [v0.9.35-rc1] - 2026-07-11

### Fixed

- Fix #239, #241, #243: coordinator assembly-phase resilience (v0.9.35-rc1)

## [v0.9.34-rc1] - 2026-07-11

### Fixed

- Fix #238: honor run-level model pin for ALL subtasks

## [v0.9.33-rc1] - 2026-07-11

### Fixed

- fix(coordinator): reviewer worktree fidelity (#236) + git-CLI worktree provisioning (#237)

## [v0.9.32-rc1] - 2026-07-11

### Fixed

- fix(aks): self-grant KV Secrets Officer + retry on RBAC propagation (#234)
- fix(coordinator): roster/breadth-aware outcome-spec drafter (#235)

### Changed

- chore(release): v0.9.32-rc1 (#235 outcome-spec breadth + #234 KV-RBAC)

## [v0.9.31-rc1] - 2026-07-11

### Fixed

- fix(coordinator): degrade single-eligible lockout to same-author fresh re-dispatch (#233)

### Changed

- chore(release): v0.9.31-rc1 (#233 single-eligible lockout degrade)

## [v0.9.30-rc1] - 2026-07-11

### Fixed

- fix(coordinator): reframe decomposition from minimality to outcome-completeness (#225)
- fix(rai): structured VERDICT sentinel contract for collective-assembly RAI gate (#231)
- fix(coordinator,sandbox): autopilot outcome-spec auto-confirm (#228) + transient k8s pod-claim retry (#230)

### Changed

- chore(release): v0.9.30-rc1 (#231 RAI sentinel + #225 outcome-complete decomposition + #226 steering test)

### Docs

- docs: sync decomposition (outcome-completeness) + RAI verdict contract (#225, #231)

### Tests

- test(coordinator): deterministic E2E coverage for mid-run steering queue->drain (#226)

## [v0.9.28-rc1] - 2026-07-11

### Other

- Ship v0.9.28-rc1: assembly-steering wave (#223 + cap-drop + #226)

## [v0.9.27-rc1] - 2026-07-11

### Fixed

- Fix #222: scope-independent worktree staging (stop dropping subdirectory deliverables)

## [v0.9.26-rc1] - 2026-07-11

### Fixed

- fix(pod-per-run): propagate AutoApproveTools to AgentHost via /configure (#221)

## [v0.9.25-rc1] - 2026-07-10

### Fixed

- fix(coordinator): pod-aware assembly-gate resumability probe (#220)

## [v0.9.24-rc1] - 2026-07-10

### Fixed

- Fix #218: coordinator lease heartbeat, ownership fencing, and per-project integration-build lock

### Other

- Bump version to 0.9.24-rc1 (#218 lease-heartbeat fix)
- Harden #218 lease heartbeat: make transient per-tick errors non-fatal

## [v0.9.23-rc1] - 2026-07-10

### Fixed

- fix(#217): remove app-side capacity/quota scheduler; let Kubernetes own pod scheduling

### Changed

- chore(release): v0.9.23-rc1 (#217 remove app-side capacity gate)

### Docs

- docs(#217): sync sandbox/coordinator/quota docs to K8s-owned scheduling

## [v0.9.22-rc1] - 2026-07-10

### Fixed

- fix(coordinator): deliver tool-approval gate live via heartbeat; guard child stall on pending approval (#212)

## [v0.9.21-rc1] - 2026-07-10

### Fixed

- fix(#196): forward tool-approval decisions to AgentHost pod in pod-per-run mode

### Docs

- docs(reliability): document FinalScribe + reaper creation-grace config keys (#207,#210)

## [v0.9.20-rc1] - 2026-07-10

### Fixed

- fix(coordinator,sandbox): bound final-Scribe recovery (#207) + reaper creation-grace (#210)
- Fix Azure Fluent MCP fidelity

### Other

- Ship Azure Fluent system
- Implement Azure Fluent system redesign
- Add self-contained Agent Fluent UI Kit

## [v0.9.19-rc1] - 2026-07-10

### Other

- v0.9.19-rc1: dependency-base propagation fix + UI fixes

## [v0.9.18-rc1] - 2026-07-10

### Other

- v0.9.18-rc1: decider-owned assembly steering routing (Fix-B) + worker RequireMtls drift fix

## [v0.9.17-rc1] - 2026-07-09

### Added

- feat(coordinator): resilient assembly-review loop (v0.9.17-rc1)

## [v0.9.16-rc1] - 2026-07-09

### Fixed

- fix(preview): discover app port via /proc/net/tcp{,6}; legible observe failures

## [v0.9.15-rc1] - 2026-07-09

### Fixed

- fix(preview): remove architecturally-invalid API-side sandbox reachability probe

## [v0.9.14-rc1] - 2026-07-09

### Fixed

- fix(preview): guarantee pod-IP-reachable preview URL via TCP forwarder + dynamic ports (v0.9.14-rc1)

### Docs

- docs(learnings): mark STEER1 resolved (live-proven v0.9.13-rc1); log in-place-resume follow-up

## [v0.9.13-rc1] - 2026-07-09

### Fixed

- fix(steering): reliable in-place revision recovery (v0.9.13-rc1)

## [v0.9.12-rc1] - 2026-07-09

### Added

- feat(steering+preview): unified autonomous steering + decoupled live preview (v0.9.12-rc1)

## [v0.9.11-rc1] - 2026-07-08

### Added

- feat(preview): enforce first-class live-preview provisioning in software-delivery pipeline

### Other

- Track A: durable terminal assembly events + build-test pod retention (v0.9.10-rc1)
- Run page UX fixes: deterministic tree order, outcome-spec rendering, RAI verdict cleanup, visible revision cycle

## [v0.9.8-rc1] - 2026-07-08

### Other

- Bind assembly Build & Test to a routable coordinator sandbox pod (pod-per-run)

## [v0.9.7-rc1] - 2026-07-08

### Fixed

- fix(preview-path): git in API image, RAI-before-BuildTest gate ordering, run-tree review/preview UX

## [v0.9.6-rc1] - 2026-07-08

### Fixed

- fix(runtime): inactivity watchdog for hung streaming agent turns
- fix(coordinator): root-cause fixes for stuck/failed orchestrations
- fix(e2e): point screenshot config baseURL at live staging host

### Docs

- docs(screenshots): add data-generation prerequisites so pages arent empty
- docs(screenshots): reconcile plan+spec to real app pages
- docs: add screenshot plan coverage for v0.9.5 pages
- docs: cover v0.9.5 staging wave
- docs: regenerate MCP tool index (88 -> 90 tools; skill_import wording)

### Other

- Block teamless orchestration + fix run-page review/topology/RAI UX + preview-from-build-test

## [v0.9.5] - 2026-07-07

### Fixed

- Fix coordinator run header wrapping
- Fix coordinator run state and review UX

### Changed

- chore(release): bump version 0.9.5

### Other

- Harden coordinator run and console experience
- Refine coordinator run action toolbar
- Checkpoint coordinator run polish

## [v0.9.4] - 2026-07-07

### Changed

- chore(release): bump version 0.9.4

### Other

- Polish board orchestration layout
- Polish dashboard overview
- Update overview page content
- Add product overview
- Move playwright-cli skill from .claude to .copilot

## [v0.9.3] - 2026-07-06

### Changed

- chore(release): bump version 0.9.3

### Other

- Unify and delight Create Project; fix Copilot runtime provisioning
- Ungate blueprint tabs in Projects create dialogs
- Polish console and projects UI

## [v0.9.2] - 2026-07-06

### Fixed

- fix(run-page): open the review panel when clicking "Review now" (was a no-op)
- fix(tool-approval): route approvals to the owning child subtask run id (recurrence of #196)
- fix(skills): show agent role in assignment UI; fix folder drag-drop import (ERR_ACCESS_DENIED)
- fix(dev): localhost sign-in wiring — port 5173, CORS AllowCredentials, GITHUB_AUTHORIZE_URL call-sites

### Added

- feat(run-page): responsive DAG reflow, wider session log, unhide Message coordinator, collapse low-signal events by default
- feat(orchestrations): stop and delete orchestrations from the list page
- feat(team): show assigned skills on agent detail panel

### Changed

- chore(release): bump version to 0.9.2
- chore(dev): Impeccable live-mode gating (DEV-only focus guard + inert z-index/pointer-events shims)
- refactor(metrics): extract usage-run loaders with postgres/sqlite dual path in DashboardReadService

### Docs

- docs: document v0.9.2 orchestration stop/delete + tool-approval routing + run-page UX + skills

## [v0.9.1] - 2026-07-06

### Fixed

- fix(web): unify /api base-path so GitHub sign-in works on staging and localhost
- fix(dev): align local frontend to :5173 + probe /health
- fix(web): settle completed tool calls + calm CLI-style tool rows
- fix(skills): block SSRF in skill import + review findings
- fix(web): orientation-aware SpineEdge + centered TB dag layout for coordinator graph
- fix(coordinator): consume live send safely
- fix(web): reuse shared ArtifactBrowser in session panel Changes/Files tabs

### Added

- feat(console): redesign /console as a true terminal UI (TUI)

### Changed

- chore: bump version to 0.9.1 (sign-in /api base-path fix)
- chore: bump version to 0.9.0

### Docs

- docs: v0.9.0 wave - console TUI, skills UX, artifact browser, graph, tool rows, live send

### Other

- Improve skill acquisition UX

## [v0.8.0] - 2026-07-06

### Fixed

- fix(timeline): resolve child_approval case shadowing from #50/#196 merge
- Fix skill catalog review findings: child-run injection, zip-slip hardening, stale-dir cleanup
- fix(coordinator): propagate child subtask outputs via shared worktree branches (#197)
- fix(web): replace remaining decorative glyphs
- fix(coordinator): surface steering events cross-replica so operator messages aren't lost
- fix(tool-approval): route child-subtask approvals to the owning child run id (#196)
- fix(run-page): compact header, flat run tree, denser session pane, clearer tool-call display, vertical graph
- fix(web): replace emoji/dingbats with FluentUI icons (constitution VIII)
- Fix build test gate terminal routing
- Fix build test workflow test imports
- Fix workflow save reload filtering
- Fix stale tool approval resolution states
- Fix coordinator graph viewport
- Fix overview attention links
- fix(coordinator): omit platform gates from workflow decomposition
- Fix child tool approval routing

### Added

- feat(skills): per-project skill catalog, acquisition, assignment + progressive disclosure (#51, #56)
- feat(mcp-integrations): add browser chat control console (#50)
- feat: harden blueprint and workflow generation

### Changed

- chore(release): bump VERSION to 0.8.0
- refactor(mcp-integrations): conversational TUI over reused coordinator machinery (#50)

### Docs

- docs: document v0.8.0 features
- docs: update v0.7.12 UI refinements

### Other

- Render agent/LLM/tool hierarchy in transaction trace (#166)
- Add preview-first delivery guidance
- Make blueprint generation gate-aware
- Add ReviewToTerminalAdapter stub to FakeWiring test double
- Add outcome plan phase to run console
- Rename outcome spec UI to outcome plan
- Redesign run page operator console
- Implement build test workflow gate
- Remove review policies and deprecate single-run starts
- Update catalog workflows for authored gates
- Remove coordinator agents summary
- Record coordinator cleanup decision
- Open assembly execution in session panel
- Remove dead single-run and review policy UI
- Make assembly review gates workflow-authored
- Surface coordinator session activity
- Polish new project dialogs
- Polish blueprint picker tabs and cards
- Scribe: log v0.7.12 iteration wave 2, merge decisions, archive old entries

## [v0.7.12] - 2026-07-05

### Fixed

- fix(web): keep outcome spec gate visible
- Fix stale assembly blocked latch
- fix(observability): emit App Insights model-turn telemetry
- Fix assemble-ready run artifact tabs
- Fix workflow selection empty response diagnostics
- fix(coordinator): capture final-message-only selection responses so no-delta output is not lost (#183)
- fix(coordinator): make workflow-selection turn tool-less and harden parse (#183)
- fix(web): disable tool-approval card when server resolves/expires it (#174)
- Fix App Insights workspace wiring
- Fix orchestration run remount on navigation
- fix(runs): notify clients when tool approval expires/resolves (#174)
- fix(workflows): reload freshly saved workflow so it becomes selectable (#175)
- fix(coordinator): flatten session tree, color-code status glyphs, show selected workflow
- fix(agent-host): remove duplicate build+runtime stages left by edit (#172)
- fix(workflows): use report_outcome for agent findings instead of writing report files (#170)
- fix(agent-host): align sandbox image with hosted-copilot-aks-sandbox reference (#171)
- fix(sandbox): default outbound network to enabled for new projects
- fix(network): open sandbox egress to RFC1918 ranges, keep IMDS blocked (#171)
- fix(agent-host): restore dev tools (Node 20, Python3, sudo) in AgentHost image (#171)
- fix(binder): bind review-policy revise loop to workflow start node so catalog build-test step survives (#168)
- fix(workspace): enforce integration branch git contract between subtasks (#169)
- fix(workflows): built-in catalog workflows always take precedence over stale project copies (#168)
- fix(web): remove Expand pipeline button from agent step cards (#162)
- fix(sandbox): persist sandbox info to DB so preview button survives stream eviction (#113)
- fix(dashboard): correct metric card titles and subtitles (#145)
- fix(workspace): make file tree panel scrollable (#149)
- fix(workflows): materialize default workflow so Workspace shows the dir
- fix(coordinator): honor explicit and active workflow on manual runs
- fix(web): add workflow dropdown to global Start task dialog
- fix(web): show all valid workflows in Start task dropdown
- fix(observability): use OTel App\* table names and column mappings
- fix(web): add coordinator.assembly_review_preserved to EventType union
- fix(preview): pre-fill preview port from agent declaration or default to 8080 (#127)
- fix(preview): gate preview button on sandbox pod Bound phase (#126)
- fix(web): show 'review still available' instead of kicking the operator out on failure
- fix(orchestration): keep the review gate open when a coordinator run fails
- fix(sandbox): real TCP liveness check for preview and workflow-gated capability injection (#146)
- fix(coordinator): harden workflow selection parser and log raw response on failure (#151)
- Fix AppInsights metrics client initialization
- fix(orchestration): don't inject browser-preview mandate into the Coordinator run
- fix(observability): filter traces by message text in final union, not just in CTE
- fix(orchestration): guard git integration merge so only one pod assembles a run
- fix(orchestration): treat in_review with a pending review gate as intentional, not orphaned
- fix(orchestration): stop reconciler infinite loop for in_review runs with active assembly
- fix(observability): propagate run_id as telemetry dimension and include traces table in run trace query
- fix(observability): add APPLICATIONINSIGHTS_WORKSPACE_ID to api and worker deployments
- fix(sandbox): strengthen coordinator preview nudge to be explicit and assertive
- fix: exempt /api/version from GitHubOrgAuthorizationMiddleware
- Fix preview start guidance and liveness checks
- Fix AppInsights run trace correlation
- fix(diagnostics): key_vault health check uses IConfiguration, not ISecretStore
- Fix metrics card typography hierarchy
- fix: exempt /api/version from auth middleware
- fix(scripts): fix az acr import flag --registry -> --name
- fix(scripts): make install scripts fully idempotent
- fix: provision and mount mcp-api-key so Auth:ApiKey is set in production
- fix(auth): increase MCP OAuth access token TTL from 15m to 8h
- fix(metrics): lazy-initialize LogsQueryClient to prevent constructor crash

### Added

- feat(web): redesign coordinator graph UI (spine edges, card accents, minimap, zoom, session tree)
- feat(dag): remove column labels, smaller minimap, full-height panel respects left nav
- feat(dag): full-width bottom slide-in panel with session tree
- feat(dag): slide-in agent session panel with Messages/Changes/Files tabs (#173)
- feat(dag): minimap, click-to-open, pod tooltip, status top-left, no view-run button
- feat(dag): restore React Flow DAG with virtual column alignment
- feat(agent-host): full dev toolchain, sandbox manifest injection, security maintenance (#172)
- feat(web): show selected workflow + selection reason in Coordinator card (#160)
- feat(web): step detail slide-in panel on click (#161)
- feat(web): redesign orchestration page pipeline layout (#160)
- feat(coordinator): emit and persist workflow selection reasoning (#167)
- feat(web): Artifacts link in Coordinator card opens workspace file browser (#165)
- feat(web): OutcomeSpec as slide-in panel via button under Coordinator card (#164)
- feat(web): show Active badge for default workflow in task start dropdown
- feat(web): redesign coordinator steering as chat side panel (#163)
- feat(workflows): decouple trigger type from workflow definitions (#158)
- feat(workflows): teach generator and binder about the build-test gate (#157)
- feat(workflows): add mandatory build-test gate before human review (#157)

### Changed

- chore(observability): compact overview metric tiles
- chore(release): bump VERSION to 0.7.11 (workflow-selection + decompose identity fix)
- chore(release): bump VERSION to 0.7.10 (6-fix staging bundle: #174 #175 #176 #179 #180/181 #183)
- chore: bump VERSION to 0.7.9 for staging redeploy (graph UI fixes)
- chore: bump VERSION to 0.7.6
- chore: bump VERSION to 0.7.5
- chore: bump VERSION to 0.7.4
- chore: bump VERSION to 0.7.3
- chore(release): bump version to 0.7.2
- chore(workflows): rename "Default Run Workflow" to "Generic Workflow"
- chore(deploy): prefer VERSION file over git SHA for IMAGE_TAG default
- chore: bump version to 0.7.1
- refactor(workflows): remove standalone code-review workflow and harden selection parser
- chore: bump version to 0.7.0

### Docs

- docs: regenerate generated MCP references
- docs: update v0.7.11 experiences and telemetry
- docs: add repository blueprint suggestions
- docs(blueprints): document blueprint-match vs workflow-gen criteria + fix under-selection (#176)
- docs(coordinator): flat session tree, color-coded status glyphs, selected-workflow header badge
- docs: regenerate docs after workflow trigger removal (#158)
- docs: sync specs and docs to trigger-decoupling design (#158)
- docs(workflows): drop stale code-review workflow references from API.md and templates

### Tests

- test(web): cover outcome spec gate states

### Other

- Bump VERSION to 0.7.12
- Update project dialog tests
- Share new project dialog shell
- Unify blueprint panel tabs
- Align reconciler recovery test with harness failure mode
- Cover project creation blueprint flows
- Redesign new project dialogs
- Add GitHub repo blueprint suggestions
- Redesign overview dashboard
- Remove project relink feature
- Render session messages as sanitized markdown
- Clarify relink workspace boundary in UI
- Secure project relink path validation
- Redesign agent session messages
- Accept in-worktree absolute artifact paths
- Publish child run ids after launch
- Fail closed on AgentHost installation token scope
- Reject installation scope for Copilot model turns
- Thread submitting user into review model turns
- Thread user identity into selection and decompose
- revert(workflows): drop catalog-precedence change from #168
- Make build-test preview server agentic instead of static port lookup
- Keep coordinator pending during review gate
- Improve agent identity in run details
- Make workspace file tree scrollable
- Harden az acr build on Windows

## [v0.7.0] - 2026-07-01

### Fixed

- fix(workflow): pass submitting user ID to Scribe agent turn (#141)
- fix: add missing Postgres migration for AssemblyReviews table
- fix(observability): address rubber-duck review findings
- fix(observability): address rubber-duck findings — meter wiring, secret mapping, metric completeness
- fix(#95): disable Confirm/Commit buttons immediately on click to prevent double-submit
- fix(workspace): canonicalize per-project workspace root in path resolution (#90 #94)
- fix(workspace): use ref-aware API in board import picker + send ref on import (#90)
- fix(coordinator): fix double-resume destroy race for restart-resume (#88)
- fix(coordinator): persist review approval before gate clear + durable scribe spawn (#92 #93)
- fix(mcp): surface memory tool API errors (#91)
- fix(coordinator): add retry+lock-cleanup to assembly branch integration (#89)
- fix(dashboard): wire Range dropdown to both leaderboard and usage panels (#45)
- fix(metrics): accept from/to range params in dashboard endpoint (#45)
- fix(coordinator): harden restart-resume recovery and improve interrupted UX (#88)
- fix(coordinator): make assembly_blocked recoverable — steering Send/Redirect/Amend resume coordinator dispatch (#86)
- fix(coordinator): auto-resolve integration branch merge conflicts and emit event (#85)
- fix(rai): remove per-child-run RAI sub-launch — RAI runs once at coordinator assembly level (#84)
- fix(orchestration-ui): confirm outcome spec immediately updates UI (#82)
- fix(orchestration-ui): show pod chip only when execution pod is assigned (#77)
- fix(orchestration-ui): stop polling after 404 on coordinator runs for outcome-spec and work-plan (#76)
- fix(orchestration-runs): harden dispatch lock retry and stall cascade (#78)
- fix(orchestration-ui): suppress expected 404s for outcome-spec and work-plan (#76)
- Fix cleared remediation blockers
- fix(coordinator): isolate child run worktrees
- fix(workflows): preserve generated schedule triggers
- fix(workflows): preserve target repository in generation
- fix: show coordinator subtask run pills
- fix: align dashboard leaderboard columns
- fix(runs): persist human gates across replicas
- fix(runs): persist control state across replicas
- fix(review-merge): defer assembly review decisions across replicas
- fix(review-merge): defer review decisions across replicas
- fix(orchestration-runs): stop coordinator children across replicas
- fix(identity-access): persist device flow state across replicas (#34)
- fix(observability-operations): list preview sessions across replicas (#36)
- fix(workflows-automation): refresh definition registries across replicas (#38)
- fix(review-merge): serialize repository merges across replicas (#39)
- fix(identity-access): serialize GitHub token refresh across replicas (#40)
- fix(api): persist execution pod name to shared store for cross-replica graph display
- fix(api): share run-stream events across replicas via Postgres + read-through refresh
- fix(web): increase coordinator DAG node spacing so fan-out cards don't overlap
- fix(agenthost): deliver per-run worktree path to warm pods via /configure
- fix(k8s): allow kata sandbox egress to Azure-CDN Copilot endpoints
- fix: add agenthost-egress-allowlist NetworkPolicy for Copilot CLI connectivity
- fix: API resolves GitHub token and passes to /configure — remove pod KV dependency
- fix: sandbox egress — allow KV, Entra ID, and Copilot API FQDNs
- fix: API egress to agent-host port 8088 + healthz always 200
- fix: SandboxWarmPool updateStrategy OnReplenish — prevent rotation race
- fix: remove spec.env from AgentHost SandboxClaim — restore warm pool assignment
- fix: AgentHost dual-stack bind, lease TTL > probe timeout, api sessionAffinity
- fix: routing.md signals from Role.Responsibilities, not hardcoded buckets
- fix: SandboxClaim v1beta1 warmPoolRef — revert a731f70 body to v0.5.0 schema
- fix: routing.md per-agent signals, auto-sync on team creation, bigger capture form
- fix: autopilot gate on spec auto-confirm, SSE reconnect, cluster diagnostics UI
- fix: use correct v1beta1 SandboxClaim spec fields (sandboxTemplateRef + warmpool)
- fix: add DeferredDecisions migration to correct Postgres migrations project
- fix: deferred decision inbox for cross-replica coordinator confirm
- fix: pass submitting user to coordinator AI agent SetupAsync calls
- fix: don't inject AgentHost\_\_KeyVaultUri via SandboxClaim env
- fix: initialize AgentHost Key Vault URI parsing
- fix: default AgentHost Key Vault URI for AKS deploy
- fix: guard against unsubstituted AGENTHOST_KEYVAULT_URI placeholder
- fix: copy Copilot native runtime into AgentHost image
- fix: restore AgentHost runtime-specific assets
- fix: publish AgentHost with linux-x64 runtime assets
- Fix 'Break into tasks' 400: pass run_id when file_path is null
- fix: add agent-host federated credential to setup script, remove mcp-api-key
- fix: remove mcp-api-key, fix resourcequotas RBAC
- fix: update ClusterPage test to match new DTO shape
- fix: multi-replica coordinator resume, cluster page types, button UX, log timestamps
- fix: strip coordinator sub-run suffixes in RunStoreSubmittingUserResolver
- fix(rbac): add list verb to sandboxclaims for reaper sweep
- fix(agent-host): copy Directory.Build.props in Dockerfile to fix NETSDK1152
- fix(web): add subtask.pending_capacity to EventType union
- fix: update DiagnosticsEndpointTests for RecordTickOutcome automationName param
- fix: release orphaned AgentHost pods on coordinator failure + sync user KV token to CSI SPC on sign-in
- fix: write user-scoped token at OAuth sign-in for pod shared store
- fix(recovery): GetLatestCheckpoint now delegates to the active checkpoint store
- fix(ui): friendly error + disabled buttons on run_not_active (interrupted run)
- fix(ui): show workflow picker when at least 1 manual workflow exists
- fix(build): pre-download Copilot CLI binary + CopilotSkipCliDownload=true in /src
- fix(build): use --msbuild-arg to pass ErrorOnDuplicatePublishOutputFiles to EF bundle
- fix(build): place Directory.Build.props at filesystem root for EF bundle
- fix(build): place Directory.Build.props at /tmp for EF bundle temp project
- fix(build): set MSBUILDADDITIONALCOMMANDLINEARGS for EF bundle temp project
- fix(build): copy Directory.Build.props into Docker build context
- fix(build): suppress Copilot CLI duplicate via Directory.Build.props
- fix(build): pass ErrorOnDuplicatePublishOutputFiles to EF migrations bundle
- fix(build): suppress duplicate Copilot CLI binary in API publish output
- fix(preview): keep preview button visible until environment cleanup
- fix(agent-runtime): upgrade Copilot SDK beta.2->1.0.0, disable session store
- fix(recovery): stop loser replica writing RunEvents + add Postgres advisory-lock leader guard
- fix: bypass GitHub org check for internal API key caller
- fix: authenticate internal agent loopback calls via shared API key
- fix: correct postgres FQDN and apply storageclass before PVC in deploy
- fix: postgres VNet integration — full DNS zone resource ID + conditional zonal-resiliency
- fix: idempotent keyvault create and federated credential in 15-setup-identity.sh
- fix: apply extensions.yaml for SandboxTemplate/WarmPool CRDs (v0.5.0)
- fix: agent-sandbox v0.5.0 + manifest.yaml (release.yaml renamed)
- fix: --nodepool-taints (not --node-taints) on az aks create
- fix: replica-safe web session exchange codes + copilot OAuth scope
- fix(api): inject AgentHost\_\_UserId so agent-host uses the user's Copilot token
- fix(checkpoint): shared Postgres checkpoint store for replica-safe cross-pod resume
- fix(checkpoint): quiet the shared-volume fallback logging at startup
- fix(api): gate A2A turns on agent-host readiness (healthz) + connect-refused retry
- fix(api): make ResilientCheckpointStore survive multi-writer lock contention
- fix(k8s): raise namespace ResourceQuota for API surge headroom
- fix(sandbox): valid agent-host configmap JSON + warm pool replicas:0
- fix(sandbox): add lifecycle.shutdownPolicy=Delete and deploy-wire the agent-host image/template/warmpool
- fix(metrics): make MetricsService & DiagnosticsService provider-agnostic (BUG B)
- fix(sandbox): pivot SandboxClaim to native v1beta1 warmPoolRef contract
- fix(sandbox): correct SandboxClaim spec shape and Ready-condition readiness for v0.4.6 CRD
- fix(api): add missing WorkPlans assembly columns to Postgres migration
- fix(web): remove duplicate keepalive_url/keepaliveUrl in PortForwardSessionDto
- fix(preview): drop unsupported Istio Telemetry (AKS App Routing has no Istio CRDs) + correct NetworkPolicy gateway namespace + doc cleanup
- fix(preview): make sandbox browser-preview replica-safe (QA rejection fixes)
- fix(sandbox-preview): adopt Seraph security review for capability tokens
- fix(replica-safety): make CoordinatorSteeringQueue replica-safe via durable SteeringDirectives drain
- fix(replica-safety): Postgres-back PendingRequestStore, HeartbeatStatusStore, and web OAuth CSRF state
- fix(oauth): make MCP OAuth broker replica-safe via Postgres-backed store
- fix(blueprints): auto-roster LLM-declared bespoke roles during generation
- fix(spec-018): wire pod-per-run pod launch + real A2A client + AgentHost image
- fix(spec-018): complete provider-agnostic data layer so Postgres cutover works
- fix(oauth): match loopback redirect_uri ignoring port per RFC 8252
- fix(decisions): prevent slug-collision data loss in decision inbox + deep-dive docs
- fix(oauth): avoid untranslatable DateTimeOffset comparison in IsJtiDenied
- fix(oauth): serve AS metadata at /mcp-suffixed and OIDC well-known paths
- fix(auth): revert authenticated public_members probe - it triggers SAML 403
- fix(auth): make org public_members probe authenticated + treat GitHub rate-limit as Inconclusive
- fix(auth): exempt /api/auth/session/exchange from GitHub token middleware
- Fix MCP tool-call 401 by using stateless HTTP transport
- fix(web): drop unused ApiError import in ProjectSwitcher
- fix(runtime): point agent loopback tools at the real API binding
- fix(web): hide Repository folder field when workspace is auto-assigned
- fix(k8s): allow MCP->API ingress for OAuth JWKS validation
- fix(web): align ProjectSwitcher with gallery + two-column create dialog
- fix(infra): pin OAuth Issuer/Audience on migrate init container
- fix(infra): raise API container memory limit 2Gi -> 4Gi
- fix(api): recreate missing git worktree on orchestration recovery
- fix(web): distinguish 401 from empty in project lists
- fix(runtime): stop agent API tools hard-failing on recoverable HTTP errors
- Fix F5: replace session_token-in-URL with one-time code exchange
- fix(api): stop materializing default.yaml that duplicates built-in workflow
- fix(api): mount-readiness probe + 503 for workspace-unavailable
- fix(auth): pin OAuth issuer/audience in prod + distinguish inconclusive org re-check (Seraph T4-T7 fixes 1-2)
- fix(infra): pin Auth:Mcp:Issuer/Audience/JwksUri in mcp-deployment.yaml
- fix: bypass statx CIFS bug in PersistentVolumeWorkspaceProvider
- fix(auth): env-guard test bypass, redirect allowlist, oauth rate limit (F1-F4)
- fix: add git safe.directory wildcard for Azure File workspace mounts
- fix: auto-create project workspace directory on persistent volume
- fix: strip /api prefix from client paths, fix /docs with FileServer
- fix: API_URL default empty (same-origin), /docs redirect to /docs/
- fix: OAuth redirect double-slash causing SecurityError on history.replaceState
- fix: OAuth login button uses /auth/github/authorize not /api/auth/...
- fix: route /auth/\* to API pod, fix docs SPA fallback override
- fix: network policy blocking gateway ingress, TLS cert ref, and label selectors
- fix: Dockerfile and deployment fixes for AKS
- fix: add --enable-acns, set westus2 default, add .dockerignore
- fix: rewrite 10-create-cluster.sh to match hosted-copilot-sandbox reference
- fix: remove software-specific assumption from casting prompts
- fix: comprehensive audit fixes across all subsystems (296 issues)
- fix: persist coordinator selected workflow to work plan; inject into decomposition prompt
- fix: workflow trigger evaluation - filter by trigger type at selection; event dispatch for task-added-to-ready
- fix: persist blueprint workflow set per project; filter WorkflowRegistry by allowed IDs
- fix: workflow save/default/generator - binder dry-run validation; peer_review now accepted
- fix: docs/reference/api.md — close unclosed HTML tag (VitePress build failure)
- fix: K8s probes -> /health; sandbox-claim API group extensions.agents.x-k8s.io/v1alpha1
- fix: workflow binder Agent->Agent topology + catalog runnable workflows + peer_review
- fix: backlog import DTO casing, dead approval events, revising spinner, RunCard approval indicator
- fix: inject active decisions into coordinator child worker system prompts
- fix: MCP /healthz probe endpoint, AddHttpContextAccessor, pass accessor to client
- fix: MCP /healthz probe endpoint, per-caller auth propagation, remove duplicate blueprint tool
- fix: Cilium FQDN egress allowlist for sandbox + agentweaver-sandbox base image Dockerfile
- fix: AKS docs accuracy (ASP.NET Core not nginx), Istio label, sandbox API group alignment
- fix: AKS deploy script - skip sandbox templates, identity envsubst, MCP build+secret
- fix: reset orch.phase to dispatching when assembly_changes_requested
- fix: propagate tool approval scope to sibling child runs via parent run allowlist
- fix: require distinct output filenames for parallel subtasks in decomposition prompt
- fix: align agent.intent rows with 'Used N tools' cluster header
- fix: parallel-dispatch shared-isolation subtasks without file-path serialization
- fix: show re-drafting spinner in OutcomeSpecPanel when spec already has content
- fix: improve workflow selector process-matching and set-as-default UX
- fix: revert wrong blueprint change; improve domain specificity and workflow selection
- fix: WorkspaceFilePicker snake_case fields + import from workspace page
- fix: trigger public_members fallback on any non-Member primary result
- fix: call public_members endpoint without auth header
- fix: correct 403 cause in comments/messages to SAML SSO enforcement
- fix: update OrgAccessNotGranted middleware message to reflect public_members fallback
- fix: public_members fallback for orgs with third-party app restrictions
- fix: add read:org scope to GitHub OAuth for private org membership checks
- fix: StartsWithSegments exempt prefix must not have trailing slash
- fix: wire IGitHubOrgAuthorizationService interface and github-authz HttpClient
- fix(frontend): replace Caddy with ASP.NET Core static file server
- fix(frontend): replace nginx with Caddy for SPA serving
- fix(aks): add --enable-acns for Cilium FQDN egress policy support
- fix(aks): remove Istio ambient/mesh resources; add Cilium for NetworkPolicy
- fix(catalog): fix blueprint rosters, dedupe groupings, fold GTM into PM (015-US2)
- fix(blueprint/casting): rollback, provenance, amend validation, proposal persistence, charter protection, workspace check
- fix(infra): migration upgrade path, sqlite exception handling, workflow binder validation, dynamic kanban columns
- fix(runs): persist events, request_changes response, worktree preservation, project ownership check
- fix(sandbox): isolation flag, root validation, shell validator wiring, output limits, governance YAML
- fix(coordinator): feedback loop, child run termination, stop semantics, child policy docs
- fix(mcp): SSE parsing, project/team tool field mismatches, inbox paths, URL encoding, docs
- fix(memory): tag OR semantics, filter params, missing endpoints, idempotency, tag normalization
- fix(rai): robust verdict-line parser to stop false-positive RED flags
- fix(009): always render homepage agent rail (show empty state when idle)
- fix(009): pickup-run 403 ownership, coordinator header dedup, per-agent rail
- fix(deps): patch SQLite native binary to resolve NU1903 (GHSA-2m69-gcr7-jv3q)
- fix(coordinator): grounded GOAL in spec event + coordinator-variant graph pre-confirmation
- fix(web): coordinator topology Human Review gate prompts the user
- fix(devscript): reliably kill the WSL backend API before relaunch
- fix(web): coordinator UX — child run rendering, status surfacing, assembly review, loopbacks
- fix(coordinator): surface orchestration status/reason, terminalize assembly, add topology loopbacks
- fix(web): render agent.intent like the muted "Used N tools" row
- fix(api): emit live workflow.step for executor gap nodes (dynamic graph)
- fix(web): live inline child sub-graph + node status/elapsed/message
- fix(dev): build API in WSL in start-dev.ps1 so the Linux apphost exists
- fix(web): clean 7 pre-existing test failures; surface review/merge lifecycle
- fix(008): exclude built-in agents (Scribe/Ralph/Rai) from coordinator dispatch
- Fix coordinator confirm-gate 409 race after revise
- fix(coordinator): redact child-failure reason before persistence (RAI YELLOW)
- fix(coordinator): Phase 2 smoke-test remediation — unified topology, sandbox-safe child prompt, observable failures
- fix(sandbox): guarantee run.degraded before done sentinel; agent self-correction
- fix(sandbox): emit run.degraded on sandbox denial; amber badge independent of agent self-assessment
- fix: resolve Scribe skip by falling back to workflow context and relaxing skip guard
- fix: ResumeSessionAsync must use inner.CreateSessionAsync not raw string overload
- fix: show amber Incomplete badge when report_outcome achieved=false
- fix: universe selection now dynamically sourced from backend policy
- fix: serve merged files from git tree after worktree is deleted
- fix: review card status, SSE reconnect after review, breadcrumb name
- fix: emit correct workflow step status for review outcomes
- fix: improve scribe skip diagnostics and guard logging
- fix: add list_directory to sandbox KnownFileTools allowlist
- fix: resume prior session on revision instead of creating fresh context
- fix: run cancellation and slow start
- fix: report_intent→agent.intent, reviewer SSE, memory page, run status labels, skipped inference
- fix: resolve scribe skipping by reading run context from DB instead of MAF state
- fix: circular identicon on workflow agent card
- fix: use shared AgentAvatar component on workflow agent card
- fix: review card, arc highlight, full-width buttons, scribe guard, project polling
- fix: guard useRunStream against empty runId; return empty history instead of 404
- fix: review/merge/scribe workflow step events + awaiting card style
- fix(workflow-viz): prop-based arc coordinates; add MemoriesPage
- fix(workflow-viz): loopback arcs exit/enter top/bottom center of cards
- fix(workflow-viz): orthogonal loop-back arcs, fixed height nodes, target lookup via id
- fix: EF Core SQLite DateTimeOffset ORDER BY not supported — sort client-side
- fix: PostRunScribeService LINQ DateTimeOffset bug and Scribe tool auth failure
- fix: persist run events to DB so Watch page can replay historical runs
- fix: 400 on /files when viewing Rai/Scribe sub-run watch page
- fix: 409 on /files for Completed runs and pending states on WorkflowRunPage
- fix(web): replace non-existent TaskListRegular with TaskListSquareLtrRegular
- fix(db): RunEvents table missing on existing databases
- fix: allow new Scaffolder API tools through sandbox; fix EF Core Contains; emit agent.task event
- fix(scribe): inject memory tools note programmatically; works with imported repo charters
- fix(scribe): add list_inbox, merge_inbox_entry, export_memory native tools; rewrite task prompt to use tool names
- Fix agent.system_prompt event to include charter (not just base prompt)
- fix: show agent name in workflow timeline; warn when charter missing
- fix: persist GitHub token across restarts on Linux
- fix: sort DateTimeOffset columns client-side in MemoryContextCompiler
- fix: remove New Run/Recent Runs from TeamPage, add agent selector to StartRunDialog, relative repo path
- fix: charter always applied regardless of memory compilation failure
- fix: guard null full_name on GitHub repo objects in CreateFromGitHubDialog
- fix(006): address architecture review findings
- fix: remove builtin MAF agent files from .github/agents — charters in .squad/agents/ are sufficient
- fix: enforce team_size in LLM selection instruction
- fix: tighten analyze tab layout, remove tabContent minHeight
- fix: replace emoji with Fluent UI icons in casting wizard
- fix(catalog): update team templates per product direction
- fix(ui): remove model ID field; universe as dropdown in casting wizard
- fix(ui): show project name in Team page breadcrumb
- fix(ui): remove origin badge from project card
- fix: force HTTP/1.1 on all GitHub API clients
- fix: scribe is a built-in agent, not castable
- fix: accept both camelCase and snake_case request_id in tool.approval_required reducer
- fix: review button two-row layout + rename to Commit and Merge
- Fix SSE recovery hang, wire report_intent as agent.intent, refine artifact browser UI
- fix(test): update ArtifactBrowser diff test for table-based DiffViewer
- fix: tree single-icon per file, diff line numbers + filename header, cancel silently
- fix(web): remove filter tabs re-added by trinity-tree agent; keep folder tree and icons
- fix(web): auto-scroll center, remove filter tabs, wider diff panel, fix icon names
- fix: segment-encode file paths in diff requests and reset state on runId change
- fix: inject workingDirectory for PermissionRequestShell in general handler path
- fix(ui): separate tools list from system prompt in debug card
- fix: Copilot shell allow in direct mode + Foundry report_intent prompt + expandable system_prompt
- fix: restore Copilot tool visibility + allow run_command in direct mode
- fix: inject system prompt into Copilot runner; Foundry tool aliases
- fix: tool name aliases + direct mode shell + stronger system prompt
- fix: policy reads from original repositoryPath not worktree; UI + types
- fix: wire direct mode end-to-end (test repo settings + UI + types)
- fix(web): strip BOM and normalize LF in all web source files
- fix: bwrap /workspace mount, governance denial logging, report_intent icon
- fix(start-dev): write bash script with LF line endings (no \r in cd path)
- fix(start-dev): fix WT semicolon splitting and checkpoint lock
- fix(security): address Seraph MEDIUM findings from Phase 6 review
- fix: harden bwrap sandbox, fix test races, normalize Foundry tool path aliases
- fix(linux): use MxcSdk.GetPlatformSupport() for Linux backend detection
- fix(linux): detect bwrap + bundled lxc-exec on native Linux host
- fix(sdk): roll forward global.json to latestMajor from 10.0.100
- fix(web): re-mount ToolClusterRow on turn completion to trigger collapse
- fix(wsl): resolve bundled lxc-exec via WSL2 mount path before executing
- fix(mxc): remove bundled bfscfg.exe — causes OS hang on Win11 25H2
- fix(mxc): skip base-container tier, fall through to WSL2 on Win11 25H2
- fix(mxc): revert to schema 0.4.0-alpha (AppContainer, no ViVeTool keys needed)
- fix(web): group tool clusters as they arrive within each turn
- fix(web): run_command shows command (not working dir) + collapse tool groups
- fix(copilot): proper governance for PermissionRequestCustomTool (post-review)
- fix: Copilot custom tools, mxc schema, denial reason transparency
- fix(copilot): mark built-in override tools with overridesBuiltInTool flag
- fix(web): hide trivial 'ok' results — no expand for report_intent and similar
- fix(web+sandbox): clean up timeline display and fix exit code indicator
- fix(sandbox): keep DeniedPaths empty — rely on allow-list for containment
- fix(web): flat tool call rows, no turn dividers, report_intent shows intent text
- fix(web): compact run timeline — reduce vertical spacing between turns/steps
- fix(runners): address post-review security and architecture findings
- Fix all security/architecture review findings (F1-F10)
- fix(security): validate and canonicalize repository_path on run submission
- fix(api): return 400 (not 500) for invalid run-submission inputs
- fix(workflow): build a fresh MAF Workflow per run to avoid single-use ownership error
- fix(worktree): delete worktree dir and prune before removing the branch
- fix(merge): apply approved merge to the working tree and loop blocked merges back to review
- fix(sandbox): add list_directory tool and accept "." as the sandbox root
- fix(runtime): emit run.completed once, from the watch loop
- fix(runtime): suppress SDK-internal tool events from the run stream
- fix(web): align review panel border with timeline cards; add MAF Workflows package
- fix(foundry): close sandbox escape — replace StartsWith with dual-layer governance
- fix(streaming): stream Copilot agent output live and gate replay by owner
- fix: use AzureOpenAIClient for Foundry endpoint + restore ME.AI 10.5.1
- fix: harden run endpoints against unhandled exceptions
- fix: suppress OperationCanceledException on SSE client disconnect
- fix: pass RepositoryPath as WorkingDirectory to Copilot session
- fix: pass SessionConfig with PermissionHandler.ApproveAll to AsAIAgent
- fix: replace SSE stream with polling in frontend
- fix: add result column migration for existing DBs
- fix: validate Foundry config lazily, not at startup
- fix: update .gitignore to include appsettings.Development.json
- fix: address all post-implementation code-review findings
- fix: WorktreeService repo-root resolution and branch placeholder
- fix: 415 Content-Type header and Swagger 404

### Added

- feat(observability): wire TransactionTracePanel to AppInsights distributed traces
- feat(observability): v0.7 observability UI — traces, model panels, agent breakdown (#44, #46, #117, #118, #119)
- feat(observability): add run throughput metrics for dashboard widgets (#106)
- feat(web): surface cost everywhere + fix DAG card overlap
- feat(k8s): open sandbox egress to all public domains/ports for research agents
- feat: pod-per-run mode + distributed coordinator lease
- feat: AgentHost warm pool with deferred /configure and runtime KV token fetch
- feat(019): frontend token and AIC usage UI
- feat(019): backend token usage store, projection service, API endpoints, metrics, MCP
- feat(019): capture AIC and token usage from AssistantUsageEvent
- feat(agent-host): add CSI-mounted token store (Option B)
- feat(k8s): Option B CSI user-token delivery for agent-host pods
- feat(spec-006): capacity-pending retry + full diagnostics health suite
- feat(web): add Automation column, Cluster page, and ClusterPage tests
- feat: surface PendingCapacity, run_not_active detail, and detailed diagnostics in the UI
- feat(sandbox): reap orphaned agent pods, quota pre-check, failure reasons
- feat: track automation name in heartbeat ring buffer
- feat(coordinator): allow manual workflow override when starting orchestration
- feat(gallery): declutter GitHub repo selector — sort, no description, URL field
- feat(board): Active after Ready, Problems in own area
- feat(mcp): expose start_preview as an MCP tool
- feat(api): agent-initiated start_preview tool with HITL approval gate
- feat(agents): auto-generate and materialize the Copilot agent definition
- feat(sandbox-preview): self-identifying -preview host label
- feat(sandbox-preview): bound preview target port to gateway ingress range
- feat(runs): advertise browser-preview capability to spawned agents
- feat(sandbox): Gateway-direct browser preview reverse-proxy leg
- feat(preview): keepalive ping, no-referrer security, keepalive_url DTO field
- feat(auth): store GitHub access + refresh tokens in Key Vault behind ISecretStore abstraction
- feat(agenthost): read GitHub token from shared RWX store for pod-per-run
- feat(ui): show executing pod name on agent boxes (K8s only)
- feat(spec-018): close P1.5 A2A round-trip gaps for pod-per-run PoC
- feat(spec-018): P2 Postgres data layer + P3 web/worker split & run leasing
- feat(spec-018): P1 agent execution in sandbox pods via A2A
- feat(api): expose workspace_auto_assigned on /api/server/info
- feat(runtime): let all agents read decisions and memory mid-run
- feat(web): make the header above a tool cluster collapse it
- feat(web): two-stage org/repo picker in Create-from-GitHub dialog
- feat(api): add GET /api/github/accounts and account-scoped repos
- feat(web): allow zoom-in up to 200% on workflow surfaces
- feat(web): group Workflows page into Active / Available / Invalid
- feat(auth): OAuth dynamic client registration (RFC 7591 / T5)
- feat(auth): rotating refresh tokens, MCP resource-server JWT + per-user identity (T4,T6,T7)
- feat(qa): MCP OAuth 2.1 — S1-S5 test scenarios + GitHubTokenAuthMiddleware test bypass
- feat(api): MCP OAuth 2.1 Authorization Server T1-T3 (metadata, JWKS, PKCE authorize/token)
- feat(infra): wire MCP OAuth 2.1 AS/RS routes, signing key, and env vars
- feat: add VitePress docs build to frontend image, serve at /docs
- feat: replace API key auth with GitHub OAuth token validation
- feat: sandbox preview port-forward proxy (backend service + endpoint)
- feat: prefer catalog roles in workflow/blueprint generation; allow bespoke with inline charter
- feat: make tool approval more prominent — warning styling, sticky banner, graph badge
- feat: implement peer_review node executor as agent binding
- feat: implement spec 017 AKS deployment amendments
- feat(workflows): new workflow from scratch — blank canvas, save, coordinator-selectable (015-US9)
- feat(blueprints): library-first workflow matching + IWorkflowGenerator fallback (015-FR062/063)
- feat(workflows): visual execution-graph workflow editor (015-US8)
- feat(mcp): workflow_generate, workflow_save, blueprint_generate MCP tools (015-FR064/065)
- feat(workflows): LLM workflow generation from natural-language description (015-US10)
- feat(workflows): workflow graph visualization on WorkflowsPage (015-US6)
- feat(blueprints): add AI Agent Engineering + Platform SRE blueprints (015-US4)
- feat(sandbox): Kubernetes-native sandbox execution via SandboxClaim warm pool (017-US2)
- feat(coordinator): replace ObserveChildAsync Task.Delay polling with IRunEventStream push (016-US2)
- feat(workflows): YAML workflow editor — edit and save workflows in-product (015-US7)
- feat(events): retire 10k cap and eviction machinery from RunStreamStore (016-US3)
- feat(storage): Azure Disk PVC for SQLite + Azure Files PVC for workspace (017-US5)
- feat(security): Istio ambient mTLS — PeerAuthentication STRICT + AuthorizationPolicies (017-US3)
- feat(events): introduce IRunEventStream with SQLite write-through + Channel pub/sub (016-US4)
- feat(workflows): generalize RunWorkflowGraphBinder with open executor factory (015-US1)
- feat(ui): add spec-to-backlog UI — OutcomeSpecPanel + KanbanBoard import (014-UI)
- feat(backlog): add spec-to-backlog decompose endpoint + workspace files API (014-backend)
- feat(aks): add AKS deployment manifests, Dockerfiles, and scripts (017-US1)
- feat(mcp): add backlog_decompose_spec MCP tool (014-MCP)
- feat: shared orchestration worktree for multi-agent coordinator runs
- feat: backlog Kanban + workflow engine, metrics/diagnostics dashboards, IA shell rework, sandbox & casting fixes
- feat(coordinator): steering-based recovery for parked/failed runs + board/graph UX
- feat(009): backlog/ready Kanban board, coordinator pickup, and run retrigger
- feat(coordinator): surface scribe/assembly work, team memory, filesystem browse, and terminal-state UI polish
- feat(coordinator): resilient checkpoints, assembly recovery, and live run-graph polish
- feat(web): Autopilot + auto-approve-tools toggles + audit timeline entries
- feat(coordinator): Autopilot (questions-only auto-answer) + auto-approve-tools run options
- feat(web): inline answer + permission affordances for bubbled questions
- feat(coordinator): ask_question tool — blocking HITL clarification + child-question bubbling
- feat(008): GitHub OAuth token refresh
- feat(008): implement Phase 3 collective assembly
- feat(008): node_type taxonomy + unified coordinator graph view
- feat(008): dynamic workflow graph descriptor (built at construction, not reflected)
- feat(008): child run identity, events endpoint, and timeline seed
- feat(coordinator): Phase 2 surface — steering runtime, Web topology view, MCP parity, HTTP endpoints
- feat: intent as timeline system message; fix useArtifactBrowser commitMessage
- feat: stream race fix + commit message in review panel
- feat: add Browse files button to Merge step card
- feat: restyle report_intent as system message row
- feat: workflow card polish — model name, revise status, modal fixes, faster file refresh
- feat: open execution stream in modal instead of navigating away
- feat: show live agent.intent text on workflow card instead of static placeholder
- feat: add per-card runtime timers to workflow diagram
- feat: separate workflow_run from execution
- feat(workflow-viz): agent role from team, variable card heights, scribe memories link, bolder arc labels
- feat(workflow-viz): role labels on cards, larger card height, back-to-workflow from watch
- feat: replace hand-rolled pipeline with React Flow + dagre diagram
- feat: Rai RED routes to Review; add Review-to-Agent return arc
- feat: Rai REVISE + Review RequestChanges retrigger loop
- feat(web): restyle Rai feedback arc to neutral connector style, connect into Agent card top
- feat(web): replace inline diagonal with red L-arc above pipeline cards for Rai feedback loop
- feat(web): replace conditional feedback banner with always-visible return arc on Rai connector
- feat(web): move rejection indicator onto connector arrow between Agent and Rai/Review
- feat(web): add feedback loop arc and fix ArrowRightRegular crash
- feat(web): redesign WorkflowRunPage to pipeline graph style
- feat: expose Scaffolder API ops as first-class agent tools
- feat: persist run events, review MAF step, delete runs endpoint
- feat: start run goes to workflow view; remove Watch button; add delete run
- feat: stream Rai and Scribe agent execution to their own sub-streams
- feat: render MAF workflow stages as visual pipeline bar in console
- feat: workflow step events for MAF run visualization
- feat: RaiAIAgent + ScribeAIAgent subclass CopilotAIAgent; charters read dynamically
- feat: split built-in agents into separate system section on TeamPage
- feat(006): block charter edits for built-in system agents
- feat(006): built-in agent guards, pixel-art avatars, MCP memory tools, updated docs
- feat(006): implement Scribe as IAgentRunner MAF workflow step
- feat: implement spec 006 - Memory and Decision Inbox
- feat(007): retire Scaffolder.Cli, register MCP server, add mcp.md docs
- feat(mcp): implement all 22 MCP tools (phases 3-7)
- feat(mcp): ScaffolderApiClient + SseClient
- feat(mcp): scaffold Scaffolder.Mcp project
- feat(squad): write squad-agentweaver.agent.md on team confirm (FR-015-020)
- feat: replace CLI with MCP server — constitution v1.5.0 + spec 007
- feat: AgentName on Run, charter as system prompt, project-scoped run endpoints
- feat(web): New Run dialog with agent picker and runs list on TeamPage
- feat: ScaffolderAgentRuntime helper with session serialization + spec 006 SessionContext update
- feat: SDK alignment -- config.json, identity files, gitignore, decisions/history format, Coordinator section
- feat: provision RAI policy + audit trail, add description to team.md
- feat: provision Scribe, Ralph, Rai as MAF agents on team confirm
- feat: seed history.md, routing.md, fix gitattributes, scaffold squad directories
- feat: fix AddMemberDialog to use full catalog roles
- feat: add GET /api/catalog/roles endpoint
- feat: add team rationale to LLM output and CastProposalDto
- feat: use proposal.rationale for Why this team display
- feat: show per-member justifications in rationale, remove redundant required roles input
- feat: add team size to Analyze tab, wire team_size field to API
- feat: add team_size parameter support to free_text and analysis casting modes
- feat: replace Configure tab with shared roles checkboxes section
- feat: add manual casting mode with explicit role selection
- feat: add Configure tab for manual role selection in casting wizard
- feat: restructure cast step with rationale, collapsible universe, team size and roles
- feat: move universe selection to review step with re-cast on change
- feat: rework cast step to tabbed layout, universe dropdown, rename CTAs
- feat: redesign team page with card grid and agent detail panel
- feat: add charter timestamps to TeamMemberDto and history endpoint
- feat(ui): redesign casting wizard as single-page form
- feat(catalog): add Azure Feature Delivery team template
- feat: agent team casting (feature 005)
- feat(005): plan Agent Team Casting; amend constitution to v1.4.0 (Copilot-only)
- feat: Allow tool scope, approval persistence fix, docs parity backfill
- feat: tool cluster expand UX, font hierarchy, report_outcome self-assessment
- feat: HITL approval scopes, Fluent 2 UI, 409 fix, DESIGN.md, sandbox system prompt
- feat: B3 request-changes feedback loop + review UX + stream hardening
- feat: per-file line counts, content endpoint, derivedRunStatus fix
- feat(web): flat changes list, improved files tree, syntax-highlighted viewer
- feat(web): two-tab artifact panel, file viewer modal, remove right diff panel
- feat(web): restore filter tabs in FileTreePanel tree view
- feat(web): three-panel horizontal layout for artifact browser
- feat(FR-034-041): implement artifact browser feature
- feat(artifact-browser): add artifact browser to Web UI and CLI (FR-041, SC-016)
- feat(ui): tools as separate card; agent.tools event
- feat: full debug info in agent.system_prompt event; Copilot prompt injection
- feat(ui): show literal tool call args in expanded ToolCallCard
- feat: emit agent.system_prompt event for debugging; stop overriding Copilot tools
- feat: add direct execution mode (direct: true in .scaffolder/settings.yml)
- feat: add start-dev.ps1 — launch API in WSL2, Web UI on Windows
- feat(phase6): align sandbox policy with Copilot CLI implementation
- feat: upgrade to Sabbour.Mxc.Sdk 0.1.2 (WSL2 bwrap/unshare support)
- feat(wsl): delegate WSL2 sandbox to Sabbour.Mxc.Sdk v0.1.2
- feat(wsl): discovery-based sandbox executor — bwrap/unshare, no lxc-exec
- feat(runner): instruct model to interpret run_command output via report_intent
- feat(002): T012 bundle mxc binaries + T017-api shell approval + scoped settings.yml
- feat(002): GitOps sandbox policy — .scaffolder/sandbox.yml
- feat(002): T020+T021 API sandbox endpoints + T022+T024 CLI + T035-T038 docs
- feat(web): T023+T025 sandbox badge, shell output in timeline, settings page
- feat(002): T018 dynamic project-scoped sandbox policy in SQLite
- feat(002): T017 HITL shell approval gate + T019 sandbox.warning event
- feat(002): Scaffolder.AgentTools package + refactor tools into ISandboxTool (T055-T057)
- feat(spike): Phase 0 — validate Sabbour.Mxc.Sdk v0.1.1 on Windows ARM64
- feat(events): add merge.started to bridge approve -> merge.completed/failed
- feat(spec/001): MAF workflow-native HITL review gate + no-changes skip
- feat(spec/001): review/merge, Foundry streaming, and run-timeline UI
- feat(runtime): surface individual Copilot tool events at parity with Foundry
- feat(sandbox): enforce run sandbox boundary across both model providers
- feat: add FoundryAgentRunner for MicrosoftFoundry model source
- feat: structured event pipeline for Story 2 (aligned to Copilot SDK events)
- feat: stream agent response over SSE
- feat: strip to MAF basics -- prove a Copilot turn
- feat: replace provider SDKs with correct implementations
- feat(spec/001): implement single-agent run — full vertical slice
- feat(tests): add Scaffolder.Tests with 43 passing tests
- feat(web): Phase 9 - React 19 + Fluent 2 Web UI [T059-T066/trinity]
- feat(cli): Phase 8 - CLI client [T052-T058/trinity]
- feat(governance): Phase 7 - responsible AI + NFR enforcement [T045-T050/morpheus+tank]
- feat(api): Phase 6 - review + merge [T041,T043,T044/tank+morpheus]
- feat(agent): Phase 5 - model source adapters + governance [T037-T040/morpheus]
- feat(api): Phase 4 - SSE streaming [T032-T036/tank]
- feat(api): Phase 3 complete - US1 agent loop, execution, endpoints [T026-T031/tank+morpheus]
- feat(agent+persistence): Phase 3 Wave 1-2 - sandbox, tools, event log, state machine [T020-T027/tank+morpheus]
- feat(persistence): repositories and DI wiring [T015-T019/tank]
- feat(persistence): EF Core initial migration - all 6 tables [T014/tank]
- feat(persistence): EF Core data model - all 6 entities [T007-T013/tank]
- feat(config): application settings schema and ScaffolderOptions [T006/tank]
- feat(setup): initialize all project scaffolds - Phase 1 Wave 2 [T002/tank T003/trinity T004/smith T005/trinity]
- feat(spec/001): event loop, Responsible AI, and governance spec update

### Changed

- refactor(observability): remove event-stream fallback from TransactionTracePanel
- chore(observability): remove DB-backed metrics layer, migrate dashboard to AppInsights
- chore(observability): add OTel/AppInsights instrumentation and AKS Managed Prometheus (#106)
- chore(ui): surface app version inside the Alpha badge (#109)
- chore(release): implement semver release process (#104)
- chore: graph zoom-in button, card navigation, and scroll indicator (#100)
- chore: replace AKS flowchart diagrams with block-beta block architecture diagrams (#101)
- chore(repo): add issue-form templates for all 6 type:\* kinds
- build(aks): image-efficient redeploy + reproducible install scripts
- chore: remove dead legacy agentweaver-sandbox image/template/warmpool
- chore(deploy): apply serviceaccount-agenthost.yaml in 30-deploy.sh
- refactor(spec-006): drive reaper from heartbeat; cluster diagnostics endpoint
- chore(deps): upgrade GitHub.Copilot.SDK 1.0.0 -> 1.0.2
- chore: remove WORKFLOW_VERIFICATION_REPORT.txt
- chore: remove SandboxExec spike folder
- chore(api): quiet EF Core and framework Info log noise in committed config
- chore: stop tracking .squad runtime/config dir
- chore: ensure \*.sh files always use LF line endings
- chore(k8s): flip API to pod-per-run agent execution (live)
- build(spec-018): apply Postgres + replicas:2 + RWX HOME cutover config
- build(spec-018): Postgres cutover tooling + worker manifest hardening
- build(spec-018): Dockerfile COPY for new Data/Migrations projects + deploy runbook
- chore: pre-audit snapshot — spec 006/007/008/009/011/012/013 implementation work
- refactor(coordinator): remove DraftDeterministic crutch from production
- refactor(rename): flip remaining plural scaffolders identifiers to agentweaver
- refactor(rename): rename web client + docs Scaffolder -> Agentweaver (phase B)
- refactor(rename): rename .NET solution Scaffolder._ -> Agentweaver._ (phase A)
- refactor(008): extract Program.cs endpoints into MapXEndpoints classes
- chore: remove legacy /watch route, simplify WatchPage to canonical route only
- chore: change web dev server port from 5173 to 8080
- refactor(runtime): consolidate system prompt; move memory tools guidance to Scribe charter
- refactor: CopilotAIAgent subclasses AIAgent for MAF session serialization
- refactor(006): implement Scribe as MAF workflow step
- refactor: remove stale charter templates, add incident_lead charter
- refactor: consolidate catalog roles round 2 (28 -> 22)
- refactor: consolidate overlapping catalog roles (qa, triage, docs)
- chore: remove errant speckit.plan artifacts; restore copilot-instructions
- refactor: minimal system prompt for both runners
- refactor: rename edit/create tools, clean up governance and native exclusions
- refactor: remove double-governance from tool bodies; rely on process isolation
- chore: remove local NuGet feed — Sabbour.Mxc.Sdk 0.1.2 now on nuget.org
- chore: ignore Vite build cache
- refactor(spec/001): single merge implementation via IMergeCoordinator.ExecuteMergeAsync
- refactor: switch FoundryClientFactory from OpenAI to Azure.AI.Inference
- chore: remove appsettings.Development.json configuration file
- chore: retarget to net10.0 (SDK 10.0.300)
- chore: remove Spec-Kit plan/tasks/implement references from Squad files
- chore(constitution): bump v1.1.0 -> v1.1.1, standardize runtime on .NET 10
- chore: migrate to .NET 9 (net9.0, SDK 9.0.314)
- chore(setup): pin to .NET 8 SDK while .NET 9 installer completes [squad-decision]
- chore(setup): scaffold solution structure and directory tree [T001/link]

### Docs

- docs: fix nav sidebar, remove AX TODO stub, fix README diagrams
- docs(reference): add Agentweaver-on-AX integration analysis
- docs: embed AKS block diagram in docs+README, add AX reference page, remove AX comparison from README
- docs: add AKS block diagram (excalidraw) and link from architecture-aks.md
- docs: narrow README reference section to AX only
- docs: add Reference section comparing Agentweaver to Agent eXecutor and Agent Substrate (#87)
- docs: update coordinator internals, reference, and experience for #76 #78 #82
- docs(sandbox): keep RealPath as supported API
- docs: repair Ralph PR docs dispositions
- docs(specs): add edit-workflows-with-generation-prompt spec (#59)
- docs(specs): add scheduled/event workflow triggers and import-and-sync skills specs
- docs(specs): add specs for backlog sync, PR action, browser console, agent skills, AKS personas
- docs: note pod-name persistence; exclude docs/ from image build context
- docs: sync to shipped features; remove stale sandbox references
- docs(squad): rebuild routing.md charter-derived; split out built-in agents
- docs: document per-run workingDirectory delivery via /configure
- docs: update coordinator autopilot, SSE reconnect, cluster diagnostics, and AKS diagram
- docs: pod-per-run + distributed coordinator lease
- docs: AgentHost warm pool architecture, deep-dive, UX, and reference docs
- docs: warm pool architecture, auth deep-dive, sandbox reference updates
- docs: security fix documentation pass
- docs(019): AI credit and token usage monitoring
- docs: update auth, sandbox, coordinator, API docs; add Cluster page and agent-token delivery guides
- docs: link published docs site in README
- docs: add workflow-selection deep-dive page and cross-links
- docs: set base to /agentweaver/ and add GitHub Pages deploy workflow
- docs: add GitHub repo social link to nav
- docs: document workflow picker + auto-selection in Start task dialog
- docs: add Agentweaver icon to README
- docs: remove all legacy SQLite backup job references; delete backup-cronjob.yaml
- docs: update deep-dive docs for PostgreSQL + 2-replica deployment
- docs: update AKS architecture doc for Postgres + 2 replicas
- docs: strip internal/removed config from configuration + deployment-aks
- docs: remove internal/unintended config references from getting-started
- docs: add GitHub OAuth App setup to getting-started
- docs: fix getting-started — OAuth token via sign-in, no static API key required
- docs: update sandbox preview — enabled by default in AKS
- docs(sandbox): document agent-initiated start_preview tool + HITL approval
- docs(kata): document dedicated kata user pool topology + scheduling
- docs(sandbox): document AgentHost cold-start readiness gate under replicas:0
- docs(checkpoint): document multi-replica checkpoint store resilience
- docs(sandbox): document v1beta1 warmPoolRef SandboxClaim contract + agent-host warm pool
- docs: document shipped sandbox browser-preview reverse proxy
- docs(agent-definition): document generation & per-project materialization
- docs: add MIT LICENSE
- docs(install): fix repo slug, add --image-tag, add Build & deploy section
- docs: fix dark-mode text for Mermaid sequence diagrams
- docs: add docs-sync mechanism (generator + CI drift check + skill)
- docs(install): true one-liner install -- bootstrap clone + one-command local/AKS
- docs: robust Mermaid lightbox binding + relabel example walkthroughs
- docs: full-width layout, top-bar nav, and Mermaid lightbox
- docs: fix Mermaid legibility and dark-mode rendering
- docs: restructure IA, re-ground against real code, add install + screenshots plan
- docs: fresh FluentUI-styled Mermaid architecture diagrams across all areas
- docs: add Microsoft Agent Framework coverage, MXC + preview sandbox, nav fixes
- docs: add deep-dive/reference/UX docs for pod execution, A2A, scaling, agent comms
- docs(experience): add UI/MCP user-experience guide
- docs(deep-dive): second coherence polish (gpt-5.5 cross-model pass)
- docs(deep-dive): coherence polish - remove hedging and legacy framing
- docs(spec-018): distributed agent execution + scaling design
- docs(deep-dive): add 7 new concept deep dives + grouped TOC
- docs(deep-dive): rewrite as concept/logic-first deep dives
- docs: deep accuracy pass — AKS deployment, architecture, guide docs
- docs: deep accuracy pass — reference docs (API endpoints, events, memory, sandbox)
- docs: deep accuracy pass — workflow, MCP, run-event-stream docs
- docs(spec): amend 017-aks-deployment spec with Cilium NetworkPolicy, GitHub org authz, external MCP auth, ISandboxExecutorRouter, SQLite reliability notes, and GitHub App redirect URL
- docs(spec): resolve feature 011 web app shell clarifications
- docs(spec): add feature 011 spec - Agentweaver web app shell / IA
- docs(spec): resolve feature 010 clarifications (load-once, per-task override, review policy)
- docs(spec): add feature 010 spec - YAML workflows + per-project Review Policies
- docs: finish Agentweaver rebrand, drop retired-CLI refs, fix docs build
- docs: catch up with spec 006 - workflow run UI, memory, MCP tools, events
- docs: remove CLI references, replace with MCP server throughout
- docs: update api.md, cli.md, web.md for feature 005 + deprecation notice on cli.md
- docs: update api.md, cli.md, web.md for feature 005 (team casting, agent runs)
- docs: update sandbox docs for Phase 6 (schema 0.5.0-alpha, network_enabled, selective mounts)
- docs(wsl): document Wslc upgrade path for when WSL 2.8.x ships
- docs(002): re-scope tools — remove memory/todo, restore report_intent
- docs(002): add sandboxed-execution spec and implementation plan
- docs: align spec to actual Copilot SDK event model
- docs: add VitePress docs site + Docs ceremony
- docs: ratify Scaffolder constitution v1.0.0 (7 principles; no-emoji rule scoped to product)

### Tests

- test(projects): provide IConfiguration to workspace provider
- test: fix pre-existing test failures across backend and frontend (#80)
- test(019): token usage store, projection service, and endpoint tests
- test: commit e2e Playwright harness source
- test(oauth): update McpOAuthServerTests for EF-backed broker + scope-factory signatures
- test(events): verify crash-safe replay — write-through durability tests (016-US1)
- test(008): restore injectable workflow-agent seam; fix content-safety terminal
- test(008): align 3 stale tests with current contracts
- test(002): unit tests for SandboxExec + SandboxedFileTools (T026-T029, T051-T052)
- test(runtime): add Copilot glob/grep escape canaries and both-provider tool-event parity assertions
- test(qa): Phase 10 - contract tests + integration QA + compliance [T051,T067-T079/smith+rai]

### Other

- bug(run-page): show preview sandbox button for completed runs (#99)
- bug(run-page): add preview sandbox to orchestration run page (#98)
- Move personas under specs/ and link from spec index
- Remove unused code and duplicate logic (code-bloat sweep)
- Replace legacy speckit specs with concise area-grouped product specs (#2-#37)
- Add user personas + persona-driven Playwright self-improvement harness (#1)
- Stop bundling/serving docs in frontend; redirect /docs to GitHub Pages
- security: remove installation token fallback — require user identity on all Copilot paths
- k8s: increase namespace quota to 32 CPU / 30 pods
- security: per-user GitHub token isolation and AgentHost SPC hardening
- spec: resolve FR-015 — per-model breakdown visible in dashboards
- spec: AI credit and token usage monitoring (019)
- Commit scaffold files to git on project creation
- revert: pause Task 3 DiagnosticsPage/StatusDot changes pending Cluster page design
- debug: add --verbose to ef bundle to diagnose failure
- scripts: remove backup-cronjob.yaml from 30-deploy.sh
- revert: remove erroneously re-added Auth\_\_User env var
- config: set Auth\_\_User=sabbour (static-key fallback owner)
- Remove static MCP API key; MCP auth via OAuth only
- infra: switch to 3-pool layout with CriticalAddonsOnly system taint
- infra(kata): wire sandbox pods to dedicated kata user pool (katapool)
- infra: apply spike verdict — DDC nested wildcard NOT supported; simplify to single-label fallback
- infra: add preview gateway bootstrap (gateway, RBAC, NetworkPolicy, deploy wiring)
- docs+ui: mark Agentweaver as alpha, MCP as experimental
- Remove platform-sre blueprint; fix AKS project creation; auto-fill repo folder
- config: set Auth:GitHub:AllowedOrg=microsoft
- Phase 2: dispatch child runs, observe, topology + subtask events
- Phase 2: coordinator orchestrator (decompose + persist work plan)
- Phase 2 foundation: coordinator EF entities, trimmed child pipeline, steering spike
- Rename revise action to 'Clarify and request changes' and clarify re-draft state
- Surface clarifying questions in revise dialog with Q/A template
- Implement Feature 008 Phase 1: Coordinator outcome-spec + confirm gate
- Add Squad Coordinator Agent implementation plan (008)
- Add Squad Coordinator Agent specification (008)
- execution route: /projects/:id/runs/:id/execution/:id + breadcrumbs
- workflow: fix arc clipping and clearance heuristics
- workflow: arc rounding, rename run→execution, team memory page
- PUT /sessions/current: upsert instead of 404 when no open session exists
- Filter workflow-orchestration events from Watch page timeline
- Auto-expire stale no-checkpoint AwaitingReview runs older than 24h on startup
- Allow abandoning any non-terminal run (in_progress, awaiting_review, etc.)
- Add DismissCircleRegular icon to Abandon button
- Separate Abandon/Delete UX for awaiting_review vs terminal runs
- Replace confirm() with Fluent UI dialog for run deletion
- Allow deleting AwaitingReview runs (force-decline + worktree cleanup)
- Phase 12: PostRunScribeService — memory flywheel close after successful runs
- ux: show server data directory in repository path hint
- ux: clarify repository path is server-side, not local machine
- ux: clarify working directory field in create project dialogs
- spec+plan: 006 add post-run Scribe loop-close (FR-031/032/033)
- spec+plan: 006 progressive disclosure via Context Compilation Pattern
- spec+plan: add agentweaver squad coordinator (US6, FR-015-020, SC-008-009)
- plan: 007-mcp-server implementation plan
- revert: remove spurious HTTP/1.1 fix and WinHttpHandler
- Commit pending session changes
- Expand destructive command patterns with comprehensive bash/shell list
- Dangerous commands surface for HITL approval instead of blocking
- Move sandbox policy into project settings, remove global Settings page
- Remove provider selection, hardcode GitHub Copilot
- [003-projects] Add repo picker to Create from GitHub dialog
- [003-projects] Reduce sign-in page padding/gaps
- [003-projects] Increase sign-in logo size to 160px
- [003-projects] Use agentweaver.png logo
- [003-projects] Rename brand to Agentweaver
- [003-projects] Fix OAuth state lost between requests
- [003-projects] Fix OAuth redirect URL to use absolute API_URL
- [003-projects] Switch to OAuth redirect flow, add avatar support
- [003-projects] Add full-page GitHub sign-in gate
- [003-projects] Redesign: GitHub device flow sign-in card UI
- [003-projects] Fix: add Accept: application/json to GitHub device flow requests
- [003-projects] Fix: normalize button sizes to medium (Sign in, Settings, Watch)
- [003-projects] Fix: defer GitHub ClientId validation; return 503 when not configured
- [003-projects] Fix: pass runId to TurnGroup for tool approvals; back-to-project nav on WatchPage
- [003-projects] Phase 7: tests + security review
- [003-projects] Phase 6: update reference docs for projects + github auth
- [003-projects] Phase 5: Web gallery, project pages, GitHub sign-in
- [003-projects] Phase 4: CLI project + github commands
- [003-projects] Phase 3: /api/projects, /api/auth/github, run-in-project endpoint
- [003-projects] Phase 1: workspace providers, git initializer, ProjectService
- [003-projects] Phase 0: domain types, interfaces, schema, SqliteProjectStore
- [Spec Kit] Simplify plan-gate review to rubber-duck only (constitution v1.3.0)
- [Spec Kit] Add 003-projects implementation plan; resolve FR-025
- [Spec Kit] Clarify 003-projects: resolve delete/dir/owner ambiguities; add FR-026
- [Spec Kit] Refine FR-005: unified GitHub sign-in replaces Copilot API key (003-projects)
- [Spec Kit] Add and clarify Projects feature spec (003-projects)
- Backend: SSE awaiting_review hang fix, commit endpoint, workspace listing
- spec(001): add User Story 5 (artifact browser) with FR-034-FR-041, SC-013-SC-017, and 2026-06-10 clarification
- debug: relax AGT to allow-all; fix Copilot shell dir; fix report_intent response
- config: disable shell in spike sandbox settings
- Wire ISandboxExecutor + 9 custom AIFunction tools into both runners (T013,T015,T016,T047,T048,T019,T018)
- Add Speckit plan, Squad scaffolding, and tasks for 001-single-agent-run
