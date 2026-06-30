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
