# Link — History (Summarized)

## 2026-06-07–2026-06-17: foundations, docs, and coordinator support

Link scaffolded the initial monorepo and web app, kept reference docs aligned with MAF HITL and Feature 005, documented the custom sandboxed tool model for spec 002, and resolved the 003-projects plan compensation fix after prior reviewer lockouts. For Feature 008, Link added MCP coordinator parity tools, coordinator reference docs, and Phase 2 topology/steering documentation. Cross-agent notes from this period: coordinator_steer accepts nullable instruction for stop/recovery parity; Seraph owned MCP OAuth 2.1 design.

## 2026-06-25–2026-06-26: AKS bring-up and live diagnosis

Link corrected the false static GitHub PAT leak assumption: runtime OAuth state is stored through `IGitHubTokenStore`; GitHub OAuth client credentials remain required for the web flow. Link rewrote AKS cluster creation around the hosted-copilot-sandbox reference and user constraints: westus2, NAP, `Standard_D4s_v3`, Kata, App Routing with Istio/Gateway API/default domain, CSI/addons at create time, and ACNS. Cluster `agentweaver-aks-2` was created successfully.

Link diagnosed workspace-create 400 as Azure Files CIFS mount-root `statx(2)` returning ENOENT before mount visibility; the fix removed the brittle `Directory.Exists(_mountRoot)` guard and Tank added a write-based readiness probe. Link corrected the "No projects yet" diagnosis: DB persisted correctly; OAuth failed because `${HOST}` was not substituted, leaving callback URLs on `agentweaver.example.com`. Deploy guidance was to redeploy with the real staging host and clear browser session storage.

## 2026-06-27: Spec 018 platform, Postgres, pod-per-run, and KV token store

Spec 018 locked distributed execution: sandbox-all-agent-execution through a thin MAF bridge, no broker, Azure PostgreSQL Flexible Server, AKS web/worker split with durable leasing, and A2A as the only worker→agent-host transport. Link's platform responsibilities included passwordless/workload-identity Postgres access, sandbox pod identity/quota controls, scoped NetworkPolicy, no sandbox egress broadening, gated `/v1/card`, DB-checkpoint resume, and rollback via `Sandbox:AgentExecutionMode=in-api`.

Link provisioned Azure PostgreSQL Flexible Server `agentweaver-pg` (private VNet, PG16, zone-redundant HA), wired Postgres secrets, authored worker Deployment/HPA/NetworkPolicies, built/pushed tag `92e4d74c`, applied the web tier, and held Postgres cutover/worker enablement for an attended run. Later, Link rebuilt and pushed `be1e28fa`, redeployed API/frontend 2/2 with Postgres/RWX intact, applied sandbox NetworkPolicy `allow-api-agenthost-egress` for API→agent-host pods on 8088, and created ConfigMap `agenthost-config` with `RequireMtls=false`.

For the Key Vault GitHub token store, Link delivered OAuth routes, KV signing-key wiring, env contracts, and network review, then pinned MCP issuer/audience/JWKS config. Link merged `sabbour/mcp-oauth` into `sabbour/spec-018` as `e7568acd` and deployed API/frontend/MCP/sandbox images. AKS validation confirmed workload identity injection, `Auth__TokenStore__Provider=keyvault`, healthy `/api/health`, and no KV auth errors. GitHub OAuth access/refresh tokens now persist in `agentweaver-kv` across redeploys.

## 2026-06-28: docs reconciliation, repo prep, and sandbox preview shipment

Link created one-liner installers (`install.sh`, `install.ps1`) with bootstrap clone-on-first-run, local/AKS modes, image-tag overrides, LF shell-script enforcement, and ordered AKS script validation. Docs build passed and install docs use `sabbour/agentweaver`.

Link completed repository prep on main: `.squad/` became gitignored and untracked, MIT LICENSE was added, and the e2e harness source was committed while artifacts stayed ignored. Final pre-push still needs history scrub of residual `.squad/` commits.

📌 Team update (2026-06-28T05:10:00-07:00): Sandbox browser preview shipped to main (`373f544`) and deployed with `SANDBOX_PREVIEW_ENABLED=true`. B1 root cause was per-process `PodNameRegistry` at replicas:2, fixed by Tank via SandboxClaim cluster-state resolution. Link's live AKS dry-run found no Istio CRDs, so Telemetry was dropped; NetworkPolicy is same-namespace gateway podSelector-only.


## 2026-06-28: Copilot auth blocker / demo pause

Link deploy smoke confirmed the autonomous preview path is blocked only by model credentials. The Agentweaver GitHub App client (`Iv23lieRvX4I63VNekKS`) requests `repo read:user read:org` and cannot receive Copilot-entitled tokens; re-auth through it will not fix Copilot SDK turns. User paused the demo; Microsoft Foundry is the recommended credential path.


## 2026-06-28T16:05:00-07:00 — Main deploy verified for login-loop fix

Link merged Tank's web session exchange fix to `main` at `20ccd42`, rebuilt the API image, retagged the remaining images, reclaimed quota by deleting an orphan warmpool and dead `SandboxClaim` objects, deployed, and verified API health at 2/2 replicas with 0 restarts. Cross-replica exchange no longer returns the 401 login-loop failure. Remaining auth note: existing sessions need re-auth with the new `copilot` scope or Foundry credentials.

## 2026-06-29T00:57:04-07:00 — Merge 022 + deploy c082df5; AKS three-pool layout scripts

Merged Tank's branch `022-startup-recovery-leader` (commit `7ccfd1a`) into `main`, built API image `c082df5`, and deployed to `agentweaver-aks-2`. Post-rollout logs confirmed: leader pod acquired advisory lock and ran startup recovery; loser pod(s) logged "startup recovery skipped — not leader". Zero Postgres 40001 errors after deployment.

AKS cluster scripts updated (pending reprovisioning):
- Switched from NAP to cluster-autoscaler; added dedicated `katapool` (User, KataVmIsolation, autoscaler 1–5, taint `sandbox=kata:NoSchedule`).
- Added `CriticalAddonsOnly=true:NoSchedule` to system pool (`nodepool1`); added taintless `apppool` (User, AzureLinux, autoscaler 1–5) for app workloads.
- Three-pool layout: `nodepool1` (system, CriticalAddonsOnly), `apppool` (app workloads), `katapool` (sandbox/kata).
- Docs updated: `deployment-aks.md`, `sandbox-pod-execution.md`.

---

## 2026-06-30: Security audit fixes #3 + A2A bearer token (Feature 018)

**Timeline:** 2026-06-29T14:30–17:30Z

**Scope:** Per-pod token isolation, deployment infrastructure, auth documentation

**Deliverables:**

### Fix #3: Per-Pod CSI SPC for AgentHost Token Isolation
- Per-pod SecretProviderClass created per run with only `ghtok-user--{base32(userId)}`
- KubernetesSandboxExecutor clones SandboxTemplate to point at run-scoped SPC
- Run-scoped SandboxWarmPool created/cleaned up with lifecycle
- AgentHostReaperService reaps orphaned SPC/template/pool resources
- Deleted obsolete AgentHostUserTokenSyncService
- Updated RBAC: API now creates/deletes per-run SandboxTemplates, SandboxWarmPools, SecretProviderClasses

### Dev Secrets & Documentation
- Added UserSecretsId to Agentweaver.Api.csproj
- Documented dotnet user-secrets for Auth:GitHub:ClientSecret in development
- Updated token-delivery docs/comments from shared SPC patching to per-run SPCs
- Configuration docs updated for run-scoped resource model

### A2A Bearer Token Path Integration
- Integrated Morpheus's per-run bearer token mechanism into KubernetesSandboxExecutor
- Token injected via `AgentHost__TurnBearerToken` environment variable
- Token lifecycle managed by PodNameRegistry (cleared on pod cleanup)
- RemoteAgentProxy applies token as default Authorization header

**Testing & validation:**
- All builds pass (0 warnings, 0 errors)
- Run-scoped SPC tests green (no shared token Secret vulnerabilities)
- No user launch failures due to missing tokens
- Per-user scoping enforced at OAuth callback time (no cross-user token bleed)

**Key learnings:**
- Per-pod CSI SPC requires explicit lifecycle management (create at run launch, delete at run release)
- Shared token Secrets are inherently unsafe; per-pod isolation removes cross-user leakage vectors
- RBAC must be updated for all run-scoped resource types (SPC, SandboxTemplate, SandboxWarmPool, etc.)
- Dev secrets should use user-secrets/configuration, not tracked appsettings; production uses Key Vault


## 2026-07-05T20:40:00-07:00 — v0.7.11 release batch
Delivered observability overhaul and AppInsights agent telemetry: `agentweaver.token.usage`, tagged spans, agentic-only traces, compact tiles, and AI-credit-over-time chart. Staging deployment is healthy pending Ahmed validation.


## 2026-07-06T07-29-39Z — v0.8.0 staging release

Link's trace hierarchy work (#166) shipped in the v0.8.0 staging wave; follow-up #200 remains open for tool-span parenting. Staging deployed healthy; do not close #166 or push/merge until Ahmed validates.


## 2026-07-06 v0.9.0 staging wave
- Bumped VERSION to 0.9.0 and deployed the staging AKS release candidate successfully.

## 2026-07-07T00:00:00Z — v0.9.2 staging ship

Link shipped v0.9.2 wave documentation (`58907d8`) across the affected docs; VitePress build was green. Coordinator tagged v0.9.2, deployed to `agentweaver-aks-2`, and verified all deployments healthy with `/api/health` 200.

## 2026-07-11T00:00:00Z — v0.9.19-rc1 staging release held for validation

Published the dependency-base propagation and UI fixes to staging as local-only v0.9.19-rc1: rebuilt api/frontend images, retagged unchanged mcp/agent-host images from v0.9.18-rc1, rolled out successfully, and verified /api/health=200. Commit fdbe9832 and tag v0.9.19-rc1 remain local and unpushed pending Ahmed validation.


## 2026-07-13T23:59:00-07:00 — BookClub regression
The v0.9.46-rc1 BookClub regression encountered `agenthost_launch_failed`, matching #305's branch-mismatch failure class. No preview URL was produced; retry after #305 is fixed.

## 2026-07-14T02:35:00-07:00 — Batch merge: #266/#270/#303/#305 fixes, #216/#278 policy work
Scribe merged inbox notes: #266 fixed and deployed to staging (v0.9.48-rc1), live run had not yet reached preview at merge time; #270 preview module failures traced to shared Kata nested-bwrap root cause, not a workspace sync race; #303 resolves deployed image tags through VERSION history before selective ACR builds; #305 re-verified already fixed on main (commit 1e54aab6), pending live E2E harness confirmation; #216 run/always tool approval policies made tool-wide; #278 requires confirmation before stopping a coordinator run.

## 2026-07-14T03:05:00-07:00 — #305 evidence, #180 wiring + live-data confirmation
#305 re-verified already fixed on main, pending one live harness-driven confirmation run. #180 App Insights workspace-id wiring re-verified fixed and live-validated for config/permissions; follow-up same batch closed the runtime gap, confirming real telemetry flows through the AppInsights query path (not DB fallback). Evidence posted to #180; not closed, left for sign-off.

## 2026-07-14T03:20:00-07:00 — #186 backend cross-check, clean bill of health
Cross-checked Trinity's #186 frontend gate-palette work at the API layer: confirmed two independent backend validation layers already reject dangling gate branches even when the frontend is bypassed. No fix needed, not blocking PR #190 / rubber-duck-186 review.

## 2026-07-14T10:15:00-07:00
#305 evidence writeup followup-cleanup pass; surface-check confirming #311 reserved-roles fix has no adjacent-surface regressions.


## 2026-07-14T10:15:00-07:00 (late arrival)
Shipped #313 fix: watchdog/executor-timeout race decoupled via WatchdogTimeoutGrace (60s) + 10-min floor scoped to Build/Test gate. 26 tests pass. Code complete, uncommitted, pending peer review + staging validation.


## 2026-07-14T11:05:00-07:00
Process note: #312 (Link2's fix) reopened by coordinator pending live v0.9.50-rc1 deploy validation, per Seraph's pass-3 closure-discipline finding.

## 2026-07-14T15:15:00Z — Queue-depth metric + deploy-path validation
Link's durable backlog-ready metric for #108 was rubber-duck approved and shipped in v0.9.50-rc1. Separate release-path verification also confirmed the #251/#303 tag-resolution/provenance chain still holds on the shipped build.

## 2026-07-15T17:30:00Z — Graceful shutdown fix for in-flight assistant termination (v0.9.67)
Root cause: `k8s/api-deployment.yaml` lacked `terminationGracePeriodSeconds` (default 30s) and `preStop` hook; ASP.NET Core default `ShutdownTimeout` (30s) was too short for legitimate 60-100+s multi-tool operator-assistant turns. On rolling deploy (11 releases/~31h), SIGTERM cancelled in-flight requests via `HttpContext.RequestAborted` with `System.OperationCanceledException`.

Fix: `k8s/api-deployment.yaml` now has `terminationGracePeriodSeconds: 120` + `preStop: sleep 5` hook; `apps/Agentweaver.Api/Program.cs` now sets `ShutdownTimeout = TimeSpan.FromSeconds(100)` (20s margin under K8s grace period for actual process teardown).

Scope exclusion: AgentHost per-run SandboxTemplate pods (`restartPolicy: Never`) not affected — no rolling deployment pattern there.

Merged to main (commit `c68b9055`), released as v0.9.67, verified live in staging with post-deploy smoke test success.

## 2026-07-16T17-19-26-07-00 — v0.9.69/v0.9.70: rollout false-alarms + stale-image fix

**Deploy false alarms:** `scripts/aks/30-deploy.ps1` reported "API deployment rollout failed"
(exit 1) twice this session (v0.9.69 and v0.9.70 deploys). Both were false alarms: `kubectl`
events showed transient `FailedScheduling` (insufficient CPU / untolerated taints on some nodes)
pushing new-pod scheduling past the script's wait timeout by ~1-2 minutes, plus normal image-pull
time. Pods reached `1/1 Running/Ready` shortly after; manual `kubectl rollout status` confirmed
success both times. **Reusable pattern:** treat `30-deploy` exit-1 as inconclusive, not proof of a
real break — check `kubectl get pods`/`kubectl rollout status` manually before escalating; the
script's timeout is tighter than worst-case scheduling+pull latency under transient node pressure.

**Stale-image catch (#251 failure mode):** After the user's requested `merge-docs-landing-main`
merge (`4c276761`, docs landing redesign) landed on `main` touching `apps/web/src` paths *after*
v0.9.69's images were already built, `scripts/aks/25-verify-image-provenance.ps1` correctly flagged
`agentweaver-frontend:v0.9.69` as a stale image. Fixed by bumping to v0.9.70 (`59a90c14`) and
rebuilding only the frontend image; api/mcp/agent-host correctly retagged unchanged. Final state:
4/4 images provenance-verified against the new HEAD. **Reusable takeaway:** run
`25-verify-image-provenance` after *any* merge to `main` that lands after a build — even a
disjoint/unrelated merge (docs-only, in this case) can invalidate an already-deployed image if the
merge touches a watched path.

## 2026-07-18T02:58:30-07:00 — Staging OAuth recovery learning

During the PowerShell reprovisioning of deleted staging `agentweaver-rg`, Link's background agent
mistakenly auto-resolved LOCAL-DEV GitHub OAuth credentials from .NET user-secrets and wrote them
to the staging Key Vault. This broke browser OAuth login (not bearer/API access) because staging
uses a separate OAuth App. The auto-resolve fallback was removed from
`15-setup-identity.ps1`/`.sh` in `75a84f38`; staging now requires explicit credentials. Correct
historical Key Vault values were restored as new latest versions, pods restarted, and
`40-verify.ps1` passed 23/23 with a live redirect at the new staging host. Always treat local
user-secrets as local-only; never use them as a staging credential fallback.
