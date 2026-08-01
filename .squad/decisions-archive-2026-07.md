# Squad Decisions Archive — 2026-07

Archived on 2026-07-31T03:40:59+03:00 by Scribe during the publish-apps exploration cleanup. Entries are preserved verbatim from the working ledger and processed inbox; use this file for historical lookup instead of loading decisions.md whole.

---

## Archived working ledger snapshot from decisions.md

# Squad Decisions

## 2026-07-29 — Entra-first authentication, authorization, and GitHub-linking design

**Scope sources merged:** `tank-entra-authz-design.md`, `seraph-entra-security-review.md`, `smith-entra-test-questions.md`.

- Agentweaver will adopt **single-tenant Microsoft Entra ID sign-in** as the primary authentication gate and decouple GitHub from platform login.
- Tier 1 platform access will use Entra App Roles on the Enterprise App; the current implementation target is `PlatformAdmin`, `ProjectCreator`, `Contributor`, and `Viewer`, with `Billing` deferred until a real billing/admin surface exists.
- Tier 2 project/data access will use app-native RBAC keyed by verified Entra `oid`, with Azure-RBAC-style project assignments for `Owner`, `Contributor`, and `Viewer`.
- Existing multi-GitHub-account groundwork already in the repo (`GitHubIdentityLink`, `IMultiIdentityGitHubTokenStore`, linked-identity scopes, default/unlink semantics, and store-level tests) is the extension point; do not re-invent it.
- Security requirements are non-optional: enforce allowed Entra app roles server-side on every protected request, authorize project/resource ownership before any linked-token resolution or use, and cut over hard rather than dual-running legacy GitHub bearer auth beside the new Entra path.
- Migration should avoid unsafe automatic identity correlation. Preferred flow is first Entra sign-in followed by explicit GitHub relink, with only interactive assisted migration when a trustworthy signed-in proof exists in-session.
- Open implementation questions remain for endpoint-level role matrices, role-change/revocation semantics, Tier 1 vs Tier 2 precedence, project membership governance, GitHub-linking edge cases, Copilot entitlement probing, and legacy ownership/token migration. Smith should treat those areas as explicitly open until Tank/coordinator resolve them.

---

## 2026-07-29 — Entra bootstrap command stays in the Node Azure toolchain

**Scope source merged:** `link-entra-install-plan.md`.

- The Entra bootstrap will ship as the Node-based Azure toolchain command `scripts/azure/setup-entra-app.mjs`, surfaced as `npm run azure:setup-entra-app`, rather than as a new PowerShell/bash script.
- The command reconciles a **single-tenant** Entra app registration only, optionally accepts `--service-management-reference <id>` on create, and must remain idempotent when re-run.
- Reconciliation rules: find by exact display name or explicit `--app-id`, fail if an existing app is not single-tenant, merge redirect URIs without deleting existing ones, patch Agentweaver-managed App Roles through Microsoft Graph, and create the service principal only when missing.
- Until Tank's final platform-role vocabulary is fully wired, the bootstrap may use stable placeholder coarse roles (`Admin`, `Contributor`, `Viewer`) with `Agentweaver:`-prefixed managed descriptions so a later update can replace them cleanly.
- The command should print the expected future config surface for downstream wiring: `ENTRA_CLIENT_ID`, `ENTRA_TENANT_ID`, Key Vault secrets `entra-client-id` / `entra-tenant-id`, and appsettings/Kubernetes names `Auth__Entra__ClientId` / `Auth__Entra__TenantId`.
- Follow-up documentation and deployment/config plumb-through remain pending across the docs, params, k8s manifests, and Azure wiring files that Link enumerated.


---

## 2026-07-29T18-19-57+03:00 — Auth mode is a deployment-level switch, with Entra default and GitHubLegacy opt-in

**Scope source merged:** `Tank-auth-mode-becomes-deployment-level-switch-entra-de.md`.

- Agentweaver supports a deployment-level `Auth:Mode = Entra | GitHubLegacy`, defaulting new deployments to `Entra` while keeping `GitHubLegacy` as a supported deprecated opt-in.
- A running deployment is in exactly one mode at a time; this is not simultaneous dual auth within the same request/session path.
- `GitHubLegacy` preserves the existing GitHub org/team login gate and single-owner project semantics.
- `Entra` uses Entra OIDC sign-in, Tier-1 app roles, Tier-2 app-native project role assignments, and GitHub only as a linked-account capability.
- Deployments must be able to migrate later from `GitHubLegacy` to `Entra` without data loss, but the security invariant remains: no ambiguous dual auth path.

---

## 2026-07-29T19-20-30+03:00 — Foundational Entra/GitHubLegacy auth-mode slice landed with legacy-compatible validation

**Scope source merged:** `Tank-implemented-foundational-entra-githublegacy-auth-m.md`.

- The API now supports deployment-scoped `Auth:Mode = Entra | GitHubLegacy`, defaults new config to Entra, validates Entra bearer tokens against configured tenant/issuer/audience/JWKS, and enforces recognized platform app roles in Entra mode.
- Legacy-oriented test hosts were updated to opt into `GitHubLegacy` explicitly so existing GitHub/static-auth coverage stays stable while Entra-specific tests verify the new path.
- Deferred follow-up work from this foundational slice included Tier-2 project role assignments, linked-account CRUD, per-project GitHub identity override, Copilot entitlement probing, repo enumeration across linked identities, and full mode-switch session invalidation.

---

## 2026-07-29T20-40-54+03:00 — Removed shared GitHub-token fallback and formalized GitHub-vs-Agentweaver authorization boundaries

**Scope source merged:** `Tank-removed-runtime-shared-github-scope-fallback-and-c.md`.

- Agentweaver project roles govern only Agentweaver-side actions; they do not grant GitHub authority.
- Real GitHub success (clone/push/PR/admin) is determined only by the resolved linked GitHub identity's actual repository permission.
- The runtime installation-scope/shared-token fallback was removed; the app now always resolves caller-linked identity scope and fails closed when no authenticated user identity is available.
- Any future unattended GitHub automation must use an explicit, auditable linked system identity rather than an implicit shared installation token.

---

## 2026-07-29T21-03-21+03:00 — Web sign-in exchange codes are bound to auth mode, closing cross-mode session handoff gaps

**Scope source merged:** `Tank-bound-web-session-exchange-codes-to-auth-mode-and-.md`.

- Web session exchange codes are now prefixed with the issuing auth mode (`entra` or `githublegacy`).
- Redeeming an exchange code now fails if its embedded issuing mode does not match the deployment's current `Auth:Mode`.
- This closes the remaining mode-switch gap: flipping auth mode immediately invalidates outstanding browser sign-in handshakes from the old mode, while old-mode bearer tokens were already rejected by the new mode's request-auth pipeline.

---

## 2026-07-29 — Tier-2 project RBAC uses explicit Entra-oid role assignments with owner-protected membership management

**Scope source merged:** `tank-tier2-rbac.md`.

- Tier-2 project RBAC is implemented with explicit `ProjectRoleAssignment` records keyed by verified Entra `oid`, storing `ProjectId`, `PrincipalId`, `Role`, `GrantedBy`, and `GrantedAt`.
- Effective access resolves as: `PlatformAdmin` => implicit project `Owner`; otherwise explicit assignment for `(projectId, principalId)`; no assignment means no access.
- In Entra mode, `Viewer` reads project-scoped resources, `Contributor` mutates project operational/content resources, and `Owner` manages project administration/settings/membership.
- Membership APIs (list/grant/upsert/revoke) are Owner-or-PlatformAdmin only. Contributors/viewers cannot self-promote.
- New Entra projects auto-seed the creator as the first explicit `Owner`, and the system blocks revoking/demoting the last explicit `Owner` until another explicit owner exists.
- Existing pre-RBAC projects may need a migration/backfill plan if they are to be administered in Entra mode without recreation.

---

## 2026-07-29 — Linked GitHub accounts and per-project identity selection ride on the existing multi-identity token-store foundation

**Scope source merged:** `tank-linked-accounts-api.md`.

- The linked-account API reuses the existing `IMultiIdentityGitHubTokenStore` / `GitHubIdentityLink` model as the sole source of truth; no parallel link store or installation-token fallback was introduced.
- Secondary account linking reuses `/auth/github/callback`, binding OAuth `state` to the current Entra `oid` so the callback links a GitHub identity to that Entra user instead of creating a new primary GitHub session.
- Added account endpoints under `/api/auth/github-accounts` for list, link, unlink, default selection, and cross-account accessible-repo enumeration.
- Added per-user per-project GitHub identity overrides plus project endpoints to read/update the effective linked identity for a project.
- Project override authorization uses Tier-2 RBAC (`Viewer+` read, `Contributor+` write).
- Cross-account repo enumeration uses each linked account's own GitHub token and reports which login can access each repo plus GitHub-reported effective permission.
- Copilot entitlement probing is cached on `GitHubIdentityLink` and refreshed only when absent or older than 12 hours.
- Unlinking a linked login also removes project overrides pointing at that login for the same Entra user so resolution falls back cleanly.

---

## 2026-07-30T00:11:00+03:00 — Trinity UX aligns Entra-first sign-in, linked GitHub accounts, project identity clarity, and quick account switching

**Scope source merged:** `trinity-entra-ui.md`.

- In Entra mode, the sign-in screen now presents Microsoft Entra ID as the only primary sign-in action and explains the two-step model: sign in first, then link GitHub if repository/Copilot work is needed.
- After Entra sign-in, users with zero linked GitHub accounts see a non-blocking warning that browsing Agentweaver still works but GitHub-backed actions require linking an account.
- Global Account settings now owns the linked-account lifecycle: link another GitHub account, unlink with impact warnings, and understand default-account / project-override consequences.
- Project Settings explicitly explains that Agentweaver project roles govern Agentweaver actions only, while actual GitHub success depends on the resolved linked GitHub identity's real repository permission.
- The GitHub import flow requires at least one linked account in Entra mode and labels repos by which linked account can access them.
- The shell footer includes a compact GitHub account switcher: current active identity, switch-to-another-account actions, add account, and sign out. In a project context it switches that project's resolved GitHub identity; outside a project it changes the user's default linked account.


---

## 2026-07-30 — Last-owner RBAC invariant is now enforced atomically in the persistence layer

**Scope source merged:** `tank-rbac-race-fix.md`.

- The “must retain at least one explicit project owner” rule was moved out of service-layer check-then-write flow and into the role-assignment stores themselves.
- `IProjectRoleAssignmentStore` now exposes guarded mutation results so service/API layers consume explicit statuses such as `Ok`, `LastOwnerConflict`, and `NotFound` rather than assuming unconditional writes.
- SQLite now protects owner removal/demotion with `BEGIN IMMEDIATE` plus invariant check and write in the same transaction, preventing concurrent last-owner removals from both succeeding.
- EF/Postgres-style storage now uses serializable transactions with retry on serialization failure so the invariant is enforced safely across multi-instance deployments.
- This closes Seraph-5's red RBAC-race finding by making the last-owner guarantee DB-atomic instead of process-local.

---

## 2026-07-30 — Legacy projects in Entra mode use proven-owner lazy backfill, otherwise fail closed

**Scope source merged:** `tank-rbac-backfill.md`.

- Pre-RBAC legacy projects in `Auth:Mode = Entra` now use a hybrid fail-closed backfill strategy.
- If a legacy project has zero Tier-2 assignments and the caller is a platform admin, the admin may still access it via Tier-1 implicit owner rights and can explicitly assign owners through the normal role-assignment API.
- If a non-admin Entra caller has a linked GitHub account whose login matches the legacy `Project.Owner`, Agentweaver lazily backfills that caller as the first explicit Tier-2 `Owner` on first access/discovery.
- Otherwise the project fails closed with a specific `project_unclaimed_in_entra_mode` error instructing the caller to use a platform admin claim flow or link/sign in as the legacy GitHub owner.
- This preserves legitimate legacy ownership continuity without reintroducing broad fallback or a first-caller-wins escalation path.

---

## 2026-07-30 — Shared auth-mode epoch hardens rolling mode-switch invalidation across pods

**Scope source merged:** `tank-mode-epoch.md`.

- Added a shared singleton `auth_mode_epochs` row in the backing store plus an `AuthModeEpochService` and startup hosted service.
- On startup, if a process sees a different configured `Auth:Mode` than the persisted shared mode, it atomically flips the mode and increments the shared epoch.
- Each process keeps its startup `(mode, epoch)` snapshot and checks the shared row on every authenticated API request before honoring any bearer, including test-bypass/internal-key flows. If the process is stale, it rejects with `401` immediately.
- `WebSessionExchangeService` also checks the shared epoch before issuing or redeeming one-time exchange codes, so old pods cannot keep minting or redeeming legacy-mode handshakes during a rolling restart.
- This addresses Seraph-5's yellow mode-switch concern by extending invalidation from process-local behavior to cluster-wide rolling transitions without adding a distributed cache/control plane.


---

## 2026-07-30 — Auth-mode epoch test failures traced to shared SQLite sidecar path resolution, not the epoch service

**Scope source merged:** `tank-epoch-test-isolation-fix.md`.

- The cross-test-host auth-mode epoch contamination was caused by a `MemoryDbContext` path-resolution bug, not by a static in-process cache inside `AuthModeEpochService`.
- The SQLite-backed memory sidecar path had been resolving to `<directory-of-Database:Path>\memory.db`, so multiple test hosts using distinct DB filenames in the same temp directory silently shared one physical `memory.db` sidecar.
- This let one test host's auth-mode epoch row invalidate another host's requests, causing cross-class 401s when Entra and GitHubLegacy fixtures ran together.
- The fix introduces a shared SQLite memory-sidecar path resolver used by `MemoryDbContext` and `SqliteRunEventStream`: preserve the legacy `agentweaver.db -> memory.db` companion path, but give custom `Database:Path` files distinct per-database sidecars unless `Database:MemoryPath` is explicitly configured.
- This restores per-test-host isolation while preserving intended same-database epoch sharing semantics for real rolling mode-switch scenarios.


---

## 2026-07-30 — PR #640 merge conflicts against `dev` were resolved additively and verified cleanly

**Scope source merged:** `tank-merge-conflict-resolution.md`.

- The three merge conflicts between the Entra authz branch and concurrently landed `dev` workflow-trigger work were resolved additively rather than by choosing one feature line over the other.
- `WorkflowTriggerEndpoints.cs` kept both the Entra/project-RBAC authorization requirement and `dev`'s workflow-trigger predicate-compatible `FireEventAsync(..., payload: null, ...)` call shape.
- `ProjectSettingsPage.tsx` kept both the Access-section RBAC/linked-account handlers and `dev`'s automatic-webhook creation UI/state.
- `ProjectSettingsPage.test.tsx` kept mocks for both the RBAC/linked-account APIs and the new automatic webhook API.
- Combined verification passed cleanly for the relevant backend slice (`Auth|ProjectRole|Rbac|WorkflowTrigger`: 278 passed, 34 skipped, 0 failed), and the branch was pushed without force-push.


---

## 2026-07-30 — Missing Postgres migration added for the shared auth-mode epoch table

**Scope source merged:** `tank-postgres-migration-fix.md`.

- PR #640's shared auth-mode epoch work required matching Postgres EF migration, designer, and model-snapshot updates in `Agentweaver.Api.Migrations.Postgres`, not just the SQLite migration in `apps/Agentweaver.Api/Migrations`.
- Without that Postgres migration, `AuthModeEpochStartupService` booted into `42P01: relation "auth_mode_epochs" does not exist` as soon as startup/tests queried the table on a Postgres-backed app.
- The follow-up landed as commit `cb3f2858`; whenever `MemoryDbContext` schema changes, the SQLite and Postgres migration chains must stay in lockstep.

---

## 2026-07-30T04:35:43Z — PR #640 shipped after the final Operator tool-policy fix and staging verification

- After the Postgres migration follow-up, the only remaining CI failure was `OperatorToolApprovalPolicyTests`: `github_accounts_list` and `github_repos_list` were missing from the Operator tool approval policy's ungated classification list.
- The coordinator added both MCP tools to `packages/Agentweaver.AgentRuntime/OperatorToolApprovalPolicy.cs` in commit `2f54998d`, restoring the intended rule that read-only GitHub account/repo discovery tools stay ungated for Operator runs.
- With that fix in place, all PR #640 checks were green (`.NET tests`, `Web tests`, `lint`, `docs`, `changeset advisory`, `architecture diagrams`, and `node toolchain`), so the branch was squash-merged into `dev` as `60699a37297c04856c6c3b3f552882778c23adbc`.
- The merged SHA was then deployed to the operator's staging AKS environment via `npm run azure:deploy-from-commit -- origin/dev` using deployment tag `60699a3`; all four images (`api`, `frontend`, `mcp`, `agent-host`) provenance-matched the source commit.
- Post-deploy verification passed cleanly: `npm run azure:verify` reported 25/25 checks green against `https://agentweaver.6a67c46fa5c83b0001c97c7c.westus2.staging.aksapp.io/`.
- Treat this as the closure checkpoint for the Entra ID dual-mode authn/authz overhaul: implementation, CI repair, merge, staging deploy, and live verification all completed on the merged `dev` SHA.

## 2026-07-31T02:54:19.830+03:00 — Scribe inbox merge: pending decision entries

Merged 98 pending inbox entries from .squad/decisions/inbox/; skipped 0 exact/near duplicates.

---

## 2026-07-29T02-10-56: Cluster quota health now uses pod and SandboxClaim headroom
**By:** Copilot
**What:** Cluster quota health now uses pod and SandboxClaim headroom
**References:** #624, #217, apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs
**Why:** For cluster diagnostics issue #624, the `agent_pod_quota` health check now derives effective admission headroom from the tighter of the namespace `pods` quota and `count/sandboxclaims.extensions.agents.x-k8s.io` quota. Healthy is `>= 10` remaining objects, warning is `1-9`, and critical is `<= 0`. Rationale: each new AgentHost consumes one pod and one SandboxClaim; with default 200-object caps, single-digit headroom means the namespace is near admission exhaustion even though CPU/memory are intentionally uncapped after #217.

---

## 2026-07-29T21:17:46+03:00: User directive
**By:** Ahmed Sabbour (sabbour) (via Copilot)
**What:** The legacy GitHub-org-membership auth model must remain SUPPORTED as an opt-in fallback for deployments/users who don't want to use Entra ID — not a hard cutover. Show a clear warning when legacy mode is active (e.g., "you're using the deprecated GitHub-org auth path; consider migrating to Entra ID"). This supersedes Seraph's earlier "hard cutover, no dual-running" recommendation — reconcile as: Entra is the recommended/default path, but legacy GitHub-org auth remains a supported, clearly-flagged configuration choice, not simultaneously valid ambiguous sessions.
**Why:** User request — captured for team memory

---

## 2026-07-29T21:37:12+03:00: User directive
**By:** Ahmed Sabbour (sabbour) (via Copilot)
**What:** Do not frame GitHubLegacy auth mode as "deprecated" anywhere (docs, UI banners, logs). It is a supported, alternate way to authorize and operate — a parallel mode, not a legacy/sunset path. Remove any deprecation language from the design/warning copy.
**Why:** User request — captured for team memory

---

## 2026-07-29T22:35:49+03:00: User directive
**By:** Ahmed Sabbour (sabbour) (via Copilot)
**What:** Two related architecture directives:
1. Do NOT bind what a user can do on GitHub to their Agentweaver Project role (Owner/Contributor/Viewer). These are independent: what a user can actually do on GitHub is governed entirely by their own real GitHub permissions on that specific repo/organization (via whichever linked GitHub identity is resolved for the project). Agentweaver's Project role only governs Agentweaver-side actions (API/UI), not GitHub-side ones. Make this separation explicit/visible, not implicit.
2. Remove the shared/fallback GitHub token mechanism entirely: `FixedInstallationScopeProvider` (Auth:GitHub:ScopeProvider = "installation") and the `GitHubTokenScope.Installation` fallback in `CallerTokenScopeProvider` for background tasks with no caller context. No operation should silently use a shared token that isn't tied to a specific resolved user identity — background/unattended work needs a different, explicit solution (not a magic shared fallback), or must fail closed if no caller identity can be resolved.
**Why:** User request — captured for team memory; corrects a design gap where a shared "installation" token could bypass per-user GitHub permission boundaries regardless of Agentweaver Project role.

---

## 2026-07-31T02:38:36+03:00: User directive
**By:** Ahmed (via Copilot)
**What:** "narration between 2.3 and 3.1 is abrupt; 3.2 drop 'This one's not tied to the specs we just generated — it's a standalone post on' and replace that I'm trying to research and write a post on .."
**Why:** User request — captured for team memory

---

## 2026-07-31T03:01:37+03:00: User directive
**By:** Ahmed (via Copilot)
**What:** "You'll need to regenerate all audio and video. There's nothing you can reuse."
**Why:** User request — captured for team memory

---

## 2026-07-27T00-09-08: Fix #539: mirror team ledger into run worktree at commit + honest /memory/export failure reporting
**By:** copilot
**What:** Fix #539: mirror team ledger into run worktree at commit + honest /memory/export failure reporting
**Why:** Root cause of #539: the DB is the authoritative store for decisions/inbox/memory/sessions, but the `.squad/decisions.md` (and `.agentweaver/context/*`) file mirror was only ever exported into `project.WorkingDirectory` (the base checkout). Autonomous runs commit and push a SEPARATE per-run worktree (`WorktreeManager.CommitChanges(worktreePath)`), so the base-dir mirror was never committed/pushed — users never saw their decisions/memory persisted in the repository, even though the API reported success. Additionally, `/memory/export` and `MemoryExportHelpers.TryExportAsync` unconditionally returned `{exported:true}` even when the filesystem write threw (caught + warning-logged), violating spec #25 ("Sync actions report success OR actionable conflicts").

Design decisions:
1. WORKTREE-COMMIT-TIME EXPORT IS THE REPO MIRROR MECHANISM. Introduced a shared `MemoryLedgerExporter` (apps/Agentweaver.Api/Memory) and hooked it into `WorktreeOperationsAdapter.CommitChanges` — the single chokepoint that AgentTurnExecutor calls for the deliverable commit of both autonomous and reviewed runs. The adapter (API layer, singleton) resolves a scope via IServiceScopeFactory to get IRunStore + MemoryDbContext, maps runId -> run.ProjectId, and materializes the ledger INTO the worktree just before staging/commit, so it rides the same commit/push flow. Chose the adapter (not AgentRuntime.AgentTurnExecutor) because AgentRuntime is deliberately decoupled from the DB (only sees IWorktreeOperations); putting DB-backed export in the API-layer adapter keeps that boundary intact. Uses sync-over-async (GetAwaiter().GetResult()) — safe under ASP.NET Core (no SynchronizationContext) and required by the synchronous CommitChanges signature.
2. GUARD AGAINST REPO POLLUTION. The worktree export only runs when `HasExportableContentAsync` is true (>=1 active decision OR any agent memory), so repos that never used the memory feature don't get an empty decisions.md injected. Mirror failures are caught and warning-logged — they must NEVER fail the run's real deliverable commit.
3. EXPORT ACTIVE DECISIONS ONLY. `MemoryLedgerExporter` mirrors only decisions with Status=="active" (accepted state per spec #25), matching PostRunScribeService. The old /memory/export and TryExportAsync exported ALL decisions (latent bug: superseded/archived would show as live boundaries). Unified to active-only across all export paths.
4. EXPLICIT SYNC ACTIONS REPORT HONEST FAILURES; PER-WRITE DB WRITES STAY AUTHORITATIVE. `/memory/export` now returns Results.Problem (500) when the on-disk mirror write fails, instead of {exported:true}. `TryExportAsync` now returns bool so `/memory/import` can surface `mirror_exported`. Per-write decision/session endpoints keep DB-authoritative success semantics (the DB write is the durable truth; the file mirror is best-effort), consistent with the existing invariant test Test_Memory_Record_DoesNotSynchronouslyExportWorkspaceSnapshot.

Tests added: WorktreeMemoryMirrorTests (mirror lands in worktree AND is committed; empty-ledger repos are not polluted) and two MemoryEndpointsTests (successful export writes decisions.md; failed export returns an error envelope, not {exported:true}). Deduped PostRunScribeService.ExportAsync onto MemoryLedgerExporter. All targeted suites green (Memory/Decisions/Tools 118, Worktree/Restart 14, new+memory 27).</body>
<parameter name="references">["issue-539", "spec-25", "morpheus", "scribe"]

---

## 2026-07-27T01-33-09: Fix #542: keep sandbox pod alive while a preview is active (defer turn-end release + orphan reap)
**By:** Cypher
**What:** Fix #542: keep sandbox pod alive while a preview is active (defer turn-end release + orphan reap)
**Why:** Root cause (confirmed via Trinity's live-staging evidence in #542 + code path review): `start_preview` genuinely works and returns a real `preview_url`/`keepalive_url`, but the preview points at the run's ephemeral sandbox pod. At subtask-turn end the coordinator/run flow calls `KubernetesSandboxExecutor.ReleaseAgentHostPodAsync(runId)` which UNCONDITIONALLY deletes the SandboxClaim → the pod is torn down within minutes, so the URL 404s from istio-envoy before any human-review gate can view it. Nothing renewed the pod's life; the pre-existing `TODO(morpheus)` in `SandboxPreviewService.KeepAliveAsync` explicitly noted the missing "renew the backing SandboxClaim/pod TTL" seam.

Design decision — Option (a) from the issue: keep the pod alive while a preview is active, rather than making the preview durable/pod-independent (Option b, much larger: persist built artifact/image + separate long-lived runner). Chose (a) because it's surgical, reuses the existing annotation-driven preview reaper for bounded teardown, and matches the existing `KeepAfterRun=true` design intent.

Implementation:
1. New `ISandboxPreviewService.HasActivePreviewAsync(runId)` — returns true iff a preview HTTPRoute exists for the run whose idle (`preview-expires-at`) AND max (`preview-max-until`) are both still in the future (reuses `PreviewReaper.Decide` with podExists:true, because the pod IS present at the teardown boundary — pod existence is not the signal here). Fail-safe: any probe exception returns false so deferral happens only on positive evidence (leak-safe).
2. `ReleaseAgentHostPodAsync` defers (skips claim delete + credential delete + registry unregister) when `HasActivePreviewAsync` is true. Injected `ISandboxPreviewService` through `SandboxExecutorRouter` → `KubernetesSandboxExecutor` (optional param, null in unit tests / preview-disabled → pre-#542 behavior). No DI cycle: preview service deps (IPreviewRunnerHttpClient, IAgentHostOriginResolver, ISecretStore) don't pull ISandboxExecutor.
3. `AgentHostReaperService.SweepOrphanedPodsAsync` also skips reaping a terminal run's orphaned claim while it has an active preview (uses the original run id from the claim's run-id annotation). This is REQUIRED — the orphan reaper counts only InProgress/Pending/AwaitingReview as active, so a completed subtask's claim would otherwise be reaped immediately, defeating the release-path deferral.

Bounded eventual teardown (no pod leak): keepalive bumps idle expiry; with no keepalive the preview idle-expires after `Sandbox:Preview:IdleTimeoutMinutes` (default 30) or hard cap `MaxLifetimeHours` (default 8). The preview reaper (`SandboxPreviewReaperService`, ~60s) deletes the route on expiry, after which the next `AgentHostReaperService` sweep reaps the now-orphaned claim. Reused the EXISTING config values (IdleTimeoutMinutes / MaxLifetimeHours) rather than inventing new ones, per the issue's guidance.

Tests: HasActivePreviewAsync (alive / idle-expired / no-route / other-run) in SandboxPreviewServiceClusterTests; ReleaseAgentHostPod defers-vs-tears-down (+ null-service) in KubernetesSandboxExecutorClaimTests; reaper defers-vs-reaps in AgentHostReaperCredentialTests. All targeted + full Preview/Sandbox suites green (519 passed). Verification level: unit/integration + code-path review (no live-staging rerun performed by me — see PR notes).</body>
<parameter name="references">["issue-542", "issue-529", "morpheus", "trinity", "seraph"]

---

## 2026-07-30T00-40-23: GitHub webhook triage demo readiness: webhook plumbing exists on main; exact comment/label predicates are only on #641 branches
**By:** Cypher
**What:** GitHub webhook triage demo readiness: webhook plumbing exists on main; exact comment/label predicates are only on #641 branches
**References:** issue #641, apps/Agentweaver.Api/Endpoints/GitHubWebhookEndpoints.cs, apps/Agentweaver.Api/Endpoints/WorkflowTriggerEndpoints.cs, docs/guide/workflows.md
**Why:** Findings for Beat 3.2 live demo prep

1) What exists today on the main checkout
- Agentweaver already mounts both a generic event-trigger endpoint and a real GitHub webhook receiver: `app.MapWorkflowTriggerEndpoints();` and `app.MapGitHubWebhookEndpoints();` are both wired in startup. [apps/Agentweaver.Api/Program.cs:1073-1074]
- The manual trigger endpoint is `POST /api/projects/{projectId}/workflow-events`; its own XML doc says it is callable directly for manual testing/bespoke integrations. [apps/Agentweaver.Api/Endpoints/WorkflowTriggerEndpoints.cs:12-15,23,46]
- The real webhook receiver is `POST /api/projects/{id}/webhooks/github`; it verifies `X-Hub-Signature-256`, requires `X-GitHub-Event`, normalizes the repo name, then emits `github.<event>` and also `github.<event>.<action>` when the payload has an `action`. [apps/Agentweaver.Api/Endpoints/GitHubWebhookEndpoints.cs:24,56,66-68,88-89,96-105,125]
- On main, workflow event triggers only match a single `event_name`; there is no per-trigger predicate field in the current domain model. The current `WorkflowTrigger` has `Type`, schedule fields, and `EventName` only. [apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:165-188]
- The current event-trigger service only checks `trigger.EventName == eventName`; it does not inspect labels, comment bodies, branches, or review state. [apps/Agentweaver.Api/Workflows/WorkflowEventTriggerService.cs:65-68]
- The current webhook payload model is intentionally minimal: repository full name plus optional `action`; it does not include comment text or labels on main. [apps/Agentweaver.Api/Webhooks/GitHubWebhookPayload.cs:6-18]
- Therefore, on main, GitHub issue comments and issue labels are usable only as raw event names (for example `github.issue_comment.created` or `github.issues.labeled`), not as filtered predicates such as “only when the comment equals `/agentweaver:triage`” or “only when label X was applied”. This follows from the emitted event naming plus the lack of trigger predicates. [apps/Agentweaver.Api/Endpoints/GitHubWebhookEndpoints.cs:96-105; apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:183-188; apps/Agentweaver.Api/Workflows/WorkflowEventTriggerService.cs:65-68]

2) What #641 adds (seen in the local #641 backend/UI worktrees)
- The #641 backend extends event triggers with an `if` predicate list; schedule triggers explicitly reject `if`, while event triggers accept it only for GitHub-style event names. [C:/Users/asabbour/Git/agentweaver/.worktrees/641-trigger-backend/apps/Agentweaver.Api/Workflows/WorkflowDefinitionLoader.cs:241,287,296,300,310]
- The #641 trigger DTOs expose `if` plus structured predicates: `hasLabel`, `isNotLabeledWith`, `baseBranch`, `reviewState`, `ref`, `category`, `commentMatches`, and nesting via `or`/`not`. [C:/Users/asabbour/Git/agentweaver/.worktrees/641-trigger-backend/apps/Agentweaver.Api/Workflows/WorkflowDtos.cs:30,35-42,64,90]
- The #641 predicate evaluator supports labels on `issues`/`pull_request`, base branch on `pull_request`, review state on `pull_request_review`, ref on `push`, category on `discussion`, and exact comment matching on `issue_comment`. [C:/Users/asabbour/Git/agentweaver/.worktrees/641-trigger-backend/apps/Agentweaver.Api/Workflows/WorkflowTriggerPredicateEvaluator.cs:25-35,37-43,54-60]
- The #641 webhook payload model is expanded specifically to carry labels, PR base ref, review state, discussion category, comment body, and ref so those predicates can be evaluated. [C:/Users/asabbour/Git/agentweaver/.worktrees/641-trigger-backend/apps/Agentweaver.Api/Webhooks/GitHubWebhookPayload.cs:8,23,37-39,51,57]
- The #641 event-trigger service evaluates `trigger.if` before firing. [C:/Users/asabbour/Git/agentweaver/.worktrees/641-trigger-backend/apps/Agentweaver.Api/Workflows/WorkflowEventTriggerService.cs:50,54,70]
- The #641 UI explicitly lists supported GitHub events as `issues`, `issue_comment`, `pull_request`, `pull_request_review`, `push`, `release`, and `discussion`. It maps predicates by event: `issues` → label predicates, `issue_comment` → `commentMatches`, `pull_request` → labels/baseBranch, `pull_request_review` → `reviewState`, `push` → `ref`, `discussion` → `category`. [C:/Users/asabbour/Git/agentweaver/.worktrees/641-trigger-ui/apps/web/src/utils/workflowYaml.ts:86-105,123-129]
- In that UI, `commentMatches` is presented as “Exact command match”, with hint text: “Matches the full comment exactly, for example /agentweaver:triage.” It serializes the value as an anchored regex `^...$`, so the phrase is configurable rather than hardcoded. [C:/Users/asabbour/Git/agentweaver/.worktrees/641-trigger-ui/apps/web/src/pages/WorkflowsPage.tsx:188,231-232; C:/Users/asabbour/Git/agentweaver/.worktrees/641-trigger-ui/apps/web/src/utils/workflowYaml.ts:278,282,367-368]
- The #641 tests exercise both a label-triggered event (`github.issues.labeled` with labels like `bug` / `needs triage`) and an issue-comment-triggered event (`github.issue_comment.created` with `/agentweaver:triage`). [C:/Users/asabbour/Git/agentweaver/.worktrees/641-trigger-backend/tests/Agentweaver.Tests/Workflows/WorkflowEventTriggerServiceTests.cs:99,101-102,152,338,347]

3) Credentials/config actually needed
- I found no repo-documented “Pass Key”/passkey requirement for this flow. The documented sign-in/config knobs are a GitHub OAuth App client ID, client secret, and callback URL. [docs/guide/configuration.md:32-35]
- Local/dev docs also show the OAuth client secret is stored via `dotnet user-secrets`, and the development example additionally documents an optional `Providers:GitHubCopilot:GitHubToken` PAT for Copilot model access. [README.md:131,186,194-206,321-325; apps/Agentweaver.Api/appsettings.Development.json.example:2; .env.example:2-5]
- The auth docs say web sign-in uses the configured GitHub App user-to-server / OAuth flow, and the user approves Agentweaver in GitHub. [docs/experience/onboarding-auth.md:56,73; docs/deep-dive/auth-security.md:169]
- For actual GitHub operations, Agentweaver requires a real linked GitHub identity; the Settings page says there is “no shared fallback token,” and the security deep dive says clone/push/PR/admin rights depend on the resolved linked GitHub identity’s real repo permission. [apps/web/src/pages/ProjectSettingsPage.tsx:993,1025,1046,1076; docs/deep-dive/auth-security.md:47]
- The webhook itself uses a per-project secret stored by reference in the project record, generated by `POST /api/projects/{id}/webhook-secret/rotate`, stored in the secret store, and then verified from `X-Hub-Signature-256` on delivery. [packages/Agentweaver.Domain/Project.cs:17-20; packages/Agentweaver.Domain/IProjectStore.cs:11-14; apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs:243-261; apps/Agentweaver.Api/Endpoints/GitHubWebhookEndpoints.cs:24,56]

4) UI/API setup path that exists today
- Project Settings has separate **Repository** and **Webhooks** sections: Repository is “Connect or create the GitHub repository for this project,” Webhooks is “Configure GitHub event delivery for this project.” [apps/web/src/pages/ProjectSettingsPage.tsx:90-97]
- The Webhooks settings page computes and shows the payload URL as `<public-api-origin>/api/projects/{projectId}/webhooks/github`, and exposes a Generate/Rotate secret action with a one-time warning (“Copy this secret now. You won’t be able to see it again.”). [apps/web/src/pages/ProjectSettingsPage.tsx:2,583,1105-1109,1123,1130]
- The workflow guide gives the GitHub-side steps verbatim: open project **Settings → Webhooks**, click **Generate secret** in Agentweaver, then in GitHub add a webhook with the project-specific payload URL, content type `application/json`, the generated secret, and the events your workflow needs. The guide also says the old global `/api/webhooks/github` URL is unsupported. [docs/guide/workflows.md:94-107]
- The same guide documents that a GitHub delivery fires `github.<event>` and, if an action exists, `github.<event>.<action>`; its example event-triggered workflow uses `event_name: github.issues.opened`. [docs/guide/workflows.md:109-132]
- The workflow authoring flow today is still YAML-first for event triggers: edit/create the workflow, save it under `.agentweaver/workflows/`, then click **Sync** because Agentweaver does not watch the filesystem. [docs/guide/workflows.md:70,76,84]
- The current Workflows page only exposes schedule management in the UI (`Manual only`, `Add schedule`, `Edit schedule`), and the current YAML helper only mutates schedule triggers. There is no event-trigger editor in the main UI yet. [apps/web/src/pages/WorkflowsPage.tsx:408,413,482,652; apps/web/src/utils/workflowYaml.ts:192,195,208-210]

5) Practical demo conclusion
- Native webhook wiring exists today on main.
- Native per-workflow exact comment text / label-name filtering does NOT exist on main; that capability is what the #641 backend/UI worktrees are adding via `trigger.if` predicates. [apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:165-188; apps/Agentweaver.Api/Webhooks/GitHubWebhookPayload.cs:6-18; C:/Users/asabbour/Git/agentweaver/.worktrees/641-trigger-backend/apps/Agentweaver.Api/Workflows/WorkflowDefinitionLoader.cs:287,296,300,310; C:/Users/asabbour/Git/agentweaver/.worktrees/641-trigger-ui/apps/web/src/utils/workflowYaml.ts:123-129]
- So for a live demo on the current main/dev behavior, the safe truth is: you can show “comment-created starts a workflow” or “issue-labeled starts a workflow,” but you cannot honestly claim “only `/agentweaver:triage` starts it” or “only label `agentweaver:triage` starts it” unless the #641 predicate work is deployed.

Recommended runbook for recording
A. If recording against current main/dev behavior
1. Ensure the deployment has GitHub sign-in configured (`Auth:GitHub:ClientId`, `Auth:GitHub:ClientSecret`, matching `Auth:GitHub:CallbackUrl`). [docs/guide/configuration.md:32-35]
2. Sign into Agentweaver with GitHub App/OAuth and ensure the project has a real linked GitHub identity for repo actions. [docs/experience/onboarding-auth.md:73; apps/web/src/pages/ProjectSettingsPage.tsx:993,1025,1046,1076]
3. In the project’s **Settings → Repository**, connect or create the target GitHub repo. [apps/web/src/pages/ProjectSettingsPage.tsx:90-91; apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs:286,304-308]
4. In **Settings → Webhooks**, copy the payload URL and generate the secret. [apps/web/src/pages/ProjectSettingsPage.tsx:583,1105-1109,1123,1130]
5. In GitHub repo **Settings → Webhooks → Add webhook**, paste that payload URL, set content type `application/json`, paste the secret, and subscribe to the needed event(s). [docs/guide/workflows.md:94-107]
6. Author the workflow trigger in YAML (because the main UI only edits schedules):
   - comment demo: `trigger: { type: event, event_name: github.issue_comment.created }`
   - label demo: `trigger: { type: event, event_name: github.issues.labeled }`
   Save under `.agentweaver/workflows/` and click **Sync**. [docs/guide/workflows.md:70,76,84,109-132; apps/web/src/pages/WorkflowsPage.tsx:413,482; apps/web/src/utils/workflowYaml.ts:192-210]
7. Dry-run before filming: create a disposable issue, then add any comment (for the comment-trigger variant) or add any label (for the label-trigger variant). GitHub should deliver to the webhook, and Agentweaver should create a Ready backlog task / visible run on the board. [apps/Agentweaver.Api/Endpoints/GitHubWebhookEndpoints.cs:96-105; apps/Agentweaver.Api/Workflows/WorkflowEventTriggerService.cs:9-12,86-87]
8. On camera, be precise: say “a GitHub issue comment” or “an issue label event” triggers the workflow — not “the exact `/agentweaver:triage` command” or “label `agentweaver:triage` only.”

B. If you need the exact `/agentweaver:triage` or `agentweaver:triage` story on camera before #641 is deployed
1. Same setup as above for repo + webhook.
2. Either deploy the #641 predicate work, or use a small manual fallback:
   - create the real GitHub issue;
   - add the visible comment `/agentweaver:triage` or label `agentweaver:triage` in GitHub;
   - then manually invoke Agentweaver’s trigger interface with an authenticated POST to `/api/projects/{projectId}/workflow-events` using the matching `event_name`.
   The endpoint is explicitly intended for manual testing/bespoke integration and returns `fired_workflow_ids`. [apps/Agentweaver.Api/Endpoints/WorkflowTriggerEndpoints.cs:12-15,23,40,46]
3. Smallest on-camera CLI fallback examples:
   - `gh issue comment <num> --body "/agentweaver:triage"`
   - `gh issue edit <num> --add-label "agentweaver:triage"`
   - then `curl -X POST https://<host>/api/projects/<projectId>/workflow-events -H "Authorization: Bearer <Agentweaver token>" -H "Content-Type: application/json" -d '{"event_name":"github.issue_comment.created"}'`
     or `...{"event_name":"github.issues.labeled"}`
4. If the goal is specifically to prove the exact phrase/label matching behavior, do not claim that on the current main deployment; wait for the #641 predicate feature or frame the manual POST honestly as a controlled trigger simulation.

---

# Dozer decision note — issue #621

Date: 2026-07-29

## Summary
Fixed the terminal merge conflicts that Squad-bootstrapped projects hit on canonical
bookkeeping files across concurrent coordinator runs, by **externalizing** those files
from the per-run branch-merge path (chosen solution 1).

## What I built
1. **`SquadStateConsolidationService`** (`apps/Agentweaver.Api/Squad/`): a project-level
   `BackgroundService` (modeled on `WorkflowScheduleTriggerService`) that, per active
   project per tick, idempotently consolidates `.squad/decisions/inbox/*.md` drop-box
   entries into the canonical `.squad/decisions.md` on the project's real **default
   branch**, via a single focused commit — NOT via `WorktreeManager`'s per-run merge
   machinery. It is now the SOLE writer of the canonical ledger.
   - Guarded by the same `RepositoryMergeLock` used for run merges (no new race).
   - Idempotent: each appended entry carries a content-addressed
     `<!-- squad-consolidated: {blobSha} -->` marker and the processed inbox files are
     deleted in the same commit, so re-ticks are no-ops and content is never duplicated.
   - Config: `Squad:StateConsolidationEnabled` (default true) +
     `Squad:StateConsolidationIntervalSeconds` (default 60), matching the
     `Workflows:ScheduleTriggerEnabled` convention so hermetic tests can disable it.

2. **`WorktreeManager` merge exclusion** (issue #621): the three `MergeCommits(origin,
   worktree)` 3-way call sites now go through `MergeCommitsPreferringSquadStateFromOurs`,
   which neutralizes the canonical ledgers to path-level "ours" (origin/default wins)
   before merging. A run's stale/racing copy of these files can no longer produce a
   conflict OR clobber consolidated content. Every other path merges with unchanged
   semantics, including genuine conflict detection.

## Deviations from the described mechanism
- **Exclusion list kept deliberately narrow**: only the three files reproduced in #621 —
  `.squad/decisions.md`, `.squad/agents/*/history.md`, `.squad/identity/now.md`. I did
  NOT exclude the append-only per-session paths (`.squad/log/**`,
  `.squad/orchestration-log/**`, `.squad/decisions/inbox/*.md`): those are uniquely-named
  per run, never collide across branches, and carry each run's own genuine output — so
  they must keep merging normally (and the inbox files MUST reach the default branch so
  the consolidation service can read them). `.squad/rai/audit-trail.md` was left out too
  (no drop-box exists for it yet; excluding it would silently drop run audit appends).
- **history.md / now.md** are excluded from merges but the consolidation service only
  drains the decisions inbox (the only documented drop-box). Runs' in-branch edits to
  those derived, non-authoritative files are dropped on merge (kept as ours) rather than
  conflicting — acceptable per squad.agent.md, which marks them "derived / append-only,
  never authoritative."

## Tests
- `SquadStateMergeTests`: bookkeeping-only divergence merges cleanly keeping ours;
  per-agent history.md same; a real non-bookkeeping conflict STILL conflicts.
- `SquadStateConsolidationServiceTests`: appends inbox → decisions.md + clears inbox +
  clean working tree; idempotent (no duplication on re-tick); no-inbox no-op.

---

## 2026-07-27T00-44-53: Anchor execute_tool span duration to SDK event timestamps, not consumer observation time (issue #546)
**By:** Dozer
**What:** Anchor execute_tool span duration to SDK event timestamps, not consumer observation time (issue #546)
**References:** issue #546, CopilotAIAgent.cs, RunEndpoints.cs:1671, trace db469dc6-7dda-4464-8521-c0048a4e7398
**Why:** ## Context

App Insights trace db469dc6-7dda-4464-8521-c0048a4e7398 showed four sibling `execute_tool` spans (`list_decisions`, `get_memory`, `list_inbox`, `web_fetch`) each reporting exactly 5.0 min, with `web_fetch` Failed. The near-instant local/DB tools should not take 5 min.

## Investigation outcome

- The proposed "web_fetch hangs ~5 min on a Cilium FQDN egress policy / long HTTP timeout" hypothesis is **REFUTED**. The 5 minutes is the deliberate HITL approval-gate deadline: `web_fetch` is allow-with-approval and its permission callback synchronously blocks the SDK callback thread via `approvalTask.Wait(...)` with `TimeSpan.FromMinutes(5)` (CopilotAIAgent.cs:1540 and the heartbeat loop ~1560-1575). Under autopilot without auto-approve-tools, it waits out the full 5 min then is denied.
- The three innocent tools inherited that 5 min because (a) the GitHub Copilot SDK dispatches tool calls SEQUENTIALLY (documented at RunEndpoints.cs:1671) so a blocked sibling stalls delivery of the others' lifecycle events, and (b) our `execute_tool` Activity was bounded purely by *when our single-consumer `await foreach` loop observed* the start/complete events (StartActivity/Dispose use ambient UtcNow). Stalled delivery → inflated, identical durations.

## Decision

Option (a) — true per-callId parallelism — is NOT feasible: the SDK's sequential permission/dispatch model is upstream and out of our control. Chosen option (b): bound each `execute_tool` span by the SDK event's own authoritative `Timestamp` (`ToolExecutionStartEvent.Timestamp` → span start via `Activity.SetStartTime`; `ToolExecutionCompleteEvent.Timestamp` → span end via `Activity.SetEndTime`), with a clamp so a backwards (clock-skew) end timestamp can never yield a negative duration (falls back to observation time). Both SDK events were verified by reflection to carry `Timestamp : DateTimeOffset`.

This decouples recorded tool duration from consumer-loop back-pressure: a fast tool observed late behind a blocked sibling now reports its true short duration, while a genuinely slow tool keeps its real long duration.

## Secondary note (not fixed here)

The synchronous 5-minute HITL `.Wait()` that blocks the SDK stream thread for the whole tool batch is a real head-of-line-blocking hazard — even with this telemetry fix, one un-approved web_fetch stalls sibling tool delivery for up to 5 min. Flagged as a possible follow-up; out of scope for the telemetry-accuracy fix.

Implemented as internal static helpers `StartToolSpanCore(..., DateTimeOffset? startTime)` and new `CompleteToolSpanCore(Activity, bool, string?, DateTimeOffset?)`, mirroring the existing testable-helper pattern. 5 regression tests added in TraceInstrumentationTests.

---

## 2026-07-26T22-55-48: Broadened GitHub OAuth navigation guard in ui-harness login flow to the full github.com origin
**By:** Fenster
**What:** Broadened GitHub OAuth navigation guard in ui-harness login flow to the full github.com origin
**References:** #537, #538, #494, scripts/ui-harness/lib/browser.mjs, scripts/ui-harness/test/browser.test.mjs
**Why:** ## Problem
`scripts/ui-harness/lib/browser.mjs`'s `isAllowedGitHubOAuthNavigation` (introduced in PR #494) allowlisted only `/login`, `/session`, and `/login/oauth/*` on github.com for the manual, human-supervised `login` subcommand. A real user (sabbour) hit Chromium's native "github.com is blocked / ERR_BLOCKED_BY_CLIENT" interstitial mid-login because GitHub routed them through `/sessions/two-factor`, which was outside the allowlist.

## Decision
Broadened the guard to permit the *entire* `https://github.com` origin whenever `options.allowGitHubOAuthNavigation === true`, rather than trying to enumerate every possible GitHub auth-flow path (2FA, new-device verification, device flow, org SSO, WebAuthn, etc. -- these paths have also drifted over time per GitHub docs/behavior).

This is judged safe because:
- `allowGitHubOAuthNavigation` is set ONLY by the manual, human-supervised, headful `login` subcommand in `tools.mjs`.
- The automated/persona-driven `action()` codepath never sets this flag, so headless/scripted flows remain fully restricted to same-origin navigation -- the actual security boundary this guard protects is unchanged.
- A human is physically watching and driving the browser during `login`, eliminating the "unattended agent tricked into navigating somewhere bad" risk for this specific codepath.
- Non-github.com destinations (including github.com-lookalike hosts) remain blocked even with the flag set; only the real `github.com` origin (via strict `target.origin === GITHUB_OAUTH_ORIGIN` check) is exempted.

## Changes
- `scripts/ui-harness/lib/browser.mjs`: simplified `isAllowedGitHubOAuthNavigation` to check origin only (dropped the `GITHUB_OAUTH_PATHS` path allowlist).
- `scripts/ui-harness/test/browser.test.mjs`: added regression coverage for previously-blocked paths (`/sessions/two-factor`, `/sessions/verified-device`, `/login/device`, `/orgs/{org}/sso`, `/settings/profile`), confirmed github.com is still blocked without the flag, and confirmed non-github.com/lookalike-host navigation is still blocked even with the flag set.
- No changeset: harness-only bug fix, same exemption rationale as PR #494 ("harness-only bug fix; it changes neither a shipped package nor user-facing product behavior").

## Validation
`npm --prefix scripts/ui-harness test` -- 16/16 pass. PR CI (Changeset advisory, Detect changed paths, Generated reference is in sync, Curated docs drift) all pass; other suite jobs skip because `scripts/ui-harness/**` is outside their path-group triggers (consistent with #494's precedent).

## References
Issue #537, PR #538 (branch `fix-github-oauth-navigation-guard` off `dev`), prior art PR #494.

---

# Creative direction applied — corrected 14-beat, 120-second sizzle reel

**Author:** Link  
**Status:** Revised proposal for beat-by-beat approval  
**Builds on:** `link-creative-director-shot-directing-scheme-for-demo-s.md`

## Pacing rebudget

The added project-creation beat fixes narrative causality: the reel now shows a repository becoming a project before presenting that project’s cast. The total remains exactly 120 seconds. Time was reclaimed mainly from orientation/navigation beats rather than uniformly shrinking proof moments.

| Arc | Beats | Time | Share | Direction |
|---|---:|---:|---:|---|
| Hook | 1 | 6s | 5.0% | Immediate visual proof; no logo pre-roll |
| Orient | 2–5 | 32s | 26.7% | Repo → project → cast → request → governed plan |
| Build/accelerate | 6, 9, 11, 12 | 34s | 28.3% | Brisk navigation; meaningful actions remain real-time |
| Payoff/proof | 7, 8, 10, 13 | 44s | 36.7% | Longest holds; state changes visibly caught |
| CTA | 14 | 4s | 3.3% | Stable branded resolve |
| **Total** | **14** | **120s** | **100%** | |

This follows the original research: vary tempo; default to hard cuts; reserve holds for readable proof; zoom/pan only to clarify; accelerate repetitive navigation/waiting but return to 1× before the result; and align music accents only with meaningful visual changes.

## Before — corrected flat outline

| # | Beat |
|---:|---|
| 1 | Hook — dashboard |
| 2 | Create project — point at repo, blueprint detected/generated |
| 3 | Team roster — cast for that project |
| 4 | Live-typed goal |
| 5 | OutcomeSpec drafts → Confirm |
| 6 | Workflow library → Generate → editor → Review Policy |
| 7 | Topology graph animating ready → running → done |
| 8 | Sandbox preview goes live |
| 9 | Board card backlog → ready |
| 10 | Trace / observability |
| 11 | Schedule / GitHub webhook setup |
| 12 | Assistant session chat |
| 13 | PR approval + GitHub PR |
| 14 | Outro / CTA |

## After — directed 120-second cut

Hard cuts and match cuts are 0ms by definition. Dissolves remain short and signal context/time changes. No decorative whip transition is justified.

| # | Duration | Cut pace | Camera | Speed treatment | Transition in / out | Headline | Music / audio |
|---:|---:|---|---|---|---|---|---|
| **1** | **6s** | Cold open; 2 shots: dashboard reveal ~3.5s, active metric/run state ~2.5s | **Full-frame.** Dashboard relationships establish scope; no zoom before context exists. | **1×.** Trim pre-load; begin on a populated dashboard. | **Cold hard cut in / hard cut out.** | **“Orchestrate work, end to end”** | Restrained pulse, **bed→build**. Soft reveal hit when metrics appear. Duck under opening VO; no click SFX. |
| **2** | **8s** | 3 shots: repo entry 3s → blueprint detection 2s → generated/recommended blueprint result 3s | Start **zoom-region ~1.20×** on repo input while retaining dialog context; return **full-frame** for blueprint detection/result so the system response reads as project-level intelligence. | Repo entry begins/ends at **1×**; middle of a long URL may run **2×** with 180–220ms ramps. Detection wait may run **2×**, but return to **1× before the blueprint appears**. | **Hard cut in / match cut out** from blueprint/cast result into roster identity/cards. | **“Point it at a repo”** | **Build** begins. Quiet typing texture, one analysis pulse, restrained reveal tone when blueprint appears. Duck for VO. |
| **3** | **7s** | 3 shots: roster full view 2.5s, agent detail 2.5s, team composition 2s | Full-frame, then **zoom-region ~1.25×** on one agent’s role/model; return to full frame. The project-specific cast remains visible as a whole. | **1×** for card/detail. Trim navigation into roster rather than speeding it. | **Match cut in / hard cut out.** | **“A team, cast for you”** | **Build** energy. One subtle selection tick. VO ducking active. |
| **4** | **8s** | One continuous cause shot with at most one invisible jump cut inside typing | **Zoom-region ~1.28×** on Start-task input; keep dialog edges visible. | First/final ~1.3s at **1×**; optional middle phrase at **2×**, ramp ~200ms. Never 4× live typing. | **Hard cut in / match cut out** from typed goal to OutcomeSpec goal field. | **“Describe the outcome”** | Add light percussion. Quiet keyboard texture; accent Define Outcome once. Duck under VO. |
| **5** | **9s** | Generation begins 1.5s → readable spec 4.5s → Confirm action/result 3s | Start **full-frame**, then **zoom-region ~1.22×** on real OutcomeSpec text; final **~1.28×** on Confirm only after the spec reads. | Empty wait may run **2×** with 200ms ramps; return to **1× before first text** and remain 1× through Confirm. | **Match cut in / hard cut out.** | **“Plan before execution”** | **Build** with restrained generative shimmer and firm approval click. Duck throughout readable VO. |
| **6** | **13s** | 4-shot mini-sequence: library 2.25s → generator/typing 2.75s → editor pan 3.75s → Review Policy 4.25s | Library **full-frame**; generator **~1.22×**; editor **pan** along actual workflow direction at ~1.08×; Review Policy **zoom-region ~1.35×** to show `rai` + mandatory human-review badge. | Menu/navigation at **2×**; typed description starts/ends **1×** with middle at 2×; editor and policy composition **1×**. | **Hard cut in; hard cuts internally / match cut out** from workflow node geometry to topology. | **“Govern every workflow”** | **Build→lift**. Light node-connect SFX; restrained badge lock/approval tone. Duck for VO, music up during editor pan. |
| **7** | **13s** | Long proof shot: establish 2.5s, ready→running 3.5s, parallel activity 4s, running→done hold 3s | **Full-frame by default.** One gentle **pan ~1.08×** from coordinator to child cluster only if graph exceeds frame; relationships and simultaneous states are the proof. | **Entirely 1×.** Remove only footage before `topology-visible`; hold after `topology-done`. | **Match cut in / match cut out** from completed node/status to preview-ready state. | **“Watch the team execute”** | First major **lift**. Sparse node-start ticks; one completion resolve. Brief VO duck, then music rises during visible execution. |
| **8** | **11s** | Launch/wait 2.5s → ready transition 2.5s → running preview payoff 6s | Start **full-frame** for approval/launch; preview **full-frame** or restrained **~1.18×** if chrome makes it too small. Keep evidence that it is live. | Repetitive spinner/build may reach **4×**, ramp 250ms; return to **1× at least 1s before ready** and remain 1× on result. | **Match cut in / 200ms dissolve out** to work-management context. | **“From plan to running”** | **Lift/payoff**. Low riser during wait, clean ready chime, music opens during result hold. Duck only under VO. |
| **9** | **7s** | One anticipation→drag→result action; no menu montage | **Full-frame.** Backlog and Ready columns must remain visible. | **1×** for cursor acquisition, drag, drop, pickup response. Trim route navigation. | **200ms dissolve in / hard cut out.** | **“Work keeps moving”** | Return to **build**. Quiet drag/drop texture; no cartoon swoosh. VO ducking active. |
| **10** | **11s** | Establish tree 3s → readable inspection 6s → selected span/result 2s; ideally continuous | **Full-frame, locked.** Explicitly no zoom-through; hierarchy is the subject. Select/highlight within stable frame. | **Entirely 1×.** No acceleration or camera drift. Enter only after trace content renders. | **Hard cut in / 250ms dissolve out.** | **“Every action, traceable”** | Music drops to **bed**, reduced percussion. One subtle selection tick. Strong VO duck, then music breath during silent hold. |
| **11** | **7s** | Schedule 2s → webhook configuration 2.75s → enabled/result state 2.25s | Start **full-frame**; **zoom-region ~1.22×** only on prepopulated cadence/webhook fields with labels retained. | Route/tab navigation at **2×**; toggle/save and enabled result at **1×**. | **Hard cut in / hard cut out.** | **“Trigger on your terms”** | Percussion returns; **build**. Toggle and success-check SFX only. Duck for VO. |
| **12** | **7s** | Composer 2.5s → submit 1s → streaming/response evidence 3.5s | **Zoom-region ~1.25×** on composer and first response, retaining session context/sidebar edge. | Concise typing and submit at **1×**. Cut dead response gap; first tokens and meaningful response remain 1×. | **Hard cut in / hard cut out.** | **“Steer it live”** | **Build→lift**. Quiet typing, submit tick, faint streaming texture at most. Short VO, then music-only response hold. |
| **13** | **9s** | Approval 3s → approve/wait 2s → GitHub PR reveal 4s | Approval **zoom-region ~1.25×**; **match cut to full-frame** GitHub PR with title/status/diff summary visible. | Approval at **1×**; remote wait may be **2×** or hard-cut if empty; return to **1× before PR appears** and hold. | **Hard cut in / 300ms dissolve out** into CTA. | **“Review. Approve. Ship.”** | Final **lift/resolve**. Approval click, restrained success tone on PR appearance. Music rises after VO and lands on reveal. |
| **14** | **4s** | One stable branded end frame | **Full-frame, locked.** Only subtle native brand motion. | **1×.** | **300ms dissolve in / 350ms fade to black.** | **“Build with Agentweaver”** | **Resolve**. No VO after first second; final hit and clean tail. No UI SFX. |

### Approval-sensitive choices

- Project creation gets **8 seconds**: enough to prove repo→blueprint causality, but it stays brisk because the later execution is the reel’s payoff.
- Beats **7, 8, 10, and 13 receive 44 seconds total**, preserving the expanded runtime for topology, preview, trace, and PR proof.
- Beat 6 remains dense but readable because governance must precede execution.
- Beat 10 is locked full-frame and 1×, directly implementing “hold longer; don’t zoom through it.”
- No whip-pan is used; no boundary has stronger real directional continuity than the proposed match/hard cuts.
- Headlines are marketing overlays, separate from synchronized accessibility subtitles.

## Storage format recommendation

### Decision: sibling JSON direction sidecar

Keep narrative/on-screen intent in markdown and add a sibling, reviewable JSON file:

```text
scripts/demo-recording/plans/sizzle-reel-beats.md
scripts/demo-recording/plans/sizzle-reel-direction.json
scripts/demo-recording/plans/direction.schema.json
```

This refines the earlier inline `Direction: {...}` idea. `beats.mjs` retains raw beat markdown, so inline JSON is technically parseable, but multi-shot beats 6–8 would become difficult to review. A sibling JSON sidecar gives clean field-level PR diffs, schema validation, and deterministic compositor input without replacing markdown.

### Compatibility with current `beats.mjs`

The current parser recognizes only headings matching `## Beat N.N — Title`, narration, blockers, and raw markdown. Keep `parseBeatPlan()` unchanged and later add a higher-level merge:

```js
const beats = await loadBeatPlan(markdownPath);
const direction = await loadDirectionPlan(directionPath);
return mergeDirectionByBeatId(beats, direction.beats);
```

Validation must fail on unknown/missing beat IDs in a strict sizzle profile, unknown cues, invalid enums, overlapping speed ranges, plan filename mismatch, or a duration sum other than 120,000ms.

The eventual markdown should use stable IDs `1.1` through `1.14`, matching the existing heading regex. Sidecar keys match exactly.

### Actual proposed direction file excerpt — corrected beats 7 and 10

```json
{
  "$schema": "./direction.schema.json",
  "version": 1,
  "plan": "sizzle-reel-beats.md",
  "profile": {
    "name": "sizzle-120s",
    "targetDurationMs": 120000,
    "output": {
      "width": 1920,
      "height": 1080,
      "fps": 30,
      "videoCodec": "h264",
      "audioCodec": "aac"
    },
    "cameraMode": "post",
    "accessibilityCaptions": "sidecar-and-social-burnin",
    "defaultTransition": { "type": "cut", "durationMs": 0 }
  },
  "beats": {
    "1.7": {
      "title": "Topology graph animating live",
      "targetDurationMs": 13000,
      "pace": "payoff",
      "shots": [
        {
          "fromCue": "topology-visible",
          "toCue": "children-running",
          "framing": { "type": "full-frame" }
        },
        {
          "fromCue": "children-running",
          "toCue": "topology-done",
          "framing": {
            "type": "pan",
            "fromTargetCue": "coordinator-node",
            "toTargetCue": "child-cluster",
            "scale": 1.08,
            "enterMs": 500,
            "easing": "out-cubic"
          }
        },
        {
          "fromCue": "topology-done",
          "toCue": "beat-end",
          "framing": { "type": "full-frame", "exitMs": 300 }
        }
      ],
      "speed": [
        { "fromCue": "topology-visible", "toCue": "beat-end", "rate": 1 }
      ],
      "transitionIn": {
        "type": "match-cut",
        "durationMs": 0,
        "match": "workflow-node-to-topology-node"
      },
      "transitionOut": {
        "type": "match-cut",
        "durationMs": 0,
        "match": "done-status-to-preview-ready"
      },
      "caption": {
        "kind": "headline",
        "text": "Watch the team execute",
        "placement": "top-left",
        "fromCue": "topology-visible",
        "toCue": "children-running",
        "enter": "fade-up",
        "enterMs": 180,
        "exit": "fade",
        "exitMs": 120
      },
      "audio": {
        "musicEnergy": "lift",
        "duckUnderVoiceover": true,
        "sfx": [
          { "cue": "child-running-first", "name": "node-start", "gainDb": -20 },
          { "cue": "topology-done", "name": "completion-resolve", "gainDb": -16 }
        ]
      }
    },
    "1.10": {
      "title": "Trace and observability",
      "targetDurationMs": 11000,
      "pace": "hold",
      "shots": [
        {
          "fromCue": "trace-visible",
          "toCue": "beat-end",
          "framing": { "type": "full-frame" }
        }
      ],
      "speed": [
        { "fromCue": "trace-visible", "toCue": "beat-end", "rate": 1 }
      ],
      "transitionIn": { "type": "cut", "durationMs": 0 },
      "transitionOut": { "type": "dissolve", "durationMs": 250 },
      "caption": {
        "kind": "headline",
        "text": "Every action, traceable",
        "placement": "top-left",
        "fromCue": "trace-visible",
        "toCue": "trace-span-selected",
        "enter": "fade-up",
        "enterMs": 180,
        "exit": "fade",
        "exitMs": 120
      },
      "audio": {
        "musicEnergy": "bed",
        "duckUnderVoiceover": true,
        "sfx": [
          { "cue": "trace-span-selected", "name": "selection-tick", "gainDb": -22 }
        ]
      }
    }
  }
}
```

### Capture cue sidecar expected by the compositor

Direction contains no absolute source timestamps. Capture produces named cue timing and normalized target rectangles:

```json
{
  "beatId": "1.7",
  "source": "raw-1-7.webm",
  "viewport": { "width": 2560, "height": 1440 },
  "cues": [
    { "name": "topology-visible", "timeMs": 720 },
    {
      "name": "coordinator-node",
      "timeMs": 810,
      "rect": { "x": 0.42, "y": 0.31, "width": 0.09, "height": 0.08 }
    },
    { "name": "child-running-first", "timeMs": 3260 },
    {
      "name": "children-running",
      "timeMs": 4820,
      "rect": { "x": 0.56, "y": 0.24, "width": 0.31, "height": 0.43 }
    },
    {
      "name": "child-cluster",
      "timeMs": 4820,
      "rect": { "x": 0.56, "y": 0.24, "width": 0.31, "height": 0.43 }
    },
    { "name": "topology-done", "timeMs": 10100 },
    { "name": "beat-end", "timeMs": 13000 }
  ]
}
```

Capture plans should add `cue` to existing selector-backed steps or use `{ "type": "cue", "name": "..." }` for state boundaries.

## Fresh-navigation discrepancy — confirmed, unchanged

The corrected outline does not alter Phase 0:

- Commit `cc22fbdc` exists on `squad/capture-plan-continuity` and adds `Start URL:` / `Fresh navigation:` parsing plus conditional navigation.
- It is not an ancestor of the current checked-out HEAD; current `beats.mjs` lacks those fields and current `capture-plan.mjs` still performs an unconditional initial `page.goto(plan.startUrl)`.
- The direction sidecar cannot make topology capture trustworthy by itself. Integrate the continuity commit into the actual Scenario 3 base before capture/compositor work.

For corrected beat **7**:

```text
Fresh navigation: false
Start URL: omitted
```

The sequence should arrive from the preceding live workflow/run actions and emit `topology-visible` only after a real node renders. It must cue actual ready/running/done changes, never recreate them in post.

## Approval checklist

1. Approve/revise each duration; total is exactly 120s.
2. Approve the new 8s project-creation beat and repo→blueprint match into roster.
3. Approve proof allocations: topology 13s, preview 11s, trace 11s, PR 9s.
4. Approve no whip-pan and locked full-frame trace.
5. Approve sibling `*-direction.json` over inline direction blocks.
6. Integrate `cc22fbdc` before Scenario 3 capture planning.

---

# Creative direction v2 — grounded timing and capture-first/direct-after

**Author:** Link  
**Status:** Proposal for review  
**Supersedes the workflow assumptions, fixed source timings, and dissolve choices in:** `decisions/inbox/link-creative-direction-applied-120s-sizzle.md`  
**Does not replace:** the approved narrative order or the 120-second *output* target.

## Decision

Adopt **capture-first/direct-after**.

The 120-second reel remains the editorial destination, but runtime-dependent beat lengths are now **soft output budgets**, never promises about how long capture will take. Record complete real behavior and semantic cues first; resolve source ranges, speed changes, cuts, holds, and camera rectangles only after the cue timeline exists.

Also:

1. Beat 7 cannot be continuously shown at 1× inside 13 seconds. Code explicitly designs child work for 5–15+ minute turns. Preserve meaningful boundary changes at 1×, compress selected active intervals, and cut empty waits.
2. Remove cross-dissolves from all product-to-product boundaries. Use hard cuts or genuine semantic match cuts. A final fade-to-black after the CTA may remain.
3. No licensed music asset or music workflow exists in the demo-recording tree. Acquire a specifically licensed track and archive evidence in a machine-readable audio manifest before rendering.
4. Phase 0 continuity remains blocked: `cc22fbdc` is still not in current HEAD, and current capture still navigates unconditionally.

---

## 1. Runtime evidence and corrected timing model

### Beat 7 — topology is a real orchestration timeline, not a 13-second animation

#### How state reaches the screen

- The coordinator advances subtasks `pending → dispatched → running`, observes real child runs, and emits a full `coordinator.topology` snapshot plus a delta on every transition. The client does not invent those states (`apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:35-60`).
- Independent children can dispatch in parallel, but dependencies and declared output conflicts serialize later waves (`CoordinatorDispatchService.cs:41-49`, `:299-315`).
- `running` is persisted only **after** `StartChildRunAsync` returns (`CoordinatorDispatchService.cs:860-881`). That launch path includes sandbox allocation/readiness.
- The AgentHost readiness code documents an observed **~20–30 seconds after pod bind** before Kestrel listens. It retries every 1 second, with a 90-second default readiness budget (`apps/Agentweaver.Api/Sandbox/AgentHostReadinessProbe.cs:5-10`, `:43-48`; `KubernetesSandboxExecutor.cs:86-89`). Claim bind is also polled rather than instantaneous.
- The coordinator’s own comment explicitly calls implement/debug child runs **“5–15+ min”** (`CoordinatorDispatchService.cs:279-285`). A child may emit no event for up to the default 5-minute stall threshold; each event resets that threshold (`:88-92`, `:1695-1707`, `:1747-1785`).
- Normal observation is push-based, not a 4-second topology poll. Durable subscription tailing is approximately 250 ms (`CoordinatorDispatchService.cs:1678-1708`, `:1828-1832`; `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:35`). The generic REST fallback polls at 2 seconds (`apps/web/src/api/sse.ts:11`), and reconnect backoff is 1/2/4/8/16/30 seconds (`sse.ts:20`).

#### Defensible source-duration range

For one child/wave on the Kubernetes path:

- **ready/pending → running:** known cold-start component is about **20–30 seconds after bind**; readiness has a **90-second** budget. Claim binding and API calls add variable time, so there is no strict whole-launch upper bound in these files.
- **running → terminal:** the implementation’s own operating assumption is **300–900+ seconds** for implement/debug work.
- **one-wave visible sequence:** approximately **320–990+ seconds** once the known startup component is included. Multiple dependency waves are additive; a graph can therefore exceed this substantially.

This is a code-backed operating envelope, not production telemetry. The repository emits real `agent.turn.usage.durationMs` and `timeToFirstTokenMs` (`packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:970-975`), but no checked-in dataset supports a tighter percentile claim. Before final editing policy is locked, the first instrumented rehearsal should export actual per-node distributions from those events.

#### What a 13-second output budget mathematically implies

If all source footage were kept continuously:

| Actual source interval | Raw compression into 13s | Compression after reserving 3s total at 1× for start/end proof |
|---:|---:|---:|
| 320s | 24.6× | 31.7× over the remaining 317s/10s |
| 600s | 46.2× | 59.7× |
| 990s | 76.2× | 98.7× |

Those ratios prove that “entirely 1×” is impossible and that a single continuous ramp is usually the wrong answer: at ~32–99× the UI state becomes a flicker rather than legible evidence.

#### Correct Beat 7 treatment

- Keep `first-child-running` and the visual reaction around it at **1×**.
- Keep the final meaningful terminal cascade and `topology-done` hold at **1×**.
- For the middle, retain only eventful windows around semantic topology deltas. Use moderate speed-up for intervals where the graph/counters visibly evolve; hard-cut intervals with no meaningful visual change.
- The compositor must calculate required compression from actual cues. Proposed QC policy, clearly an editorial policy rather than a measured system fact:
  - up to 4×: continuous footage is normally reviewable;
  - 4×–12×: allow only when state changes remain readable in preview;
  - above 12× required: prefer event-window selection and cuts rather than further continuous acceleration.
- If a take cannot preserve start, at least one active middle change, final completion, and readable graph relationships inside the budget, increase Beat 7’s output allocation and reclaim time from navigation—not from trace or preview proof.

### Beat 8 — preview going live is also runtime-dependent

The fixed 11-second v1 source assumption is unsupported.

- `start_preview` is requested only after the agent has built/started a server sufficiently to nominate a port. That upstream work occurs inside the real child run and can consume part of the 5–15+ minute interval.
- Preview approval returns immediately only when auto-approved. Otherwise it can wait for a human until a configurable timeout: default **900 seconds**, with invalid/non-positive configurations clamped to a **60-second timeout window** (`apps/Agentweaver.Api/Sandbox/Preview/AgentPreviewGate.cs:18-34`, `:42-45`, `:77-89`). A human may approve sooner; 60 seconds is not a mandatory minimum wait.
- If deterministic run-command discovery fails, the fallback proposal model itself has a **30-second** budget because 8 seconds was found too short under real load (`CopilotPreviewCommandModel.cs:36-37`).
- After approval, publication performs Kubernetes/control-plane operations but exposes no cinematic SLA. Therefore “approval click → usable route” has no reliable pre-capture duration promise.

Correct treatment:

- Cue separately: `preview-approval-visible`, `preview-approved`, `preview-publish-start`, `preview-ready`, `preview-page-interactive`.
- Preserve approval and ready/interactivity boundaries at 1×.
- Compress or remove only the measured middle gap.
- Do not start the edited beat at “agent begins building”; start at the first narrative-relevant preview cue unless the build itself is the story.
- Treat 11 seconds as an output budget. A capture may take seconds, tens of seconds, or—if human approval is not controlled—up to 900 seconds before timeout.

### Beat 10 — trace should not be captured by waiting live

- `TransactionTracePanel` performs one `getRunTraces(runId)` call when `runId` changes. It does not poll (`apps/web/src/components/runs/TransactionTracePanel.tsx:381-395`).
- If no spans are returned, it displays “No trace data available for this run yet” and remains empty until remount/refresh (`:427-429`).

Therefore the safe capture recipe is:

1. choose a completed/preverified run with populated trace data;
2. open the panel;
3. emit `trace-visible` only when at least one real span row and the detail surface are rendered;
4. hold and inspect at 1×.

There is no defensible “wait N seconds for traces” instruction. Waiting can be indefinite from the shot’s perspective because the component does not retry. The 11-second output hold remains reasonable only **after** `trace-visible`; it is not a load-time promise.

### Gut-check of the other variable beats

| Beat | Runtime-dependent source behavior | v2 capture rule |
|---:|---|---|
| 1 Dashboard | Data load/network timing varies, but no generative dependency is required. | Start only after `dashboard-populated`; trim pre-load. |
| 2 Create project/blueprint | Repo access, suggestion/generation, and project creation are network/generative operations without a cinematic SLA in the capture plan. | Cue repo submit, suggestion/detection, project-created, blueprint-visible. Budget is post-hoc. |
| 3 Roster | Predominantly deterministic once project exists. | Cue `roster-visible` and target cards; normal trim only. |
| 4 Goal typing | Capture script controls typing timestamps. | Preserve first/last words at 1×; optional middle compression after capture. |
| 5 OutcomeSpec | Model drafting is variable. Confirm/reply classification alone has an 8-second bound in one synchronous path (`OutcomeSpecReplyClassifier.cs:80`, `:135-144`), but initial drafting has no demonstrated cinematic duration. | Cue draft-start, first-content, spec-complete, confirmed. Resolve wait after capture. |
| 6 Workflow generation | Library/editor navigation is deterministic; generate-from-description is model-backed and variable. | Separate deterministic navigation cues from generation cues; do not give the whole beat one fixed source duration. |
| 7 Topology | 320–990+ seconds for one code-backed operating envelope; serialized waves can be longer. | Full raw capture plus event cues; directed selection afterward. |
| 8 Preview | Build/start, approval (0–900s), optional 30s fallback model, and K8s publication vary. | Capture complete; cut from measured cues. |
| 9 Board drag | Deterministic interaction. | Preserve drag/drop at 1×; trim route navigation. |
| 10 Trace | No retry if data is absent. | Preverify populated trace; cue only after render. |
| 11 Schedule/webhook | Prepopulated setup is mostly deterministic; save request may vary slightly. | Cue form-visible, save-start, saved. |
| 12 Assistant | TTFT and completion vary; runtime already records TTFT/duration. | Cue submit, first-token, useful-response, response-complete; cut dead TTFT only after capture. |
| 13 PR | Approval is deterministic; remote GitHub creation/network/indexing varies. Human approval may also be open-ended depending on gate policy. | Cue approval, create-start, PR-url-known, GitHub-PR-visible. |
| 14 CTA | Deterministic. | Fixed hold may be authored directly. |

---

## 2. Dissolve investigation and verdict

### What the evidence does and does not establish

The v1 report overreached by treating dissolves as a normal contextual punctuation for this reel. The cited material does **not** establish that Linear, GitHub, Arc, Stripe Sessions, or Screen Studio routinely use cross-dissolves between adjacent product UI scenes.

- Screen Studio’s documented editorial model is post-recording, click/target-driven zoom blocks that can be adjusted after capture—not decorative cross-dissolves as the organizing grammar: <https://screen.studio/guide/adding-editing-zooms>.
- Camtasia provides transition effects as a general editor feature, but availability is not evidence that a dissolve is appropriate for a modern SaaS continuity sequence: <https://www.techsmith.com/camtasia/features/video-transitions/>.
- The official/product catalogs previously reviewed predominantly expose native UI motion, direct cuts, typography, and matchable screen geometry. A rigorous frame-count study was not available, so this document does not invent a dissolve frequency.
- The safe genre conclusion is narrower: use the product’s own motion as continuity; use a hard cut when the next view is simply the next fact; use a match cut only when there is a real visual/semantic correspondence.

### Verdict

**Remove all cross-dissolves from the body.** They soften state changes that should feel responsive and, without a demonstrated time/context leap, read as generic presentation software.

Changes from v1:

| Boundary | v1 | v2 |
|---|---|---|
| Beat 8 preview → Beat 9 board | 200ms dissolve | Hard cut on a stable preview result to a populated board, or match cut only if a shared project title/status occupies the same area. |
| Beat 9 entry | 200ms dissolve | Hard cut. |
| Beat 10 trace → Beat 11 trigger setup | 250ms dissolve | Hard cut, preferably on selection/click cadence. |
| Beat 13 PR → Beat 14 CTA | 300ms dissolve | Hard cut or branded graphic match; no blended UI frames. |
| Beat 14 entry | 300ms dissolve | Same hard/match cut from Beat 13. |
| Beat 14 ending | 350ms fade to black | May remain. This is an end treatment, not a cross-dissolve between product scenes. |

Whip-pans remain disallowed unless capture contains genuine directional motion that can be matched; none is currently specified.

---

## 3. Music sourcing and licensing

### Repository finding

No music track exists under `scripts/demo-recording` (no MP3/WAV/M4A/AAC/OGG/FLAC asset), and no soundtrack/royalty/music-license guidance exists there. Current audio code supports narration generation/muxing and A/V padding, not music selection, ducking, stems, or license tracking (`scripts/demo-recording/lib/ffmpeg.mjs`).

### Track brief

Select one instrumental electronic/minimal technology track with:

- **105–120 BPM** as a search brief, not a claim about genre norms;
- no lead vocal;
- a restrained opening, clear build, one principal lift/drop near topology/preview, a lower-density section for trace narration, and a clean 120-second resolve;
- stems or alternate mixes if available;
- enough spectral space for narration.

The chosen BPM should be measured from the actual licensed track. Cuts should follow meaningful accents, not force UI actions onto every beat.

### Concrete source recommendation

**Preferred production path:** purchase/download a track under a commercial license that explicitly covers synchronization in a company product-promotion video across the intended website, GitHub/social, YouTube, events, and paid-media contexts. For example:

- **Artlist Pro/Business**, not the basic personal-channel license. Artlist’s current official license page says the base license is limited to registered personal channels and directs third-party/promotion/advertising uses to Pro; projects published during the licensed term may remain published afterward: <https://artlist.io/help-center/privacy-terms/artlist-license/>. Verify the exact plan and obtain the license certificate at purchase.
- **PremiumBeat** is a viable one-track alternative if its license purchased at the time explicitly lists corporate/promotional web synchronization and the intended distribution. Official license page: <https://www.premiumbeat.com/license>. Archive the actual invoice/license; do not rely on a marketing summary.

**Zero-budget fallback:** use an FMA track explicitly marked **CC0** or **CC BY** and follow attribution. FMA states:

- NC music cannot be used for commercial endeavors without permission;
- ND music cannot be synchronized to video without written permission because synchronization is an adaptation;
- SA requires the derivative video to use the identical license;
- BY requires title/author/source/license attribution in the video and accompanying blurb.

Source: <https://freemusicarchive.org/FAQ_For_Videos>.

For this product-promotion use, avoid **NC**, **ND**, and **SA** unless counsel/owner provides separate written permission. CC0 is simplest; CC BY is acceptable if credits are operationally guaranteed.

**YouTube Audio Library:** appropriate only after checking the individual license and intended distribution. YouTube calls Audio Library tracks copyright-safe on YouTube, identifies attribution requirements, and explicitly says it cannot provide legal guidance for off-platform issues: <https://support.google.com/youtube/answer/3376882>. That makes it weaker as the default for a reel expected to travel beyond YouTube.

**Pixabay/Mixkit:** may be candidates, but “royalty-free” is not enough. Save the exact license/version and track page on download and check platform, standalone, attribution, third-party-right, and Content ID restrictions. Terms can change; a link alone is insufficient evidence.

### Required license manifest

Add a reviewable manifest when a track is selected (future implementation; no file added in this proposal):

```json
{
  "$schema": "./music-license.schema.json",
  "trackId": "vendor-stable-id",
  "title": "Exact Track Title",
  "artist": "Exact Artist",
  "source": "Artlist",
  "sourceUrl": "https://…",
  "licensePlan": "Pro",
  "licenseVersionOrDate": "2026-07-30",
  "downloadedAtUtc": "2026-07-30T12:00:00Z",
  "project": "Agentweaver 120s sizzle reel",
  "permittedSurfaces": ["website", "youtube", "social", "events", "paid-media"],
  "attributionRequired": false,
  "attributionText": null,
  "assetSha256": "…",
  "evidenceFiles": ["licenses/receipt.pdf", "licenses/certificate.pdf", "licenses/terms.html"]
}
```

The compositor should fail closed when music is configured but the manifest or referenced evidence is absent.

---

## 4. Capture-first/direct-after redesign

### Why it is better here

It is strictly better for runtime-dependent beats because the important times and regions do not exist until the real system produces them. The old order was:

1. predict a 13,000ms beat;
2. predict where nodes will appear;
3. capture an orchestration that may last 320–990+ seconds;
4. hope prediction matches reality.

The corrected order is:

1. define **what evidence must be captured**, semantic cue names, framing intent, and an output budget range;
2. record the complete raw take while emitting actual timestamps and DOM rectangles as events occur;
3. inspect/analyze measured cue intervals;
4. author or auto-generate the take-specific direction manifest with resolved cuts, rates, holds, camera paths, and transitions;
5. render and QC.

This also follows the useful part of Screen Studio’s model: capture first, then adjust automatically generated zoom regions/timing in an editor.

### On-disk format

Keep the beat markdown. Add three structured layers; do not overload `beats.mjs`.

```text
scripts/demo-recording/plans/sizzle-reel-beats.md
scripts/demo-recording/plans/sizzle-reel.capture.json
scripts/demo-recording/plans/sizzle-reel.direction.json
recordings/raw/sizzle-reel/<take-id>/capture-cues.json
```

- `*.capture.json`: pre-capture semantic contract and soft editorial budgets. Human-reviewable, stable across takes, no absolute source timestamps, no predicted x/y coordinates.
- `capture-cues.json`: generated take evidence with real timestamps, normalized rectangles, viewport, event source, and raw-media hash.
- `*.direction.json`: authored/generated **after capture**, references a take and cues, and deterministically describes the render. It contains resolved output durations and rates; these are decisions against real data, not pre-capture promises.
- `beats.mjs` remains responsible for headings/narration/blockers/raw markdown (`scripts/demo-recording/lib/beats.mjs:3-45`). A new loader later joins all layers by exact beat ID and validates schemas.

The capture and direction files live alongside the markdown and are PR-diffable. Raw video need not be committed; the cue manifest should be retained with the take artifact and may be checked in when review/audit value warrants it.

### Pre-capture format — actual Beat 7 and Beat 10 example

```json
{
  "$schema": "./capture.schema.json",
  "version": 2,
  "plan": "sizzle-reel-beats.md",
  "outputRuntimeBudgetMs": 120000,
  "beats": {
    "1.7": {
      "title": "Topology graph animating live",
      "outputBudget": { "preferredMs": 13000, "minMs": 11000, "maxMs": 20000 },
      "capturePolicy": "record-until-required-cues-or-timeout",
      "requiredCues": [
        { "name": "topology-visible", "source": { "kind": "selector", "value": "[data-testid='topology-graph']" } },
        { "name": "first-child-running", "source": { "kind": "run-event", "eventType": "subtask.running" } },
        { "name": "topology-done", "source": { "kind": "topology-state", "predicate": "all-terminal" } }
      ],
      "optionalCues": [
        { "name": "coordinator-node", "source": { "kind": "selector", "value": "[data-node-kind='coordinator']" }, "captureRect": true },
        { "name": "active-child-cluster", "source": { "kind": "selector-union", "value": "[data-node-status='running']" }, "captureRect": true, "on": "each-change" },
        { "name": "terminal-cascade-start", "source": { "kind": "run-event", "eventType": "subtask.completed", "occurrence": "first" } }
      ],
      "framingIntent": {
        "default": "full-frame",
        "allowed": ["full-frame", "cue-rect-pan"],
        "mustKeepVisible": ["coordinator-node", "active-child-cluster"],
        "maxScale": 1.12
      },
      "pacingIntent": {
        "preserveAt1x": ["first-child-running", "topology-done"],
        "compressibleIntervals": [["first-child-running", "terminal-cascade-start"]],
        "cuttableWhenNoVisualChange": true
      },
      "transitionPolicy": { "in": ["hard-cut", "semantic-match-cut"], "out": ["hard-cut", "semantic-match-cut"] }
    },
    "1.10": {
      "title": "Trace and observability",
      "outputBudget": { "preferredMs": 11000, "minMs": 9000, "maxMs": 14000 },
      "prerequisites": ["selected run has at least one persisted span"],
      "requiredCues": [
        { "name": "trace-visible", "source": { "kind": "selector", "value": "[data-testid='trace-tree'] [data-trace-span]" }, "captureRect": true },
        { "name": "trace-span-selected", "source": { "kind": "interaction", "action": "click", "target": "[data-trace-span]" }, "captureRect": true }
      ],
      "failureCue": { "name": "trace-empty", "source": { "kind": "text", "value": "No trace data available for this run yet." }, "action": "abort-beat" },
      "framingIntent": { "default": "full-frame", "allowed": ["full-frame"], "cameraLocked": true },
      "pacingIntent": { "preserveAt1x": ["trace-visible", "trace-span-selected"], "minimumReadableHoldMs": 7000 },
      "transitionPolicy": { "in": ["hard-cut"], "out": ["hard-cut"] }
    }
  }
}
```

This file says what must be proved and where rectangles should be captured. It does not claim topology finishes at 13 seconds or that the active cluster is at a predicted coordinate.

### Generated cue evidence — actual take example

```json
{
  "$schema": "./capture-cues.schema.json",
  "takeId": "sizzle-20260730-a",
  "source": "recordings/raw/sizzle-reel/sizzle-20260730-a/raw.webm",
  "sourceSha256": "…",
  "viewport": { "width": 2560, "height": 1440 },
  "timebase": "capture-start-ms",
  "beats": {
    "1.7": {
      "range": { "startMs": 412300, "endMs": 1027400 },
      "cues": [
        { "name": "topology-visible", "timeMs": 412820, "origin": "selector", "rect": { "x": 0.08, "y": 0.15, "width": 0.84, "height": 0.72 } },
        { "name": "coordinator-node", "timeMs": 412840, "origin": "selector", "rect": { "x": 0.42, "y": 0.28, "width": 0.10, "height": 0.08 } },
        { "name": "first-child-running", "timeMs": 441960, "origin": "run-event", "eventSequence": 138 },
        { "name": "active-child-cluster", "timeMs": 442030, "origin": "selector-union", "rect": { "x": 0.54, "y": 0.22, "width": 0.34, "height": 0.46 } },
        { "name": "terminal-cascade-start", "timeMs": 1015110, "origin": "run-event", "eventSequence": 782 },
        { "name": "topology-done", "timeMs": 1024010, "origin": "topology-state" }
      ]
    },
    "1.10": {
      "range": { "startMs": 1092100, "endMs": 1106500 },
      "cues": [
        { "name": "trace-visible", "timeMs": 1093870, "origin": "selector", "rect": { "x": 0.06, "y": 0.17, "width": 0.88, "height": 0.69 } },
        { "name": "trace-span-selected", "timeMs": 1099820, "origin": "interaction", "rect": { "x": 0.11, "y": 0.39, "width": 0.41, "height": 0.05 } }
      ]
    }
  }
}
```

The numbers are illustrative file-format content, not measurements from a real take. Production files must be generated from capture instrumentation.

### Post-capture direction — actual resolved example

```json
{
  "$schema": "./direction.schema.json",
  "version": 2,
  "plan": "sizzle-reel-beats.md",
  "takeId": "sizzle-20260730-a",
  "cueManifest": "../../../recordings/raw/sizzle-reel/sizzle-20260730-a/capture-cues.json",
  "targetRuntimeMs": 120000,
  "defaultTransition": { "type": "hard-cut", "durationMs": 0 },
  "beats": {
    "1.7": {
      "resolvedOutputDurationMs": 13000,
      "segments": [
        { "fromCue": "topology-visible", "toCue": "first-child-running", "selection": { "tailMs": 1700 }, "rate": 1 },
        { "fromCue": "first-child-running", "toCue": "first-child-running", "selection": { "afterMs": 1800 }, "rate": 1 },
        { "fromCue": "first-child-running", "toCue": "terminal-cascade-start", "selection": { "mode": "activity-windows", "outputMs": 5200, "maxContinuousRate": 12, "dropStaticGapsOverMs": 2500 } },
        { "fromCue": "terminal-cascade-start", "toCue": "topology-done", "selection": { "tailMs": 3000 }, "rate": 3 },
        { "fromCue": "topology-done", "selection": { "afterMs": 1300 }, "rate": 1 }
      ],
      "camera": [
        { "fromCue": "topology-visible", "toCue": "first-child-running", "type": "full-frame" },
        { "fromCue": "first-child-running", "toCue": "terminal-cascade-start", "type": "pan-between-cue-rects", "fromTargetCue": "coordinator-node", "toTargetCue": "active-child-cluster", "scale": 1.08 }
      ],
      "transitionIn": { "type": "semantic-match-cut", "match": "workflow-node-to-topology-node" },
      "transitionOut": { "type": "hard-cut" }
    },
    "1.10": {
      "resolvedOutputDurationMs": 11000,
      "segments": [
        { "fromCue": "trace-visible", "selection": { "afterMs": 11000 }, "rate": 1 }
      ],
      "camera": [{ "fromCue": "trace-visible", "type": "full-frame", "locked": true }],
      "transitionIn": { "type": "hard-cut" },
      "transitionOut": { "type": "hard-cut" }
    }
  }
}
```

The resolved manifest is where exact output durations belong. Validation must confirm that all cue references exist, source ranges are monotonic, selected source is available, output durations sum to 120 seconds, rates are positive, camera rectangles exist for rect-anchored moves, and no cross-dissolve appears under this reel’s policy.

### Camera anchoring across all 14 beats

The rule applies to every beat, not only topology and trace:

- full-frame shots need no coordinate;
- zooms use a selector/interaction cue whose **actual rect** is captured at the relevant time;
- pans use two or more cue rectangles;
- if a target moves, capture rect samples on change rather than assuming a static location;
- no direction file may contain hand-authored source-pixel `x/y` coordinates for live product UI.

Examples: repo input rect (2), agent card rect (3), goal composer rect (4), OutcomeSpec panel/Confirm rects (5), workflow policy badge rect (6), preview viewport rect (8), card/source/destination rects (9), schedule fields (11), assistant composer/response rects (12), approval/PR summary rects (13).

### Beat readiness under this model

| Beat | Cue model status | Required v2 rework |
|---:|---|---|
| 1 | Cleanly cue-anchored | Replace fixed lead-in with `dashboard-populated`. |
| 2 | Cue-anchored but variable | Convert 8s from source promise to output budget; add generation/result cues. |
| 3 | Cleanly cue-anchored | Selector rectangles only. |
| 4 | Cleanly cue-anchored | Use actual typing action timestamps. |
| 5 | Variable | Split model wait from readable result/confirm. |
| 6 | Mixed deterministic/variable | Segment library, generation, editor, policy; resolve generation after capture. |
| 7 | Variable, biggest rework | Full capture, event-derived cues, activity-window edit. |
| 8 | Variable | Separate approval, publication, ready, interactive cues. |
| 9 | Cleanly cue-anchored | Preserve drag/drop 1×. |
| 10 | Clean only with prerequisite | Preverify spans; abort on empty trace. |
| 11 | Cleanly cue-anchored | Capture save/result states. |
| 12 | Variable | Cue TTFT and useful response; post-cut wait. |
| 13 | Mixed deterministic/variable | Separate approval from remote PR creation/load. |
| 14 | Deterministic | Fixed output hold is acceptable. |

---

## 5. Tooling implications and implementation order

The current pipeline records Playwright video, marks generic activity, trims static gaps, pads A/V duration, muxes narration, and stream-concats clips. It has no semantic cue collector, take analyzer, camera compositor, variable-speed audio/video graph, transitions, caption renderer, music bed, ducking, or license validator (`scripts/demo-recording/lib/ffmpeg.mjs`).

Scoped order:

1. **Phase 0 — continuity:** integrate/port `cc22fbdc`; add regression coverage for `freshNavigation: false` and omitted start URL.
2. **Capture schema + cue collector:** validate `*.capture.json`; emit event-, selector-, text-, interaction-, and topology-state cues with normalized rectangles.
3. **Take analyzer:** compute source intervals, static/activity windows, missing cues, topology status chronology, TTFT/turn durations, and suggested budget pressure.
4. **Direction authoring/generation:** create `*.direction.json` from real cues; never silently invent missing cues or rectangles.
5. **FFmpeg compositor:** trim/concat with re-encoding, `setpts` speed segments, `atempo`/audio replacement as appropriate, crop/scale/pan from cue rects, caption layers, hard/match cuts, final fade, and loudness/ducking.
6. **Music/license gate:** validate manifest/evidence, loop/edit licensed track, duck under VO, mix SFX, and run loudness QC.
7. **QC:** cue completeness, 120-second sum, frame-bound crop safety, text safe zones, readable accelerated states, no clipped narration, and A/V sync.

Rough effort remains medium rather than a small FFmpeg patch: approximately 2–3 engineering days for cue instrumentation/analyzer, 3–5 for a reliable compositor and schemas, and 1–2 for audio/license/QC integration, excluding production rehearsal and edge-case hardening.

---

## 6. Phase 0 fresh-navigation finding

**Unchanged and reconfirmed.** `git merge-base --is-ancestor cc22fbdc HEAD` returns non-ancestor; only `squad/capture-plan-continuity` contains the commit. Current `capture-plan.mjs:33` unconditionally emits `page.goto(plan.startUrl)`, while current `beats.mjs` parses no `Start URL` or `Fresh navigation` fields.

Capture-first does not make discontinuous capture trustworthy. Beat 7 must inherit the live run from preceding actions, and semantic cues must be emitted against that same run. Integrate the continuity fix before relying on a topology take.

---

## Approval requested

1. Approve capture-first/direct-after as the pipeline order.
2. Approve 120 seconds as an output target with per-beat budget ranges, not pre-capture durations.
3. Approve removal of every body cross-dissolve; retain only optional final fade-to-black.
4. Choose music procurement path: commercial Pro/Business sync license preferred; CC0/CC BY fallback with archived evidence.
5. Approve Phase 0 integration of `cc22fbdc` before Scenario 3 capture work.


---

## 7. FollowCursor prior art — incorporated into capture-first design

`github.com/sabbour/followcursor` is publicly reachable. Review was against commit [`b22da76a764cdc24f4fc560e938db690ad3f624b`](https://github.com/sabbour/followcursor/tree/b22da76a764cdc24f4fc560e938db690ad3f624b). The repository is MIT-licensed ([LICENSE](https://github.com/sabbour/followcursor/blob/b22da76a764cdc24f4fc560e938db690ad3f624b/LICENSE)), so implementation patterns can be reused with the required copyright/license notice if code is copied substantially.

### What FollowCursor actually does

FollowCursor validates the central v2 recommendation rather than contradicting it: it has explicit **Record** and **Edit** modes, records raw evidence first, then authors camera/speed decisions on a timeline and exports afterward ([architecture](https://github.com/sabbour/followcursor/blob/b22da76a764cdc24f4fc560e938db690ad3f624b/docs/ARCHITECTURE.md)).

Relevant techniques:

1. **One shared capture epoch.** Video recording, mouse polling, and click tracking are started from the same epoch in `MainWindow` ([`main_window.py:1355-1360`](https://github.com/sabbour/followcursor/blob/b22da76a764cdc24f4fc560e938db690ad3f624b/followcursor/app/main_window.py#L1355-L1360)). Mouse and click timestamps are milliseconds relative to that epoch ([`models.py:22-40`](https://github.com/sabbour/followcursor/blob/b22da76a764cdc24f4fc560e938db690ad3f624b/followcursor/app/models.py#L22-L40), [`models.py:77-90`](https://github.com/sabbour/followcursor/blob/b22da76a764cdc24f4fc560e938db690ad3f624b/followcursor/app/models.py#L77-L90)).
2. **Actual frame timing is retained.** `RecordingSession` stores `frameTimestamps` in addition to nominal duration/FPS ([`models.py:265-310`](https://github.com/sabbour/followcursor/blob/b22da76a764cdc24f4fc560e938db690ad3f624b/followcursor/app/models.py#L265-L310)). Export builds a source timeline from those timestamps, creates a constant-FPS output timeline, binary-searches the active source frame for every output time, and evaluates zoom/cursor/click overlays on that same source time ([`video_exporter.py:1025-1078`](https://github.com/sabbour/followcursor/blob/b22da76a764cdc24f4fc560e938db690ad3f624b/followcursor/app/video_exporter.py#L1025-L1078), [`:1195-1242`](https://github.com/sabbour/followcursor/blob/b22da76a764cdc24f4fc560e938db690ad3f624b/followcursor/app/video_exporter.py#L1195-L1242)).
3. **Camera direction is editable metadata, not baked capture behavior.** A zoom keyframe stores timestamp, zoom, normalized center, transition duration, reason, and speed; video segments independently store source ranges and playback rates ([`models.py:94-130`](https://github.com/sabbour/followcursor/blob/b22da76a764cdc24f4fc560e938db690ad3f624b/followcursor/app/models.py#L94-L130), [`:204-259`](https://github.com/sabbour/followcursor/blob/b22da76a764cdc24f4fc560e938db690ad3f624b/followcursor/app/models.py#L204-L259)).
4. **Pointer/click capture can seed post-hoc camera suggestions.** Mouse positions are sampled at ~60 Hz using physical pixels; clicks are separately timestamped. Auto-zoom converts positions to normalized coordinates, groups click bursts, merges spatially close activity, and builds pan chains rather than repeatedly zooming out/in ([`activity_analyzer.py:143-184`](https://github.com/sabbour/followcursor/blob/b22da76a764cdc24f4fc560e938db690ad3f624b/followcursor/app/activity_analyzer.py#L143-L184), [`:199-254`](https://github.com/sabbour/followcursor/blob/b22da76a764cdc24f4fc560e938db690ad3f624b/followcursor/app/activity_analyzer.py#L199-L254), [`:381-466`](https://github.com/sabbour/followcursor/blob/b22da76a764cdc24f4fc560e938db690ad3f624b/followcursor/app/activity_analyzer.py#L381-L466)).
5. **Raw media and editable metadata remain coupled but separable.** `.fcproj` is a ZIP containing `recording.mp4`, `project.json`, and optional voiceover audio; metadata can be rewritten without re-encoding/copying the video ([`project_file.py:1-7`](https://github.com/sabbour/followcursor/blob/b22da76a764cdc24f4fc560e938db690ad3f624b/followcursor/app/project_file.py#L1-L7), [`:74-89`](https://github.com/sabbour/followcursor/blob/b22da76a764cdc24f4fc560e938db690ad3f624b/followcursor/app/project_file.py#L74-L89)).

### Patterns to borrow

#### A. Promote the “shared epoch” to a hard capture invariant

The cue collector, Playwright activity logger, raw video, cursor path, network/run events, and frame PTS must all map to one take-relative timeline. The generated cue manifest should add:

```json
{
  "clock": {
    "timebase": "milliseconds-from-screencast-start",
    "controllerClock": "node-performance-monotonic",
    "screencastStartMonotonicMs": 842193.441,
    "pageClockOffsetMs": -0.83,
    "firstVideoPtsMs": 0
  },
  "frameTimeline": {
    "source": "ffprobe-frame-pts",
    "timestampsMs": [0, 33.37, 66.71]
  }
}
```

Unlike FollowCursor, Playwright does not hand the current pipeline a per-frame array directly. The take analyzer should extract frame PTS with `ffprobe` after capture and store them. Browser-emitted cues should preserve both browser event time and Node receipt time; the normalized `timeMs` used for editing is calibrated onto the screencast clock. This prevents cue/camera drift when screencast delivery is irregular.

#### B. Keep raw observation tracks separate from authored direction tracks

Borrow the separation implied by `RecordingSession`:

```json
{
  "observationTracks": {
    "pointer": [{ "timeMs": 1020, "x": 0.41, "y": 0.63 }],
    "clicks": [{ "timeMs": 1180, "x": 0.42, "y": 0.64, "button": "left" }],
    "semanticCues": [{ "name": "first-child-running", "timeMs": 441960 }],
    "frameTimestampsMs": [0, 33.37, 66.71]
  },
  "directionTracks": {
    "camera": [],
    "edits": [],
    "captions": [],
    "audio": []
  }
}
```

Observation tracks are immutable evidence from capture. Direction tracks are replaceable editorial decisions. Re-running auto-direction must never rewrite the raw cues/pointer/frame timeline.

#### C. Use semantic cues first, pointer/click clusters second

FollowCursor necessarily infers intent from pointer and clicks. Agentweaver can do better because Playwright and the application expose DOM elements and run events.

Priority for camera proposals:

1. semantic state cue plus captured DOM rectangle (`subtask.running`, `preview-ready`, populated trace);
2. interaction target rectangle (`click`, `drag`, typed field);
3. pointer/click clustering as fallback when no semantic target exists.

FollowCursor’s spatial clustering and pan-chain idea is still valuable: nearby targets should become one sustained zoom; temporally adjacent targets in different regions should become pan points, not zoom-out/zoom-in churn. The numeric thresholds in FollowCursor are tuned for its desktop recorder and should not be copied blindly; Agentweaver thresholds must be expressed in normalized viewport space and validated on actual takes.

#### D. Adopt source-range edit segments and camera keyframes, but decouple speed from zoom

FollowCursor proves a simple, serializable timeline model works. The Agentweaver resolved direction format should use explicit tracks:

```json
{
  "cameraKeyframes": [
    {
      "id": "cam-topology-running",
      "atCue": "first-child-running",
      "scale": 1.08,
      "targetRectCue": "active-child-cluster",
      "transitionMs": 500,
      "easing": "quintic-ease-out",
      "reason": "Keep the active topology wave readable"
    }
  ],
  "editSegments": [
    {
      "id": "topology-active-window",
      "fromCue": "first-child-running",
      "toCue": "terminal-cascade-start",
      "selection": "activity-windows",
      "rate": 8
    }
  ]
}
```

One deliberate divergence: FollowCursor stores speed on a zoom-in keyframe. Agentweaver should keep speed/edit segments independent from camera keyframes because a full-frame wait may be accelerated, and a zoomed proof moment may remain 1×. Coupling them would make the 14-beat cut harder to reason about and validate.

#### E. Preserve coordinate-space metadata before normalization

FollowCursor records physical screen pixels and the monitor rectangle, then derives normalized centers. For browser capture, every rect/pointer sample should carry enough information to reproduce that conversion:

```json
{
  "coordinateSpace": {
    "kind": "browser-viewport-css-pixels",
    "viewportWidth": 2560,
    "viewportHeight": 1440,
    "deviceScaleFactor": 1,
    "videoWidth": 2560,
    "videoHeight": 1440
  },
  "rectCssPx": { "x": 1382, "y": 317, "width": 870, "height": 662 },
  "rectNormalized": { "x": 0.5398, "y": 0.2201, "width": 0.3398, "height": 0.4597 }
}
```

Storing only normalized values hides DPI/viewport mismatches; storing only pixels makes alternate-resolution rendering brittle. Retain both and validate the conversion.

#### F. Optional take bundle, without sacrificing PR diffs

FollowCursor’s `.fcproj` bundle is useful for artifact portability. Agentweaver should keep the canonical review surfaces as sibling JSON files in `scripts/demo-recording/plans/`, as already proposed, but may additionally package a take for transfer/replay:

```text
sizzle-20260730-a.awtake (ZIP)
  raw.webm
  capture-cues.json
  capture-analysis.json
  direction.json
  narration/
  licenses/
```

The bundle is an artifact, not the only editable source of truth. `*.capture.json` and `*.direction.json` stay plain text so reviewers can inspect timing/camera changes in a PR.

### Revised tooling order after reviewing FollowCursor

1. Integrate the continuity fix.
2. Establish and test the shared capture epoch.
3. Extract/store actual frame PTS after every capture.
4. Capture immutable semantic, pointer, click, interaction-target, and rectangle tracks.
5. Generate camera proposals from semantic targets first, then spatial pointer/click clustering.
6. Present/edit camera keyframes and source-range speed/cut segments after capture.
7. Render every overlay and camera transform against the calibrated source timeline into a deterministic CFR output.
8. Optionally archive the raw media plus manifests as an `.awtake` bundle.

### Effect on the v2 verdict

FollowCursor strengthens the capture-first/direct-after recommendation. The main borrowed ideas are the shared epoch, actual frame timestamps, immutable raw activity tracks, editable serialized keyframes/segments, spatially coherent pan chains, and deterministic source-to-output timeline mapping. Agentweaver’s advantage is richer semantic instrumentation; it should not regress to cursor-following as the primary source of meaning.


---

## 8. Revision — DOM-only semantic cue detection (Sabbour decision)

**Revision date:** 2026-07-30  
**Normative status:** This section supersedes every earlier `run-event` / `topology-state` cue example and any tooling step that implied subscribing to Agentweaver SSE, run events, or backend coordinator internals.

### Final constraint

The recording harness observes **rendered DOM only**. It may wait for or passively watch selectors, visibility, attributes, text, counts, and declarative DOM predicates. It must not subscribe to the product’s SSE endpoint, query the coordinator event log, import topology state types, or interpret backend event payloads.

This preserves a clean boundary: the harness records what a user can actually see. Backend timing evidence remains useful for budgeting, but backend events are not capture inputs.

### Corrected cue-source vocabulary

Allowed `source.kind` values:

```text
selector   — an element first exists/reaches the requested visibility state
attribute  — an element attribute first equals/matches a declared target
text       — an element’s rendered text first contains/matches a declared target
predicate  — a declarative computation over DOM elements/attributes/text/counts
```

Removed entirely:

```text
run-event
 topology-state
```

No arbitrary JavaScript strings belong in the plan schema. Computed predicates use a small validated operator set so plans remain deterministic and reviewable:

```text
exists
count-gte
count-eq
any-attribute-in
all-attribute-in
text-includes
text-matches
```

A predicate can require `minCount` to prevent an empty collection from vacuously satisfying `all-attribute-in`.

---

### Real frontend markup audit

#### Topology: current markup is visually observable but not yet a stable automation contract

Today `CoordinatorTopologyGraph` renders:

- the graph container as a plain `<div className={styles.container}>` around React Flow (`apps/web/src/components/CoordinatorTopologyGraph.tsx:610-638`);
- each node card as `<div role="article" aria-label="${node.title}: ${status label}">` (`CoordinatorTopologyGraph.tsx:261-285`);
- status as visible badge text such as `Pending`, `Running`, `Ready for assembly`, and `Completed` (`CoordinatorTopologyGraph.tsx:228-247`, `:289-310`).

It does **not** currently expose stable `data-testid`, node-kind, node-id, or raw node-status attributes. A DOM-only watcher could temporarily query `[role="article"][aria-label$=": Running"]`, but that is fragile: it couples capture to translated/presentation text and cannot robustly distinguish coordinator cards from subtask cards. React Flow’s internal classes/attributes are third-party implementation details and should not become the harness contract.

**Required frontend prerequisite:** add stable, nonvisual observability attributes:

```tsx
<div data-testid="coordinator-topology-graph" className={styles.container}>

<div
  data-testid="topology-node"
  data-node-id={node.id}
  data-node-kind={node.kind}
  data-node-status={node.status}
  role="article"
  aria-label={`${node.title}: ${sm.label}`}
>
```

These names are proposed prerequisites; they do not exist in current HEAD. They expose state already rendered by the component and do not expose backend transport details.

The actual status values the DOM contract must preserve are the frontend’s existing values: `pending`, `dispatched`, `running`, `assemble_ready`, `rai_flagged`, `pending_capacity`, `completed`, and `failed`. Do not invent a literal `done` status merely for the demo.

#### Trace: current markup also lacks stable capture selectors

`TransactionTracePanel` currently renders:

- a root panel with no test id;
- a tree container with only a generated style class;
- each span row as a `<button>` with `aria-expanded` when applicable, but no stable span id/type/selected attribute (`apps/web/src/components/runs/TransactionTracePanel.tsx:218-281`);
- empty text `No trace data available for this run yet.` (`TransactionTracePanel.tsx:427-429`).

**Required frontend prerequisite:** add:

```tsx
<div data-testid="transaction-trace-panel" className={styles.panel}>
<div data-testid="trace-tree" className={styles.tree}>
<button
  data-testid="trace-span"
  data-span-key={node.key}
  data-span-type={node.type}
  data-selected={isSelected ? "true" : "false"}
  aria-pressed={isSelected}
  ...
>
```

Again, these are proposed stable DOM contracts, not current attributes.

---

### Corrected Beat 7 capture example — DOM only

This replaces the prior example containing `run-event` and `topology-state`. It assumes the topology markup prerequisite above has landed.

```json
{
  "1.7": {
    "title": "Topology graph animating live",
    "outputBudget": { "preferredMs": 13000, "minMs": 11000, "maxMs": 20000 },
    "capturePolicy": "record-until-required-cues-or-timeout",
    "requiredCues": [
      {
        "name": "topology-visible",
        "source": {
          "kind": "predicate",
          "operator": "count-gte",
          "selector": "[data-testid='coordinator-topology-graph'] [data-testid='topology-node']",
          "value": 1
        },
        "rect": {
          "mode": "element",
          "selector": "[data-testid='coordinator-topology-graph']"
        }
      },
      {
        "name": "first-child-running",
        "source": {
          "kind": "selector",
          "selector": "[data-testid='topology-node'][data-node-kind='subtask'][data-node-status='running']",
          "state": "visible",
          "occurrence": "first"
        },
        "rect": { "mode": "matched-element" }
      },
      {
        "name": "topology-done",
        "source": {
          "kind": "predicate",
          "operator": "all-attribute-in",
          "selector": "[data-testid='topology-node'][data-node-kind='subtask']",
          "attribute": "data-node-status",
          "values": ["assemble_ready", "completed"],
          "minCount": 1
        },
        "rect": {
          "mode": "union",
          "selector": "[data-testid='topology-node'][data-node-kind='subtask']"
        }
      }
    ],
    "optionalCues": [
      {
        "name": "terminal-cascade-start",
        "source": {
          "kind": "predicate",
          "operator": "any-attribute-in",
          "selector": "[data-testid='topology-node'][data-node-kind='subtask']",
          "attribute": "data-node-status",
          "values": ["assemble_ready", "completed"]
        },
        "rect": {
          "mode": "first-matching",
          "selector": "[data-testid='topology-node'][data-node-kind='subtask'][data-node-status='assemble_ready'], [data-testid='topology-node'][data-node-kind='subtask'][data-node-status='completed']"
        }
      },
      {
        "name": "active-child-cluster",
        "source": {
          "kind": "attribute",
          "selector": "[data-testid='topology-node'][data-node-kind='subtask']",
          "attribute": "data-node-status",
          "equals": "running",
          "occurrence": "first"
        },
        "rect": {
          "mode": "union",
          "selector": "[data-testid='topology-node'][data-node-kind='subtask'][data-node-status='running']"
        }
      }
    ],
    "framingIntent": {
      "default": "full-frame",
      "allowed": ["full-frame", "cue-rect-pan"],
      "mustKeepVisible": ["topology-visible", "active-child-cluster"],
      "maxScale": 1.12
    },
    "pacingIntent": {
      "preserveAt1x": ["first-child-running", "topology-done"],
      "compressibleIntervals": [["first-child-running", "terminal-cascade-start"]],
      "cuttableWhenNoVisualChange": true
    }
  }
}
```

`topology-done` intentionally treats `assemble_ready` and `completed` as successful worker-terminal states because those are real rendered statuses. If the approved story specifically requires full orchestration completion rather than “all workers finished,” change `values` to `["completed"]`; do not alias either state to a fake `done` value.

### Corrected Beat 10 capture example — DOM only

This assumes the trace markup prerequisite above has landed.

```json
{
  "1.10": {
    "title": "Trace and observability",
    "outputBudget": { "preferredMs": 11000, "minMs": 9000, "maxMs": 14000 },
    "prerequisites": ["selected run has at least one persisted span"],
    "steps": [
      {
        "type": "waitFor",
        "selector": "[data-testid='trace-tree'] [data-testid='trace-span']",
        "timeout": 30000,
        "cue": {
          "name": "trace-visible",
          "rect": {
            "mode": "element",
            "selector": "[data-testid='trace-tree']"
          }
        }
      }
    ],
    "passiveCues": [
      {
        "name": "trace-span-selected",
        "source": {
          "kind": "attribute",
          "selector": "[data-testid='trace-span']",
          "attribute": "data-selected",
          "equals": "true",
          "occurrence": "first"
        },
        "rect": { "mode": "matched-element" }
      },
      {
        "name": "trace-empty",
        "source": {
          "kind": "text",
          "selector": "[data-testid='transaction-trace-panel']",
          "includes": "No trace data available for this run yet."
        },
        "onMatch": "abort-beat"
      }
    ],
    "framingIntent": {
      "default": "full-frame",
      "allowed": ["full-frame"],
      "cameraLocked": true
    },
    "pacingIntent": {
      "preserveAt1x": ["trace-visible", "trace-span-selected"],
      "minimumReadableHoldMs": 7000
    }
  }
}
```

---

### Concrete capture-plan mechanism

#### 1. Named cues on existing blocking waits

Current `capture-plan.mjs` behavior is confirmed:

- `waitFor` blocks on a hand-authored locator, then emits only generic `__demoActivityMark('waitFor')` (`scripts/demo-recording/lib/capture-plan.mjs:176-182`);
- `waitText` blocks on `document.body.innerText.includes(...)`, then emits only generic `waitText` activity (`capture-plan.mjs:188-190`).

Extend both step types with an optional `cue` object:

```json
{
  "type": "waitFor",
  "selector": "page.locator('[data-testid=trace-span]')",
  "timeout": 30000,
  "cue": {
    "name": "trace-visible",
    "rect": { "mode": "matched-element" }
  }
}
```

```json
{
  "type": "waitText",
  "selector": "[data-testid='preview-status']",
  "text": "Ready",
  "timeout": 180000,
  "cue": {
    "name": "preview-ready",
    "rect": { "mode": "matched-element" }
  }
}
```

When the wait resolves, generated code should:

1. keep the existing generic activity mark for idle trimming/backward compatibility;
2. resolve the exact matched element;
3. call the in-page cue emitter with the named cue and element;
4. capture the element’s `getBoundingClientRect()` plus viewport dimensions;
5. report the cue to the Node-side collector.

For `waitText`, a `selector` should be required when `cue.rect.mode` is `matched-element`; body-wide text search cannot identify a meaningful rectangle. A separate `cue.rect.selector` may target a containing panel.

Conceptual generated code:

```js
const target = page.locator('[data-testid="trace-span"]').first();
await target.waitFor({ state: 'visible', timeout: 30000 });
await page.evaluate(() => window.__demoActivityMark?.('waitFor'));
await target.evaluate((element, cue) => window.__demoEmitDomCue?.(cue, element), {
  name: 'trace-visible',
  source: { kind: 'selector', selector: '[data-testid="trace-span"]' },
  rect: { mode: 'matched-element' }
});
```

#### 2. Passive declared watchers via MutationObserver

Add a cue bootstrap beside the existing zoom/cursor/activity bootstrap. It is installed through the same `page.addInitScript(...)` pattern, but does not watch backend traffic.

At capture start, the Node harness passes a validated set of passive cue definitions to:

```js
window.__demoConfigureDomCueWatchers(definitions)
```

The in-page watcher:

1. performs an immediate evaluation so already-rendered targets are not missed;
2. observes `document.documentElement` with one shared `MutationObserver` using `subtree`, `childList`, `attributes`, and `characterData`;
3. coalesces mutation bursts into one microtask or animation-frame evaluation;
4. evaluates only unfired one-shot definitions;
5. emits the first successful match with current DOM evidence and rectangle;
6. permanently marks that globally unique cue name fired for the take.

This is one observer for all declared cues, not one observer per selector and not an unconstrained “record every mutation” stream. Definitions are declarative and finite.

Attribute filtering can be derived from definitions—for example `data-node-status`, `data-selected`, and `aria-label`—while `childList`/`characterData` remain enabled for selector/text/count changes.

#### 3. Cue transport and durability

Use a Playwright page binding, not session storage as the authoritative cue sink:

```js
const cueLog = [];
const firedCueNames = new Set();
const captureClockStart = performance.now();

await page.exposeBinding('__demoReportCue', (_source, cue) => {
  if (firedCueNames.has(cue.name)) return;
  firedCueNames.add(cue.name);
  cueLog.push({
    ...cue,
    timeMs: performance.now() - captureClockStart,
    pageObservedTimeMs: cue.pageObservedTimeMs
  });
});
```

The in-page watcher calls `globalThis.__demoReportCue(payload)`. Detection remains DOM-only; the binding merely transfers evidence from the page to the capture process. The Node array survives document and cross-origin navigation, unlike `sessionStorage`.

After every explicit `goto`, re-run `__demoConfigureDomCueWatchers` for watchers scoped to the new page. `page.addInitScript` reinstalls the API on each document; the Node-side fired-name set prevents duplicates across reloads.

Cue names must be globally unique within a take, preferably `${beatId}:${cueName}` internally even when the plan uses a beat-local short name.

#### 4. Cue record and rectangle format

```json
{
  "name": "1.7:first-child-running",
  "timeMs": 29481.2,
  "pageObservedTimeMs": 29479.9,
  "url": "https://app.example/projects/42/runs/abc",
  "source": {
    "kind": "selector",
    "selector": "[data-testid='topology-node'][data-node-kind='subtask'][data-node-status='running']"
  },
  "observed": {
    "matchCount": 2,
    "attribute": "data-node-status",
    "value": "running",
    "text": "Running"
  },
  "coordinateSpace": {
    "kind": "browser-viewport-css-pixels",
    "viewportWidth": 2560,
    "viewportHeight": 1440,
    "devicePixelRatio": 1
  },
  "rectCssPx": { "x": 1382, "y": 317, "width": 870, "height": 662 },
  "rectNormalized": { "x": 0.5398, "y": 0.2201, "width": 0.3398, "height": 0.4597 }
}
```

Rectangle rules:

- `matched-element`: first visible element that satisfied the definition;
- `element`: explicit rectangle selector;
- `first-matching`: first visible element from an explicit selector;
- `union`: bounding union of all visible matches, clamped to viewport;
- `none`: timestamp/state only.

Use `getBoundingClientRect()` at the moment the cue fires. Store raw CSS pixels and normalized values, plus viewport and DPR. If an element is missing, detached, zero-sized, or outside the viewport, emit the cue with `rectStatus` explaining why; do not fabricate a rectangle.

For the post-camera sizzle profile, capture-time body zoom should be disabled so cue rectangles describe the unwarped product frame. If legacy capture-time zoom remains active, `getBoundingClientRect()` describes the transformed captured pixels and must be labeled `rectSpace: "captured-transformed-viewport"`.

#### 5. Watcher evaluation semantics

- **Visibility:** element exists, has nonzero rect, is not `display:none`/`visibility:hidden`, and intersects the viewport.
- **Attribute:** evaluate the actual DOM attribute, not React state or CSS class names.
- **Text:** normalize whitespace; default to case-sensitive exact/includes as declared. Regex must be schema-bounded and serialized as pattern + flags.
- **All predicate:** requires `matches.length >= minCount` and every value in the allowed set.
- **First occurrence:** one-shot per cue name; mutation churn cannot emit duplicates.
- **Stable state option:** optionally require a predicate to remain true for `stableForMs` before emission. Use this for layouts that briefly render an intermediate tree; do not use it for short state transitions such as first-running.
- **Timeout:** passive watchers may specify `deadlineMs`; missing required cues fail take validation after recording rather than blocking all other watchers.

---

### Revised tooling implementation order — DOM only

This replaces Section 5’s earlier ordering where any wording could imply run-event instrumentation.

1. **Phase 0 continuity:** integrate/port `cc22fbdc`; test same-page beat continuity.
2. **Stable frontend DOM contracts:** add topology and trace attributes/test ids listed above, plus focused frontend tests asserting status/selection attributes update when rendered state changes.
3. **DOM-only capture schema:** validate `selector`, `attribute`, `text`, and declarative `predicate`; reject `run-event`, `topology-state`, arbitrary JavaScript predicates, duplicate cue names, and invalid rectangle modes.
4. **Shared capture clock + Node cue sink:** establish the take clock immediately around screencast start; expose `__demoReportCue`; keep cue evidence across navigations.
5. **Blocking cue extension:** add optional named cue + rectangle capture to existing `waitFor` and `waitText` steps while retaining generic activity marks.
6. **Passive DOM watcher:** install one MutationObserver-backed evaluator for declared one-shot cues; support immediate evaluation, dedupe, stable-state option, deadlines, and reconfiguration after `goto`.
7. **Frame timing extraction:** use `ffprobe` to retain actual video PTS and calibrate cue times to source frames.
8. **Take analyzer:** validate required cues, report source intervals, static/activity windows, missing/zero-size rectangles, and budget pressure.
9. **Post-capture direction authoring:** generate/edit camera keyframes and source-range cut/speed segments from the DOM cue manifest.
10. **Compositor/audio/QC:** render camera, speed, hard/match cuts, captions, licensed music/ducking, and validate the 120-second output.

There is no backend event-stream adapter, SSE client, coordinator event decoder, or topology state import in this design.

### Final effect on the capture-first verdict

The redesign remains capture-first/direct-after, now with a stricter and more portable observation boundary. FollowCursor’s shared-clock, raw-observation, and post-authored-keyframe patterns still apply; Agentweaver’s raw observations are DOM state, pointer/click activity, and frame PTS—not backend events.

---

## 2026-07-29T21-34-14: Creative-director shot-directing scheme for demo sizzle reel
**By:** link
**What:** Creative-director shot-directing scheme for demo sizzle reel
**References:** scripts/demo-recording/lib/beats.mjs, scripts/demo-recording/lib/capture-plan.mjs, scripts/demo-recording/lib/zoom.mjs, scripts/demo-recording/lib/pacing.mjs, scripts/demo-recording/lib/ffmpeg.mjs, scripts/demo-recording/cli.mjs, scripts/demo-recording/plans/blueprint-demo-beats.md, scripts/demo-recording/plans/azure-aks-demo-beats.md, PR #643 / commit cc22fbdc (request context)
**Why:** # Creative-director shot-directing scheme for demo sizzle reel

## Executive recommendation

Treat the 60-second sizzle reel as an editorial composition built from beat clips, not as a shorter version of the long walkthrough. Capture clean, readable UI actions; then make camera, pacing, transitions, captions, and music deterministic in a post-production manifest. Default to straight cuts, full-frame context, real-time “proof” moments, and restrained zooms. Reserve speed-up and stylized transitions for clearly signaled time/context changes.

The minimal repository change should be one optional `Direction:` JSON object per markdown beat, parsed into `beat.direction`, plus named timeline cues emitted by capture steps. A new FFmpeg compositor can then resolve cue-relative camera/speed/caption/transition instructions without hard-coded timestamps.

## Research summary

### Reference corpus and factual observations

- Motion Swell’s SaaS-specific practitioner guide defines a sizzle reel as a short, high-energy highlight reel rather than a detailed demo, recommends a strong visual hook in the first 5–7 seconds, a maximum around 60 seconds, varied pacing, and a clear CTA. This is vendor guidance, not a universal measured law, but it maps directly to this deliverable: https://www.motionswell.com/blog/sizzle-reels-for-saas
- Linear’s official channel currently lists several compact feature-launch pieces: “Introducing Linear Releases” (0:30), “Introducing Code Intelligence” (0:34), “Introducing Linear Diffs” (0:52), and “Introducing Linear Agent” (0:55). That is strong evidence that 30–60 seconds is a normal format for a single feature/value reel, distinct from its longer 7:09 “Introducing coding sessions” walkthrough: https://www.youtube.com/@linear/videos
- Arc’s official “A Quick Tour of Arc Basics” is a useful contrasting reference: a guided product tour built around continuous UI flow, clear cursor interaction, voiceover, and gentle music rather than a rapid feature montage: https://www.youtube.com/watch?v=sKUdS1LUzhs
- GitHub’s official channel similarly separates formats: short customer/product stories (for example a 1:05 ASOS/Copilot piece), mid-length feature introductions (3:29), and a 7:43 app tour. The editing format follows the communication goal; a reel should not inherit walkthrough pacing merely because it uses walkthrough footage: https://www.youtube.com/@GitHub/videos
- Stripe Sessions is primarily longer keynote/demo material, but is a useful reference for clean UI explanation and motion used to clarify flows rather than decorate every cut: https://stripe.com/sessions/2025
- Screen Studio makes click-driven zooms editable timeline objects, with explicit duration, level, auto/manual target, and removal controls. That validates representing zoom as metadata rather than permanently coupling it to every click: https://screen.studio/guide/adding-editing-zooms
- TechSmith’s Camtasia guidance says zoom/pan should focus attention; its SmartFocus recommendations explicitly say to limit random clicking, keep the cursor deliberate, and avoid “talking” with the cursor. Its transition guide says artistic transitions should be sparse and purposeful: https://www.techsmith.com/learn/tutorials/camtasia/animations/ and https://www.techsmith.com/learn/tutorials/camtasia/video-transitions/
- Adobe documents editing to musical beat markers and gradual time-remapping ramps; the important operational detail is that speed changes should have eased handles rather than instantaneous jumps: https://helpx.adobe.com/ph_fil/premiere-pro/how-to/edit-music-video.html and https://www.adobe.com/creativecloud/video/hub/guides/premiere-pro-speed-ramp.html
- Adobe’s audio-ducking implementation drives music gain keyframes from dialogue tracks, supporting narration + music rather than forcing one or the other: https://helpx.adobe.com/premiere/desktop/add-audio-effects/adjust-volume-and-levels/automatically-duck-audio.html
- W3C says captions must synchronize spoken words and meaningful non-speech audio (speaker identity, music, important sounds). Captions are an accessibility track; short marketing headlines are a separate graphic layer: https://www.w3.org/WAI/perspective-videos/captions/

### Practical editorial grammar for Agentweaver

#### 1. Cut pacing and rhythm

Recommended 60-second arc (editorial target, not claimed as an industry statistic):

- **0–5s — hook:** 2–4 shots, usually 0.8–1.8s each. Lead with Agentweaver’s strongest visual proof (topology waking up, multiple agents moving, live preview resolving), not setup or a logo animation.
- **5–18s — orient:** 2–3s shots. Establish “request → plan → team” clearly enough that the viewer understands causality.
- **18–42s — accelerate:** mostly 0.7–1.5s cuts through dispatch, board motion, approvals, trace/preview/decision outputs. Insert a 2–3s hold whenever text must actually be read.
- **42–54s — payoff:** decelerate to 2–4s holds for the live preview, assembled output, topology completion, or trace tree. The audience needs proof, not another montage.
- **54–60s — resolve/CTA:** 3–5s stable branded end frame, preferably with one concise promise and URL/CTA.

Cut on musical accents when the visual meaning also changes. Do not cut merely because a beat marker exists. Narration clause boundaries, UI state changes, and music accents should ideally coincide. Use jump cuts to remove mechanical navigation; preserve a short anticipation–action–result sequence for important clicks.

**Hold when:** a topology node changes state, generated text streams, a human approval appears, a preview first becomes usable, or the viewer must read a result.

**Cut fast when:** traversing menus, repeated agent dispatches, repetitive board movement, or enumerating breadth after the core workflow is already understood.

#### 2. Camera moves for screen capture

- **Full-frame:** default for topology, board, dashboard, preview, or any state where relationships matter.
- **Zoom-region:** use for a single meaningful UI detail: OutcomeSpec rationale, one topology node lighting up, approval card, trace branch, preview result, or a decision entry. Recommended final scale is generally modest (about 1.2–1.45 for 1080p output from higher-resolution capture); avoid the current heuristic’s routine 1.55–1.65 unless the source is captured above 1080p.
- **Pan:** only when the destination is causally related and cannot fit in one crop—e.g., move from the coordinator node to newly spawned child nodes. Avoid panning across ordinary navigation.
- **Push-in timing:** start just before the meaningful state change, settle by the moment of change, hold long enough to read, then either cut while close or return to full frame. Constant zoom-in/zoom-out around every click reads as automated screen-recorder behavior, not direction.
- **Do not double-zoom:** the existing browser-body transform is baked into capture. A post compositor must either use clean capture (`cameraMode: post`) or honor capture-time zoom (`cameraMode: capture`), never apply both.

For a sizzle profile, prefer capturing a clean 1440p/4K full frame and composing to 1080p. Keep capture-time zoom for long-form walkthroughs where the interaction itself must remain readable.

#### 3. Speed ramping

Use real time for:
- typing a short, legible phrase;
- the first visible token/event of streaming output;
- topology nodes changing state;
- approvals and result reveals;
- the exact moment a preview becomes live.

Use 2× for short navigation, tab changes, moderate scrolling, or a UI operation whose progression still matters. Use 4× for long waits, repetitive polling, install/build logs, or multiple equivalent task updates. For waits with no informative motion, a hard cut or 6–12-frame branded time-passage bridge is cleaner than showing a spinner at 4×.

Ramp into/out of acceleration over roughly 150–300ms; do not instantaneously switch rate mid-cursor movement. Preserve 6–12 frames at 1× before and after the important result so cause and effect remain readable. Screen content generally should not use slow motion; a longer real-time hold is clearer and avoids interpolation artifacts.

#### 4. Transitions

Recommended hierarchy:

1. **Hard cut (default):** between most feature shots and UI states. Modern, fast, and invisible.
2. **Match cut:** same object/region across states—for example a work-plan node matched to its corresponding board card, or an approval card matched to the completed result. This feels designed without adding a decorative effect.
3. **Short dissolve (150–300ms):** genuine time passage, environmental change, or transition into/out of the CTA. Avoid dissolving between adjacent clicks because it weakens causality.
4. **Whip/pan cut (200–350ms):** at most once or twice, only when both outgoing and incoming shots share a clear direction of travel—e.g., moving from coordinator to agents or UI to deployed preview.

Avoid cube rotations, page curls, star wipes, long blur wipes, and unrelated glitch effects. Even though editing tools expose them, the product references above rely principally on clean cuts, native UI motion, restrained fades/slides, and purposeful branded movement.

#### 5. Text and caption overlays

Separate three concepts:

- **Accessibility subtitles:** synchronized narration plus meaningful music/SFX labels; deliver WebVTT/SRT and optionally burn an ASS version for social exports.
- **Headline:** 2–7 words expressing the benefit (“Plan. Dispatch. Ship.”), not repeating the current narration sentence.
- **Callout:** a short label anchored to a UI region (“Human approval”, “Isolated sandbox”, “Live topology”).

Use no more than one headline or callout at a time. Enter with a 150–250ms opacity + 8–16px rise/slide; exit faster (100–180ms) or cut with the shot. Avoid typewriter animation except when the product itself is generating text. Keep essential text inside the central ~80% title-safe area; keep lower captions above player controls and never cover the UI target. Prefer top-left/top-right for product labels and lower-center for subtitles.

#### 6. Audio and music

For the 60s reel, use **music + selective voiceover**, not voiceover-only and not wall-to-wall narration. Music supplies rhythm during montage sections; voiceover should state the transformation and key differentiator, while headlines carry section labels. Duck music under narration using generated gain keyframes or sidechain compression, then allow it to rise between lines and into the payoff/CTA.

Add subtle UI SFX only to high-value state changes (dispatch, approval, preview ready, CTA hit). Do not sonify every click. Place major cuts/reveals on downbeats or phrase changes, but let readable product moments overrun a beat when necessary.

## Existing pipeline: what is present

### Beat/source model

- `lib/beats.mjs` parses only beat heading, title, act, narration, blockers, and retains raw beat markdown. `On screen:` is not structured.
- The committed markdown plans are narration/action specifications. There is no parsed directing metadata.
- `classifyZoom()` in `lib/zoom.mjs` infers a broad scale from keywords, but repository search shows it is currently exercised by tests rather than integrated into final assembly.

### Capture

- Capture plans are JavaScript objects with `startUrl`, `videoPath`, `viewport`, auth, and ordered `steps` (`badge`, `pause`, `click`, `hover`, `type`, `press`, `eval`, `waitFor`, `select`, `waitText`, `goto`).
- `capture-plan.mjs` produces a Playwright function, starts a page screencast, draws a synthetic cursor/click ripple, and performs browser-body zoom/pan during `focus()`.
- Activity events are persisted in session storage and returned for trimming, but events are generic (`click`, `focus`, `mutation`, etc.), not named editorial cues and do not preserve target rectangles.
- Despite the task context referencing `freshNavigation`/per-beat `startUrl`, no `freshNavigation` token exists in this current worktree and the inspected renderer performs one unconditional initial `page.goto(plan.startUrl)`. Reconcile the intended PR #643 state before implementing against that field.

### Post-processing

- `pacing.mjs` removes the middle of long static gaps; it does not speed-ramp them.
- `ffmpeg.mjs` can probe, concatenate WAV/video, mux, pad video/audio to equal duration, detect activity, extract frames, and trim/concat kept intervals.
- `assemble-final` concatenates already-synced WebM segments with FFmpeg concat demuxer and stream copy. Therefore transitions are hard joins only and depend on matching stream parameters.
- There is no post camera move, crop animation, rate change, xfade, caption rendering, music/SFX mix, ducking, beat-marker analysis, color/background treatment, editorial timeline manifest, or final loudness/QC pass.

## Proposed minimal annotation schema

Add one optional single-line JSON object after `On screen:` (or after `Narration:` when no on-screen note exists):

```md
Direction: {"pace":"payoff","camera":{"shot":"zoom-region","cue":"preview-ready","scale":1.32,"enterMs":320,"holdMs":2200},"speed":[{"from":"build-start","to":"preview-ready","rate":4,"rampMs":220}],"transitionOut":{"type":"match-cut","cue":"preview"},"caption":{"kind":"headline","text":"From plan to live preview","placement":"top-left"},"music":{"sync":"downbeat","energy":"lift"}}
```

Parse into this minimal typed shape:

```ts
type BeatDirection = {
  pace?: 'hook' | 'hold' | 'normal' | 'fast-cut' | 'payoff' | 'cta';
  camera?: {
    shot: 'full-frame' | 'zoom-region' | 'pan';
    cue?: string;
    toCue?: string;       // required for pan
    scale?: number;       // zoom-region only
    enterMs?: number;
    holdMs?: number;
    exitMs?: number;
  };
  speed?: Array<{
    from: string;         // named capture cue
    to: string;
    rate: 1 | 2 | 4;
    rampMs?: number;
  }>;
  transitionIn?: {
    type: 'cut' | 'dissolve' | 'match-cut' | 'whip';
    durationMs?: number;
    cue?: string;
    direction?: 'left' | 'right' | 'up' | 'down';
  };
  transitionOut?: sameAsTransitionIn;
  caption?: {
    kind: 'none' | 'headline' | 'callout';
    text?: string;
    placement?: 'top-left' | 'top-right' | 'lower-third' | 'center';
    cue?: string;
    enter?: 'cut' | 'fade' | 'fade-up' | 'slide';
    exit?: 'cut' | 'fade';
  };
  music?: {
    sync?: 'none' | 'beat' | 'downbeat' | 'phrase';
    energy?: 'bed' | 'build' | 'lift' | 'drop' | 'resolve';
  };
};
```

Defaults: `pace=normal`, `camera.shot=full-frame`, transition=`cut`, no speed segments, no marketing caption, music sync=`none`. Accessibility subtitles should be a render-profile/global option, not repeated per beat.

### Cue extension to capture plans

Add one optional property to any existing step:

```js
{ type: 'waitFor', selector: "page.getByTestId('preview-frame')", cue: 'preview-ready' }
```

When a cued step executes, capture should write a timeline event containing `cue`, timestamp, URL, and—when a selector exists—the post-layout bounding rectangle normalized to the viewport. Also allow an explicit zero-action `{ type: 'cue', name: 'build-start' }`. This is the key de-risking design: markdown direction remains stable even if capture timing changes, and post-production never depends on fragile absolute seconds.

### Why this is minimal

- One optional parsed line in beat markdown.
- One optional `cue` on existing steps plus one explicit cue step.
- No attempt to encode an entire NLE timeline in markdown.
- Sensible defaults preserve existing long-form plans.
- The same beat plan can render through profiles: `walkthrough` (capture-time focus, narration, hard cuts) or `sizzle` (clean capture, post camera, music, accelerated montage).

## Tooling gaps and scoped implementation plan

### Phase 0 — reconcile pipeline state (0.5 day)

Confirm where the `freshNavigation`/`startUrl` continuity work from PR #643 lives and merge/rebase the intended implementation before adding direction metadata. The current checked-out worktree does not contain `freshNavigation`.

### Phase 1 — schema, parser, validation (0.5–1 day)

- Extend `beats.mjs` with `extractDirection()` parsing strict one-line JSON.
- Validate enums/ranges; report beat ID and field path on errors.
- Add parser tests for CRLF, absent metadata/defaults, malformed JSON, and preservation in narration output.
- Add a small documented JSON Schema or JSDoc typedef.

### Phase 2 — cue/timeline sidecar (1–2 days)

- Add step `cue` support and explicit cue steps.
- Emit `{name,t,rect,url}` into the existing activity log or a separate `timeline.json`.
- Record capture dimensions and source fps.
- Add a `cameraMode` render profile so capture-time and post-time zoom cannot stack.

### Phase 3 — deterministic FFmpeg compositor MVP (3–5 days)

Build `lib/compositor.mjs` and a CLI command such as `render-directed`:

1. Normalize each clip to common resolution, SAR, pixel format, fps, timebase, and audio sample rate. This is required before FFmpeg `xfade`/`acrossfade`.
2. Resolve cue-relative ranges to timestamps.
3. Apply constant-rate 2×/4× segments with `trim` + `setpts`; preserve audio with `atempo` only if useful, otherwise mute capture audio because narration/music are authoritative. Approximate ramp shoulders initially; true smooth variable-rate remapping can be a later refinement.
4. Apply camera moves using cue rectangles and time-based scale/crop expressions. Capture above delivery resolution to avoid soft zooms.
5. Support only `cut` and short `dissolve` in MVP. Treat `match-cut` as a cut with aligned outgoing/incoming crop; defer synthetic whip blur.
6. Burn headline/callout overlays with `drawtext` or, preferably, generated ASS/libass for consistent typography. Export WebVTT/SRT separately; optionally burn subtitles for social.
7. Render H.264/AAC MP4 master rather than stream-copy WebM.

### Phase 4 — music, narration, SFX (2–3 days)

- Add a licensed music input and optional manually supplied beat/phrase marker JSON. Manual markers are more reliable than automatic beat detection for an MVP.
- Mix narration + music with `amix`; automate music gain or use `sidechaincompress`; add intro/outro fades.
- Add sparse named SFX cues.
- Add `loudnorm` analysis/render pass and clipping checks.

### Phase 5 — advanced polish (2–4 days)

- Whip transitions with directional crop/blur only where metadata supplies matching directions.
- Better eased variable speed ramps.
- Automated match-cut alignment from cue rectangles.
- Optional waveform beat detection and proposed cut suggestions (never automatic final cuts).
- Preview contact sheet or low-resolution draft render for review.

### Phase 6 — tests and QC (1–2 days)

- Unit tests for cue resolution and filter-graph generation.
- Tiny synthetic FFmpeg fixtures verifying output duration, dimensions, transition overlap, caption presence, and A/V sync.
- `ffprobe` assertions: constant fps/timebase, yuv420p, audio sample rate, expected duration.
- Visual QC checklist: no double zoom, readable text, no target under caption, no frozen padded frame during energetic montage, music ducked under every VO line.

**Rough total:** 8–13 engineering days for a dependable MVP plus polish, excluding recapture and creative iteration. A basic cut/zoom/speed/caption compositor can land in roughly 4–7 days if music analysis, whip transitions, and editor previews are deferred.

## Recommended first sizzle assembly

Before implementing advanced effects, prove the editorial model with 8–12 existing beat clips:

1. topology lights up (hook, full frame);
2. request typed (brief real-time fragment);
3. OutcomeSpec appears (short zoom/hold);
4. parallel agents dispatch (fast cuts);
5. board/task motion (2×);
6. human approval (1× hold);
7. live preview resolves (payoff, 2–3s);
8. trace/decision/cluster proof (quick breadth montage);
9. full-frame product + CTA.

This sequence will expose whether cue-relative metadata is sufficient before building advanced transitions.

## POC

No POC file was added. The repository already proves FFmpeg availability and filter execution; the highest-risk unknown is not whether FFmpeg can crop/scale/setpts/xfade, but whether capture emits stable semantic cues and target rectangles. Implementing the cue sidecar first de-risks every later effect more effectively than a standalone filter-graph sample.

---

# Demo creative-direction pipeline PR opened

**Author:** link  
**Branch:** `link/demo-creative-direction`  
**Base:** `dev`  
**PR:** https://github.com/sabbour/agentweaver/pull/649

## Synchronization

Fetched current `origin/dev` after workflow-trigger predicates PR #647 merged and merged it into the feature branch without conflicts.

- `origin/dev`: `dffbe9ed`
- merge commit: `69971247`
- pushed branch: `origin/link/demo-creative-direction`

The shared checkout was not touched; all work occurred in `.worktrees/demo-creative-direction`.

## PR scope

The PR contains:

- reusable `.github/skills/agentweaver-demo-creative-direction/SKILL.md` guidance;
- beat continuity through `freshNavigation` / `startUrl` metadata;
- DOM-only blocking and passive semantic cue collection;
- normalized rectangle evidence and cross-navigation Node-side cue persistence;
- capture/cue schemas and exact markdown beat joining;
- ffprobe frame extraction and take analysis;
- three interval categories: action, wait, dead-time;
- lenient missing/order warnings, 500ms cue/frame tolerance, and 12x continuous speed threshold;
- explicitly unapproved draft direction seeding;
- stable topology and trace capture attributes.

No backend event-stream coupling and no compositor are included.

## Validation after merging dev

- Demo-recording suite: **30 passed, 0 failed**.
- Capture instrumentation markup test: **2 passed, 0 failed**.
- Web lint: **passed**.
- Full web suite: **926 passed, 3 failed**.
  - Two failures were the known intermittent `SkillsPage` cases tracked by issue #648.
  - One `NotificationsCenter` dismissal test failed in the crowded full run; an immediate isolated rerun passed **14/14**, confirming it was unrelated to this branch's topology/trace/demo-recording changes.

The PR description references #648 and records the isolated notification rerun rather than hiding the full-suite result.


## Changeset advisory fix

Added `.changeset/demo-creative-direction-pipeline.md` with an `agentweaver: minor` release intent describing the DOM-based capture pipeline, semantic cues, take analyzer, stable markup, and reusable creative-direction skill.

- Commit: `498a2995` (`chore: add demo pipeline changeset`)
- Pushed to `origin/link/demo-creative-direction`
- Local validation: `npm run changeset:check -- --base origin/dev` passed and validated 1 changeset fragment.

---

## 2026-07-29T19-58-53: Demo-recording beats continue on the live page by default; Fresh navigation is opt-in
**By:** Link
**What:** Demo-recording beats continue on the live page by default; Fresh navigation is opt-in
**References:** scripts/demo-recording/lib/capture-plan.mjs, scripts/demo-recording/lib/beats.mjs, scripts/demo-recording/plans/blueprint-demo-beats.md, scripts/demo-recording/plans/azure-aks-demo-beats.md
**Why:** Implemented capture-plan continuity for demo recording. `renderCaptureScript(plan)` now skips the initial `page.goto(plan.startUrl)` when the current page is already at the same URL and `plan.freshNavigation` is not set, so consecutive beats keep the live browser state (open modal, active run page, etc.). A fresh load still happens for the first beat of a session (`about:blank`/no current URL), whenever `plan.freshNavigation === true`, and whenever a later beat targets a different `plan.startUrl` than the current page. In `beats.mjs`, beat markdown now supports optional metadata lines `Fresh navigation: true|false` and `Start URL: ...`, mapped to `beat.freshNavigation` and `beat.startUrl`, so scene-cut beats can declare intent in the master plans.

---

# Docs update round — judgment calls (Link, 2026-07-27)

Branch: `docs/update-round-model-ids-and-specs`. Requested by @sabbour after a coordinator audit
found docs/specs drift. Recorded here per the AI-agent process (auditable decisions inbox).

## 1. Model IDs — what I changed vs. left alone

Verified against actual code (`apps/Agentweaver.Api/Generation/GenerationModelOptions.cs`,
`apps/Agentweaver.Api/Coordinator/CoordinatorModelDefaults.cs`) before touching anything, since
"fix the retired model ID" and "match what the code actually does" can conflict:

- `Generation:Model`'s real code default is `gpt-5.6-sol` (`GenerationModelOptions.DefaultModel`),
  but docs said `gpt-5.4` in three places (`docs/guide/configuration.md`, `docs/reference/api.md`
  x2). This was genuine drift — fixed all three to `gpt-5.6-sol`.
- `Providers:GitHubCopilot:Model`'s real code default is **still literally** `claude-sonnet-4.6`
  (`CoordinatorModelDefaults.DefaultCopilotModel`), confirmed in code. The docs describing that
  default (`docs/guide/configuration.md` "Providers:GitHubCopilot:Model" row,
  `docs/reference/api.md` same row, `docs/reference/coordinator.md`'s precedence writeup, and
  `docs/deep-dive/coordinator-internals.md`'s SelectModel writeup) are **accurate to the code as it
  stands today**, so I left those as `claude-sonnet-4.6` rather than rewriting them to
  `claude-sonnet-5` — changing only the docs would have made them describe a default the running
  code doesn't actually have. If `claude-sonnet-4.6` should retire, that requires an app-code change
  to the `DefaultCopilotModel` constant (and dev appsettings/CastingService per its own doc
  comment), which is out of scope for a docs-only round — flagging as a probable follow-up issue.
- `docs/reference/coordinator.md`'s "Supported model families" table lists `gpt-5.4`, `gpt-5.4-mini`,
  `claude-opus-4.6`, `claude-sonnet-4.6`, `claude-sonnet-4.5` as valid catalog entries. I verified
  this table is a literal match of the code's own XML-doc comment listing the current Copilot CLI
  catalog (`CoordinatorModelDefaults.cs`), so these are NOT retired IDs — they're still valid
  choices in the actual catalog per code. Left unchanged.
- Fixed the two explicitly-flagged example values (`docs/guide/configuration.md` `Generation:Model`
  row and `docs/guide/getting-started.md` local-dev appsettings example `claude-sonnet-4.6` →
  `claude-sonnet-5`) exactly as instructed, plus the same illustrative example ID in
  `docs/experience/projects.md`.

## 2. Undocumented shipped features

- `#563` (suggested prompt buttons) was already fully documented in the same commit
  (`docs/experience/assistant-sessions.md`) — no action needed, docs-as-you-go worked as intended.
- `#560`/`#564`/`#570`/`#571`/`#574`/`#575` (preview-sandbox pod TTL renewal + autoscaler
  safe-to-evict pinning): these are reliability/bug fixes making previously-specified behavior
  ("inactive previews expire automatically", explicit stop) actually work correctly — they don't
  change the user-facing contract already captured in
  `specs/agent-execution-sandbox/preview-sandbox-apps.md`. No spec changes made; judgment call to
  treat these as internal/ops, consistent with the task's own guidance to use judgment here.
- `#570`/`#571` (sandboxclaims patch/update RBAC grant): `docs/deep-dive/infra-deployment.md`
  already carries a general, still-accurate statement ("The API has narrow RBAC for creating and
  interacting with these sandbox resources") — there's no verb-by-verb RBAC table in end-user docs
  to update, and this is an internal-implementation-detail fix, not new user-facing behavior. No
  change made.
- Additional undocumented-but-real user-facing changes I found via `git log` diffing (not in the
  original task list) and added docs for: `#555`/`#556` (drag-to-connect fix in the Visual Workflow
  Editor — already implied by existing "connect them" docs wording, no separate note needed) and
  `#558`/`#559` (grouped/deduped "Add node" palette). Added a short paragraph to
  `docs/guide/workflows.md`'s "Visual editor" section describing the new grouped palette
  (Reviewers & gates / Agent steps / Flow control) since that's a real, user-visible UX change with
  no prior docs coverage.

## 3. specs/README.md index

- `#452` (per-project GitHub webhooks) and `#453` (MCP client configuration): searched `specs/` for
  any file referencing these issues or topics (webhook, MCP client config) — found none. Per the
  task's own conditional ("if they have spec files under specs/ not yet indexed"), no spec files
  exist for either, so no index entries were added. Flagging that these two features currently have
  **no product spec at all** (only guide-level docs), which the coordinator may want to task
  separately as new specs, but that's out of scope for this docs-only round.
- Added `specs/agent-execution-sandbox/build-images-with-rootless-buildkit.md` (new, design-only
  spec for issue #582, rootless BuildKit-on-Kubernetes image builds for AgentHost) and linked it
  from `specs/README.md` under "Agent execution & sandbox". Modeled on
  `isolate-agent-workspaces.md`/`preview-sandbox-apps.md` conventions but added a "Proposed design"
  and "Open questions" section since the issue explicitly asked for a design/security-review
  artifact, not a shipped-feature user story — the acceptance-criteria format didn't fit a
  not-yet-built, not-yet-approved feature. Issue #582 is linked from the spec header.

---

# Link decision: UI harness Entra login compatibility

- Date: 2026-07-30
- Author: Link

## Decision

Generalize the UI harness `login` browser boundary from a GitHub-only escape hatch to a login-only identity-provider allowlist.

## Details

- Keep automated `action()` sessions fully same-origin; they must never receive the IdP navigation flag.
- Rename the login-only flag to `allowIdentityProviderNavigation` for clarity.
- Allow these IdP origins during the human-supervised `login` flow:
  - `https://github.com`
  - `https://login.microsoftonline.com`
  - `https://login.live.com`
  - the origin derived from `/api/auth/config`'s Entra `authority`, so tenant-specific or custom authority hosts can be honored when configured.
- Expand `isAuthExpired()` so same-origin redirects to `/auth/<provider>/(authorize|callback)` are treated as auth-expired states in addition to legacy `/login|/signin|/oauth` paths.

## Rationale

Agentweaver's backend config resolves Entra authority from `Auth:Entra:Authority` or, when blank, from `https://login.microsoftonline.com/{tenantId}/v2.0`. The UI harness therefore needs to tolerate Microsoft-hosted sign-in hops once Entra becomes the entry auth, but only in the manual `login` command. Using `/api/auth/config` lets the harness honor a real configured authority if staging later moves to a custom host while keeping today's default Microsoft origins working.

---

# Link — Entra phase 2 docs and MCP surface plan

## Summary

Treat `Auth:Mode=Entra` and `Auth:Mode=GitHubLegacy` as **two supported authorization modes**, not a migration/sunset story. The Entra bootstrap command stays optional, and the MCP surface must grow in two directions: (1) mode-aware auth/account tooling and (2) project-role / linked-GitHub management.

## Decisions and concrete edits

- Updated the Entra bootstrap command/docs framing so `scripts/azure/setup-entra-app.mjs` is explicitly **optional** and only needed for deployments choosing `Auth:Mode=Entra`. Deployments staying on `Auth:Mode=GitHubLegacy` can ignore it.
- Updated the bootstrap role set to the finalized Tier 1 platform roles from `decisions.md`: `PlatformAdmin`, `ProjectCreator`, `Contributor`, `Viewer`.
- Started the dual-mode docs pass:
  - `docs/deep-dive/auth-security.md` now has a new `Auth:Mode` section describing Entra mode vs. GitHub-based authorization mode, Tier 1/Tier 2 RBAC, and the MCP/client implications.
  - `docs/guide/authentication.md` now frames sign-in and GitHub linking as mode-dependent instead of GitHub-only.
- Started the MCP surface update by exposing the already-existing backend repository discovery endpoints through MCP:
  - `github_accounts_list` → wraps `GET /api/github/accounts`
  - `github_repos_list` → wraps `GET /api/github/repos?account=...`
  These are the first building blocks for linked-identity-aware GitHub discovery.

## MCP changes still needed next

To fully support the finalized Entra design, the MCP/API surface still needs these additions once Tank's backend endpoints land:

1. **Linked GitHub identity management**
   - list linked GitHub identities for the signed-in Entra account
   - set/change default linked GitHub identity
   - unlink a GitHub identity
   - surface Copilot entitlement per linked GitHub identity
   - optional follow-up: initiate link flow explicitly as a GitHub-link action distinct from platform sign-in

2. **Project role assignment / mapping**
   - list project role assignments
   - grant/update a project role assignment
   - revoke a project role assignment
   - list effective platform/project roles for diagnostics

3. **Cross-token GitHub repository discovery**
   - list repositories across all linked GitHub tokens for the current Entra account
   - allow selecting a specific linked GitHub identity (default or explicit override) when creating/loading/connecting a project
   - support project-level GitHub identity overrides where the backend supports them

## Highest-priority docs file list (updated)

- `docs/deep-dive/auth-security.md` — technical source of truth for dual-mode auth, Tier 1/Tier 2 RBAC, GitHub linking, and MCP implications.
- `docs/guide/authentication.md` — user/operator-facing sign-in and GitHub-linking behavior by mode.
- `docs/guide/configuration.md` — document `Auth:Mode`, Entra config keys, and mode-specific GitHub behavior.
- `docs/guide/getting-started.md` — local auth setup story by mode.
- `docs/guide/deployment-aks.md` — optional Entra bootstrap command plus mode-specific deploy wiring.
- `docs/guide/architecture-aks.md` — auth/config/secret flow updates.
- `docs/reference/mcp.md`, `docs/reference/mcp-tools.md`, `docs/deep-dive/mcp-server.md`, `apps/Agentweaver.Mcp/README.md` — MCP auth/account/repo tool surface.
- `README.md` and `CONTRIBUTING.md` — stop describing GitHub-only login as the single canonical path.

## Validation

- Regenerated MCP generated docs with `node scripts/gen-docs.mjs`.
- Added MCP tool tests for `github_accounts_list` and `github_repos_list`.
- Retained the Entra bootstrap Node tests for the updated role set and optional-mode framing.

---

## 2026-07-27T02-54-31: Filed spike issue #553 to evaluate agent-substrate on AKS with Kata Containers (not gVisor)
**By:** Link
**What:** Filed spike issue #553 to evaluate agent-substrate on AKS with Kata Containers (not gVisor)
**References:** issue-553, issue-542, issue-487, issue-471, agent-substrate, kubernetes-sigs/agent-sandbox
**Why:** Filed GitHub spike issue sabbour/agentweaver#553 ("Spike: Evaluate agent-substrate on AKS with Kata Containers as sandbox execution runtime"). Labels: type:spike, go:needs-research, area:runtime-resilience, squad:link.

Judgment calls recorded:
1. No prior local agent-substrate research existed. Swept .squad/decisions.md, .squad/decisions/inbox/, .squad/orchestration-log/, and .squad/agents/*/history.md — zero substrate references. So the issue is grounded in a FRESH assessment of the substrate repo + current sandbox code/docs, and the issue states this explicitly (with instruction to link/reconcile if an earlier assessment later surfaces).

2. Central technical tension surfaced as the crux of the spike: substrate's headline suspend/resume + snapshot multiplexing is implemented via gVisor (README: "at the kernel level (via gVisor)"; interior helper cmd/ateom-gvisor runs `runsc` checkpoint/restore). The task mandates Kata Containers and forbids gVisor. Framed the first open question as "can substrate run workers under a Kata RuntimeClass at all, and what capability (snapshot/teleport) is lost if so" — gating all performance questions.

3. Current-state baseline documented accurately: Agentweaver ALREADY uses Kata (runtimeClassName: kata-vm-isolation on dedicated katapool), on top of kubernetes-sigs/agent-sandbox controller v0.5.3 (CRD group extensions.agents.x-k8s.io, v1beta1). Custom code that substrate might offload: KubernetesSandboxExecutor (claim lifecycle), AgentHostReaperService (quota-reclaim orphan reaper), SandboxWarmPool (cold-start hiding), and preview keepalive (#542). No gVisor anywhere in the cluster path today.

4. Scope bounded to a time-boxed PoC producing a findings write-up + go/no-go recommendation. Out of scope: any production migration, removing the existing KubernetesSandboxExecutor/agent-sandbox path, and introducing gVisor anywhere.

No code changed, no PR, no git branch/notes touched — issue-filing only.

---

## 2026-07-27T08-04-10: Found & fixed: dev was never synced after v0.11.6 release, causing v0.12.0 release branch to conflict with main
**By:** Link
**What:** Found & fixed: dev was never synced after v0.11.6 release, causing v0.12.0 release branch to conflict with main
**References:** PR #554, PR #565, PR #566, RELEASING.md
**Why:** While preparing release/v0.12.0 (PR #565) from dev, `gh pr view 565` showed `mergeable: CONFLICTING`, `mergeStateStatus: DIRTY` against main, with an unexpectedly huge diff (64306 additions / 11588 deletions). Root cause: `dev`'s VERSION was still 0.11.5 and still carried ~6 changeset fragments (fix-539, fix-540, fix-541, fix-542, fix-546, fix-runcard-task-text-truncation) that had ALREADY been released as v0.11.6 to main via PR #554 (`chore(release): promote v0.11.6 to main`) — the `release:sync-dev` forward-port step (RELEASING.md, "Before deleting the release branch...") was apparently skipped after that release, so dev was never brought back in sync with main's v0.11.6.

Fix applied: created `chore/sync-dev-v0.11.6` from dev, ran `npm run release:sync-dev -- 83a1f63e` (83a1f63e = "chore(release): prepare v0.11.6" commit on main), which cherry-picked cleanly (10 files, version mirrors now synchronized at 0.11.6). Opened PR #566 (dev-target) to land this before re-attempting the v0.12.0 release branch. Plan: merge #566 to dev first, then rebase/recreate release/v0.12.0 on the corrected dev so PR #565 no longer conflicts with main.

This is a process gap worth Squad awareness: after every `azure:release`/`release:publish` + main-promotion, someone must remember to also run `release:sync-dev` before the release branch is deleted, or the next release's `release:prepare` will silently re-bundle already-shipped changesets and produce a huge, conflicting diff against main.

---

## 2026-07-27T08-27-09: Found repo bug: release:publish's ignored-file allowlist is stale (missing node_modules/bin/obj/harness dirs) vs shared.mjs; worked around via pristine clone, did not patch
**By:** Link
**What:** Found repo bug: release:publish's ignored-file allowlist is stale (missing node_modules/bin/obj/harness dirs) vs shared.mjs; worked around via pristine clone, did not patch
**References:** scripts/azure/release-publish.mjs:65-108, scripts/changesets/shared.mjs:143-183, RELEASING.md
**Why:** `npm run release:publish` (scripts/azure/release-publish.mjs) failed with "Working tree has uncommitted changes. Commit or stash first." on a clean checkout at the exact `origin/main` SHA (9a45c1d3, v0.12.0) with zero tracked-file diffs and zero unexpected untracked files — the only "dirty" items were ordinary ignored build artifacts (`node_modules/`, `apps/**/bin/`, `apps/**/obj/`, `scripts/api-harness/node_modules/`, `.squad/log/`, `.squad/orchestration-log/`, `.worktrees/`, a harness findings JSON).

Root cause: `release-publish.mjs` has its OWN local copy of `getUnexpectedIgnoredFiles()` (duplicated from `scripts/changesets/shared.mjs`) whose allowlist is stale — it only permits `.squad/`, `.idea/`, `.vscode/`, `.vs/`, `.security/`, `.worktrees/`, `.env`, a few azure-script scratch paths, and file extensions like `.user/.suo/.userprefs`. It is MISSING the `node_modules/`, `dist/`, `bin/`, `obj/`, and `scripts/(api|mcp|ui)-harness/(findings|...)/ ` patterns that `shared.mjs`'s copy already has (with an explicit code comment explaining exactly why those must be allowed — "node_modules/, dist/, bin/, obj/ and test/harness output always exist ... The meaningful protection is catching ignored files in UNEXPECTED locations"). Net effect: `release:publish` cannot succeed from ANY real dev checkout that has ever run `npm install`/`dotnet build` — only from a pristine clone with zero build output.

Workaround used (no script changes made, to stay in scope): cloned a pristine copy of `origin/main` into a sibling directory (`agentweaver-publish-clean`, no `npm ci`/`dotnet build` ever run there) and invoked `node scripts/azure/cli.mjs publish-release` directly there — script only imports Node builtins + local `.mjs` files, no `node_modules` needed to execute. Succeeded cleanly: tag `v0.12.0` pushed, GitHub Release created (https://github.com/sabbour/agentweaver/releases/tag/v0.12.0).

Recommended real fix (not applied this session, out of scope for a release task): dedupe `release-publish.mjs`'s `getUnexpectedIgnoredFiles` to import the shared, already-correct implementation from `scripts/changesets/shared.mjs` instead of maintaining a second, drifted copy. File an issue/PR for this.

---

## 2026-07-29T09-26-39: Let the API remain the single validator for MCP GitHub project creation
**By:** Link
**What:** Let the API remain the single validator for MCP GitHub project creation
**References:** apps/Agentweaver.Mcp/Tools/ProjectTools.cs, apps/Agentweaver.Api/Contracts/Dtos.cs, apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs
**Why:** For the `project_create` MCP fix, I added an optional `source_repository` tool parameter and forward it as `source_repository` to match the API contract defined by `CreateProjectRequest` (`[JsonPropertyName("source_repository")]`). I intentionally did not add duplicate client-side validation inside `ProjectCreateAsync`; the API already enforces that `source_repository` is required when `origin == "github"`, and existing MCP error propagation surfaces that 400 clearly. This keeps one validation source of truth while still documenting the requirement in the MCP tool description/schema so assistants know to supply it up front.

---

# Phase 0 continuity + DOM cue + schema implementation

**Author:** link  
**Branch:** `link/demo-creative-direction`  
**Isolated worktree:** `.worktrees/demo-creative-direction`  
**Status:** implemented and tested; take analyzer intentionally not implemented

## Scope completed

1. Cherry-picked `cc22fbdc` (`fix: preserve demo beat continuity`) onto current `origin/dev` as `b906416a`.
2. Moved the reusable creative-direction skill into the isolated worktree and removed only its stray untracked copy from the shared dirty checkout.
3. Implemented DOM-only semantic cue collection. There is no SSE, coordinator-event, run-event, or topology-state subscription.
4. Added the capture-plan and cue-manifest schemas plus a markdown/capture-plan joining loader.
5. Added targeted tests and ran the existing demo-recording test suite.

## Implementation

### Blocking semantic cues

`scripts/demo-recording/lib/capture-plan.mjs` now allows `waitFor` and `waitText` steps to include an optional `cue` object. After the existing blocking wait resolves, the generated Playwright program:

- preserves the existing generic activity mark for idle trimming;
- emits the named semantic cue;
- captures source evidence, URL, viewport, DPR, CSS-pixel rectangle, and normalized rectangle;
- sends the observation through a Playwright exposed binding to a Node-side take log.

The binding is installed once per Page and routes to the current capture sink, so it survives full-page and cross-origin navigation. Node-side name deduplication preserves first-occurrence semantics across documents.

### Passive DOM watcher

New `scripts/demo-recording/lib/dom-cues.mjs` installs one page-local `MutationObserver` through the existing bootstrap path. It:

- evaluates declared watchers immediately when armed;
- observes child, text, and declared attribute changes;
- coalesces mutation bursts;
- fires each cue once;
- supports `stableForMs` and `deadlineMs`;
- re-arms after explicit `goto` operations;
- never executes arbitrary JavaScript predicates from JSON.

Supported source kinds are only:

- `selector`
- `attribute`
- `text`
- `predicate`

Supported predicate operators are `exists`, `count-gte`, `count-eq`, `any-attribute-in`, `all-attribute-in`, `text-includes`, and `text-matches`.

Rectangle modes are `matched-element`, `element`, `first-matching`, `union`, and `none`. Detached, hidden, or zero-sized targets produce an explicit `missing-or-not-visible` result rather than invented coordinates.

### Schemas and loader

Added:

- `scripts/demo-recording/schemas/capture.schema.json`
- `scripts/demo-recording/schemas/capture-cues.schema.json`
- `scripts/demo-recording/lib/capture-config.mjs`

The joining loader keeps beat markdown authoritative and joins `<scenario>.capture.json` definitions by exact beat ID. It rejects unknown beat IDs, optionally rejects missing definitions, rejects duplicate beat/cue names, validates rectangle and declarative predicate definitions, and explicitly rejects backend-coupled source kinds such as `run-event` and `topology-state`.

The generated cue manifest is capture-relative, sequence-stable, sorted, and suitable as the immutable observation layer before any director cut.

## Markup prerequisites still required

The generic mechanism is implemented, but reliable Scenario 3 topology and trace plans still require stable rendered attributes in `apps/web`; this branch deliberately does not add unrelated frontend markup.

Topology currently exposes node cards primarily through `role="article"` and a composed accessible label. A robust plan needs stable graph/node identity and raw status attributes, such as:

- `data-testid="coordinator-topology-graph"`
- `data-testid="topology-node"`
- `data-node-id`
- `data-node-kind`
- `data-node-status`

Trace currently lacks stable root/tree/span/selection attributes. A robust plan needs equivalents such as:

- `data-testid="transaction-trace-panel"`
- `data-testid="trace-tree"`
- `data-testid="trace-span"`
- `data-span-key`
- `data-span-type`
- `data-selected` and/or `aria-pressed`

Until those exist, capture plans must use weaker accessible text/role selectors and should treat the relevant cues as fragile.

## Verification

From `scripts/demo-recording`:

```text
npm test
26 tests passed, 0 failed
```

Both schema files also parsed successfully as JSON. Tests cover continuity, exact beat joining, missing/unknown IDs, duplicate cue names, rejection of backend source kinds, blocking cue generation, passive watcher generation, navigation re-arming, normalized rectangles, valid browser bootstrap syntax, and cue-manifest ordering.

## Proposed take-analyzer design — review required before implementation

The take analyzer should be a read-only deterministic analysis stage. It must not render video, mutate cue observations, query backend state, or make final creative decisions.

### Inputs

- raw video path and `ffprobe` stream/frame metadata;
- joined markdown + `<scenario>.capture.json` plan;
- generated `capture-cues.json`;
- existing generic activity log;
- optional pointer/click observation tracks.

### Analysis

1. Validate required/optional cue presence, uniqueness, order constraints, rectangle validity, viewport/DPR consistency, and clock monotonicity.
2. Extract actual frame PTS and map capture-relative cue timestamps to nearest source frames; retain mapping error for diagnostics.
3. Build source intervals between semantic cues and classify them as causal action, readable proof, variable wait, navigation, or static gap from declared capture intent plus activity density.
4. Compare measured intervals with soft preferred/minimum/maximum output budgets. Calculate budget pressure and the ratio that would be required to fit each interval.
5. Suggest candidate treatments without finalizing them:
   - preserve causal boundaries and readable reveals at 1×;
   - select activity windows around meaningful cue clusters;
   - suggest 2×/4× ramps for repetitive middles;
   - prefer hard cuts over ratios above the configured review threshold;
   - flag shots lacking a trustworthy camera rectangle.
6. Produce warnings for missing cues, ambiguous ordering, large browser-to-frame timing error, unstable/invalid rectangles, source discontinuities, and impossible budget constraints.

### Proposed output

`recordings/raw/<scenario>/<take-id>/take-analysis.json` should contain:

- source/take hashes and schema versions;
- frame timeline summary;
- cue-to-frame mappings and errors;
- measured semantic intervals;
- activity/static-gap windows;
- budget-pressure calculations;
- treatment suggestions with reasons and confidence;
- validation errors/warnings.

This output becomes evidence for a human-authored or separately generated `<scenario>.direction.json`. Speed segments and camera keyframes remain separate; the analyzer must not silently turn suggestions into an approved cut.

## Recommended next review

Before implementing the analyzer, approve or revise:

1. the interval classification vocabulary;
2. required-cue/order policy;
3. source-to-frame timing tolerance;
4. thresholds for continuous acceleration versus activity-window cuts;
5. whether analyzer suggestions may be auto-seeded into a draft direction file or remain analysis-only.


---

## 2026-07-30 revision — frontend selector contract and take analyzer implemented

Sabbour approved the analyzer policy and authorized implementation on `link/demo-creative-direction`.

### Approved analyzer policy

1. **Interval vocabulary:** only `action`, `wait`, and `dead-time`.
   - Prior **causal action** maps to `action`.
   - Prior **readable proof** maps to `action` when it is a short cue-bounded hold, because the compositor must preserve its legibility at 1×.
   - Prior **navigation** maps to `action` when the activity log contains a route or interaction mark, because the causal path should remain understandable rather than being treated as an arbitrary delay.
   - Prior **variable wait** maps to `wait`; passive DOM/mutation activity may be accelerated while retaining visible progress.
   - Prior **static gap** maps to `dead-time`; it is suggested for removal.
   Capture plans may explicitly override an interval when product semantics are clearer than activity heuristics.
2. **Cue validation is lenient:** missing or out-of-order expected cues produce warnings and never reject the take. Analysis continues with all usable evidence.
3. **Cue/frame sync tolerance:** the nearest `ffprobe` video frame must be within 500ms of the DOM cue timestamp; larger drift is a sync warning.
4. **Acceleration threshold:** waits needing at most 12× may use continuous speed ramps. Above 12×, the analyzer suggests activity-window selection plus hard cuts.
5. **Draft seeding:** suggestions may produce a direction JSON only with `status: "draft-suggestion"`, `approved: false`, and `reviewRequired: true`. It cannot silently drive the compositor as approved direction.

### Stable frontend capture markup

Minimal additive attributes were added without restructuring components:

- `CoordinatorTopologyGraph.tsx`
  - graph: `data-testid="coordinator-topology-graph"`
  - nodes: `data-testid="topology-node"`, `data-node-id`, `data-node-kind`, `data-node-status`
- `TransactionTracePanel.tsx`
  - panel: `data-testid="transaction-trace-panel"`
  - tree: `data-testid="trace-tree"`
  - rows: `data-testid="trace-span"`, `data-span-key`, `data-span-type`, `data-selected`, `aria-pressed`

These values expose the raw stable identities and states already rendered by the components; no backend coupling was introduced.

### Take analyzer implementation

Added `scripts/demo-recording/lib/take-analyzer.mjs` and CLI command `analyze-take`. The analyzer is read-only with respect to the backend and rendering. It:

- runs `ffprobe -show_frames` for actual video frame PTS;
- normalizes non-zero source PTS to the take clock;
- maps each DOM cue to its nearest video frame and records drift;
- validates expected cue presence/order and rectangles leniently;
- constructs cue-bounded source intervals;
- classifies intervals into the approved three categories;
- measures source duration and preferred/minimum/maximum budget pressure;
- keeps action/proof at 1×;
- suggests speed ramps for waits up to 12×;
- suggests candidate activity windows and hard cuts when more than 12× would be required;
- suggests removal of dead-time;
- writes `take-analysis.json` with input hashes and warnings;
- optionally writes a review-required draft direction file.

Command:

```text
node scripts/demo-recording/cli.mjs analyze-take \
  --video <raw.webm> \
  --capture-plan <scenario.capture.json> \
  --cues <capture-cues.json> \
  --activity-log <activity.json> \
  --beat-id <beat-id> \
  --out <take-analysis.json> \
  --draft-direction <scenario.direction.draft.json>
```

Added `take-analysis.schema.json`; extended `capture.schema.json` with output budgets, expected cue/order declarations, and optional interval-category overrides.

### Validation

- Demo-recording: 30 tests passed, 0 failed.
- New frontend markup test: 2 tests passed, 0 failed.
- Web lint: passed.
- Web production build: passed.
- Full web suite: 921 passed, 2 failed in `SkillsPage.test.tsx`. Re-running that unrelated file alone produced a different existing asynchronous marketplace-source failure (34 passed, 1 failed), confirming the failures are outside the topology/trace changes and are timing/flakiness in pre-existing SkillsPage coverage.

The analyzer and draft output remain evidence/suggestions only. No compositor or backend behavior was added.

---

## 2026-07-31T00-02-37: Publish-apps analysis: reuse ACR + preview Gateway, one shared published namespace in phase 1, PublishedApp CRD deferred, ASO not a default dependency
**By:** Link
**What:** Publish-apps analysis: reuse ACR + preview Gateway, one shared published namespace in phase 1, PublishedApp CRD deferred, ASO not a default dependency
**References:** issue #582, issue #21, issue #20, issue #37, k8s/base/gateway-preview.yaml, k8s/base/rbac-api.yaml, scripts/azure/image-spec.mjs, scripts/azure/steps/10-create-cluster.mjs, apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs
**Why:** Analysis-only (no spec, no code) for Ahmed's "publish apps from Agentweaver" request. Grounded in verified repo state:

Verified facts
- Images today are built with `az acr build` against an ACR that already exists per environment (scripts/azure/image-spec.mjs, scripts/azure/steps/20-build-push-images.mjs, scripts/azure/variables.mjs ACR_NAME=agentweaverregistry). ACR is attached to AKS at create time via `az aks create --attach-acr` (scripts/azure/steps/10-create-cluster.mjs), so kubelet already has AcrPull on the whole registry.
- Ephemeral previews already do dynamic Service + HTTPRoute creation against a wildcard `*.{zone}` Gateway with the zone wildcard TLS secret `agentweaver-tls` (k8s/base/gateway-preview.yaml, apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs). Preview lifetime caps: IdleTimeoutMinutes=30, MaxLifetimeHours=8 (SandboxPreviewOptions.cs).
- API RBAC already has create/delete on services + httproutes, namespace-scoped to `agentweaver` (k8s/base/rbac-api.yaml). Namespace is `pod-security.kubernetes.io/enforce: baseline` and default-deny ingress/egress (k8s/base/namespace.yaml, networkpolicy-default-deny.yaml).
- Workload identity + federated credentials are provisioned imperatively by `az` today (scripts/azure/steps/15-setup-identity.mjs); AgentHost has a deliberately Key-Vault-less dedicated identity (#471).
- #582 (specs/agent-execution-sandbox/build-images-with-rootless-buildkit.md) is design-only and already assumes rootless BuildKit in a dedicated `buildkit` namespace with `buildx --push`.

Decisions recorded for review
1. Publish topology: one shared `agentweaver-published` namespace in phase 1, with per-app labels + a per-project ResourceQuota-bearing namespace only when multi-tenancy demands it. Rationale: namespace-per-app multiplies quota/NetworkPolicy/Gateway ReferenceGrant objects and hits the existing single-namespace RBAC and `allowedRoutes.from: Same` assumptions.
2. Hostname: reuse the existing `*.{zone}` wildcard cert but a NEW Gateway (`agentweaver-published-gateway`) with a stable single-label host `{slug}-app.{zone}` — mirroring the `{token}-preview.{zone}` scheme forced by the AKS DefaultDomainCertificate nested-wildcard limitation confirmed in k8s/base/gateway-preview.yaml. Custom domains are a later phase.
3. Registry: do NOT add an in-cluster registry or OCI-in-blob. Push published-app images to the existing ACR under a reserved `published/{projectId}/{appSlug}` repository prefix, digest-pinned in the Deployment. Kubelet AcrPull already covers it.
4. ASO is NOT adopted as a default dependency. The Azure resources publishing needs (ACR repo, UAMI, federated credential) are already created by the imperative Node toolchain, and the ASO controller's own Azure identity would carry a far larger blast radius than any resource it manages here. If declarative provisioning is wanted later, put it behind a provider abstraction (IPublishRegistryProvider / IPublishIdentityProvider) so the AKS-on-Azure path stays the default and ASO is an optional operator-installed backend.
5. No `PublishedApp` CRD in phase 1. Agentweaver's API already owns dynamic K8s objects (Service + HTTPRoute) with annotation-based, replica-safe state; extend that pattern with plain Deployment/Service/HTTPRoute plus a reaper. Revisit a CRD only when GitOps/external reconciliation or non-Agentweaver-managed clusters become a requirement.

Conflicts flagged
- None hard against .squad/decisions.md. Soft tension: a published-app path that pushes to ACR from inside the cluster diverges from the "all images are built by `az acr build` from the deploy toolchain" model in image-spec.mjs, and must be namespaced (`published/*` prefix) so it can never collide with or retag the 4 product images or their `prov-<sha>` provenance tags (20-build-push-images.mjs).

---

## 2026-07-27T08-13-45: Release branch -> main PRs need an explicit `git merge origin/main -X ours` reconcile step before they'll show as mergeable/clean
**By:** Link
**What:** Release branch -> main PRs need an explicit `git merge origin/main -X ours` reconcile step before they'll show as mergeable/clean
**References:** PR #567, commit 3897d19a, RELEASING.md
**Why:** After fixing dev's sync (#566), the recreated release/v0.12.0 branch (PR #567) still showed `mergeable: CONFLICTING` against main with a huge diff. Root cause (different from the dev-sync issue): `main` and `dev` have structurally divergent histories in this repo — `main` only ever receives squash/reconcile-style "promote vX.Y.Z" commits, never the individual dev commits, so a plain 3-way merge of a dev-based release branch into main produces spurious content conflicts in every file both branches touched (even though dev's content is authoritative), e.g. GitHubWebhookEndpoints.cs, AgentHostReaperService.cs, KubernetesSandboxExecutor.cs, test files, docs.

Confirmed via `git log dev..origin/main` that main has ZERO unique source commits beyond its own past "chore(release): promote/prepare vX" commits — no independent hotfixes. Found precedent: commit 3897d19a "chore(release): reconcile main into release/v0.11.6 (main content superseded by dev)" — a merge of main into the release branch using `-X ours` (favoring release/dev content), done before promoting v0.11.6.

Applied the same pattern here: `git merge origin/main -X ours -m "chore(release): reconcile main into release/v0.12.0 (main content superseded by dev)"` on release/v0.12.0. Merged cleanly (ort strategy, no conflicts since main has no unique content), version mirrors stayed synchronized at 0.12.0, and the PR #567 diff against main dropped from ~64k lines to 646 insertions/41 deletions and became MERGEABLE.

Takeaway for future releases: after creating a release branch from dev and running `release:prepare`, ALWAYS also merge `origin/main` into the release branch with `-X ours` (mirroring commit 3897d19a) before opening/updating the PR to main, or the PR will show spurious conflicts even when dev and main are otherwise compatible.

---

## 2026-07-27T07-55-40: Release version corrected: v0.12.0 (minor), not v0.11.7 (patch), due to #563's minor changeset
**By:** Link
**What:** Release version corrected: v0.12.0 (minor), not v0.11.7 (patch), due to #563's minor changeset
**References:** #560, #563, #564, RELEASING.md
**Why:** Task brief assumed the next patch release would land as v0.11.7. Ran `npm run changeset:status` and `npm run release:plan` against dev (current VERSION 0.11.5) and confirmed the pending changeset set includes `feat-assistant-run-suggested-prompts.md` (PR #563), which is tagged `minor` per repo convention (features get `minor` even at 0.x). Changesets computes the release bump as the max across all pending changesets, so the presence of one `minor` changeset forces the whole release to bump minor, not patch — regardless of the other ~10 pending changesets being `patch` (including `fix-560-preview-claim-ttl-renewal.md` for the critical #564/#560 sandbox-TTL fix).

`release:plan` output: `Planned release: 0.11.5 -> 0.12.0 (minor)`, including all 11 currently pending changesets (not just #563/#564 — there are ~9 other already-merged-to-dev changesets riding along, e.g. fix-539, fix-540, fix-541, fix-546, fix-555, fix-558, fix-runcard-task-text-truncation, fix-webhook-sourcerepo-match).

Decision: proceeding with v0.12.0 as the correct next version per RELEASING.md's documented versioning rules, not forcing an artificial v0.11.7. This is the correct behavior of the release tooling — no override applied.

---

## 2026-07-30T21-36-45: Resolved locked demo scripts merge by preserving oracle narrative and retaining DOM metadata
**By:** Link
**What:** Resolved locked demo scripts merge by preserving oracle narrative and retaining DOM metadata
**References:** oracle/demo-scenario-scripts, scripts/demo-recording/plans/blueprint-demo-beats.md, scripts/demo-recording/plans/azure-aks-demo-beats.md, scripts/demo-recording/plans/sizzle-reel-beats.md
**Why:** While landing branch oracle/demo-scenario-scripts onto dev, I treated the oracle branch's demo beat narrative as authoritative and re-applied dev's DOM-capture pipeline metadata only as metadata. The doc-header Fresh navigation/Start URL description was restored in the two existing plan files. Fresh navigation markers from dev were attached to matching oracle beats: blueprint 1.1, 2.1, 3.1, 4.1, 5.1; Azure/AKS 1.1, 2.2 for the folded issue-triage setup, 2.3 for writing-skill import, 3.1 for run-triage-now, and 5.1. Removed/stale narrative beats from dev were not resurrected.

---

## 2026-07-27T03-17-33: Resolved release/v0.11.6→main conflicts via `merge -s ours` after confirming dev is a strict content-superset of linear-squashed main
**By:** Link
**What:** Resolved release/v0.11.6→main conflicts via `merge -s ours` after confirming dev is a strict content-superset of linear-squashed main
**References:** PR #554, release/v0.11.6, issue #542, RELEASING.md
**Why:** The release/v0.11.6 → main PR (#554) came up mergeStateStatus=DIRTY / CONFLICTING with 11 conflicting files including real source (Program.cs, SandboxExecutorRouter.cs, KubernetesSandboxExecutor.cs, ui-harness browser.mjs, several test files) plus VERSION/CHANGELOG/package.json/package-lock.json.

Investigation:
- `main` is NOT a merge-based branch: `promote v0.11.5` (15a01ca4) has a SINGLE parent (promote v0.11.4). main is a linear chain of *squash-merged* release commits; release PRs are squash-merged onto main.
- Because promotes are squashes (not 2-parent merges) and dev is only ever forward-fixed with `sync vX.Y.Z metadata to dev` cherry-picks (never a merge of main), main never becomes an ancestor of dev. merge-base(main,dev)=1ce521ac (ancient) and drifts further every release, so overlapping-file textual conflicts are expected and grow over time.
- Every commit on main-not-in-dev is a release commit (promote/prepare v0.11.0–v0.11.5); there are NO emergency hotfixes carrying unique content. `git diff origin/dev origin/main` is almost entirely deletions — every differing hunk shows main holding the OLDER version of code that dev has since fixed (e.g. the #541 IPreviewCommandModel registration, #538 OAuth-origin change, #542 preview-service wiring). Conclusion: dev@a1170050 is a strict content-superset of main.

Decision: reconcile with `git merge -s ours origin/main` on the release branch. This records main as a parent (so the PR merges cleanly / main becomes an ancestor) while keeping the release tree byte-for-byte equal to the prepared dev+0.11.6 state. Taking `-s ours` discards nothing of value because dev already supersedes all of main's content. Verified the branch tree is unchanged after the merge (identical to commit 83a1f63e). This matches main's established large-squash promote pattern (promote v0.11.5 was itself a large squash diff). Pushed once; then leave the branch untouched while CI runs (ci.yml concurrency group cancels in-flight CI on re-push).

Follow-up worth filing: the recurring main/dev divergence means release PRs will keep conflicting. Consider periodically merging main into dev (or making promotes real merges) so merge-base advances and release PRs stay conflict-free.

---

## 2026-07-27T03-55-59: Resolved staging env identity for v0.11.6 deploy (RG asabbour / cluster agwv / ACR agwvacr / vault agwvkv), picking agwvkv by reading live SecretProviderClass wiring not guessing
**By:** Link
**What:** Resolved staging env identity for v0.11.6 deploy (RG asabbour / cluster agwv / ACR agwvacr / vault agwvkv), picking agwvkv by reading live SecretProviderClass wiring not guessing
**References:** v0.11.6, issue #521, issue #542, RESOURCE_GROUP=asabbour, KEYVAULT_NAME=agwvkv
**Why:** `azure:deploy-from-release -- v0.11.6` failed fast with "KEYVAULT_NAME is not set" (the #521 guard that refuses to guess a vault to avoid silently redirecting GitHub OAuth secret lookups at the wrong vault). No params file exists in the repo/worktrees, and variables.mjs DEFAULTS (agentweaver-rg / agentweaver-aks / agentweaverregistry) do NOT match the real staging environment, so all resource identifiers must be supplied explicitly.

Discovered the real staging environment by enumerating the logged-in subscription ("AKS INT/Staging Test", 26fe00f8-...):
- RESOURCE_GROUP=asabbour
- CLUSTER_NAME=agwv (node RG MC_asabbour_agwv_westus2)
- ACR_NAME=agwvacr
- NAMESPACE=agentweaver
- KEYVAULT_NAME=agwvkv

KEYVAULT selection was ambiguous: RG `asabbour` contains TWO vaults, `agwvkv` and `akwvkv`. To avoid the exact wrong-but-plausible-vault trap #521 warns about, I did NOT guess — I read the LIVE cluster wiring: `kubectl get secretproviderclass -n agentweaver` shows both `agentweaver-secrets` and `agentweaver-user-tokens` reference keyvaultName=`agwvkv`. So agwvkv is authoritative; akwvkv is unrelated. HOST/DOMAIN are auto-derived by the deploy from the cluster DefaultDomainCertificate, and GitHub OAuth is vault-backed via the CSI SecretProviderClass, so GITHUB_CLIENT_ID/SECRET are NOT required for an existing-environment release deploy.

Decision: run the deploy with those five identifiers supplied as environment variables for the single invocation (not committing a params file). Recording the environment identity here so future deploys/verifies use the same values instead of re-deriving.

Follow-up worth filing: consider committing a documented, secret-free `scripts/azure/params.staging.json` (git-ignored is allowed by the guard) or documenting these identifiers in RELEASING.md so each release cut doesn't have to rediscover RG/cluster/ACR/vault from az.

---

## 2026-07-27T03-10-27: v0.11.6 release prep: manually synced package-lock version mirror after changeset version left it stale
**By:** Link
**What:** v0.11.6 release prep: manually synced package-lock version mirror after changeset version left it stale
**References:** release/v0.11.6, issue #542, RELEASING.md, scripts/changesets/prepare-release.mjs
**Why:** While preparing release/v0.11.6, `npm run release:prepare -- --expected 0.11.6` failed at its final `assertVersionMirrors` guard with: "Version mirrors disagree: VERSION=0.11.6, package.json=0.11.6, package-lock.json=0.11.5". The wrapped `@changesets/cli version` step bumped VERSION, package.json and CHANGELOG.md and consumed all 6 changesets, but did NOT update package-lock.json's `version` / `packages[""].version` mirrors (the prepare-release.mjs comment "Changesets owns package and lockfile updates" assumes it does). Prior prepare commits (e.g. #503 v0.11.1) show both lock fields bumping, so the expected end-state is the lock mirroring the new version.

Decision: rather than re-run prepare (which would fail again the same way and the changesets are already consumed), I synced the lockfile deterministically with `npm install --package-lock-only --ignore-scripts`. This produced exactly the expected 2-line diff (top-level `version` and `packages[""].version` 0.11.5 -> 0.11.6) and no dependency-graph changes. `npm run version:check` then reported "Version mirrors are synchronized at 0.11.6." Committed as `chore(release): prepare v0.11.6` (83a1f63e).

Follow-up worth filing: prepare-release.mjs / the changesets config should reliably update the lockfile mirror (or the wrapper should run `npm install --package-lock-only` itself) so release:prepare doesn't fail on a clean run and require manual intervention.

---

## 2026-07-27T10-37-36: v0.12.1 published but staging deploy BLOCKED — no azure environment reachable from this session
**By:** Link
**What:** v0.12.1 published but staging deploy BLOCKED — no azure environment reachable from this session
**References:** #570, #571, #572, v0.12.1, issue #569
**Why:** Cut and published release v0.12.1 (patch): release/v0.12.1 branch created from origin/dev @ ab5133b5, `release:prepare --expected 0.12.1` run (had to run `npm install --package-lock-only` first — package-lock.json version mirror lagged behind VERSION/package.json after the changeset bump, same class of gotcha as v0.12.0). PR #572 (release/v0.12.1 -> main) hit the expected spurious conflict on promotion; reconciled with `git merge origin/main -X ours` per precedent (3897d19a, v0.12.0). CI's ".NET tests" job was cancelled by an external "runner shutdown signal" at ~5m (not a real failure — no superseding run existed); `gh run rerun --failed` passed clean on retry. Squash-merged PR #572 into main at fa7fac138b202acc0909dedcd8f6ad341ffef6f5. Published via `release:publish` from a pristine clone (`git clone` + `npm ci`) — hit issue #569 again: `release:publish`'s and `deploy-from-release`'s shared `isWorkingTreeClean` check treats a plain `node_modules/` as an unexpected ignored path (not in the `getUnexpectedIgnoredFiles` allowlist), so `npm ci` before publish/deploy must be undone (`Remove-Item -Recurse -Force node_modules`) immediately before running `release:publish` / `azure:deploy-from-release`, then `npm ci` re-run afterward only if further npm-dependent steps are needed. Tag v0.12.1 and GitHub Release created: https://github.com/sabbour/agentweaver/releases/tag/v0.12.1.

BLOCKER: `azure:deploy-from-release -- v0.12.1` failed immediately — `KEYVAULT_NAME is not set and there is no default`. Investigated: the default `RESOURCE_GROUP` (`agentweaver-rg`, scripts/azure/variables.mjs:45) does not exist in the currently `az login`-authenticated account (checked directly via `az group show --name agentweaver-rg` → ResourceGroupNotFound, and confirmed via `az graph query` across ALL subscriptions visible to this identity — zero matches for a resource group named `agentweaver-rg` anywhere). No `.env.local`, no `scripts/azure/params.*.json`, no `.azure/` folder exists on disk in any checkout to supply real values — only the `.example` templates are present. This means the actual staging Azure infrastructure for Agentweaver is not reachable from this session/identity, so steps 7 (azure:deploy-from-release), 8 (azure:verify), and 9 (kubectl RBAC live-cluster confirmation) CANNOT be performed here. This is an environment/access gap, not a code or release-process defect — do not assume or report deployment success. A human (or an agent session with the correct `KEYVAULT_NAME`/`az login` context for the actual agentweaver staging subscription) must run `npm run azure:deploy-from-release -- v0.12.1` and `npm run azure:verify`, then confirm live via `kubectl get role agentweaver-api-sandbox -n agentweaver -o yaml` that `sandboxclaims` now grants `patch`/`update`.

---

## 2026-07-27T13-38-14: v0.12.2 release:prepare lockfile-sync workaround (repeat of v0.12.0/v0.12.1 gotcha)
**By:** Link
**What:** v0.12.2 release:prepare lockfile-sync workaround (repeat of v0.12.0/v0.12.1 gotcha)
**References:** #574, release/v0.12.2, scripts/changesets/prepare-release.mjs
**Why:** `npm run release:prepare -- --expected 0.12.2` fails: `scripts/changesets/prepare-release.mjs` requires a clean tree on entry, then runs `npx changeset version` (bumps package.json/CHANGELOG.md only) followed immediately by `assertVersionMirrors`, which also checks package-lock.json's `version` field — but nothing in the changesets pipeline updates the lockfile, so the second mirror check always fails with `package-lock.json=<old>` vs `package.json=<new>`.

Workaround applied (same as v0.12.0/v0.12.1): on the `release/v0.12.2` branch, ran the three prepare-release.mjs steps by hand instead of via the npm script:
1. `npx changeset version` (consumes `.changeset/fix-574-preview-pod-safe-to-evict.md`, bumps package.json + CHANGELOG.md)
2. `npm install --package-lock-only` (syncs package-lock.json's version field — the missing step)
3. Manually wrote `VERSION` to `0.12.2\n`

Then verified all four mirrors (VERSION, package.json, package-lock.json, CHANGELOG.md section) agreed before committing `chore(release): prepare v0.12.2`.

Recommend filing a follow-up issue to patch `prepare-release.mjs` to run `npm install --package-lock-only` itself between the changeset-version step and the second `assertVersionMirrors` call, so this manual workaround isn't needed on every release.

---

## 2026-07-29T03-44-48: Widen ACR provenance-tag polling to tolerate eventual-consistency lag
**By:** Link
**What:** Widen ACR provenance-tag polling to tolerate eventual-consistency lag
**References:** scripts/azure/steps/20-build-push-images.mjs, scripts/azure/tests/build-provenance.test.mjs
**Why:** Observed production deploys showed `az acr import` succeeding while `az acr repository show-manifests` lagged by minutes under concurrent multi-image provenance stamping. We are keeping the existing safety contract (the deploy still fails if the provenance tag never appears or resolves to the wrong digest) but replacing the old 5x2s poll loop with a configurable exponential-backoff schedule starting at 2s, capping at 15s, and spending a 5-minute total budget before timing out. This keeps deploys resilient to ACR read-after-write lag without weakening provenance verification.

---

## 2026-07-27T14-38-36: #560/#574 real-flow A/B verification: FAIL — new, distinct teardown mechanism kills previewed pod ~1min after start_preview, unrelated to cluster-autoscaler
**By:** Morpheus
**What:** #560/#574 real-flow A/B verification: FAIL — new, distinct teardown mechanism kills previewed pod ~1min after start_preview, unrelated to cluster-autoscaler
**References:** #560, #574, #575, PR#575, v0.12.2
**Why:** ## Verdict: FAIL (do not declare "start_preview works" met yet)

Ran the definitive end-to-end live A/B verification of the #575 safe-to-evict fix through the REAL application flow (not an isolated annotation experiment), per Ahmed's request, on staging v0.12.2.

### What I did
1. Reused project `morpheus-ttl-verify-560` (80da10be-f9e6-4498-9581-e6fbfa6ca69a).
2. POST /api/projects/{id}/orchestrations (start_mode=direct, autopilot=true, autoApproveTools=true) → coordinator run `517d32b3-519b-4407-bf16-a933dbd16327`.
3. Polled /api/runs/{id}/children until subtasks reached terminal state. Child run `99a0cef4-892e-42c4-b17e-2ad1e20d130e` (subtask 64, agent Deckard) reached `AssembleReady` at 14:19:42Z (workflow log: "agent(Deckard) → completed" at 14:19:34Z), backed by pod `agentweaver-agent-host-gb4w6` / claim `agent-99a0cef4892e`.
4. At 14:22:50Z called `POST /api/runs/99a0cef4-892e-42c4-b17e-2ad1e20d130e/sandbox/preview` `{"target_port":3000}` → **200 OK**, real `preview_url` (`https://marten-orbit-harbor-...-preview.6a63b4fb256d5a00017339af.westus2.staging.aksapp.io`), `pod_name=agentweaver-agent-host-gb4w6`.
5. At 14:23:06Z confirmed via kubectl: pod annotation `cluster-autoscaler.kubernetes.io/safe-to-evict=false` — **the #575 fix IS applied correctly**, within 16s.
6. Started a tight poll (25s interval) of pod status + preview HTTP code. By 14:24:06Z (first read), the pod was **already gone** (`NotFound`) and preview HTTP was **000 (connection failure, not even a clean 404/NXDOMAIN)**.

### Root-cause evidence gathered (do NOT guess-fix — flagging for a real investigation)
- `kubectl get events -n agentweaver` shows **zero** ScaleDown/TriggeredScaleDown/node-drain events for pod `gb4w6` — this is **definitively NOT the #574 cluster-autoscaler mechanism** (contrast with the #574 evidence, which showed explicit `ScaleDown`→`Killing` pairs). The only event is a plain `Killing: Stopping container agentweaver-agent-host` at 14:23:49Z.
- agent-sandbox-controller logs show only two lines for claim `agent-99a0cef4892e` (adoption at 14:18:44Z), then at 14:23:49Z the `sandbox` controller starts reconciling "sandbox resource not found. Ignoring since object must be deleted" — i.e. **something deleted the claim/Sandbox object, but neither `KubernetesSandboxExecutor.ReleaseAgentHostPodAsync` nor `AgentHostReaperService.SweepOrphanedPodsAsync` logged doing so** (grepped both API pods' full logs for `agent-99a0cef4892e` — only the launch/bind lines appear, never a "releasing"/"deferring"/"deleted orphaned claim" line for this claim).
- `SandboxPreviewService`'s own 60s reaper found the preview `reason=Orphan` at 14:24:17Z — i.e. it discovered the pod ALREADY gone; this is a consequence, not the cause.
- Source review: `SandboxPreviewService.StartPreviewAsync` (the method invoked directly by `start_preview`) applies the safe-to-evict pod annotation (#574 fix) but **never calls `RenewBackingClaimTtlAsync`** (the #560 fix). TTL renewal only happens in: (a) `KubernetesSandboxExecutor.ReleaseAgentHostPodAsync`'s defer branch — but only if a preview is ALREADY active at the exact moment the run's turn ends; (b) `AgentHostReaperService.SweepOrphanedPodsAsync`'s defer branch — same "already active" precondition, driven by a periodic coordinator-heartbeat tick; (c) `KeepAliveAsync`. In this real-world flow, the run's turn ended (14:19:34Z) ~3 minutes BEFORE start_preview was ever called (14:22:50Z) — exactly the natural way a human/agent would request a preview after work finishes. If either of the "at completion" hooks already ran during that 3-minute gap (finding no preview yet, hence not renewing TTL), the claim's cluster-side `ttlSecondsAfterFinished` (confirmed live default = 600s on a sibling claim) is left un-renewed, and **starting the preview afterward does nothing to reset that clock**. This is a highly plausible, code-confirmed gap, though the exact elapsed time (~4m15s from turn-completion to kill) doesn't cleanly match the 600s default, so the exact trigger is not 100% pinned down — flagging as the leading hypothesis, not a confirmed root cause.
- Contrast: sibling child run `0b14d731-113b-48dd-95fd-6bd7899fa7ce` (no preview requested) was reaped normally by `AgentHostReaperService` at 14:25:42Z (~6 min after its own AssembleReady) — i.e. slower and via a logged, known path. Our previewed run's claim died FASTER (14:23:49Z) than the normal unprotected reap cadence, despite having "protection" — strong evidence the preview-protection path is not actually engaging for this timing pattern.

### Bottom line
The #575 fix (dynamic safe-to-evict annotation) works exactly as designed against cluster-autoscaler eviction — reconfirmed live. But this real end-to-end run surfaced a **different, still-open bug**: a preview started for a run whose turn already completed some time earlier can still have its backing pod/claim destroyed within ~1 minute, via a mechanism NOT explained by cluster-autoscaler eviction and not logged by any of the three known guard paths. **Recommend NOT declaring "start_preview works" met.** File as a new issue (successor to #560/#564/#570/#571/#574) referencing this evidence; the next investigator should instrument/trace `StartPreviewAsync`'s interaction with claim TTL more directly (e.g. does it need its own `RenewBackingClaimTtlAsync` call, or is there a 4th unlogged deletion path) before attempting a fix.

### Cleanup
No manual kubectl cleanup was needed — `SandboxPreviewService`'s reaper already removed the orphaned HTTPRoute/Service for the dead preview. Left project `morpheus-ttl-verify-560` and its now-completed/awaiting_review coordinator run `517d32b3-519b-4407-bf16-a933dbd16327` in place (this project is the established shared reusable fixture for this whole issue chain per prior turns).

References: issues #560, #564, #570, #571, #574; PR #575 (v0.12.2); scripts/harness-shared/learnings.md entries dated 2026-07-27.

---

## 2026-07-27T07-13-43: #560 root cause is the cluster-side SandboxClaim TTL, not a "direct executor" teardown path; fix renews the claim TTL while a preview is active
**By:** Morpheus
**What:** #560 root cause is the cluster-side SandboxClaim TTL, not a "direct executor" teardown path; fix renews the claim TTL while a preview is active
**References:** #560, #551, #542, Trinity, Cypher
**Why:** ## Decision (issue #560)

**Trinity's "direct sandbox executor teardown path" hypothesis was a misdiagnosis.** There is no separate `direct` executor that tears down pods. `PassthroughExecutor.BackendName => "direct"` runs *inside* the agent-host pod (AgentRuntime) and never manages pod teardown; the per-run `sandbox.backend=direct` label just reflects which in-pod executor AgentRuntime selected. Both coordinator and child runs tear down their pods through the SAME API-side paths (`KubernetesSandboxExecutor.ReleaseAgentHostPodAsync` + `AgentHostReaperService`), which #551 already gated.

**True root cause:** every `SandboxClaim` (both `agent-*` via `CreateAgentHostClaimAsync` and `run-*` via `CreateClaimAsync`) is created with `spec.lifecycle.ttlSecondsAfterFinished = Sandbox:TimeoutSeconds` (default **600s**) and `shutdownPolicy: Delete`. The sandbox controller enforces this TTL **independently of the API**: when a coordinator-dispatched child subtask's pod workload *finishes* (turn ends within seconds), the controller reaps the pod ~TimeoutSeconds later — matching the observed ~8 min NXDOMAIN. #551 only deferred the API-side delete/reap, which is powerless against the controller. This is why two API-side-only fixes were incomplete.

**Why the A/B "control" (coordinator run) survived:** it stays ACTIVE (InProgress/AwaitingReview) so its workload never "finishes" → its claim TTL never fires. So Trinity's A/B did NOT actually exercise the terminal-run deferral path it was meant to prove.

**Fix (implements the deferred `TODO(morpheus)` in `SandboxPreviewService.KeepAliveAsync`):** added `ISandboxPreviewService.RenewBackingClaimTtlAsync(runId)` which JSON-merge-patches `spec.lifecycle.ttlSecondsAfterFinished` up to `MaxLifetimeHours*3600 + 600s` on both candidate claim names (`agent-*`, `run-*`; missing candidate 404s ignored; MergePatch preserves `shutdownPolicy`). Called from: (a) `ReleaseAgentHostPodAsync` defer branch, (b) `AgentHostReaperService` defer branch, (c) `KeepAliveAsync` (reads the durable `preview-run-id` route annotation). Leak-safe: no-op when disabled, never throws; bounded because the API-side reaper still deletes the claim promptly on idle/max expiry (which supersedes the TTL) — the long TTL is only a backstop.

**PRIMARY RESIDUAL RISK (must be validated on staging):** this assumes the sandbox controller RECOMPUTES the pod deletion deadline when `ttlSecondsAfterFinished` is patched mid-life (as Kubernetes `Job` TTL does). If the controller snapshots the TTL at finish time instead, the renewal is ineffective and the TTL must instead be raised at claim CREATION for preview-eligible runs. I have no cluster access to verify the CRD controller's behaviour. Verification level: unit-verified + traced-through, NOT live-verified.

**Verification:** 49 targeted tests pass (`SandboxPreviewServiceClusterTests`, `KubernetesSandboxExecutorClaimTests`, `AgentHostReaperCredentialTests`), including new #560 tests asserting the extended-TTL claim PATCH is issued on release-deferral, reaper-deferral, and keepalive, and NOT issued when preview is disabled / no active preview.

**Possible remaining gap to watch:** if the controller does snapshot TTL at finish, or if a preview can be started on a run whose pod already finished before the first renewal lands, a race window remains. Recommend staging A/B specifically on a TERMINAL child-subtask preview (not the still-active coordinator run) to actually exercise this path.

---

## 2026-07-27T13-18-57: #574 fixed via dynamic safe-to-evict pod toggle (Option B), empirically validated on live staging; PR #575 opened against dev
**By:** Morpheus
**What:** #574 fixed via dynamic safe-to-evict pod toggle (Option B), empirically validated on live staging; PR #575 opened against dev
**References:** #574, #560, #564, #570, #571, PR #575, @sabbour
**Why:** Implemented and LIVE-VALIDATED the Option B fix for issue #574 (approved by @sabbour). PR: https://github.com/sabbour/agentweaver/pull/575 (base: dev).

## Decision
Fix the ~6-min preview-pod death by dynamically toggling the backing pod's `cluster-autoscaler.kubernetes.io/safe-to-evict` annotation rather than touching the SandboxClaim TTL (which #560/#564/#570/#571 all targeted and which was the WRONG mechanism). New method `SandboxPreviewService.SetBackingPodSafeToEvictAsync(runId, bool, ct)` merge-patches the pod annotation to "false" at every live-preview assertion point (StartPreviewAsync, KeepAliveAsync, KubernetesSandboxExecutor.ReleaseAgentHostPodAsync defer branch, AgentHostReaperService defer branch — symmetric with the existing RenewBackingClaimTtlAsync call sites) and back to "true" on teardown (StopPreviewAsync, also reached by expiry via ReapAsync). Best-effort/no-throw, no-op when preview disabled / no bound pod, ignores 404.

## Root cause (recap)
agent-sandbox v0.5.3 defaults sandbox pods to safe-to-evict:"true"; kata pool has cluster-autoscaler (min1/max5) and no agent-host PDB, so scale-down drains kata nodes and kills serving preview pods independent of ttlSecondsAfterFinished. shutdownPolicy:Delete then removes the workload-less claim instantly (so it "vanished" faster than any 600s TTL).

## RBAC
No change needed — `agentweaver-api-sandbox` Role already grants `pods: patch` (k8s/base/rbac-api.yaml). Confirmed live: `kubectl auth can-i patch pods --as=system:serviceaccount:agentweaver:agentweaver-api` => yes. (Unlike #571 which had to add sandboxclaims verbs.)

## Empirical proof (the non-negotiable step the prior 3 fixes skipped)
Live A/B on the agwv staging kata pool with a probe pod on an underutilized 2nd kata node:
- safe-to-evict="false": autoscaler reported `scaleDown: NoCandidates` for that node for ~14 min continuously (node excluded, pod survived).
- CONTROL safe-to-evict="true": within ~3.5 min the autoscaler marked the node `candidates: 1` (transition 13:02:29Z), then cordoned it (SchedulingDisabled) and evicted the probe pod via `ScaleDown` "deleting pod for node scale down" + `Killing` at 13:12:33Z — the EXACT event pair that killed the real agent-host pods in #574.
The annotation is the deciding factor. Evidence captured in scripts/harness-shared/learnings.md.

## Option C (PDB)
SKIPPED, noted as follow-up. Agent-host pods are ephemeral pod-per-run; a PDB risks blocking legitimate node drains / warm-pool recreation and would not stop a safe-to-evict:true scale-down anyway. The dynamic annotation is the correct targeted control.

## Tests
4 new SandboxPreviewServiceClusterTests + assertions in executor/reaper defer tests. Full Agentweaver.Tests: 2975 passed / 100 skipped; the only 5 failures are pre-existing PostgresIntegration Testcontainers tests needing a local Docker engine (unavailable on dev box) — all Sandbox/Preview/Reaper/Executor tests pass; CI runs Postgres with Docker.

## Status
PR #575 open against dev; CI running. NOT merging — awaiting @sabbour per the "verified evidence before ship" rule. Empirical proof above is that evidence.

---

# 2026-07-27T18-52-48Z — #578 definitively root-caused: the worker heartbeat's AgentHostReaper deletes preview-backed child SandboxClaims because preview detection is disabled on worker pods

**By:** Morpheus  
**What:** Closed the delete-attribution diagnostic for #578. The actual deleter is **Agentweaver's worker pod**, specifically `AgentHostReaperService` running under the worker heartbeat. The worker deployment does not enable sandbox preview config, so its reaper can never observe a live preview and incorrectly deletes completed child claims as "orphaned".  
**References:** #578, #560, #564, #570, #571, #574, #575, staging `agwv`, run `d39cd048-52f2-4026-ad0e-5baf9c37de3a`, claim `agent-d39cd04852f2`, pod `agentweaver-agent-host-xm9ml`

## Definitive evidence

1. **Kubernetes audit log attribution (WHO issued the delete)**
   - During the live repro, the first delete on the claim was:
     - `2026-07-27T18:27:14.1358555Z`
     - verb: `delete`
     - resource: `sandboxclaims`
     - name: `agent-d39cd04852f2`
     - user: `system:serviceaccount:agentweaver:agentweaver-worker`
   - Seconds later, the generic garbage collector deleted the sandbox and then the pod. That proves the claim delete was the first mover; GC was only downstream cleanup.

2. **Worker logs match the audit line exactly (WHAT code path fired)**
   - `kubectl logs -n agentweaver deploy/agentweaver-worker --since=20m` showed, at the same second:
     - `18:27:14 info: Agentweaver.Api.Sandbox.AgentHostReaperService[0] AgentHostReaper: deleted orphaned claim agent-d39cd04852f2`
     - `18:27:14 info: AgentHostReaper: deleted orphaned preview-runner credential for run d39cd048-52f2-4026-ad0e-5baf9c37de3a`
     - `18:27:14 info: AgentHostReaper: reaped 1 orphaned claims`
   - API pod logs did **not** contain the delete, which is why prior API-only logging never caught it.

3. **Source explains why the worker reaper was allowed to delete it**
   - `Program.cs` registers `CoordinatorHeartbeatService` unconditionally, and the heartbeat periodically calls `IAgentHostReaper` when Kubernetes sandboxing is enabled.
   - `AgentHostReaperService.GetActiveClaimMapAsync()` only treats `InProgress`, `Pending`, and `AwaitingReview` runs as active. A completed / `AssembleReady` child preview run is therefore considered "orphaned".
   - Before deleting, `AgentHostReaperService` calls `HasActivePreviewAsync(runId)`.
   - `SandboxPreviewService.HasActivePreviewAsync()` returns `false` immediately when preview is disabled (`if (!Enabled) return false;`).
   - The **API** deployment sets `Sandbox__Preview__Enabled=true` and the preview gateway vars (`k8s/base/api-deployment.yaml`), but the **worker** deployment does not set any `Sandbox__Preview__*` env vars (`k8s/base/worker-deployment.yaml`).
   - Therefore on the worker pod, preview detection is disabled by configuration, `HasActivePreviewAsync()` is always false there, and the reaper proceeds to delete preview-backed claims that are no longer in the active-run status set.

## Why this explains the full symptom chain

- The bug only appears after `start_preview` succeeds on a **completed child** run.
- The claim still has a live preview route and a serving pod, but the worker reaper's active-run map no longer counts that child as active.
- Because the worker pod lacks preview config, its "is a preview still live?" guard is a permanent false negative.
- The next worker heartbeat reaper sweep deletes the claim as orphaned.
- The sandbox and pod then disappear via normal Kubernetes garbage collection.
- This exactly matches the observed 60-90s post-preview death window and explains why:
  - TTL renewal did not help,
  - autoscaler was not the cause in this repro,
  - agent-sandbox controller logs only showed downstream cleanup,
  - API logs showed no delete path.

## Decision

Do **not** ship any guess-fix based on TTL, controller TTL semantics, or API-only delete paths for #578. The confirmed root cause is an **Agentweaver worker reaper / worker config mismatch**:

- the worker participates in orphan-claim reaping,
- the worker can delete `SandboxClaim`s,
- but the worker is not configured to recognize live previews.

The follow-up fix should make the worker reaper preview-aware (or prevent the worker role from performing preview-sensitive reap decisions) before any further validation run.

## Staging cleanup

- Removed temporary AKS diagnostic setting `morpheus-578-audit`.
- Redeployed staging back to the clean released tag `v0.12.2` / image tag `fdd59df`.
- Left no extra runtime diagnostics enabled in staging.

---

## 2026-07-27T17-05-01: #578 TTL-renewal hypothesis REFUTED live: claim held ttlSecondsAfterFinished=29400 yet was still reaped by an unlogged non-TTL delete path. Do NOT ship the StartPreviewAsync TTL fix as the #578 fix; add SandboxClaim delete-watch instrumentation instead.
**By:** Morpheus
**What:** #578 TTL-renewal hypothesis REFUTED live: claim held ttlSecondsAfterFinished=29400 yet was still reaped by an unlogged non-TTL delete path. Do NOT ship the StartPreviewAsync TTL fix as the #578 fix; add SandboxClaim delete-watch instrumentation instead.
**References:** #578, #560, #564, #570, #571, #574, #575, branch:fix-578-start-preview-claim-ttl-renewal
**Why:** ## Context
#578: a preview pod for a terminal child subtask is destroyed ~60-90s after a successful start_preview, on staging v0.12.2 (which already has the #575 safe-to-evict fix, confirmed not the cause). Successor to #560/#564/#570/#571/#574. The strongest prior lead: `SandboxPreviewService.StartPreviewAsync` never calls `RenewBackingClaimTtlAsync` itself.

## What I confirmed at code level (lead is TRUE)
`StartPreviewAsync` (apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:187) sets safe-to-evict=false inline on the bound pod but never renewed the backing SandboxClaim TTL. TTL renewal only happened in `ReleaseAgentHostPodAsync`'s defer branch, `AgentHostReaperService`'s defer branch, and `KeepAliveAsync` — all gated on HasActivePreviewAsync being true when a later hook fires. I implemented the renewal at the start-path (RenewBackingClaimTtlAsync inside StartPreviewAsync), unit-tested it (27/27 preview cluster tests pass), and deployed a surgical API-only image (tag fc9cceb1) to live staging (cluster agwv, ns agentweaver).

## What the LIVE deployment let me prove (hypothesis REFUTED)
With the fix deployed, I drove a real coordinator run, caught an in_progress child (run 20157f98-a388-401b-a11b-b8883d90347a, claim agent-20157f98a388, pod agentweaver-agent-host-bmttk), enabled per-run auto-approve, and called start_preview -> HTTP 200.
- OBSERVED: claim `ttlSecondsAfterFinished` flipped 600 -> 29400 the instant start_preview returned (my fix firing), BEFORE any keepalive. Verified twice (also on coordinator run 238261da).
- The child then stalled -> failed (agent_stall_timeout) at 16:13:26Z. Logs show `ReleaseAgentHostPodAsync` CORRECTLY DEFERRED ("a live preview is still active") and renewed TTL again.
- I polled survival every 15-20s. The claim stayed PRESENT with ttl=29400 through 16:13:52Z, then was GONE by ~16:14:00-16:14:11Z.
- Pod `Killing` event fired; agent-sandbox-controller only observed "sandbox resource not found ... must be deleted" (a consequence). The preview route was reaped reason=Orphan at 16:14:36Z, AFTER the pod was already gone.

DECISIVE: the claim's TTL was 29400 (8h) the ENTIRE time and the claim was STILL deleted ~34s after a logged defer+renew. Grepping BOTH agentweaver-api replicas for the claim/run/pod in the 16:12-16:14 window shows NO `DeleteClaimAsync` ("deleted claim {Claim}") log and NO `AgentHostReaper` "reaped N orphaned claims" log. The claim has NO ownerReferences (k8s GC cascade ruled out). So the deleter is a non-API path not governed by `ttlSecondsAfterFinished`.

CONCLUSION: The "StartPreviewAsync doesn't renew TTL" hypothesis is REFUTED as the #578 root cause. Renewing ttlSecondsAfterFinished cannot prevent an explicit/controller-driven delete that ignores the TTL. My start-path renewal is a safe, minor consistency/defense-in-depth improvement but does NOT fix #578. This matches and hardens the prior session's observation (sibling previewed child died faster than the normally-reaped no-preview sibling, via an unlogged path).

## Secondary finding (reaper scope)
`AgentHostReaperService.GetActiveClaimMapAsync` treats only InProgress/Pending/AwaitingReview runs as active (comment at AgentHostReaperService.cs:40-44). An `AssembleReady` child (the exact #578 state) is therefore "orphan" per the reaper. HasActivePreviewAsync catches ALL exceptions and returns false ("treating as no active preview") — so a transient k8s API timeout during the reaper sweep (one such TaskCanceledException was logged at 16:13:18Z) can false-negative and let the reaper delete a claim with a live preview. This is a plausible fragility but was NOT the confirmed deleter in my capture (no reaper delete was logged for this claim at all).

## Decision
Do NOT open a "Fixes #578" PR for the TTL-renewal change (per the standing rule: no guess-fix without a confirmed, empirically validated root cause; this bug chain has had 3 fix attempts + 3 failed verifications). The change is retained on branch `fix-578-start-preview-claim-ttl-renewal` (commits f9623e0e + fc9cceb1) for reference but is explicitly NOT the fix.

## Recommended instrumentation (required before the next fix attempt)
1. **SandboxClaim delete-watch informer** (highest value): a lightweight k8s watch/informer on `agent-*` SandboxClaims in the agentweaver namespace that logs EVERY delete event with a UTC timestamp, the claim's last-known annotations (run-id, preview labels), and its ttlSecondsAfterFinished at delete time. App-side logging demonstrably MISSES this deletion; only a watch can attribute a non-API/controller delete.
2. **Raise agent-sandbox-controller verbosity** (agent-sandbox-system/agent-sandbox-controller) to capture its reconcile/delete decisions for adopted warm-pool Sandboxes when the associated run terminalizes; investigate whether it reaps on SandboxClaim status/phase transitions independent of ttlSecondsAfterFinished, and the warm-pool reclaim path for adopted sandboxes.
3. **CallerMemberName + ttl on every API delete/patch**: add `[CallerMemberName]` and the claim's current ttl to `KubernetesSandboxExecutor.DeleteClaimAsync` and `AgentHostReaperService.TryDeleteClaimAsync`, and log before/after on `RenewBackingClaimTtlAsync`/`SetBackingPodSafeToEvictAsync`/`StopPreviewAsync` — so the exact API path (if any) and reason is attributable.
4. Once instrumented and redeployed, reproduce the EXACT #578 scenario (AssembleReady child, start_preview a few minutes after turn-finish, poll every 10-15s) and read the delete-watch to identify the true deleter, THEN fix at that site (likely: make that path honor HasActivePreviewAsync, or harden HasActivePreviewAsync so a transient list timeout does not false-negative into a delete).

## Staging note
Staging currently runs the unreleased API image `fc9cceb1` (fix superset; harmless). Recommend reverting to the released tag after this investigation, or leaving it pending a decision. Per-run auto-approve was durably enabled on runs 20157f98 and 238261da for validation.

---

## 2026-07-29T06-53-02: Align skill generation with shared Generation model defaults
**By:** Morpheus
**What:** Align skill generation with shared Generation model defaults
**References:** apps/Agentweaver.Api/Skills/ISkillGenerator.cs, apps/Agentweaver.Api/Generation/GenerationModelOptions.cs, tests/Agentweaver.Tests/Skills/CopilotSkillGeneratorTests.cs, tests/Agentweaver.Tests/Coordinator/CoordinatorSpecGenerationModelTests.cs
**Why:** Context: The skill generator (`CopilotSkillGenerator`) was the outlier among server-side AI drafting flows. Blueprints, workflows, and coordinator outcome-spec drafting already resolve their model from `GenerationModelOptions` (defaulting to `Generation:Model`, currently `gpt-5.6-sol`), while skill generation was still reading `Providers:GitHubCopilot:Model`, which is a generic runtime fallback and can be configured to a weaker/cheaper agent-execution model.

Decision: Move skill generation onto `GenerationModelOptions` as well, with a new `Generation:SkillModel` override and shared fallback to `Generation:Model` / `GenerationModelOptions.DefaultModel`.

Rationale: This makes skill generation consistent with comparable one-shot drafting features, avoids silently degrading this UX when the runtime Copilot model is tuned for speed, and still preserves an escape hatch for skill-specific tuning later without affecting other generation paths.

---

# Morpheus — demo direction compositor design

## Summary
The new `render-direction` pipeline renders from the raw captured beat video plus the approved `direction.json`, then syncs the finished picture edit against the beat narration audio at the very end.

## Decision
- Treat the cue-timed raw capture as the only valid picture source for `render-direction`; do not render from `sync-beat` output because activity trimming/padding would shift cue-relative source times.
- Preserve narration tempo unchanged. The renderer refuses playback-rate changes on `action` segments and only supports `playbackRate >= 1` so accelerated edits stay confined to wait/dead-time spans that the analyzer already classifies as non-causal.
- Use hard cuts only for beat assembly and scenario assembly. No dissolve/xfade path is permitted.
- After concatenating the approved picture segments, use the existing end-padding sync helper to absorb small end-of-beat drift rather than time-stretching spoken narration.

## Rationale
This is the most conservative way to honor the existing analyzer contract: `take-analyzer` already keeps action/proof footage at 1x and suggests acceleration only for waits. Rendering from raw capture preserves DOM-cue anchoring exactly, while late audio sync avoids introducing a second set of cue offsets. Refusing rate changes on action segments prevents a human-edited direction file from silently desynchronizing narration and proof footage.

---

## 2026-07-27T09-29-35: FAIL — #560/#564 TTL-renewal fix (v0.12.0) is a no-op in the live staging cluster: agentweaver-api RBAC Role never got `patch`/`update` added for sandboxclaims
**By:** Morpheus
**What:** FAIL — #560/#564 TTL-renewal fix (v0.12.0) is a no-op in the live staging cluster: agentweaver-api RBAC Role never got `patch`/`update` added for sandboxclaims
**References:** issue-560, pr-564, release-v0.12.0, k8s/base/rbac-api.yaml, apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs, apps/Agentweaver.Api/Sandbox/AgentHostReaperService.cs
**Why:** ## Verification objective
Definitive live A/B test of #560/#564 (RenewBackingClaimTtlAsync), shipped in v0.12.0, staging (25/25 healthy). Required scenario: a TERMINAL CHILD/SUBTASK run (not the coordinator's own long-lived run) — dispatch a worker, let its turn finish in seconds, call start_preview for that child run, then confirm the preview survives 12-15+ minutes past the 600s/10min default SandboxClaim TTL.

## What I ran (live, staging: agentweaver.6a63b4fb256d5a00017339af.westus2.staging.aksapp.io, cluster agwv/asabbour, ns agentweaver)
1. Created project `morpheus-ttl-verify-560` (80da10be-f9e6-4498-9581-e6fbfa6ca69a), cast a 2-person team (Deckard/Roy) via casting proposal 27e1749e2f924471833900bfe7925fc5.
2. Started a `direct`+`autopilot`+`autoApproveTools` coordinator orchestration (runId 3ba25de4-79a7-4512-ba1c-563c949c7abd) with a trivial one-file goal designed to dispatch exactly one worker subtask.
3. Child run d8426592-e0ad-4ebf-b347-df4980effb6d (subtask 61, agent Deckard) reached terminal `AssembleReady` (turn genuinely completed) at 2026-07-27T12:13:43+03:00 (09:13:33 UTC per API logs: "agent(Deckard) → completed").
4. Called `POST /api/runs/{childRunId}/sandbox/preview` (agent-initiated endpoint, called as run owner) at 2026-07-27T12:18:06+03:00 (09:18:07 UTC) — succeeded, returned a real preview_url (confirms the separate #529 IsOwnerOrServiceCaller 403 fix still holds).
5. Backing SandboxClaim `agent-d8426592e0ad` confirmed via kubectl at baseline: `spec.lifecycle.ttlSecondsAfterFinished: 600` (default), claim/pod both present.
6. Polled kubectl + curl'd the preview URL every ~90s for the following ~17 minutes.

## Result: FAIL, and fast — not even a slow leak
- 09:18:37 & 09:18:54-55 UTC: `AgentHostReaperService` correctly detected the run's claim as orphaned-but-preview-active and correctly called `RenewBackingClaimTtlAsync` (the #560 fix code path IS being exercised, exactly as intended) — but every single patch attempt failed:
  `warn: SandboxPreviewService: best-effort claim TTL renewal failed for agent-d8426592e0ad (run d8426592-...); preview may NXDOMAIN when the cluster TTL elapses (#560)`
  `k8s.Autorest.HttpOperationException: ... 'Forbidden' ... "sandboxclaims.extensions.agents.x-k8s.io \"agent-d8426592e0ad\" is forbidden: User \"system:serviceaccount:agentweaver:agentweaver-api\" cannot patch resource \"sandboxclaims\" in API group \"extensions.agents.x-k8s.io\"..."`
  (Same 403 for the `run-*` claim-name candidate too.)
- 09:19:53 UTC: `SandboxPreviewReaperService` reaped the preview itself with `reason=Orphan` (its own `PodExistsForRunAsync` pod-label lookup came back empty — the pod was already gone by then), only ~1'46" after start_preview was called and only ~6'20" after the subtask actually finished.
- By 2026-07-27T12:20:37+03:00 (09:20:37 UTC) the SandboxClaim `agent-d8426592e0ad` and pod `agentweaver-agent-host-mk6jj` were both fully gone (`kubectl get` → NotFound), and the preview URL returned curl exit `000` (connection failure, effectively NXDOMAIN/unreachable) — confirmed again at the end of the full 17-minute observation window.
- **Root cause, confirmed from the deployed manifest, not guessed**: `k8s/base/rbac-api.yaml` (Role `agentweaver-api-sandbox`, bound to the `agentweaver-api` ServiceAccount) grants only `get, list, create, delete` on `sandboxclaims.extensions.agents.x-k8s.io` — **no `patch` or `update`**. I diffed this against the *live* deployed Role via `kubectl get role agentweaver-api-sandbox -n agentweaver -o yaml` and it matches the repo file exactly (same 4 verbs, no patch/update). By contrast the same Role *does* grant `patch` on `pods` and `patch`+`update` on `gateway.networking.k8s.io` httproutes — sandboxclaims was simply missed when PR #560/#564 added the JSON-merge-PATCH call in `SandboxPreviewService.RenewBackingClaimTtlAsync`.
- Net effect: the #560 fix's application-level logic (which run/claim to renew, when to renew, TTL renewal math bumping to `MaxLifetimeHours*3600+600`) is all correct and IS being invoked at exactly the right moments — but every renewal attempt is unconditionally rejected by Kubernetes RBAC before it can take effect, so in this real deployed cluster the fix is a complete no-op. This is precisely the class of gap that source-reading (which I did for the original fix) cannot catch: the C# code and its own unit tests have no way to see a namespace-scoped Role that was never updated in the same PR.

## Why this wasn't caught by #560/#564's own testing
The original fix (merged, tests presumably mocking `ICustomObjectsOperations`) never exercised real RBAC — hermetic tests almost certainly use a fake/mocked Kubernetes client, so a real Forbidden response was structurally impossible to observe pre-merge. This is a hard argument for including this exact live-cluster scenario in a release gate, not just unit coverage, for anything touching RBAC-scoped verbs.

## Recommended fix (NOT applied by me — flagging per instructions, not guess-patching)
Add `patch` (and likely `update`, for symmetry with the other resources in the same Role and to also cover any future direct pod/sandbox-status patch paths) to the `sandboxclaims.extensions.agents.x-k8s.io` rule in `k8s/base/rbac-api.yaml` (currently `get, list, create, delete` only), then redeploy and re-run this exact same live A/B scenario end-to-end before re-closing #560. Whoever picks this up should also add an explicit RBAC-permission integration/smoke check (e.g. a startup or release-gate probe that actually calls `RenewBackingClaimTtlAsync` against a real/kind cluster with the real Role bound) so a future verb omission on this Role fails CI instead of silently no-op'ing in prod again.

## Evidence retained
- API pod logs (agentweaver-api-674456c4f7-gxh9z, -fpm2m), `--since=30m`, grepped for the run id / reaper / #560 / #542, saved locally during investigation (not committed).
- `kubectl get sandboxclaim -o yaml` snapshots before/after.
- Deployed Role verbs captured via `kubectl get role agentweaver-api-sandbox -n agentweaver -o yaml`.
- Test project `morpheus-ttl-verify-560` (80da10be-f9e6-4498-9581-e6fbfa6ca69a) left in staging as a live repro; can be deleted once the fix lands and is re-verified.

## Judgment calls made
- Used direct curl-driven API calls (not a dispatched PersonaActor) since this is a mechanical infra/timing verification, not a persona-behavior scenario — matches the free-text/exploratory intent of the api-harness skill without adding an extra LLM-driven agent into a precise 12-15+ minute timing test.
- Used the operator `port-forward`-equivalent agent-initiated `POST /api/runs/{runId}/sandbox/preview` endpoint directly as the run owner (valid per `IsOwnerOrServiceCaller`) rather than routing through the AgentPreviewGate approval UI, since `autoApproveTools` was already set and this endpoint exercises the exact same `StartPreviewForRunAsync`/`RenewBackingClaimTtlAsync` code path as the real `start_preview` tool call a live agent would make.

---

## 2026-07-27T09-44-58: Fix #570: grant patch/update on sandboxclaims to agentweaver-api-sandbox Role (worker Role left untouched)
**By:** Morpheus
**What:** Fix #570: grant patch/update on sandboxclaims to agentweaver-api-sandbox Role (worker Role left untouched)
**References:** issue-570, issue-560, pr-564, morpheus
**Why:** Root cause confirmed via live A/B on staging (prior turn): `SandboxPreviewService.RenewBackingClaimTtlAsync` / `KeepAliveAsync` (added for #560, PR #564) JSON-merge-patch `spec.lifecycle.ttlSecondsAfterFinished` on the run's backing `SandboxClaim` custom resource, but `k8s/base/rbac-api.yaml`'s `agentweaver-api-sandbox` Role only ever granted `get, list, create, delete` on `sandboxclaims.extensions.agents.x-k8s.io` — `patch`/`update` was never added when the renewal call shipped. Every renewal attempt 403s, so #564 was a silent no-op in production.

Fix: added `patch` and `update` to that single rule's verb list, keeping the existing four verbs. Added an explanatory comment above the rule referencing #570/#560/#564.

Judgment calls made:
1. SCOPE — did NOT touch `agentweaver-worker-sandbox`'s identical-looking `sandboxclaims` rule (also missing patch/update). Traced `ISandboxPreviewService`/`SandboxPreviewService` registration in `Program.cs` (`AddSingleton<ISandboxPreviewService>`) and confirmed it is only wired into the `Agentweaver.Api` process (SandboxExecutorRouter, AgentHostReaperService, KubernetesSandboxExecutor, CoordinatorAssemblyService, PreviewStep, SandboxEndpoints — all API-side). Nothing in the worker's code path calls patch/update on sandboxclaims today, so widening the worker Role would violate least-privilege for no functional benefit. Left a note here in case that changes.
2. OTHER RESOURCES — audited all `Patch*`/`Update*` calls reachable from `RenewBackingClaimTtlAsync`/`SandboxPreviewService`/`KubernetesSandboxExecutor`/`AgentHostReaperService`: pods (`PatchNamespacedPodAsync`, keepalive touch) and HTTPRoutes (`PatchNamespacedCustomObjectAsync` for preview annotation bump) both already carry `patch` in the existing Role. `sandboxclaims` was the only gap.
3. REGRESSION TEST — added `KubernetesRemoteApiManifestTests.ApiSandboxRole_GrantsPatchAndUpdateOnSandboxClaims`, following the existing manifest-text-assertion pattern in that file (regex over the raw YAML, no new test infra), pinning the full verb list so a future edit can't silently drop patch/update again. Did not build a live in-cluster or fake-apiserver RBAC-vs-code-static-analysis check — that would need new test infrastructure disproportionate to a one-line YAML fix; noted as a gap in the PR description instead.

---

## 2026-07-27T12-32-07: Issue #574 root-caused: AgentHost preview pods are evicted by cluster-autoscaler kata-node scale-down (safe-to-evict:true), NOT by SandboxClaim TTL — the entire #560/#564/#570/#571 TTL-renewal fix line targeted the wrong mechanism.
**By:** Morpheus
**What:** Issue #574 root-caused: AgentHost preview pods are evicted by cluster-autoscaler kata-node scale-down (safe-to-evict:true), NOT by SandboxClaim TTL — the entire #560/#564/#570/#571 TTL-renewal fix line targeted the wrong mechanism.
**References:** #574, #560, #564, #570, #571, AgentHostReaperService.cs, SandboxPreviewService.cs, k8s/base/sandbox-template-agenthost.yaml, scripts/azure/steps/10-create-cluster.mjs
**Why:** ## Confirmed root cause (live evidence, not a guess)

The ~6-minute preview-pod death in #574 is NOT a SandboxClaim `ttlSecondsAfterFinished` reap and NOT the AgentHostReaperService. It is **cluster-autoscaler node scale-down (and NotReady node replacement) evicting the kata pod**, entirely independent of the TTL/reaper machinery the last three fixes touched.

### Decisive live evidence (staging `agwv` cluster, agentweaver ns, 2026-07-27 ~12:22Z)
1. `kubectl get events` shows repeated **`ScaleDown` → `Killing`** pairs on agent-host pods: e.g. `pod/agentweaver-agent-host-cshwb: "deleting pod for node scale down"` + `Killing`, and `pod/agentweaver-agent-host-6v2rd: "deleting pod for node scale down"` + `Killing`. Also `TriggeredScaleUp [aks-katapool 1->2]`. The kata pool oscillates 1↔2 nodes continuously.
2. The live agent-host **pod carries `cluster-autoscaler.kubernetes.io/safe-to-evict: "true"`** (via the controller's `agents.x-k8s.io/propagated-annotations`). This explicitly authorizes the autoscaler to drain the kata node and kill the pod at any time to scale down.
3. There is **no PodDisruptionBudget** for agent-host pods (only api/frontend/gateway/mcp/worker have PDBs). Nothing blocks their disruption.
4. `cluster-autoscaler-status` configmap shows an active `scaleDown:` section for `aks-katapool-28841148-vmss`.
5. Kata pool is provisioned with `--enable-cluster-autoscaler --min-count 1 --max-count 5` (scripts/azure/steps/10-create-cluster.mjs) — so it aggressively scales back to 1 node whenever load drops, draining whatever agent-host pods sit on the node being removed. Pods also tolerate `not-ready:NoExecute` for only 300s, so a NotReady node (the one-off 000y event in #574) evicts them after 5 min.
6. Our SandboxTemplate `podTemplate.metadata` sets NO annotations, so `safe-to-evict:true` is an agent-sandbox v0.5.3 **controller default**, not something we set.

### Why this explains every #574 observation
- Faster than 600s TTL: because it was never a TTL reap — the autoscaler/kubelet evicted the pod, then `shutdownPolicy: Delete` promptly removed the now-workload-less SandboxClaim (explaining why the claim "vanished" instantly).
- Reaper correctly logged "deferring reap" and RenewBackingClaimTtl fired with no 403s — all true and all irrelevant, because the pod was killed at the node layer, not the claim-TTL layer.
- Multiple pods dying at the same second (jvqtp+xwlv8, bs4sd+qmv4z): two ~1000m-CPU kata pods share one 4-vCPU node, so draining/losing one node kills both its pods together.

### Candidate (a) — cascading delete / reaper race: RULED OUT
Source review of AgentHostReaperService, KubernetesSandboxExecutor.ReleaseAgentHostPodAsync, and SandboxPreviewService confirms every API-side delete path correctly gates on HasActivePreviewAsync and defers; no legacy parallel delete path exists. The claim disappearance is a *consequence* of node-level pod eviction + shutdownPolicy:Delete, not an API delete.

### Proposed fix (NON-TRIVIAL — stopping before implementing, per instruction, given 3 prior failed attempts)
The real fix is to stop the cluster-autoscaler from draining kata nodes that host a live/serving AgentHost pod. Options:
- **Option A (static, blunt):** add `cluster-autoscaler.kubernetes.io/safe-to-evict: "false"` to the SandboxTemplate `podTemplate.metadata.annotations`. One-line, simple. Downside: pins kata nodes for ALL agent-host pods including idle warm-pool spares (replicas:2) → kata pool effectively never scales back to min=1 → cost/capacity regression. Also unverified that the controller lets a template annotation override its propagated default.
- **Option B (dynamic, targeted — recommended):** patch the backing POD's `safe-to-evict` annotation to `"false"` when a preview goes live (alongside the existing RenewBackingClaimTtlAsync call sites), and back to `"true"` on preview teardown/release. Mirrors the existing TTL-renewal pattern; only pins the node while a preview is actually live. More code; needs a pod-patch RBAC verb check (the #571 Role currently covers sandboxclaims, likely not pods).
- **Option C (defense-in-depth):** add a PDB for agent-host pods. Weak alone (doesn't stop safe-to-evict:true scale-down) but complements A/B for drains.

### Required verification before shipping ANY fix (do not repeat the "looked-correct-but-failed" pattern)
Empirically confirm on staging that setting `safe-to-evict:"false"` on the pod actually prevents the autoscaler `ScaleDown` "deleting pod for node scale down" eviction — i.e. patch a live serving pod's annotation to false, force/observe a scale-down window, and confirm the node with that pod is NOT drained. Only then wire it into the template (A) or the preview lifecycle (B).

RECOMMENDATION: pursue Option B (lifecycle-scoped safe-to-evict:false on the backing pod, symmetric with the TTL renew/reset), gated behind a live safe-to-evict experiment first. Keep #560/#574 open until that experiment passes.

---

## 2026-07-29T20-07-17: Represent workflow event trigger predicates as a typed AST with generator-time validation
**By:** Morpheus
**What:** Represent workflow event trigger predicates as a typed AST with generator-time validation
**References:** issue #641, apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs, apps/Agentweaver.Api/Workflows/WorkflowDefinitionLoader.cs, apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs
**Why:** For issue #641's generator slice, I represented `trigger.if` as a typed event-predicate AST on `WorkflowTrigger` and taught `WorkflowDefinitionLoader` to parse/validate it against the issue contract before a generated draft is accepted. The loader now validates the curated GitHub event families plus per-event predicate allowlists, regex syntax for `commentMatches`, and recursive `or`/`not` wrappers so Copilot generation can reuse the existing single correction-pass path instead of inventing a second trigger-only retry flow. I intentionally scoped this to schema/round-trip/generator validation only; runtime predicate evaluation remains a separate backend concern, so any field-shape mismatch with Tank's implementation should be resolved at integration time rather than blocked here.

---

## 2026-07-27T07-33-34: Source-verified (agent-sandbox v0.5.3): controller recomputes expiry from the live claim ttl field each reconcile, and never puts a TTL on the underlying Sandbox — so the #564 mid-life TTL patch is effective. No revision needed.
**By:** Morpheus
**What:** Source-verified (agent-sandbox v0.5.3): controller recomputes expiry from the live claim ttl field each reconcile, and never puts a TTL on the underlying Sandbox — so the #564 mid-life TTL patch is effective. No revision needed.
**References:** #560, #564, kubernetes-sigs/agent-sandbox v0.5.3
**Why:** ## Verification resolved (issue #560 / PR #564): controller DOES recompute the deadline from the live field

The residual risk I flagged — whether the sandbox controller recomputes the pod deletion deadline when `ttlSecondsAfterFinished` is patched mid-life, or snapshots it at finish — is now **definitively resolved by reading the upstream source at the pinned version** `kubernetes-sigs/agent-sandbox` **v0.5.3** (`SANDBOX_CONTROLLER_VERSION` default in `scripts/azure/steps/10-create-cluster.mjs`). No controller source is vendored in this repo; I fetched it from GitHub at the v0.5.3 tag.

**Finding: the controller recomputes on every reconcile from the LIVE spec field. The K8s Job-TTL analogy holds. My fix is correct; no revision needed.**

Evidence (`extensions/controllers/sandboxclaim_controller.go` @ v0.5.3):
```go
func (r *SandboxClaimReconciler) checkExpiration(claim *extensionsv1beta1.SandboxClaim) (bool, time.Duration) {
    if claim.Spec.Lifecycle == nil { return false, 0 }
    finishedCondition := lifecycle.FinishedCondition(claim.Status.Conditions, string(v1beta1.SandboxConditionFinished))
    return lifecycle.TimeLeft(time.Now(), claim.Spec.Lifecycle.ShutdownTime, claim.Spec.Lifecycle.TTLSecondsAfterFinished, finishedCondition)
}
```
`internal/lifecycle/expiry.go` @ v0.5.3:
```go
func ExpireAt(shutdownTime *metav1.Time, ttlSecondsAfterFinished *int32, finishedCondition *metav1.Condition) *time.Time {
    var expireAt *time.Time
    if shutdownTime != nil { shutdownAt := shutdownTime.Time; expireAt = &shutdownAt }
    if !NeedsCleanup(ttlSecondsAfterFinished, finishedCondition) { return expireAt }
    finishedAt := FinishedTime(finishedCondition)          // = Finished condition LastTransitionTime (fixed)
    if finishedAt == nil { return expireAt }
    ttlExpireAt := finishedAt.Add(time.Duration(*ttlSecondsAfterFinished) * time.Second)  // live ttl
    if expireAt == nil || ttlExpireAt.Before(*expireAt) { expireAt = &ttlExpireAt }
    return expireAt
}
```

Why this makes the fix sound:
1. `checkExpiration` reads `claim.Spec.Lifecycle.TTLSecondsAfterFinished` **live** each reconcile — no cached/snapshot deadline in status. Patching the field upward → `ttlExpireAt = finishedAt + newTtl` is later → not expired → `RequeueAfter`.
2. A spec patch triggers an immediate reconcile (controller-runtime `For(&SandboxClaim{})`), so the new TTL applies right away, far inside the old 600s window.
3. `ShutdownTime` is a **spec** field we never set (nil), so it doesn't cap the expiry.
4. The controller **never copies `spec.lifecycle` onto the underlying `Sandbox`** object (grep of the whole reconciler: the only `.Lifecycle` reads are `claim.Spec.Lifecycle`; there is no `sandbox.Spec.Lifecycle = ...`). So the claim TTL is the SOLE TTL-driven expiry — there is no independent Sandbox-level TTL that could reap the pod behind our back. The "underlying Sandbox expired independently" condition only fires for Sandboxes created with their own lifecycle, which is not Agentweaver's path.

**Remaining (minor, acceptable) constraint, stated honestly:** a renewal must land within `TimeoutSeconds` (600s) of the workload finishing. Satisfied by three independent triggers (turn-end release at ~finish, ~2-min reaper deferral, per-request keepalive). The only theoretical gap is a >600s API/renewal outage in that specific window — no worse than today.

**Version dependency:** this is verified for v0.5.3 specifically. If `SANDBOX_CONTROLLER_VERSION` is bumped, re-verify `checkExpiration`/`ExpireAt` still read the live field. Documented in `docs/reference/live-preview-provisioning.md`.

Verification level upgraded: **source-verified against the pinned controller version** (was: unit + traced-through). Still not live-cluster-tested, but the source is now conclusive on the specific question that made the two prior fixes risky.

---

## 2026-07-27T12-18-49: TTL A/B re-verify on v0.12.1: FAIL with a new, different failure mode (not RBAC 403). Recommend reopening #560, do not close.
**By:** Morpheus
**What:** TTL A/B re-verify on v0.12.1: FAIL with a new, different failure mode (not RBAC 403). Recommend reopening #560, do not close.
**References:** issue #560, issue #570, PR #564, PR #571, project morpheus-ttl-verify-560
**Why:** Ran the exact same live A/B verification against staging v0.12.1 that previously failed with RBAC 403 (issue #570, fixed by PR #571). This run:

- Reused project morpheus-ttl-verify-560 (80da10be-f9e6-4498-9581-e6fbfa6ca69a).
- Dispatched coordinator run ec4d003d-bd77-4121-aa18-61fd78e3f69c with goal "Create a file called hello.txt...".
- Child subtask run d24edd14-968c-4036-bd52-74ffb3a9866b (agent Deckard) finished its turn at 2026-07-27T12:02:10.403Z, backed by SandboxClaim agent-d24edd14968c / pod agentweaver-agent-host-jvqtp.
- Called POST /api/runs/d24edd14-968c-4036-bd52-74ffb3a9866b/sandbox/preview at 12:04:37Z -> HTTP 200, real preview_url returned (https://orbit-ivory-bronze-rosjm4tz2sia7e3r2c73kvj52u-preview.6a63b4fb256d5a00017339af.westus2.staging.aksapp.io).
- Confirmed via `kubectl get sandboxclaim agent-d24edd14968c -o yaml` at 12:05:19Z: ttlSecondsAfterFinished=600 still present (expected, pre-renewal snapshot).
- API logs show AgentHostReaperService correctly deferred reap (not RBAC 403'd) at 12:06:30Z AND 12:07:57Z: "deferring reap of claim agent-d24edd14968c ... a live preview is still active" -- confirming the #571 RBAC fix IS still working (consistent with the prior 25/25 verification; no 403s anywhere in these logs).
- BUT: kubelet issued a Killing event for pod agentweaver-agent-host-jvqtp at 12:07:57Z (the SAME second as that second deferral log). The SandboxClaim agent-d24edd14968c is now completely gone from the cluster (`kubectl get sandboxclaim` no longer lists it), and the preview_url NXDOMAINs.
- Critically: 12:07:57Z is only ~5m47s after the subtask finished (12:02:10Z) -- well BEFORE the un-renewed default 600s TTL window would even expire (12:12:10Z). So this is NOT a recurrence of the original #560 cluster-TTL-clock bug; something reaped the pod much earlier than even the un-patched baseline would predict.
- Corroborating signal: two other AgentHost pods (agent-00123ee8302d's pod xwlv8, which WAS legitimately orphaned with no active preview) died at the exact same 12:07:57Z timestamp in the same reaper sweep. A further pair of unrelated pods (bs4sd/qmv4z) died together ~4-6 min after their own creation in a later sweep. Node aks-katapool-28841148-vmss00000y also went NodeNotReady and was replaced by a new node (...000z) in this same window, though the actual pod-kill events were issued by kubelet on nodes that stayed Ready (...000x, ...000z), not the NotReady node.

Judgment call: I am NOT attempting a guess-fix. The evidence shows the #571 RBAC fix is holding (no 403s), so #570 stays closed. But #560 cannot be closed: the live preview does not actually survive past its creation for more than ~6 minutes in this environment, for a reason that is NOT the original ttlSecondsAfterFinished clock and NOT RBAC. Two live hypotheses (unconfirmed): (a) a cascading-delete interaction when the AgentHostReaperService's periodic sweep legitimately reaps one orphaned sibling claim in the same tick as a deferred/renewed claim, or (b) unrelated node/kubelet churn in this ephemeral staging cluster coincidentally killing multiple agent-host pods in the same ~1-2 min window regardless of claim state. Recommend: reopen/keep #560 open, do not mark "start_preview works" as met yet, and have a fresh investigation (ideally with more staging headroom / isolated node pool, and/or explicit instrumentation of DeleteNamespacedCustomObjectAsync call sites) determine which of these two hypotheses (or a third) is the real cause before attempting fix #4.

---

# Neo — approval watcher grace period

## Summary
Demo capture scripts should start a background approval watcher that auto-approves `Tool Approval Required` cards only after a short grace period, with a plan-level opt-out.

## Decision
Use a default grace period of **2250ms** before auto-clicking the approval action, and support `plan.disableApprovalWatcher` plus `plan.approvalWatcherGraceMs` in `renderCaptureScript()`.

## Rationale
Existing beats 2.5/4.5/2.6/4.6 already script approval moments intentionally, so an immediate auto-click would erase the narrator's chance to call out the human-in-the-loop gate. A ~2.25s delay is long enough to preserve that beat while still rescuing forgotten, early, late, or repeated approval cards before the preview times out. The opt-out keeps future captures free to hold a gate longer on purpose without editing the shared watcher logic.

## References
- scripts/demo-recording/lib/capture-plan.mjs
- scripts/demo-recording/plans/blueprint-demo-beats.md
- apps/web/src/components/AgentSessionPanel.tsx
- apps/web/src/pages/AssistantRunPage.tsx
- apps/web/src/components/LifecycleEventCard.tsx

---

## 2026-07-28T19-49-20: Blueprint-demo defect fix: reuse existing narration + per-beat segments; recapture only defective beats; fix pipeline cursor/zoom code
**By:** Neo
**What:** Blueprint-demo defect fix: reuse existing narration + per-beat segments; recapture only defective beats; fix pipeline cursor/zoom code
**References:** PR #613, issue #610, scripts/demo-recording/plans/blueprint-demo-beats.md, Trinity (azure-aks demo)
**Why:** Context: @sabbour reported 14 defects in the merged blueprint-demo video (PR #613). Root-cause analysis:

1. Narration synthesis (Azure AI) is unavailable in this environment (no AGENTWEAVER_DEMO_AI_ENDPOINT/KEY). However, NONE of the 14 defects require narration TEXT changes — the committed master beat narration in scripts/demo-recording/plans/blueprint-demo-beats.md already describes every behavior the user says is missing (skills marketplace/import/assign, steering, topology, preview approval, decisions, webhook). The defects are VIDEO/capture mismatches, not narration errors. Decision: reuse the existing per-beat narration WAVs (recordings/_scratch/audio/beat-*.wav) unchanged; do NOT attempt narration re-synthesis.

2. All 22 per-beat synced segments, raw captures, JSON capture plans, and narration WAVs already exist under recordings/_scratch/ (Morpheus's pipeline). Reassembly = replace ONLY the defective beats' synced segments and re-run `cli.mjs assemble-final`. Keep the already-good segments (1.1, 1.2 preamble aside, 2.2, 2.6, 2.9, 4.1-4.3, 4.6, 4.7, 5.1).

3. Pipeline CODE fixes (deterministic, unit-testable) go in scripts/demo-recording/lib/capture-plan.mjs (cursor must move AFTER the zoom transform settles + recompute post-transform box; defect #1) and zoom minimization (defects #4/#14 — make zoom opt-in per step / reduce default scale & movement). Duplicate-video (#2) is a plan-authoring overlap: beat 1-1 and 1-2 both re-run the create-from-github preamble.

4. Live cleanup (defects #6, #10): remove stray `bug-fix-copy` workflow and any duplicate work items from the Trailhead Getaway Planner project (e454eb56-8862-4bf2-bb3b-70fc897ef2f0) via API BEFORE recapture. The `AKS` project (60c3a36f...) belongs to Trinity's concurrent Azure/AKS demo — do not touch.

Target project for this demo: Trailhead Getaway Planner (source repo sabbour/agentweaver-demo-dryrun, blueprint-pm-and-software-development).

---

# Neo — Cluster page + heartbeat beat patch decisions

Date: 2026-07-29

## Summary

Patched both demo plans/videos to add Cluster coverage, and expanded blueprint Beat 3.1 to show the live heartbeat/pickup settings UI that backs the narration's configurable concurrency claim.

## Decisions

1. **Blueprint Cluster coverage stayed inside Beat 2.7 instead of introducing a non-parser-friendly `2.7b` heading.**
   The beat-plan parser only recognizes `## Beat X.Y` headings, so I extended Beat 2.7's narration and on-screen spec to move from Dashboard -> Observability/Traces -> Cluster in one continuous operational-health beat.

2. **Blueprint Beat 3.1 shows two UIs for the heartbeat claim: Heartbeat page + Board Pickup settings dialog.**
   The live background service status is on `/heartbeat`, while the configurable concurrency control itself lives in the Board's `Pickup settings` dialog as `Max Ready items per heartbeat`. Showing both made the narration factual on screen without inventing a settings surface that does not exist.

3. **Azure AKS got a new Beat 4.4 before the Outro.**
   This keeps the new Cluster page in the natural "real infrastructure behind the staging deployment" slot the requester asked for, immediately after the read-only proof and before wrap-up.

4. **The three Azure narration rewrites (0.1, 3.4, 4.3) were treated as picture-locked narration patches.**
   Their factual content changed only in tone, not in demonstrated product behavior, so I kept their existing visual story and patched those segments with regenerated narration rather than broadening scope into additional UI re-recording.

5. **Narration synthesis used Edge TTS (`en-US-AvaNeural`) as a fallback.**
   The documented Azure demo-recording synthesis path requires `AGENTWEAVER_DEMO_AI_ENDPOINT`/`AGENTWEAVER_DEMO_AI_KEY`, which were not present in this environment. To complete the requested narration rewrites, I generated the updated per-beat audio with Edge TTS and converted to WAV for the existing sync pipeline.

---

# Neo — Empty Decisions tab (defect #9) is a live-data gap, not a beat-script bug

**Date:** 2025 (blueprint-demo defect-fix pass)
**Author:** Neo (demo-recording specialist)
**Status:** Data gap — recapture-dependent

## Context

Blueprint-demo defect #9: beat 2.8 narrates the Decisions tab but it renders empty.

## Finding

`GET /api/projects/{proj}/decisions` returns `total_count: 0` for the target
Trailhead project, even though two "Collective assembly scribe" runs
(`a7671e4f…` for `bbce928d…`, `e4770e90…` for `246e641a…`) show as `completed`.
So Scribe passes ran but no accepted decision is persisted/queryable for this
project — the tab genuinely has nothing to show.

## Decision

- The beat's narration is correct; the defect is that **no real decision exists**
  to display. This is fixed at the data layer, not by re-scripting camera moves.
- Correct fix path for a clean capture: drive (or wait for) a run that completes a
  Scribe pass which records an accepted decision into the project's decisions
  store **before** beat 2.8 is captured, then verify `total_count > 0` via the API
  before rolling. If the decisions store can only be populated by a completing
  merge/scribe flow that isn't reconstructable in the capture window, beat 2.8
  should be re-captured opportunistically once a real decision lands rather than
  shown empty.
- Do **not** fabricate a decision purely for the camera if it wouldn't occur in a
  real run — that would misrepresent the product.

---

# Neo — No workflow DELETE API blocks stray-workflow cleanup (defect #10)

**Date:** 2025 (blueprint-demo defect-fix pass)
**Author:** Neo (demo-recording specialist)
**Status:** Product gap — flagged, not worked around

## Context

Blueprint-demo defect #10: a stray `Copy of Bug Fix` workflow (`bug-fix-copy`,
"Manual only", "Valid") is contaminating the target project's workflow list and,
worse, is **actively spawning junk runs** — the project's run list shows multiple
`in_progress` `Event run: Copy of Bug…` and `Scheduled run: Copy of Bug…`
executions ("use bug-fix-copy" tasks). These pollute the Orchestrations list that
several demo beats (3.1 schedule, 3.2 webhook) put on screen.

## Finding

There is **no way to delete a custom workflow via the public API or UI**:

- `apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs` exposes only
  GET / POST / PUT — no DELETE. PUT writes `.agentweaver/workflows/{id}.yaml` into
  the server workspace; there is no inverse.
- `WorkflowsPage.tsx` has no delete affordance.
- A custom workflow id is appended to the project's `allowed_workflow_ids` when
  created; only a blueprint re-apply resets that set. `DELETE /…/workflows/{id}`
  returns 405.

So the stray workflow (and the junk event/scheduled runs it keeps generating)
cannot be cleaned up with the tools available to a demo-capture agent.

## Decision

1. Do **not** hand-hack server files to remove it (out of scope, unsafe on shared
   staging).
2. Re-target the schedule beat (3.1) onto the legitimate delivery workflow rather
   than the stray copy, and avoid framing the polluted Orchestrations list.
3. Flag this as a **real product gap** for the team: custom workflows need a
   delete/disable endpoint + UI, and event/schedule triggers on a deleted/disabled
   workflow must stop firing. Tracks with `Tank-github-event-trigger-webhooks-never-fire-for-impor.md`.

---

# Blueprint-demo recapture — defect-fix judgment calls (Neo, 2026-07-29)

Branch: `neo/recapture-blueprint-demo`. Requested by @sabbour after his review of the merged
blueprint-demo video (PR #613) surfaced 14 concrete defects. Pipeline root-cause fixes shipped
separately as PR #614 (merged into `dev`); this round is the full live recapture + reassembly.
Recorded here per the AI-agent auditable-decisions process.

## 1. Live preview-approve gate (defects #7 / #13) — not stageable, shown as evidence instead

`start_preview` HITL approval was confirmed NOT a product bug (see `AgentPreviewGate.cs`). The
narrative goal for beats 2.5 / 4.5 was to click "Approve" on the preview HITL card on camera, then
show the rendered preview. In practice the preview sandbox on staging is time-limited and the
approve card is volatile, so a reliable on-camera "approve → live preview iframe renders" could not
be scripted deterministically.

- Beat 2.5 now ends on a cleanly **Completed** Software-Delivery run (all gates green, no
  "Preview Unavailable: approval timed out" error) — the literal defect the user flagged is gone.
- Beat 4.5 now shows the **real repaired code diff** for the tablet-banner bug (run `bbce928d`
  Changes tab → `styles.css` fix + `welcome-banner-tablet-overlap.spec.ts` regression test),
  which is stronger evidence of the fix than a preview iframe and never shows "Preview Unavailable".

Residual gap flagged: no live preview iframe render on camera. All runs launched with
auto-approve-tools ON except these two (per @sabbour's instruction).

## 2. Beat 2.3 steering (defect #5) — topology fixed; steering directive accepted but not rendered

Topology (#5b) is fully fixed: the beat holds a zoom-free full view showing the 10-node run tree,
the Topology mini-graph, and the enabled coordinator steering input. Node-click zooms were removed
because the topology graph is small and bottom-anchored, so node-click pans landed on blank space
(and nodes 0–2 sit under the left-nav rail, intercepting clicks).

Steering (#5a): a real `POST /api/runs/{id}/steer` directive is sent against an owned orchestration
and accepted (201), but the accepted directive is not rendered as an on-screen timeline card in the
brief capture window (the live "Message coordinator…" input only appears during volatile
`assembly_steering` windows). Directive is genuine, on-screen card is the residual gap.

## 3. Beat 3.1 stray `bug-fix-copy` workflow (defect #10) — no delete API, target by name

There is still no workflow-delete API/UI, so the stray "Copy of Bug Fix" (`bug-fix-copy`) duplicate
cannot be removed. Mitigation: the capture targets the intended `software-delivery-copy` workflow
**explicitly by name** (not a positional/first-item selector), so the stray duplicate is never
selected by accident. It remains visible-but-unused in the list (an accepted, non-hidden workaround).

## 4. Beats 2.6 / 4.6 (Approve & merge gate) — review bar is conditionally rendered

The "Approve & merge" action lives inside the ArtifactBrowser review bar, which only renders when
`runStatus === 'awaiting_review'` AND the live `orch.phase === 'in_review'` is actionable (see
`CoordinatorRunPage.tsx` `reviewActionable`). Several API-`awaiting_review` runs had already passed
their Human Review node, so the bar did not surface. Both beats were captured against an owned/pickup
run (`d5993132`) that had a genuinely open, actionable gate: 2.6 shows the diff review + Approve &
merge; 4.6 focuses on the Approve & merge action. Same run reused because only one open actionable
gate existed at capture time — acceptable since the review-gate UI is identical and the beats sit in
different acts.

## 5. Beat 2.1 (defect #4) — root cause was timing, not only the zoom

The "meaningless zoom around 00:27" had two causes: (a) a `scale: 1.15` zoom on the goal/scope panel,
and (b) the beat ended while the OutcomeSpec was still `Drafting`, so the panel was empty. Fix:
removed the zoom AND added an API-poll `eval` step that waits until the fresh run reaches
`awaiting_confirmation` before holding on the inline OutcomeSpec (Goal / Outcome / Scope /
Assumptions). This is why the eval-handler was upgraded (PR #614 area) to wrap snippets in an async
IIFE and accept a `code`/`expression` alias — several beats now need `await`-driven polling evals.

## 6. Beat 2.7 follow-up — transaction-trace view added (2026-07-29)

@sabbour asked to also show viewing an actual transaction trace in the observability beat,
not just the dashboard. Beat 2.7 now switches to Observability > Traces and clicks a run's
**Preview trace** button so `TransactionTracePanel` expands and renders the real agent/LLM/tool
span tree. Judgment calls:
- Targeted the trace by **run id** (246e641a, a completed 97-span run), not `.first()`, so a
  freshly-created run prepended to the list can't accidentally be opened (same name-targeting
  principle as defect #10).
- The panel briefly shows a "No trace data available" empty state while the async trace fetch
  resolves; changed the capture to wait on a span label ("Invoke Agent") before the final hold
  so the beat ENDS firmly on the loaded span tree, not the transient empty state.
- Extended the 2.7 narration ("Opening a run's transaction trace reveals the full distributed
  tree...") and regenerated only that beat's audio (Azure TTS, default Ava voice) so pacing
  stays audio-driven instead of leaving ~11s of silent tail. Single-beat recapture + reassemble
  (not a full re-record) since the pipeline supports partial recapture cleanly.

---

## 2026-07-27T00-28-22: Verified & documented playwright-cli + ui-harness auth bridge for demo recording (new skill: .github/skills/agentweaver-demo-recording)
**By:** Niobe
**What:** Verified & documented playwright-cli + ui-harness auth bridge for demo recording (new skill: .github/skills/agentweaver-demo-recording)
**References:** scripts/ui-harness/lib/auth.mjs, scripts/ui-harness/agent-driver-ui/tools.mjs, .copilot/skills/agentweaver-playwright-cli/SKILL.md, .github/skills/agentweaver-demo-recording/SKILL.md
**Why:** ## Context
playwright-cli (video recording, chapters, overlays) and the ui-harness's staging auth (scripts/ui-harness/lib/auth.mjs, .auth/staging.storageState.json + sessionStorage sidecar) were never connected. Agents kept hitting "no persisted authenticated session" when trying to record demo videos of the deployed staging app.

## What I verified interactively (not just documented theoretically)
1. `playwright-cli open --persistent --browser=msedge` works on Windows; `--browser=chrome` fails ("Chromium distribution 'chrome' is not found") unless Chrome happens to be installed.
2. `playwright-cli state-load scripts/ui-harness/.auth/staging.storageState.json` alone is **not** sufficient to authenticate into the Agentweaver app — the storageState file for this project only contains GitHub OAuth cookies (`origins: []`, no localStorage). Confirmed by navigating to the staging app after state-load: it showed the "Sign in with GitHub" page.
3. Agentweaver's own session lives entirely in `sessionStorage` (`agentweaver.sessionLogin`, `agentweaver.sessionToken`), captured separately by the ui-harness's `.sessionStorage.json` sidecar (auth.mjs already documents *why* — storageState() can't see sessionStorage).
4. `playwright-cli run-code` runs in a sandboxed vm context with **no `require` and no dynamic `import`** (both verified to throw) — so a run-code script cannot read the sidecar file from disk itself. The working pattern is to generate the seed script *outside* playwright-cli (PowerShell/Node), embedding the sessionStorage entries literally, then invoke it with `run-code --filename=... --raw`. The `--raw` flag is essential: it suppresses the "Ran Playwright code" echo section so the plaintext session token is never printed back into the calling agent's own conversation/logs.
5. After seeding sessionStorage for the matching origin and doing a `reload`, `playwright-cli snapshot` showed the fully authenticated Agentweaver Overview page (nav, logged-in username "sabbour", Projects/Sessions links) — confirmed against the live staging deployment.
6. Video recording mechanics (`video-start` → `video-chapter` → real click/nav → `video-chapter` → `video-stop`) were exercised against this authenticated session and produced a valid ~850KB non-empty `.webm` file. Deleted afterward as a throwaway test artifact (not committed).

## Design decision
Documented the recipe as a new skill at `.github/skills/agentweaver-demo-recording/SKILL.md`, cross-referencing (not duplicating) `.copilot/skills/agentweaver-playwright-cli/references/video-recording.md` for the video API and `scripts/ui-harness/SKILL.md`/README for the login/auth flow. Chose the "generate seed script outside the sandbox + run-code --filename + --raw" pattern over alternatives (inline run-code string, sessionstorage-set per key) because both alternatives echo the literal secret value back into command output/transcripts — a credential-exposure risk that --raw + generated-file avoids.

## Honest caveat
The sessionStorage bridging DOES work via playwright-cli's CLI surface — I did not need to fall back to scripting inside ui-harness's own tools.mjs. The only real friction is the run-code sandbox's lack of `require`/`import`, worked around by generating the script content outside the sandbox rather than having the script read the file itself.

---

## 2026-07-29T15:10: Fixed live staging authz config regression and stale auth-security deep-dive docs
**By:** Seraph
**What:** (1) Landed docs sync PR for the team-membership rule-list authz model shipped in PR #631 / v0.13.0 (deep-dive prose + fig3 diagram were still describing the old two-tier org+AllowedTeam model). (2) Fixed the real, confirmed live-staging regression where every deploy this session used `GITHUB_ALLOWED_ORG=microsoft` (the DEFAULTS fallback in scripts/azure/variables.mjs:60) instead of the user's requested `Azure/aks,Azure/AKS PM,azure-management-and-platforms/*`. (3) Persisted per-env params file so future deploys can't silently regress.
**References:** PR #640 (docs), PR #631 (feature, merged), CHANGELOG v0.13.0, scripts/azure/params.asabbour2.json (uncommitted, gitignored), docs/deep-dive/auth-security.md, docs/diagrams/src/auth-security-fig3.json

**Live cluster state (after fix):**
- kubectl context: `agwv2` (RG `asabbour2`, cluster `agwv2`, ACR `agwv2acr`, KV `agwv2kv`, sub `AKS INT/Staging Test` 26fe00f8-9173-4872-9134-bb1d2e00343a).
- Before: `kubectl get configmap agentweaver-runtime-config -n agentweaver -o jsonpath="{.data.GITHUB_ALLOWED_ORG}"` returned literal `microsoft`.
- After: same command now returns `Azure/aks,Azure/AKS PM,azure-management-and-platforms/*`.
- Live API pod env verified via `kubectl exec ... -- printenv`: `Auth__GitHub__AllowedOrg=Azure/aks,Azure/AKS PM,azure-management-and-platforms/*`.
- Post-fix rollout: api/frontend/mcp/worker deployments restarted to pick up the new ConfigMap (they were 4h6m old and cached the pre-fix value in memory). Frontend `/` and API `/api/health` both return HTTP 200 after rollout.

**Deploy mechanics:**
- Ran `node scripts/azure/cli.mjs deploy-from-release v0.13.0` from a pristine sibling clone at `C:\Users\asabbour\Git\agentweaver-deploy-clean` because the in-place repo tripped the known-stale allowlist bug in `release-publish.mjs`'s `isWorkingTreeClean` (documented earlier this month by Link — allowlist is missing `node_modules/`, `bin/`, `obj/`, `dist/`, harness output dirs). Kept scope tight by using the same workaround Link used rather than patching the allowlist inside this task.
- Provenance verification at the tail of deploy-from-release reported 3 unverified images. Root cause: the manifests were rendered against configmap-referenced env vars, so the api Deployment came out `unchanged` — no rollout was triggered by the initial `kubectl apply`, and the still-running pods carried old digests when the provenance script snapshotted them. Follow-up `kubectl rollout restart` (needed anyway to pick up the ConfigMap change) resolved this: the new pods are on the freshly-pushed `v0.13.0` digests. Not a security regression from this change; the provenance script is fine.

**Team-slug confirmation (`Azure/AKS PM`):**
- `gh api orgs/Azure/teams --paginate --jq '.[] | select(.name | test("AKS PM|aks-pm"; "i")) | {name, slug}'` returned exactly `{"name":"AKS PM","slug":"aks-pm"}`. Real GitHub slug is `aks-pm`, which is exactly what the defensive slugifier (lowercase + space-to-hyphen) produces for the config literal `Azure/AKS PM`. No config change needed.

**Persistence:**
- Created `scripts/azure/params.asabbour2.json` (uncommitted, verified gitignored via `git check-ignore`; the `.gitignore` rule `scripts/azure/params.*.json` already existed with `!scripts/azure/params.example.json` exception -- no gitignore change required) recording RESOURCE_GROUP=asabbour2, CLUSTER_NAME=agwv2, ACR_NAME=agwv2acr, KEYVAULT_NAME=agwv2kv, NAMESPACE=agentweaver, GITHUB_ALLOWED_ORG=`Azure/aks,Azure/AKS PM,azure-management-and-platforms/*`. Note: `deploy-from-release.mjs`'s `parseArgs` currently only accepts a tag + `--dry-run` (no `--params-file` support), so env vars are still required for that subcommand -- the params file also serves as the canonical shape+values recipe/comment block for the next operator. `provision-infra` does honor `--params-file`.

**Docs PR (do not merge):**
- https://github.com/sabbour/agentweaver/pull/640 -- rewrites the intro to "GitHub org authorization and the SAML nuance" (rule syntax, defensive slug normalization), JWT fast-path description (rule-string claim + anti-grandfathering), "Result semantics" (per-rule signals + aggregation precedence), and "Invariants to preserve" (rule-string JWT, team-scoped rules have no unauthenticated public-membership fallback, `AllowedTeam` is a deprecated OR'd-in shim). Regenerated `docs/diagrams/auth-security-fig3.png` from updated JSON source via `npm run docs:render-diagrams`. Explicitly opened against `dev` with a "second pair of eyes" callout.

**Not touched:** `docs/guide/configuration.md` was already accurate for the new rule-list format (confirmed by coordinator); left alone.

**Why:** Ahmed's real target rules (`Azure/aks,Azure/AKS PM,azure-management-and-platforms/*`) had never been present on the live staging cluster despite v0.13.0 shipping the code that supports them -- the deploy pipeline silently defaulted to `microsoft` on every `deploy-from-release` call because no env var was set and no persisted params file existed. Feature and docs are now in sync with the code, and the runtime config finally matches the user's stated intent.

---

# Security Assessment — Native `bash` denial vs. `run_command` routing

**Author:** Seraph (Security Reviewer)
**Requested by:** Ahmed (@sabbour) — "think deeply about it"
**Date:** 2026-07-27
**Scope:** The recurring wasted round-trip where the model calls the SDK native shell,
gets `"Native Copilot shell is disabled; use the sandboxed run_command tool (routed
through the sandbox executor)"`, then retries the identical command via `run_command`.

Enforcement points reviewed:
- `packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:1539` (permission handler denial)
- `packages/Agentweaver.AgentRuntime/GitHubCopilotAgentRunner.cs:539` (permission handler denial)
- `tests/Agentweaver.Tests/Sandbox/AssemblyBuildTestShellGuardTests.cs:64`

---

## Question 1 — Can prompt/tool guidance prevent the wasted first attempt?

**Verdict: YES. Root cause confirmed; safe scoped fix implemented.**

### Evidence
- The SDK always advertises its **native shell tool** to the model. The runtime does not
  (and, as far as the SessionConfig surface exposes, *cannot*) unregister it; instead both
  runners intercept `PermissionRequestShell` in `BuildPermissionHandler` and `Reject(...)`
  it. So the model sees a native `bash`/shell tool, naturally reaches for it first, is
  denied, and only then falls back to `run_command`. That is one guaranteed wasted
  tool-calling turn per shell-first attempt.
- The base system prompt (`packages/Agentweaver.AgentRuntime/AgentBasePrompt.cs`,
  `AgentBasePrompt.Base`) is injected into **both** runners
  (`CopilotAIAgent.RebuildInnerAgent` and `GitHubCopilotAgentRunner` system message).
  Before this change it mentioned "shell access" and "shell command" generically but
  **never named `run_command`, never said the native shell is disabled, and never told the
  model to skip the native shell**. Nothing steered the model away from the doomed first
  attempt.

### Fix implemented
Added a **SHELL COMMANDS — ALWAYS USE run_command** section to `AgentBasePrompt.Base`. It:
- tells the model to run every shell command through `run_command`;
- states the native bash/sh/shell tool is **permanently disabled** and quotes the exact
  runtime denial string so the model recognizes and preempts it;
- says explicitly *"Do NOT attempt the native shell first and wait for it to fail — go
  straight to run_command"*;
- includes the fail-closed caveat (mirroring the #268 team-coordination-prompt lesson): if
  `run_command` is not in the tool list, the run has no shell — do not call any shell tool.

Placing it in `AgentBasePrompt.Base` covers **both** runners with one change and matches
how the existing WORKSPACE/SANDBOX guidance is already unconditional in `Base` (the base
prompt already assumes shell exists, so naming `run_command` introduces no new hallucination
surface beyond what was already there).

### Why this is safe
- Prompt-only + a new test. No change to the enforcement/deny path, tool registration, or
  sandbox boundary. The denial remains as belt-and-suspenders if the model ignores guidance.
- Regression tests (`tests/Agentweaver.Tests/Sandbox/ShellRoutingPromptGuidanceTests.cs`)
  lock the prompt to (a) name `run_command`, (b) say the native shell is disabled, (c) tell
  the model not to try it first, and (d) keep the prompt's quoted denial string byte-identical
  to the handler's, so the two cannot drift.
- **Residual:** this reduces (not provably eliminates) the wasted attempt — it is a
  behavioral nudge to a probabilistic model. It cannot make the round-trip impossible while
  the SDK keeps advertising the native shell. A future hard fix would be an SDK-level switch
  to not register the native shell tool at all; that is out of scope here and unavailable
  through the current SessionConfig.

---

## Question 2 — Is `run_command`'s sandbox routing a real privilege boundary, or theater?

**Verdict: GENUINE, MATERIAL security boundary. NOT equivalent-privilege theater.
The block must stay.**

`run_command` is strictly more confined than the native shell would be, on multiple
independent axes. Evidence, by code path actually compared:

### What the native shell would get (if unblocked)
Per the handler comments at both enforcement points, the SDK native shell **executes
in-process** and never routes through `ISandboxExecutor`/bubblewrap. The permission gate
validates only the *declared working directory* — **not** the command text, embedded
absolute paths, or per-command filesystem confinement. In-process = the full privileges of
the agent host process (pod filesystem, KV-injected GitHub token at `/etc/...`, sibling
runs on the shared `/workspace` PVC, `/proc`, network egress).

### What `run_command` actually enforces (`packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs`)
Before anything executes, in order:
1. **Backgrounding rejection** — `ContainsBackgrounding` blocks `&`, `nohup`, `setsid`,
   `disown` (quote/escape-aware).
2. **Destructive-pattern rejection** — policy patterns (e.g. `rm -rf`), whitespace-normalized.
3. **HITL approval gate** — destructive / all-shell commands require an operator approval
   (`shell.approval_required` + `POST /api/runs/{id}/shell-approvals`), with per-command-hash
   deny/approve memory. The native shell has no such gate.
4. **`ShellCommandValidator.Validate`** (`packages/Agentweaver.SandboxExec/ShellCommandValidator.cs`):
   working-dir containment via `SandboxPathValidator.ValidateAbsoluteContained` (issue #581),
   64 KB command cap, null-byte rejection, and the **`SharedWorkspacePathGuard` cross-run
   mount-escape check (#476)** that inspects *absolute paths inside the command text* (e.g.
   `git -C /workspace/<other-project>`) — exactly the class of attack the native shell's
   working-dir-only check cannot see.
5. **`SandboxFsPolicyBuilder.Build`** — computes per-command RW/RO roots (workspace +
   scratch only) passed to the executor.
6. **`ISandboxExecutor.ExecuteAsync`** — the real isolation boundary:
   - `LinuxBwrapExecutor` (`IsRealIsolation => true`,
     `packages/Agentweaver.SandboxExec/LinuxBwrapExecutor.cs`): bubblewrap with a **selective
     mount allowlist** — `--bind {wd} /workspace` (only the run's own worktree is RW),
     `/usr/*` RO, tmpfs `/tmp` `/home` `/root`, `--unshare-pid`, `--unshare-user`,
     `--unshare-net` by default (network only when policy enables it), `--new-session`, plus
     host-worktree-path redaction. The rest of the host FS is simply **not mounted** →
     invisible/inaccessible.
   - Production (`apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs`, selected by
     `SandboxExecutorRouter`): dispatches the command into a **separate claim-bound AgentHost
     pod** (`SandboxClaim` → bound pod), with workspace-path validation and timeout clamped
     below the claim TTL — a cross-pod boundary, not the API/worker process.
7. **Resource limits** — caller `timeout_ms` floored to policy min and capped to policy max;
   `ShellExecutionTracker` watchdog deadline (executor timeout + grace); stdout capped at
   4 MiB, stderr at 1 MiB.
8. **Output redaction** — `SandboxOutputRedactor.Redact` on stdout/stderr (plus host-path
   redaction in bwrap). The native shell result is not run through this.

### Gating that reinforces the boundary
`run_command` is registered **only** when `executor.IsRealIsolation || BackendName=="direct"`
**and** `policy.ShellEnabled` (`SandboxToolRegistry.Build`; `SandboxGovernance` denies shell
when `IsRealIsolation == false`). So the tool exists precisely where a real isolation backend
(or an explicitly-accepted direct mode) backs it. Removing the native-shell block would
*re-introduce* an in-process shell that bypasses every layer above — a strict privilege
expansion.

### Conclusion
This is not a naming/routing convention over the same OS process. `run_command` runs under a
different confinement (bubblewrap namespaces on native Linux; a separate claim-bound pod in
K8s), goes through command-text path validation (`SandboxPathValidator` + cross-run
`SharedWorkspacePathGuard`), enforces resource limits, adds a HITL gate for
destructive/all-shell commands, and redacts output — none of which the in-process native
shell would have. **The block is a real security control. Do NOT weaken or remove it.**

---

## Recommendations
1. **Ship the Q1 prompt/tests fix** (PR against `dev`) — safe, reduces the wasted round-trip.
2. **Keep the `run_command` routing/block exactly as-is** — Q2 shows it is a genuine boundary.
3. **Follow-up (not in this change):** investigate whether the Copilot SDK exposes any switch
   to omit the native shell tool from the advertised catalog. If so, that would eliminate
   (rather than merely discourage) the wasted attempt. Until then, prompt guidance is the
   correct, scoped mitigation.

---

## Addendum — 2026-07-29 mode-switch clarification

Revising finding #3 in light of the explicit product directive:

- Supporting **both** auth modes as a **deployment-level, mutually exclusive configuration** (`Auth:Mode = Entra | GitHubLegacy`) is acceptable from a security-boundary perspective. The original High risk was about **simultaneous mixed-mode acceptance during migration**, not about retaining the legacy mode as an optional product capability.
- The non-optional requirement is now: **exactly one auth mode may be active per running instance**, with startup validation that rejects ambiguous/mixed configuration.
- **Any mode transition on an existing deployment must invalidate all existing sessions/tokens/cookies immediately** and require fresh authentication under the newly active mode.
- Long-term, the legacy mode should be treated as **security debt**: supported with a prominent warning is acceptable for now, but it should remain capability-constrained and re-reviewed periodically because it preserves the older GitHub-token/org-membership trust model.


## Post-implementation review addendum — blocking Tier-2 RBAC finding (2026-07-30)

**BLOCKING — last-owner guard is race-bypassable.**

`ProjectRoleAssignmentService` enforces the "cannot remove the last explicit Owner" rule with a read-check-write sequence (`GetAsync`/`ListByProjectAsync` → `DeleteAsync` or `UpsertAsync`) but without any transaction, compare-and-swap predicate, or DB constraint tying the guard to the final write. Two concurrent owner removals/demotions can both observe another owner still present and both commit, leaving zero explicit owners.

Relevant code paths:
- `apps/Agentweaver.Api/Auth/ProjectRoleAssignmentService.cs` lines 45-62, 75-89
- backends perform unconditional upsert/delete after the pre-check:
  - `apps/Agentweaver.Api/Infrastructure/Ef/EfProjectRoleAssignmentStore.cs`
  - `apps/Agentweaver.Api/Infrastructure/SqliteProjectRoleAssignmentStore.cs`

Required fix direction: make the last-owner invariant atomic at write time (transaction with serializable/retry semantics, conditional delete/update that proves another owner still exists, or a stronger DB-backed invariant). Treat this as blocking for shipping Tier-2 RBAC because it defeats the explicit recovery guarantee under concurrency.

---

## 2026-07-29T19-53-32: Issue #641 security blockers: keep comment text private, make commentMatches ReDoS-safe, keep predicate eval post-HMAC, and request write:repo_hook only on explicit click
**By:** Seraph
**What:** Issue #641 security blockers: keep comment text private, make commentMatches ReDoS-safe, keep predicate eval post-HMAC, and request write:repo_hook only on explicit click
**References:** github-issue-641, apps/Agentweaver.Api/Webhooks/GitHubWebhookPayload.cs, apps/Agentweaver.Api/Workflows/WorkflowEventTriggerService.cs, apps/Agentweaver.Api/Endpoints/GitHubWebhookEndpoints.cs, apps/Agentweaver.Api/Auth/GitHubOAuthRedirectService.cs, tests/Agentweaver.Tests/Webhooks/GitHubWebhookEndpointsTests.cs
**Why:** For issue #641, the design is shippable only if four requirements stay non-optional:

1) Raw GitHub issue/comment/review body text must remain inside a tiny post-HMAC matcher boundary. Today `GitHubWebhookPayload` models only `action` and `repository.full_name`, `WorkflowEventTriggerService` explicitly says it never receives or interprets raw issue/PR/comment body text, and `GitHubWebhookEndpointsTests.MaliciousIssueContentInPayload_DoesNotReachFiredTask` codifies that trust boundary. The new `commentMatches` path must preserve it: only a boolean match result may escape. Logging/persisting the body, logging the matched substring, copying the body into backlog/run context, or forwarding it to any LLM prompt is release-blocking.

2) `commentMatches` must be ReDoS-safe. Shipping user-authored regex over untrusted webhook text without a safe engine or hard timeout is a blocker. Prefer .NET's `RegexOptions.NonBacktracking` with an explicit timeout and config-time validation that rejects unsupported/unsafe constructs; otherwise use a guaranteed-safe engine such as RE2. Runtime timeout/failure must fail closed and emit only sanitized diagnostics.

3) Predicate evaluation must remain strictly after `GitHubWebhookSignatureVerifier.Verify` in the project-scoped webhook path. Do not move comment/body parsing or predicate evaluation into unauthenticated middleware or into a generic pre-verification path. The authenticated manual `/workflow-events` endpoint should remain event-name-only and must not become an alternate raw-GitHub-payload ingestion surface.

4) Incremental `write:repo_hook` must be a per-request, user-consented scope upgrade only when the operator clicks the auto-create button. `GitHubOAuthRedirectService` currently builds authorize URLs from a fixed configured scope string, so implementation must thread requested scopes/intents through state or a dedicated endpoint instead of broadening the default sign-in scope globally. A denied/cancelled upgrade must leave the existing token/state intact and cleanly return the user to the manual webhook flow.

---

## 2026-07-31T00-03-42: Publishing apps: split push/pull identities, no ASO in the workload cluster, published apps get zero cluster credentials
**By:** Seraph
**What:** Publishing apps: split push/pull identities, no ASO in the workload cluster, published apps get zero cluster credentials
**References:** #582 build-images-with-rootless-buildkit, #21 preview-sandbox-apps, #20 isolate-agent-workspaces, #19 govern-agent-tools-and-questions, #2 sign-in-and-carry-identity, #37 self-host-agentweaver, k8s/base/rbac-api.yaml, k8s/base/serviceaccount-agenthost.yaml, k8s/base/networkpolicy-sandbox.yaml, k8s/base/vap-sandbox-exec.yaml, scripts/azure/steps/15-setup-identity.mjs, scripts/azure/steps/10-create-cluster.mjs
**Why:** Security verdict on "Publish apps from Agentweaver" (long-lived hosting of agent-generated apps on the Agentweaver cluster). Design-stage findings; three items are BLOCKING for phase 1.

## Core threat
Publishing converts "untrusted LLM-generated code runs for one bounded turn behind a kata boundary" into "untrusted LLM-generated code runs indefinitely, unattended, as a network-reachable long-lived workload, in the same cluster as the Agentweaver control plane, its Key Vault-privileged identities, and every other tenant's project data". Every existing sandbox control (SandboxClaim TTL, AgentHostReaperService, per-run token brokering, kata runtimeClass) is run-scoped and does NOT transfer to a published workload.

## BLOCK-level requirements (must exist before the first publish ships)

1. **Split push and pull. Different identities, different scopes.**
   - The builder (BuildKit) identity gets PUSH to exactly one repository path (`published/{projectId}/{appId}`) via a short-lived, repository-scoped ACR token; never registry-wide, never `AcrPush` on the registry.
   - The published workload gets PULL only, and only for its own repository path.
   - Today `scripts/azure/steps/10-create-cluster.mjs:191` uses `--attach-acr`, which grants the kubelet identity registry-wide `AcrPull`. That is acceptable for platform images but must NOT be the pull path for generated images. Generated images belong in a SEPARATE registry (or at minimum a separate registry with its own scope maps), never co-resident with `agentweaver-api/-mcp/-agent-host` under one blanket pull grant, because a compromised published pod that can pull platform images gains offline access to platform code and config baked into layers.

2. **A published pod gets zero cluster and zero cloud credentials.**
   - `automountServiceAccountToken: false` (the agent-host template sets it to `true` for CSI workload identity — published apps must not inherit that pattern).
   - No `azure.workload.identity/use: "true"` label, no federated credential, no Key Vault access, no CSI SecretProviderClass mount by default.
   - Default-deny egress with an explicit block of 169.254.0.0/16 (IMDS) and all RFC1918 — reuse the CIDR-except pattern already proven in `k8s/base/networkpolicy-sandbox.yaml`, and note the recorded finding there that Cilium `toFQDNs` does NOT enforce for kata pods, so IP/CIDR rules are the only enforceable control.
   - No network path to `agentweaver-api`, `agentweaver-mcp`, or Postgres. Ingress only from the publish gateway.

3. **The Agentweaver API must never act as a confused deputy for the publish decision.**
   The API identity is Key Vault-privileged (`Key Vault Secrets Officer` on the vault, per `scripts/azure/steps/15-setup-identity.mjs`). The publish request must be authorized against the human caller's project role (Tier-2 `Owner`/`Contributor` via `ProjectAuthorization`) BEFORE any registry credential is minted, and the internal-service key identity (`ProjectAuthorization.InternalServiceUser`) must NOT be able to trigger a publish. An agent may REQUEST publish; only a human may APPROVE it (reuse the #19 approval gate). Publishing is an irreversible, externally-visible action under Constitution IX.

## Rejected: Azure Service Operator in this cluster
ASO's controller holds broad Azure ARM write permissions (typically Contributor at subscription or RG scope) and reconciles from CRs in-cluster. Running that controller on the same cluster that hosts arbitrary generated workloads means any container escape, any RBAC misconfiguration, or any path that lets generated content create/patch an ASO CR converts into arbitrary Azure resource creation in the subscription — including new identities and role assignments if `authorization` types are enabled. That is an unacceptable escalation ceiling for the stated benefit (creating an ACR / registry token). Verdict: **do not deploy ASO in the workload cluster for phase 1.** Provision registry and namespace infrastructure from the existing Node Azure toolchain (`scripts/azure/steps/*`), which runs out-of-band under an operator identity, not from a cluster-resident controller. If ASO is later wanted, it belongs on a separate management cluster with no untrusted workloads, scoped to a single resource group, with `crdPattern` limited to the exact resource types needed and `authorization.azure.com/*` explicitly excluded.

## Other durable positions
- **Published namespace posture:** one namespace per project (`aw-published-{projectId}`), `pod-security.kubernetes.io/enforce: restricted` (not `baseline` — the platform namespace is baseline only because API/worker specs don't meet restricted; a new namespace has no such legacy), ResourceQuota with hard CPU/memory caps (unlike `k8s/base/quota.yaml`, which deliberately dropped compute caps — that rationale does not apply to untrusted long-lived workloads and cryptomining is the expected abuse), and `runtimeClassName: kata-vm-isolation` on a tainted node pool separate from the control plane.
- **Secrets for published apps:** never baked into the image, never delivered through the agent's context. A human sets them post-publish via a platform surface; they land as a namespace-scoped Secret the agent never reads back. The build must run with no secrets mounted so a prompt-injected `RUN` cannot exfiltrate them, and build logs must be treated as attacker-controlled output.
- **Image provenance:** deploy by digest, not tag, and record the builder run id, project, approving human, and source commit as image annotations so any published workload is attributable.
- **Blast-radius review of buildx driver RBAC (#582) is a prerequisite.** Publish depends on the BuildKit design landing with its own namespace and service account; the API must not gain pod-create in the buildkit namespace, and the existing `vap-sandbox-exec.yaml` VAP name-prefix restriction must be extended so no identity can exec into BuildKit or published pods.

## Deferred to phase 2 (acceptable to ship without)
Registry build cache, custom domains/BYO TLS, image signing + admission verification (cosign/Ratify), egress allowlisting beyond default-deny, per-app autoscaling, gVisor as an alternative to kata, automated vulnerability scanning gates.

---

# Seraph — Team-membership authorization

**Date:** 2026-07-29
**Author:** Seraph (Security Reviewer)
**Requested by:** @sabbour
**Branch:** `seraph/team-membership-authz`

## Feature

Switch authorization from a single-tier org allowlist to a mixed **rule** list, OR'd
across the list. Each rule is one of:

- `org` — bare org name; satisfied by ANY-form (private OR public) org membership.
- `org/*` — explicit wildcard; canonicalized to bare org (identical semantics).
- `org/team-slug` — satisfied only by that specific team's membership.

A caller is allowed if they satisfy **any one** rule. This generalizes and replaces
the previous two-tier `AllowedOrg` (OR list) + `AllowedTeam` (single AND restriction)
model.

Example config (from the user's request):

```
Auth:GitHub:AllowedOrg = "Azure/aks,Azure/AKS PM,azure-management-and-platforms/*"
```

## Key design decisions

### 1. Config format — reuse existing `Auth:GitHub:AllowedOrg` key

We generalize the existing key rather than introduce a new one. It's already wired
end-to-end (env var `GITHUB_ALLOWED_ORG` → k8s `Auth__GitHub__AllowedOrg` → service),
and mixing `org` / `org/*` / `org/team-slug` in one delimited list is the smallest
UX change. Bare-org configs behave identically to today.

Legacy `Auth:GitHub:AllowedTeam` (never wired to deployment) is kept as a compat
shim: if set, its `org/team-slug` value is **appended as an additional OR'd rule**,
with a deprecation warning asking the operator to move it into `AllowedOrg`. This
intentionally changes its semantic from AND-restriction to OR-rule — the AND
semantics were part of the two-tier model the user is explicitly replacing.

### 2. Team-slug normalization

GitHub team endpoints require the slug (lowercase, hyphenated), not the display
name. If a configured team-part contains an uppercase letter or a space (e.g.
`Azure/AKS PM`), we defensively slugify (`lowercase`, `space → hyphen`) at parse
time and log a warning noting the raw form and the slugified form we will use.
The user's example `Azure/AKS PM` will hit `/orgs/Azure/teams/aks-pm/memberships/…`.

### 3. JWT fast-path — matched rule stamped into the `org` claim

The Agentweaver-minted access-token `org` claim now carries the **matched rule
string** (`"org"` or `"org/team-slug"`, canonical lowercased form) rather than
just an org name. `GitHubOrgAuthorizationMiddleware`'s fast-path parses that
claim back into an entity and compares (case-insensitively) against the current
`AllowedEntities`. Consequences:

- A JWT minted under a team-scoped rule can only satisfy a matching team-scoped
  rule in the current allowlist.
- Legacy JWTs (minted before this change) carry a bare org name; that parses as
  a bare-org entity and satisfies only bare-org entities in the current list —
  no accidental team-scope satisfaction across a config demotion.

This design avoids adding a new JWT claim (and any storage-side schema change)
while preserving fail-closed semantics on config demotions.

### 4. No EF migrations

Rather than persisting the matched rule on `McpAuthorizationCode` /
`McpRefreshToken`, we re-derive it at each token-endpoint mint via a new
`IGitHubOrgAuthorizationService.ResolveAsync(...)` method that returns
`(OrgAuthResult, AllowedGitHubEntity?)`. It is served from the same 5-minute
in-process cache the broker just populated, so this adds no real perf cost.
`CheckMembershipAsync` remains as a thin wrapper for callers that only need the
pass/fail signal (the middleware slow path).

### 5. Aggregation precedence preserved

The SAML-enforced > Inconclusive > Denied precedence from PR #464 is preserved
per-rule and aggregated across the rule list. Team-scoped rules contribute their
own SAML-enforcement (403 on the team endpoint) and inconclusive (401/5xx)
signals to the aggregate exactly like bare-org rules did.

## Reviewer callout

Security-critical. Please verify:

1. Fast-path cannot accept a bare-org JWT against a team-scoped-only allowlist
   (and vice versa).
2. Slugification never sends an un-slugified team name to the GitHub API (which
   would return 404 and look like a not-a-member answer).
3. Aggregation precedence across mixed bare-org and team-scoped rules matches
   PR #464 semantics.
4. Legacy `AllowedTeam` shim behavior — semantic changed from AND to OR.

**Do not merge without a second reviewer.** The Coordinator will route this PR
for an additional review pass before merge.

---

# Smith API harness findings for issue 641

Date: 2026-07-29
Branch: `squad/641-trigger-backend`
Worktree: `C:\Users\asabbour\Git\agentweaver\.worktrees\641-trigger-backend`
Harness evidence:
- Verdict: `scripts/api-harness/verdicts/smith-641-api-harness-20260729T205338Z.json`
- Transcript: `scripts/api-harness/transcripts/smith-641-api-harness-20260729T205338Z.jsonl`

## Summary
API harness-style validation against a local API instance built from `squad/641-trigger-backend` found two real backend bugs:

1. **PATCH trigger route missing**
   - `PATCH /api/projects/{projectId}/workflows/{workflowId}/trigger` returned HTTP 405.
   - Live OpenAPI for `/api/projects/{projectId}/workflows/{workflowId}/trigger` exposed only `get`, `put`, and `delete`.
   - This blocks the advertised CRUD surface from supporting partial updates.

2. **NOT predicates break when persisted through trigger CRUD**
   - `PUT` accepted a NOT predicate trigger, but subsequent `GET /trigger` read it back as plain `hasLabel` instead of `{ not: { hasLabel: ... } }`.
   - Raw YAML after save was:

```yaml
trigger:
  type: event
  event_name: github.issues.opened
  if:
    - not:
      has_label: { label: blocked }
```

   - That indentation causes the persisted trigger to drop the `not` wrapper on reload.
   - Matching webhook evidence: payload with label `bug` should have fired the NOT trigger but returned `fired_workflow_ids: []`.
   - This also caused the combined AND/OR/NOT webhook case to fail.

## What passed
- Schedule trigger PUT/GET/DELETE regression passed.
- Event trigger CRUD + webhook evaluation passed for `hasLabel`, `isNotLabeledWith`, `baseBranch`, `reviewState`, `ref`, `category`, `commentMatches`, and `or`.
- Validation/error paths passed: malformed payloads returned clear HTTP 400s, including unsupported predicate/event combinations and unsafe `commentMatches` regex.
- Targeted existing tests still passed:
  - `dotnet test tests\Agentweaver.Tests\Agentweaver.Tests.csproj --filter "FullyQualifiedName~GitHubWebhookEndpointsTests|FullyQualifiedName~WorkflowEventTriggerServiceTests" -p:CopilotSkipCliDownload=true`

## Recommendation
Fix the missing PATCH endpoint and the NOT-predicate YAML serialization/round-trip bug before merging the trigger CRUD surface.

---

# Smith — Entra phase 2 test scaffolding update

Date: 2026-07-29

## What I added

### New test files
- `tests/Agentweaver.Tests/Auth/AuthModeSwitchTests.cs`
  - live unit coverage for `AuthModeResolver` default (`Entra`) and explicit `GitHubLegacy`
  - live integration regression proving `GitHubLegacy` mode still preserves current Project.Owner-based authorization behavior
  - skip-marked placeholders for pending Entra-only exclusivity / informational exposure assertions

- `tests/Agentweaver.Tests/Auth/PlatformRolePolicyTests.cs`
  - live unit coverage for `PlatformRoles.FilterRecognized`
  - live unit coverage proving `PlatformRoleAuthorizationHandler` succeeds for recognized platform roles
  - live unit coverage proving unrecognized roles do **not** satisfy the requirement
  - live unit coverage proving the internal service principal bypass still works
  - skip-marked endpoint-matrix policy tests pending Tank's finer-grained requirements beyond baseline `PlatformAccess`

- `tests/Agentweaver.Tests/Auth/ProjectRoleAssignmentTests.cs`
  - scaffolded xUnit contract tests for project role-assignment CRUD / escalation / last-owner behavior, skip-marked until Tank lands the actual store/endpoints

- `tests/Agentweaver.Tests/Auth/MultiIdentityGitHubTokenStoreExtendedTests.cs`
  - live `InMemoryGitHubTokenStore` coverage for explicit default switching without token loss
  - live `InMemoryGitHubTokenStore` coverage for linked-identity isolation per Entra user
  - live `KeyVaultGitHubTokenStore` coverage for persisted default switching in the linked-identity index
  - live `KeyVaultGitHubTokenStore` coverage for unlink-default reassignment staying scoped to the same Entra user
  - skip-marked future tests for per-project override resolution, cross-user linked-login uniqueness, and Copilot entitlement probe wiring

## Coupled fixes needed for tests to compile/run
Because Tank's Entra auth work is actively landing in parallel, the new/updated auth code needed a few direct compile/runtime fixes so the test project could execute:

1. `apps/Agentweaver.Api/Auth/PlatformRoleAuthorization.cs`
   - added missing `System.Security.Claims` import
   - tightened requirement success from "any role claim" to **recognized Agentweaver platform roles only**

2. `apps/Agentweaver.Api/Program.cs`
   - added missing `Microsoft.AspNetCore.Authorization` import so `IAuthorizationHandler` registration compiles

3. `tests/Agentweaver.Tests/Auth/AuthModeSwitchTests.cs`
   - the legacy-mode factory now registers `EntraAccessTokenValidator` so the app can boot after Tank's middleware constructor change

## Validation run
Targeted validation passed:
- `dotnet test tests/Agentweaver.Tests/Agentweaver.Tests.csproj --filter "FullyQualifiedName~AuthModeSwitchTests|FullyQualifiedName~PlatformRolePolicyTests|FullyQualifiedName~ProjectRoleAssignmentTests|FullyQualifiedName~MultiIdentityGitHubTokenStoreExtendedTests|FullyQualifiedName~EntraAuthModeTests" -p:CopilotSkipCliDownload=true`

Result at time of writing:
- **Passed:** 18
- **Skipped:** 15
- **Failed:** 0

## Current gaps / follow-ups for Tank
1. Tier-2 project RBAC endpoints/store/service are still not present in the visible code yet, so CRUD/escalation tests remain scaffolded and skipped.
2. Per-project GitHub identity override resolution has not landed yet, so precedence-order tests remain skipped.
3. Cross-user uniqueness enforcement for linked GitHub logins is not yet implemented at the visible store/service layer; that test is scaffolded and skipped.
4. Baseline Entra platform-access coverage now exists, but the endpoint-specific policy matrix (`PlatformAdmin` vs `ProjectCreator` vs `Contributor` vs `Viewer`) still needs the actual authorization requirements/endpoints to be wired before Smith can unskip those assertions.

---

## 2026-07-29T19-49-39: Event-trigger regex matching must be ReDoS-safe and comment-body private
**By:** Smith
**What:** Event-trigger regex matching must be ReDoS-safe and comment-body private
**References:** github-issue-641, issuecomment-5122693520
**Why:** For issue #641, `commentMatches` is acceptable only if it is both ReDoS-safe and privacy-preserving. The implementation must either reject unsafe regex patterns at validation time or evaluate them with a guaranteed safe engine/time bound so a user-authored pattern cannot stall webhook processing; this is a hard requirement, not an optimization. Separately, raw GitHub comment bodies must never be logged, telemetered, persisted, or forwarded to prompts as part of trigger evaluation — only the boolean match result may escape the matcher. QA will treat either condition as a release-blocking failure.

---

## 2026-07-30T06-23-37: Scenario 1, 2, and 3 demo scripts locked for recording
**By:** Squad-Coordinator
**What:** Scenario 1, 2, and 3 demo scripts locked for recording
**References:** scripts/demo-recording/plans/blueprint-demo-beats.md, scripts/demo-recording/plans/azure-aks-demo-beats.md, scripts/demo-recording/plans/sizzle-reel-beats.md, oracle, link
**Why:** Ahmed confirmed all three demo-recording scripts are locked and ready for recording once the release/deploy (Entra ID auth gap fix, session 2b27d1d4) lands:

- Scenario 1 — `scripts/demo-recording/plans/blueprint-demo-beats.md` (full product walkthrough)
- Scenario 2 — `scripts/demo-recording/plans/azure-aks-demo-beats.md` (Azure/AKS repo walkthrough)
- Scenario 3 — `scripts/demo-recording/plans/sizzle-reel-beats.md` (14-beat sizzle reel, oracle/demo-scenario-scripts commit 9d65c60d), assembled entirely from S1/S2 source beats with explicit citations. Locked constraints: no music, no dissolves/cross-fades, hard cuts only, every cut lands on a DOM-grounded visual cue.

Known open item not blocking lock-in: beat 3.2's exact GitHub webhook trigger configuration (label/comment string, Pass Key requirement) won't be known until tried live against the deployed environment.

Next: compositor build (background, in a worktree) to render these into final videos once takes are captured.

---

## 2026-07-26T23-54-34: Issue #541 LLM preview-command fallback reuses the existing tool-less classifier pattern (not the full agent-turn machinery)
**By:** Switch
**What:** Issue #541 LLM preview-command fallback reuses the existing tool-less classifier pattern (not the full agent-turn machinery)
**Why:** Design fork resolved for #541 (LLM-powered fallback when PreviewCommandResolver.Resolve() returns Unresolved).

Fork: build the fallback as (a) a full agentic turn via CopilotAIAgent/WorkflowAgentFactory, or (b) reuse the existing lightweight "tool-less single completion" classifier pattern already established in the coordinator (CopilotWorkflowSelectionModel, CopilotAssemblyGateCodeClassifier, CopilotPreviewClassifier, StoryIndependenceClassifier, OutcomeSpecReplyClassifier).

Decision: reuse pattern (b). A cheap-model-call convention already exists, so no new machinery is invented.
- New IPreviewCommandModel + CopilotPreviewCommandModel mirror CopilotPreviewClassifier exactly: GitHubCopilotClientFactory + IGitHubTokenScopeProvider, SessionConfig with Tools=[]/AvailableTools=[] + RejectAllToolPermissionHandler (XPIA defense — worktree file contents are untrusted), EnableSessionStore=false, 30s timeout, JSON-only response, null-on-failure fail-safe.
- Model tier: GenerationModelOptions.ResolveReplyClassificationModel() (defaults to gpt-5-mini) — the codebase's designated fast/cheap tier for deterministic-ish classification, keeping cost/latency bounded.
- Worktree view is a bounded digest (file listing minus node_modules/.git/build dirs + truncated contents of package.json/README/Dockerfile/Makefile/index.html/manifests) to cap tokens.
- Trust boundary unchanged: the model only chooses the command STRING; it still flows through the same MapExecutionCwd + AgentHost supervised start + port-observe + AgentPreviewGate approval path in PreviewStep.RunAsync. Model cwd is containment-validated against the worktree before use.
- Additive only: if the model declines or returns an unusable command, PreviewStep still emits terminal preview_command_unresolved. Heuristics remain the unchanged first pass. Tier is observable via the existing command_source field ("heuristic:<source>" vs "llm").
- The IPreviewCommandModel ctor param on PreviewStep is optional/nullable so behavior is identical when unwired (existing tests unaffected).</body>
<parameter name="references">["issue:541", "Trinity", "Tank"]

---

# Tank decision: optional Entra client authentication for browser sign-in

- Date: 2026-07-30
- Author: Tank
- Context: The staging tenant blocks password credentials for the Entra app registration, but Microsoft identity platform supports authorization-code + PKCE redemption without a client secret when the app allows public client flows.

## Decision

Keep the existing Entra browser sign-in redirect flow and derive token-endpoint client authentication at runtime:

- if `Auth:Entra:ClientSecret` is configured, redeem as a confidential client and include `client_secret`
- if `Auth:Entra:ClientSecret` is absent, redeem with PKCE only and omit `client_secret` entirely

## Rationale

This preserves backward compatibility for tenants that still allow secrets while unblocking corporate tenants that forbid password credentials. It also keeps the code open for a future certificate-based branch without hardcoding PKCE-only as the sole mode.

## Notes

Tests were updated to assert both request shapes by capturing the posted form body in the Entra sign-in test factory.

---

## 2026-07-27T19-14-53: #580 recurrence cause: deploy tooling grouped networkpolicy-mcp.yaml incompletely, so release deploys silently skipped allow-agenthost-to-mcp
**By:** Tank
**What:** #580 recurrence cause: deploy tooling grouped networkpolicy-mcp.yaml incompletely, so release deploys silently skipped allow-agenthost-to-mcp
**References:** issue #580, run 58cf42ad-0a21-49e8-941a-7be1e164aeeb, run 9ffd740e-5d2a-4dab-95e7-8756c1ddc105, run 8586b313-63c3-497e-bb3a-a2b3d49d9ff1, scripts/azure/lib/kustomize.mjs, scripts/azure/tests/deploy-render.test.mjs, scripts/azure/steps/30-deploy.mjs
**Why:** Follow-up on #580: the missing live NetworkPolicy was not just historical drift. The staging environment lacked allow-agenthost-to-mcp because the deploy renderer/grouping layer silently omitted it.

Details:
- k8s/base/networkpolicy-mcp.yaml in source/tag v0.12.2 contains three docs: allow-gateway-to-mcp, allow-api-to-mcp, and allow-agenthost-to-mcp.
- scripts/azure/steps/30-deploy.mjs already includes networkpolicy-mcp.yaml in NETWORK_POLICY_MANIFESTS, so apply order was not the problem.
- The bug was scripts/azure/lib/kustomize.mjs: FILE_RESOURCES["networkpolicy-mcp.yaml"] listed only the first two MCP policies. manifestForFilename() uses FILE_RESOURCES to regroup kubectl kustomize output back into per-file apply manifests, so azure:deploy-from-local / deploy-from-release applied a truncated networkpolicy-mcp.yaml that never contained allow-agenthost-to-mcp.
- That explains the live staging state exactly: allow-gateway-to-mcp and allow-api-to-mcp existed, allow-agenthost-to-mcp did not.

I fixed the tooling locally by adding allow-agenthost-to-mcp to FILE_RESOURCES and adding a regression test that every kustomize-built resource is accounted for by FILE_RESOURCES, preventing future silent omissions of newly-added docs. Targeted tests passed: node --test scripts/azure/tests/deploy-render.test.mjs scripts/azure/tests/deploy-apply.test.mjs.

Caveat: I did not open a PR from this checkout because the shared working tree is currently on branch morpheus-578-delete-watch with unrelated uncommitted changes from another investigation. Committing here would contaminate that branch; the fix is ready to cherry-pick into a clean branch/PR.

---

## 2026-07-27T19-01-15: #580 was caused by a missing live allow-agenthost-to-mcp NetworkPolicy on staging, not AgentHost pod readiness or the #578 autoscaler chain
**By:** Tank
**What:** #580 was caused by a missing live allow-agenthost-to-mcp NetworkPolicy on staging, not AgentHost pod readiness or the #578 autoscaler chain
**References:** issue #580, issue #574, issue #578, run 58cf42ad-0a21-49e8-941a-7be1e164aeeb, run 9ffd740e-5d2a-4dab-95e7-8756c1ddc105, run 8586b313-63c3-497e-bb3a-a2b3d49d9ff1, k8s/base/networkpolicy-mcp.yaml, scripts/harness-shared/learnings.md
**Why:** Investigated staging run 58cf42ad-0a21-49e8-941a-7be1e164aeeb and reproduced with fresh run 9ffd740e-5d2a-4dab-95e7-8756c1ddc105. App Insights shows the AgentHost pod lifecycle was healthy: claim created at 18:34:40Z, bound pod agentweaver-agent-host-gnhdt at 18:34:42Z, /healthz 200 after 1 probe, /configure 200, RemoteAgentProxy SetupAsync complete at 18:34:44Z. The failure happened ~60s later inside the AgentHost operator-assistant turn: AgentweaverMcpToolProvider.ConnectAsync timed out connecting to AgentHost__McpEndpoint=http://agentweaver-mcp:8080/mcp, producing 'Initialization timed out' (McpClient.ConnectAsync timeout path). Code timeout threshold is 30s per MCP connect attempt in packages/Agentweaver.AgentRuntime/AgentweaverMcpToolProvider.cs.

Live cluster state was missing NetworkPolicy allow-agenthost-to-mcp even though repo/tag v0.12.2 already contains it in k8s/base/networkpolicy-mcp.yaml. With default-deny-ingress on the MCP pod, AgentHost->MCP traffic was dropped: from a live AgentHost pod, curl http://agentweaver-mcp:8080/healthz hung until timeout and curl http://agentweaver-mcp:8080/mcp also timed out. I live-mitigated staging by applying the committed manifest (kubectl apply -f k8s/base/networkpolicy-mcp.yaml), which created allow-agenthost-to-mcp. After that, the same AgentHost pod could reach MCP (/healthz 200, /mcp 401 without auth as expected), and a fresh assistant run 8586b313-63c3-497e-bb3a-a2b3d49d9ff1 succeeded live with tools_invoked:["project_list"].

Conclusion: #580 does not share the #574/#578 preview-pod teardown/autoscaler mechanism. The pod was ready and alive; the failure was east-west MCP connectivity caused by missing live ingress policy / staging drift. Remaining open question is why the live resource was absent despite being present in source/release manifest.

---

# Tank decision note — issue #607

Date: 2026-07-28

## Summary
The staging `delegated_to_backlog` / `subtasks: []` symptom is not caused by the workflow worktree-materialization fix from #597/#601.

## Findings
- `CoordinatorRunService.TryHandOffToDispatchAsync(...)` only starts dispatch when a persisted work plan has at least one subtask row.
- `CoordinatorOrchestratorExecutor.OrchestrateAsync(...)` persists `WorkPlanStatus.Delegated` with zero subtasks when `PartitionStoriesAsync(...)` returns no inline drafts and at least one promoted story.
- That partitioning path runs before any child run starts and does not depend on worktree workflow materialization.
- The effect is that an all-promoted decomposition completes the parent run as `delegated_to_backlog`, so no `subtask.dispatched` event can ever fire.

## Action taken
I patched the partitioner to fail closed when promotion would delegate the entire confirmed plan: it now keeps all stories inline, emits a warning, and preserves normal live child-run dispatch.

---

# Tank — issue #641 backend

## Decision
Added dedicated workflow-trigger CRUD endpoints at `/api/projects/{projectId}/workflows/{workflowId}/trigger` instead of requiring future UI work to round-trip raw YAML for every trigger edit.

## Rationale
The issue required trigger configuration to be representable over the REST API for a future UI, not just via YAML. A focused trigger endpoint lets the UI manage structured event predicates safely while the server still serializes back to canonical workflow YAML and re-validates through the existing loader before persisting.

## Notes
- PUT accepts the structured trigger JSON shape and reuses loader validation by converting it to the internal trigger DTO first.
- DELETE clears the trigger without changing the rest of the workflow definition.
- GET returns the current trigger config, including nested `if` predicates.

---

## 2026-07-30T00-54-07: #641 integration should land via backend-first order; generator/UI/docs need schema-aligned integration fixes
**By:** Tank
**What:** #641 integration should land via backend-first order; generator/UI/docs need schema-aligned integration fixes
**References:** issue #641, PR #645, PR #644, PR #642, PR #646
**Why:** I created an isolated integration worktree off origin/dev and merged the open #641 branches in dependency order. During merge verification I found that the generator and UI work still assumed older trigger YAML shapes (camelCase predicate keys in YAML, legacy ref payloads, and older examples) even though backend issue #641 settled on backend-owned schema as the source of truth: snake_case predicate keys in YAML, camelCase in API JSON, `ref: { branch, match_mode }`, and `not:` wrapping a single predicate.

Decision:
1. Treat PR #645 (backend predicates) as the schema-defining base and land it first.
2. Land generator work only after rebasing/alignment to that backend schema.
3. Land UI work only after schema alignment plus passing targeted trigger UI tests.
4. Land docs last so they describe the final merged behavior, including the `commentMatches` privacy/ReDoS boundary.
5. Use the integration branch fixes as the conflict-resolution source when updating downstream PRs, because those fixes preserve backend compatibility and make the targeted UI + harness validations pass together.

Validation on the integrated branch:
- .NET build passed.
- Targeted workflow + GitHub webhook tests passed.
- apps/web lint passed.
- Targeted workflow trigger UI tests passed after YAML-schema alignment.
- API harness tests passed.
- UI harness tests passed.
- Full web test suite still shows unrelated pre-existing failures outside #641 scope (CoordinatorRunPage / SkillsPage), so merge confidence for #641 should rely on the targeted trigger coverage plus harness runs, not those unrelated failures.

---

# Tank decision: alumni role resolution for observability

## Context
The observability Agent token breakdown aggregates usage across historical runs, but GET /api/projects/{id}/team only exposed the live roster from .squad/team.md. Agents archived under .squad/agents/_alumni therefore lost their role titles and rendered as the generic AI Assistant fallback.

## Decision
Extend GET /api/projects/{id}/team additively with a retired_members collection while keeping members unchanged. Resolve retired members by scanning .squad/agents/_alumni/*/charter.md, parsing the preserved charter heading/role section for role titles, and merge retired_members into the observability role map only when no active member with the same name exists.

## Rationale
This avoids regressing other team consumers, keeps the fix localized, and uses the preserved alumni charter as the source of truth for historical role labels.

---

# Tank decision: legacy AllowedTeam overlap stays warn-and-continue

- Context: PR #631 changed GitHub org authorization from legacy `AllowedOrg` + `AllowedTeam`
  AND semantics to a mixed OR rule list. If operators configure `AllowedOrg=org` and the legacy
  `AllowedTeam=org/team`, appending the team as an extra OR rule silently widens access to the
  full org.
- Decision: keep startup as warn-and-continue, but detect the overlap and emit a prominent warning
  that names the exact org/team and the effective rule set. Do not append the legacy team as an
  independent OR rule in that overlap case.
- Rationale: this preserves runtime compatibility while making the widened access impossible to miss
  in logs. It also matches existing auth/config handling here: the codebase hard-fails for
  production-breaking or auth-disabling misconfiguration (for example `OAuthConfigGuard` and
  `TestingBypassGuard`), but uses warnings for deprecated/ambiguous auth rule inputs that still have
  a deterministic fail-closed or operator-actionable interpretation.

---

## 2026-07-29T04-42-16: Expose retired team members separately on GET /team for historical role resolution
**By:** Tank
**What:** Expose retired team members separately on GET /team for historical role resolution
**References:** apps/Agentweaver.Api/Contracts/Dtos.cs, apps/Agentweaver.Api/Endpoints/TeamEndpoints.cs, packages/Agentweaver.Squad/Squad/SquadReader.cs, apps/web/src/pages/observability/ObservabilityAgentsPage.tsx
**Why:** Context: the observability Agent token breakdown aggregates usage across historical runs, but GET /api/projects/{id}/team only surfaced the live roster from .squad/team.md. Retired agents archived under .squad/agents/_alumni therefore lost their role titles and rendered as the generic AI Assistant fallback.

Decision: keep TeamDto.members semantics unchanged and extend GET /api/projects/{id}/team additively with a retired_members collection. The backend resolves retired members by scanning .squad/agents/_alumni/*/charter.md, parsing the charter heading/role section for the preserved role title, and emitting those entries separately so active roster consumers are unaffected. The observability agents page merges retired_members only when no active member with the same name already exists, so active roles win on conflicts.

Rationale: this is lower risk than changing members to include alumni universally or adding a bespoke observability-only endpoint. It keeps existing team consumers stable while giving historical role-resolution consumers the missing data they need.

---

## 2026-07-27T06-15-23: GitHub event-trigger webhooks never fire for "import from GitHub" projects (URL-vs-owner/repo mismatch); fixed by normalizing repo comparison
**By:** Tank
**What:** GitHub event-trigger webhooks never fire for "import from GitHub" projects (URL-vs-owner/repo mismatch); fixed by normalizing repo comparison
**Why:** ## Context
Ahmed asked for a live end-to-end test of the GitHub webhook / event-trigger feature against staging (v0.11.6) using a real throwaway GitHub repo (sabbour/agentweaver-webhook-spike-20260727090023), not mocked payloads.

## Finding (high-confidence bug)
Real GitHub `push` deliveries to `/api/projects/{id}/webhooks/github` returned **HTTP 204 and fired no workflow**, even though signature verification succeeded and an event-trigger workflow (`github.push`) was configured.

Root cause: `GitHubWebhookEndpoints` matches `project.Origin.SourceRepository` against the webhook payload's `repository.full_name` ("owner/repo"). But `ProjectService.CreateFromGitHubAsync` stores `Origin.SourceRepository` as the **full HTTPS clone URL** (`https://github.com/owner/repo`), violating the `ProjectOrigin` domain contract that documents it as "owner/repo". The only creation path that stores the contract form is the "create a new repo" connect path (`POST /api/projects/{id}/github/repository`, stores `creation.FullName`). Therefore every project made via the primary "import from GitHub" flow can never fire event-triggered workflows from real deliveries.

Existing unit tests missed it because they construct `ProjectOrigin.FromGitHub("owner/repo")` directly, honoring the contract, rather than going through `CreateFromGitHubAsync`.

## Isolating evidence (staging, project 4a30fa6c-819b-460b-bf43-3c3afe771ff0)
- Real push delivery id 3833542386751897600 -> 204, no run.
- Manual signed A) bad signature -> 401 (verification works).
- Manual signed B) valid sig + full_name="sabbour/repo" (real GitHub form) -> 204, NOT fired (the bug).
- Manual signed C) valid sig + full_name=stored URL -> 200, fired `webhook-e2e` (proves receive->verify->match->dispatch is otherwise fully functional; only the repo-name format comparison is broken).
- Direct `POST /workflow-events` control -> fired `webhook-e2e` (trigger+dispatch pipeline healthy).

## Decision
Fix in the webhook receiver rather than changing storage/API format: normalize BOTH sides to canonical lowercase `owner/repo` (added `NormalizeRepoFullName`, accepts URL or owner/repo) before comparing. This is surgical, fixes both creation paths, and avoids changing the API-visible `source_repository` field (frontend/skill provenance/other consumers still get the URL). The deeper storage inconsistency (import path stores URL, connect path stores owner/repo) is noted in the PR for a possible follow-up, but is intentionally not changed here to keep blast radius small and because staging cannot be redeployed to E2E-verify a storage-format change this session.

Branch fix/webhook-sourcerepo-match off dev; added regression test `Push_ProjectOriginStoredAsCloneUrl_StillFiresWorkflow`. All 14 GitHubWebhookEndpointsTests pass locally.</body>
<parameter name="references">["ProjectService.CreateFromGitHubAsync", "GitHubWebhookEndpoints.cs", "packages/Agentweaver.Domain/ProjectOrigin.cs", "issue #53"]

---

# Tank — preview approval timeout resolution

- Issue: #615
- Summary: `AgentPreviewGate` now resolves `Sandbox:Preview:ApprovalTimeoutMinutes` before the direct env-var fallback `SANDBOX_PREVIEW_APPROVAL_TIMEOUT_MINUTES`.
- Rationale: the issue asked to mirror the existing auto-approve pattern while still treating the env var as a fallback rather than a competing hierarchy source. Using config-first, env-second preserves explicit app configuration when both are present, still honors the legacy flat env name when config is absent or invalid, and keeps invalid/missing values on the documented 15-minute default with a 1-minute minimum clamp.

---

## 2026-07-31T00-09-10: Published apps: control-plane design decisions (snapshot-pinned revisions, dedicated lifecycle state machine, cluster-as-truth reconciliation, OAuth-client projection apps)
**By:** Tank
**What:** Published apps: control-plane design decisions (snapshot-pinned revisions, dedicated lifecycle state machine, cluster-as-truth reconciliation, OAuth-client projection apps)
**References:** #21 preview-sandbox-apps, #582 build-images-with-rootless-buildkit, #20 isolate-agent-workspaces, #37 self-host-agentweaver, Link (k8s topology), Seraph (security), Trinity (UI)
**Why:** Backend design analysis for "Publishing apps from Agentweaver" (control-plane scope: domain model, state machine, API surface, persistence, build inputs, workflow-projection contract). Grounded in verified repo paths.

## Decisions taken (Tank, backend scope)

1. **Source pinning = immutable git-ref snapshot, not a run workspace.**
   A publish MUST pin to `(repository, commit_sha, tree_hash, subdirectory)`, never to a live run
   workspace path. Verified reason: run worktrees live on the SHARED RWX Azure Files PVC
   (`k8s/base/pvc-workspace.yaml`) under the API HOME tree, are explicitly documented there as
   "NOT an isolation boundary", and are reaped by `AgentHostReaperService`
   (`apps/Agentweaver.Api/Sandbox/AgentHostReaperService.cs`). `Run.TreeHash` +
   `Run.WorktreeBranch` are already the codebase's hand-off contract
   (`apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:802`), so reuse them.

2. **`PublishedApp` (mutable head) + `PublishedAppRevision` (immutable snapshot) split.**
   The app is the stable identity/hostname/policy holder; each build+deploy is a revision. This is
   what makes rollback a pointer move rather than a rebuild, and it mirrors the existing
   run/revision split (`apps/Agentweaver.Api/Infrastructure/IRunRevisionStore.cs`).

3. **Its own state machine + its own event stream, NOT the run state machine.**
   `RunStatus` (`packages/Agentweaver.Domain/RunStatus.cs`) is a converging one-shot pipeline that
   always reaches a terminal state; a published app is a long-lived reconciled resource that
   re-enters `Running` repeatedly. Reusing it would corrupt run semantics (board columns, reapers,
   coordinator pickup all key off run status). Publish progress gets
   `GET /api/published-apps/{id}/stream` with the SAME framing/resume contract as
   `GET /api/runs/{id}/stream` (`id:` = sequence, `Last-Event-ID`, `event: done`) — see
   `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs` and `EndpointHelpers.WriteSseEventAsync`.

4. **Kubernetes is the truth for RUNTIME state; the DB is the truth for INTENT.**
   Follows the existing preview precedent, which stores all per-preview state in HTTPRoute
   annotations and no DB row at all (`apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs`,
   `PreviewReaper.cs`). Published apps DO need durable DB rows (they outlive runs), so the rule is:
   DB holds desired state + revision history; a `PublishedAppReconciler` (BackgroundService, same
   shape as `SandboxPreviewReaperService`) reconciles observed cluster state onto
   `observed_status`. Orphan cluster objects labelled `agentweaver.dev/published-app` with no DB
   row are deleted; DB rows with no cluster objects are re-deployed, not silently failed.

5. **Persistence: dual-store, matching the existing provider split.**
   `IPublishedAppStore` + `EfPublishedAppStore` (Postgres) + `SqlitePublishedAppStore` (SQLite),
   registered conditionally on `Database:Provider` in `apps/Agentweaver.Api/Program.cs`. Requires
   BOTH an EF migration under `apps/Agentweaver.Api.Migrations.Postgres/Migrations/` AND SQLite DDL.
   Flags a known repo hazard: PR #640's post-mortem (decisions.md 2026-07-30, "Missing Postgres
   migration added for the shared auth-mode epoch table") — do not repeat it.

6. **Dockerfile authorship: auto-detect → generate → agent-authored, in that order, with the
   generated Dockerfile committed to the pinned revision.**
   Rejecting buildpacks for phase 1 (new supply chain, new base-image trust boundary). The build
   input must be a file in the pinned tree so a rebuild of revision N is byte-reproducible. This
   directly constrains #582 (`specs/agent-execution-sandbox/build-images-with-rootless-buildkit.md`),
   which is still design-only and is a HARD BLOCKER for phase 1.

7. **Workflow-projection apps talk back through the EXISTING OAuth 2.1 AS as a first-class
   confidential client — not a bespoke token.**
   `apps/Agentweaver.Api/Endpoints/OAuthServerEndpoints.cs` already implements RFC 8414 metadata,
   RFC 7591 dynamic client registration, PKCE S256 authorize/token, rotating refresh + RFC 7009
   revoke, and JWKS; `apps/Agentweaver.Api/Auth/OAuth/McpTokenService.cs` mints RS256 JWTs with
   `aud`/`sub`/`scope`. Publishing a projection app registers a client, and new scopes
   (`app:workflow.read`, `app:workflow.invoke`, `app:artifact.read`) plus a `pub_app` claim carry
   the binding. Security verdict deferred to Seraph.

8. **BLOCKING GAP flagged: workflows have no typed input/output contract.**
   `apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs` has NO `Inputs`/`Outputs`. The manual
   trigger `POST /api/projects/{projectId}/workflows/{workflowId}/run`
   (`WorkflowDefinitionEndpoints.cs`) just creates a free-text backlog task via
   `WorkflowTriggerBacklogFactory`. Flavor (b) is not buildable until `WorkflowDefinition` gains a
   declared `inputs:`/`outputs:` schema. Recommend that as a separate spec that lands BEFORE (b).

## Boundaries respected
K8s topology (namespace, gateway, registry, RBAC) -> Link. Security verdict on the OAuth handshake,
scope model, tenant isolation, and BuildKit privilege surface -> Seraph. UI -> Trinity.

---

# Tank — native shell denial relabeling

## Context
The Copilot SDK can emit a `tool.call` start event for its built-in native shell using the real `ToolCallId` even when Agentweaver immediately rejects that same call in `BuildPermissionHandler` and tells the model to use `run_command` instead.

## Decision
Normalize native-shell lifecycle start events to `run_command` inside `CopilotAIAgent` before they hit the append-only run event stream, rather than trying to overwrite a previously emitted `tool.call` later.

## Rationale
`EmitToolCallOnce` is intentionally first-write-wins for all tool types, and the frontend reducer appends `tool.call` rows by `callId`, so emitting a second corrective `tool.call` after a raw `bash` event would either no-op or create duplicate UI rows. Rewriting the native-shell lifecycle start event itself keeps the existing dedup guarantee intact for every other tool while ensuring the first visible label is already the sandboxed `run_command` name.

---

# Trinity decision — issue #641 event-trigger UI

- **Issue:** #641
- **Author:** Trinity
- **Decision date:** 2026-07-29

## Decision

The workflows UI keeps **schedule** and **event** trigger editing as two separate dialogs, but saving either one replaces the other trigger on the workflow.

## Rationale

The current workflow YAML shape still stores a single top-level `trigger:` object, so supporting both editors side by side is the least surprising UI that matches today's schema without inventing a composite trigger model in the client. The row actions therefore switch copy between `Add ...`, `Edit ...`, and `Replace with ...`, and the event dialog warns when saving will replace an existing schedule trigger.

## Follow-up

If the backend later supports multiple trigger objects or a combined trigger model, the UI can promote these dialogs into a unified trigger builder without changing the condition-row predicate editor introduced in #641.

---

## 2026-07-29T09-28-20: Approval watcher keys each visible approval card by a recording-only DOM data attribute
**By:** Trinity
**What:** Approval watcher keys each visible approval card by a recording-only DOM data attribute
**References:** PR #634, scripts/demo-recording/lib/capture-plan.mjs, Tank review findings
**Why:** For PR #634's demo approval watcher fix, I am keying per-card grace-period state with a recording-only `data-demo-approval-watcher-id` stamped onto each approval card DOM element the first time the watcher sees it. This keeps timers independent across concurrent approval cards and resilient to locator re-creation/re-rendering during polling, without relying on unstable global-first selection or fragile pure-index keys. The watcher still scopes `[role="alert"]` cards by `Tool Approval Required`, but it matches `session-approval-gate` and `assistant-approval-gate` containers regardless of heading text so shell-command approvals are also covered.

---

# Trinity — Assistant new-session route reset

Date: 2026-07-28
Related issues: #590

## Finding

The assistant page kept its own `runId` in component state and only seeded that state from `?runId=` on the initial mount. When the left-nav or Sessions page navigated from `/assistant?...&runId=<existing>` to `/assistant?...` for a new conversation, the mounted route did not reset. The page immediately preserved the old run state, so the New session action appeared to do nothing.

## Decision

Treat `runId` route changes as a remount boundary in `AssistantRoute` by keying `AssistantRunPage` on `projectId + runId`.

## Why

This keeps the route as the source of truth for session selection without adding effect-driven state synchronization inside the page component. It fixes the in-run New session path and also covers direct navigation between assistant conversations.

---

## 2026-07-27T06-37-54: Assistant Run page: suggested-prompt chip wording/placement for smoke-test quick-start
**By:** Trinity
**What:** Assistant Run page: suggested-prompt chip wording/placement for smoke-test quick-start
**References:** apps/web/src/pages/AssistantRunPage.tsx, docs/deep-dive/assistant-runtime.md, docs/experience/assistant-sessions.md
**Why:** Added 5 suggested-prompt chips to the Assistant Run page's empty state (apps/web/src/pages/AssistantRunPage.tsx), per user request to give "basic testing context" quick-start buttons.

Judgment calls made:
1. **Source of examples**: did NOT reuse the landing page's `components/landing/scenarios.tsx` demo scenarios (rate-limit feature, blog post, RFP response, etc.) — those are long-form coding/writing demo goals aimed at the marketing landing page's Coordinator-orchestration theater. The Assistant Run page is a different, lighter-weight surface (`docs/deep-dive/assistant-runtime.md`): an "operator assistant" chat that only has MCP tool access (project_list, project_list_runs, run_status, coordinator_start/run_task, skill_list, etc.) and no worktree/sandbox. So the 5 chips are written to be realistic requests for driving/inspecting the platform itself, not general coding tasks:
   - "List my projects and each one's most recent run status."
   - "Start a quick smoke-test run: ask the coordinator to add a one-line README update to a project."
   - "Show me the status of my most recent run, and flag anything waiting on my approval."
   - "What MCP tools and skills do you currently have access to?"
   - "Create a new test project and kick off a small run to verify everything is wired up."
2. **Count**: 5 chips (within the requested 3-5 range) — enough variety (read-only lookups + an actual smoke-test run kickoff) without cluttering the empty state.
3. **Click behavior**: populate-only, not auto-submit — matches the Composer's existing edit-then-send flow (Enter to submit) and the explicit instruction in the task ("unless existing UX auto-submits similar chips" — no such existing pattern was found in this codebase).
4. **Component/placement**: reused the existing Fluent `Button` (appearance="outline", size="small", shape="circular") rather than introducing a new Chip component, since no dedicated chip/pill component exists yet in `components/ui/`; placed directly under the empty-state invitation text in the transcript area (only rendered when `!runId`), not in the Composer's `contentBelow` slot, so the chips disappear once a conversation starts (contentBelow persists for the whole session).
5. Docs updated: added a short "Suggested prompts" subsection to `docs/experience/assistant-sessions.md`.

PR branch: trinity/assistant-run-prompt-buttons, opened against dev.

---

# Trinity — avoid-ai-writing import decision

Date: 2026-07-29

## Decision

For the Azure/AKS staging demo project, import `conorbronsdon/avoid-ai-writing` from the repository root candidate (`SKILL.md`) via a project marketplace source named `Avoid AI Writing`, keep the existing `write-blog-post` structural skill assigned to Hermione, and add the imported `avoid-ai-writing` skill to Hermione. After the first full draft still carried too many em dashes in the assembled review artifact, also assign the imported skill to Ron for the editorial pass and validate with a fresh draft-only run.

## Rationale

- Agentweaver's auto-detected marketplace path supports a repo-root `SKILL.md` directly: browse returned both `SKILL.md` and `plugins/avoid-ai-writing/skills/avoid-ai-writing`, and import accepts the selected candidate location as the auto source subpath.
- The root candidate is the simplest, most honest representation of the upstream repo because it is the canonical single-skill entry point and exposes the same `name`, `description`, and `version: 3.18.0` metadata as the nested plugin copy.
- Keeping `write-blog-post` on Hermione preserves the repo-convention discovery and blog-structure guidance that is specific to Azure/AKS, while `avoid-ai-writing` supplies the prose-quality constraints the generated skill lacked.
- The first imported-skill run proved the marketplace skill was actually injected into Hermione's system prompt, but the assembled draft still had 11 em dashes and one remaining rule-of-three pattern. Assigning the same imported skill to Ron for the editorial pass and launching a fresh draft-only run produced a cleaner draft with 0 em dashes and 0 detected rule-of-three markers in the checked patterns.

## Evidence

- Code path: `apps/Agentweaver.Api/Endpoints/SkillEndpoints.cs`, `apps/Agentweaver.Api/Skills/SkillCatalogService.cs`, `apps/Agentweaver.Api/Skills/MarketplaceCatalogIndexer.cs`.
- Live staging browse result on project `AKS`: candidates `SKILL.md` and `plugins/avoid-ai-writing/skills/avoid-ai-writing`.
- Imported skill id: `cd97c350-1939-44a4-afc3-fa52a6636b0d`.
- First imported run coordinator: `3b6b07d5-1942-4ccf-9633-46c01e767cba`; writer child: `f5c05e77-004f-4360-a38c-a2dd76b90fbe`.
- Stricter validation rerun coordinator: `8f6b3a48-e818-42f6-b91f-5218e0326990` (cancelled after draft capture to avoid any publish path); writer child with improved draft: `9b2f044e-c66d-47e6-944e-de3988c2ed71`.

---

## 2026-07-29T12-30-38: Azure/AKS demo recapture for v0.13.0: narration verified (no changes needed); full live video re-drive blocked by failing content runs + one-PR guardrail
**By:** Trinity
**What:** Azure/AKS demo recapture for v0.13.0: narration verified (no changes needed); full live video re-drive blocked by failing content runs + one-PR guardrail
**Why:** ## Task
Fresh full recapture of the Azure/AKS live-repo demo (`scripts/demo-recording/plans/azure-aks-demo-beats.md`) against the v0.13.0 staging deployment, incorporating PRs #634/#635/#636 and the other v0.13.0 changes.

## What I verified (all green)
- **Auth session reusable** — the persisted ui-harness session (`scripts/ui-harness/.auth/staging.*`, written 2026-07-29 08:16) is still valid; `list-projects` returns live data. Did NOT attempt OAuth.
- **TTS pipeline works end-to-end** — `synthesize-beats` against the `agentweaverdemoai0728` resource produced all 15 per-beat WAVs (total ≈ 6.5 min narration). Key stayed scoped to the one command, never printed.
- **Demo-recording unit tests pass** — `node --test test/*.test.mjs` → 17/17, including the #634 approval-watcher that now covers all three approval surfaces (session/assistant/shell gates + timeline alert card).

## Narration review (the user's explicit ask)
Re-read every `Narration:` line. Conclusion: **no narration text changes are needed for v0.13.0.**
- `synthesize-beats` speaks only the committed `Narration:` lines; the `On screen:` notes are stage directions, never voiced.
- The narration already reads as natural demo VO (first-person-plural walkthrough), not like meta-instructions, and follows Microsoft writing style. The earlier "sounds like my instructions" feedback appears already addressed in the committed file (#616/#632/#635).
- The v0.13.0 changes relevant here (cluster-page diagnostics fixes, tool-approval-card overlap fix, run-timeline step-numbering fix, observability agent-role-label fix, approval-watcher) are **on-screen bug fixes** — they change what the camera shows, not the voiceover. The team-membership authz change (#631) is not shown on-screen in any beat, so needs no narration.
- Beat 4.4 (Cluster page) On-screen note still references real, present elements (`agent_pod_quota` health check, "Warm pool ready" tile, "Sandbox claims" table); the removed empty "Sandbox objects" section was never referenced, so the note stays valid.

## Why the full live video recapture is blocked (genuine product blocker — stopped rather than looping, per instructions)
1. **Content-authoring & triage runs on the AKS project are all failing.** Current run states: `failed/abandoned`, `failed/revision_start_failed`, `failed/checkpoint_missing`, `merge_failed/needs_resolution` (x2), `idle`. There is no clean "draft landed" (Beat 3.3) or "triage results" (Beat 4.2) footage to show, and the prior blog run (`fdebbc74`) is now merge_failed/needs_resolution.
2. **Re-running content-authoring risks a second Azure/AKS PR.** The standing guardrail is exactly ONE PR against Azure/AKS; blog PR **#5880** (`agentweaver-demo/blog-multi-agent-aks`) is still open. The content workflow's job is to draft + open a PR, so a fresh live run could open a 2nd PR — not allowed.
3. **All-or-nothing assembly.** Per-beat synced segments are scratch (deleted for token safety), so `assemble-final` needs a full 15-beat re-capture; there is no partial-update path.

## Outcome
- No committable changes: narration verified correct (no edits), and a faithful new video cannot be produced under the current run-failure state + one-PR guardrail without risking a guardrail violation or showing failed runs. Therefore **no agentweaver PR was opened** (an empty/no-op PR would be noise).
- Recommendation: the live re-capture needs (a) the content/triage runs to succeed on this project/environment first (the coordinator failures above look environment/product-side, worth a separate look), and (b) a human decision on whether Beat 3.4 should show the existing open PR #5880 rather than opening a new one. Once runs are green, the existing pipeline (`synthesize-beats` → capture driver → `sync-beat` → `assemble-final`) is ready and validated.

## Cleanup
Deleted `.recapture-scratch/` (regenerated audio — no tokens, but scratch) and closed orphaned playwright-cli sessions (`blueprintfinal`, `demo31`) left from prior demo work (their user-data-dirs hold live auth).
</body>
<references>["scripts/demo-recording/plans/azure-aks-demo-beats.md", "scripts/demo-recording/cli.mjs", "scripts/demo-recording/lib/capture-plan.mjs", ".changeset/fix-cluster-page-diagnostics.md", ".changeset/cluster-health-checks-fixes.md"]</references>
</invoke>

---

## 2026-07-27T05-42-44: Deduped + grouped the Add-node palette (PR #559); dropped raw build_test primitive since the Build & Test preset supersedes it
**By:** Trinity
**What:** Deduped + grouped the Add-node palette (PR #559); dropped raw build_test primitive since the Build & Test preset supersedes it
**References:** #558, #559, #556
**Why:** Follow-up to the Visual Workflow Editor triage: @sabbour reported the "Add node" dropdown is confusing — "Build & Test" appears twice and the list is ungrouped.

Findings:
1. LITERAL DUPLICATE (unambiguous bug): The menu (apps/web/src/components/VisualWorkflowEditor.tsx) concatenates SPECIAL_GATES presets with AUTHORABLE_WORKFLOW_NODE_TYPES. SPECIAL_GATES has a "Build & Test" preset (build_test + qa-engineer agent + branches) AND AUTHORABLE includes raw build_test labeled "Build & Test" via NODE_TYPE_LABELS -> identical label twice, same underlying type. An existing test even asserted the duplicate as expected (getAllByRole length > 0).
2. UNGROUPED/AMBIGUOUS (UX): flat list, mostly no icons, mixed gate/step/flow concepts, cryptic labels.

Judgment call — HOW to dedupe: Two options considered. (a) Relabel both "Build & Test" entries to distinguish preset vs raw. (b) Drop the raw build_test primitive from the palette entirely since the preset is a strict superset (same type + sensible defaults) and users can edit the preset in the inspector. Chose (b): it removes the confusing redundancy rather than papering over it, matches how the other presets (RAI/Rubberduck/Human Review) are the primary entry points for `check` gates, and loses no capability. Implemented via a new NODE_TYPE_META map that intentionally omits build_test. Did NOT touch WORKFLOW_NODE_TYPES / the YAML contract — purely the picker presentation.

Judgment call — SCOPE: The task allowed same-or-follow-up PR and filing an issue if grouping needs design thought. Grouping was feasible cleanly by reusing the codebase's EXISTING Fluent MenuGroup/MenuGroupHeader/MenuDivider pattern (WorkflowsPage.tsx) + its multi-line MenuItem title+description pattern, so I implemented the full grouped/iconed/described palette (Reviewers & gates / Agent steps / Flow control) rather than only the duplicate fix. Filed issue #558, opened PR #559 off dev (separate worktree, independent of drag-connect PR #556 in the same file — different regions).

Verification: lint clean, tsc -b clean, VisualWorkflowEditor.test.tsx 5/5 (updated the palette test to assert Build & Test appears exactly once + group headers render + representative primitives reachable).

---

## 2026-07-27T05-44-50: Extended preview-durability re-verification on v0.11.6: #551's fix holds for kubernetes-sandbox-claim backend (survives 20-35min) but NOT for the direct backend used by real execution subtasks (still 404s/NXDOMAINs ~8min after turn-end) — filed as new issue #560; demo recording readiness still not confirmed
**By:** Trinity
**What:** Extended preview-durability re-verification on v0.11.6: #551's fix holds for kubernetes-sandbox-claim backend (survives 20-35min) but NOT for the direct backend used by real execution subtasks (still 404s/NXDOMAINs ~8min after turn-end) — filed as new issue #560; demo recording readiness still not confirmed
**References:** issue #560, issue #542/#551 (partially fixed, backend-specific gap), run 1f221e72-9800-4205-93b7-117deda9cf0c, run 805f1cb0-17bc-4e6f-9539-a9b8f10b5613, project 9ad0f178-3b79-4fa5-ac89-68e231f2a528, scripts/api-harness/transcripts/oracle-live-2026-07-27T04-40-00Z.jsonl
**Why:** ## Context
Requested re-verification of #542's fix (PR #551, "keep sandbox pod alive while a live
preview is active") on freshly-redeployed **v0.11.6** staging, using an extended-window
methodology (checks at T0+2/5/9-10/15/20+ min, plus active `keepalive_url` use), per the
user's explicit instructions to check well past the old ~8-9 min failure point.

**Housekeeping note (not investigated/touched):** `agentweaver-api` was observed at **2/2**
replicas this pass, not the usual 1-replica pin. Flagging as instructed, not scaling or
otherwise acting on it.

## Result: MIXED — fix holds for one backend, not the other (real, reproducible finding)

Dispatched a fresh Harness `PersonaActor` as Oracle, brand-new project
(`9ad0f178-3b79-4fa5-ac89-68e231f2a528`), purpose-built minimal Express task-tracker
blueprint, avoided the known-broken static demo repo.

- Coordinator run `1f221e72-9800-4205-93b7-117deda9cf0c` dispatched real execution subtask
  `805f1cb0-17bc-4e6f-9539-a9b8f10b5613` (agent "Roslin", genuinely solid app code — read
  `server.js` myself before judging it: correct `escapeHtml()`, honors `process.env.PORT`).
- Subtask's `sandbox.backend = direct` (not `kubernetes-sandbox-claim`). It called
  `start_preview(port=4790)` at T0 = `2026-07-27T05:02:30Z` → got a real `preview_url`
  (`willow-azure-ridge-...`), ended `assemble_ready` ~14s later (the exact original #542
  timing).
- **T0+~8min**: real `preview_url` → DNS `NXDOMAIN` across 3 independent public resolvers
  (8.8.8.8/1.1.1.1/9.9.9.9), while the base cluster domain resolved fine on the same
  resolvers (rules out a general DNS outage).
- **Control (A/B), same run**: independently called `POST .../sandbox/preview` directly
  against the **parent coordinator run** (`sandbox.backend=kubernetes-sandbox-claim`) →
  got a second real `preview_url` + `keepalive_url` (`ivory-maple-lunar-...`). This one
  **resolved within ~3.5min and stayed keepalive-able/reachable through the full ~20-35
  minute observation window** (confirmed at T0+20min: `200 kept_alive:true`; T0+22min still
  HTTP-routable at the ingress layer).
- Directly hit the real subtask's own `keepalive` endpoint twice
  (`~T0+21min` and `~T0+34min`) → both times **`404 {"error":"Preview not found for this
  run."}`** — the server-side session record itself is gone, not just a DNS-propagation
  lag. The control session's keepalive succeeded at the same checkpoints.
- **Verdict: #551's deferred-teardown/retention fix appears to hold for
  `kubernetes-sandbox-claim`-backed sandboxes but NOT for `direct`-backed sandboxes — and
  `direct` is the backend actually used by real execution subtasks in this environment.**
  So the practical, demo-relevant preview experience is still broken in exactly the way
  #542 originally described, just narrowed to a specific code path. Confirmed via PR #551's
  changed files (`KubernetesSandboxExecutor.cs`, `AgentHostReaperService.cs`,
  `SandboxPreviewService.cs`/`SandboxExecutorRouter.cs`) — consistent with the fix only
  having been wired into the `kubernetes-sandbox-claim` executor path.
- **Filed as new issue: https://github.com/sabbour/agentweaver/issues/560.**
- Stopped at the pending human-review/assembly gate for run `1f221e72-...` **without
  approving it** — did not blind-approve since the durability finding needed triage first
  and I could not currently browse the real running app to validate demo-readiness.

## What's still blocked
- Live preview is still not reliably usable for a normal coordinator-dispatched execution
  subtask (the common case), pending a fix for #560.
- Recording readiness is therefore NOT yet confirmed — recommend holding the "record a
  demo" decision until #560 is addressed, per the original ask ("if it passes, we'll
  proceed to record a demo; if it still fails, give the exact new failure mode").

## Evidence
- Transcript (21 turns, full verbatim request/response):
  `scripts/api-harness/transcripts/oracle-live-2026-07-27T04-40-00Z.jsonl`
- Project `9ad0f178-3b79-4fa5-ac89-68e231f2a528`, coordinator run
  `1f221e72-9800-4205-93b7-117deda9cf0c`, subtask `805f1cb0-17bc-4e6f-9539-a9b8f10b5613`
  (failing, direct backend), control session on same coordinator run (succeeded,
  kubernetes-sandbox-claim backend).
- New issue: https://github.com/sabbour/agentweaver/issues/560

## Left running / untouched
- Did not scale `agentweaver-api` (observed at 2/2, left as-is per instructions).
- Did not approve the pending assembly/human-review gate on run
  `1f221e72-9800-4205-93b7-117deda9cf0c` — left pending for a human/infra-owner decision.
- Did not touch other in-progress projects/runs on the dashboard.

---

## 2026-07-28T22-08-41: Fix tool-approval card overlapping run activity feed by pinning RunTimeline root to flex-shrink: 0
**By:** Trinity
**What:** Fix tool-approval card overlapping run activity feed by pinning RunTimeline root to flex-shrink: 0
**Why:** ## Bug
On run/orchestration detail views (AgentSessionPanel), the in-thread "Tool Approval Required" card rendered overlapping the agent activity feed text and the "Used N tools" toggle instead of flowing below them.

## Root cause (reproduced live against staging)
`RunTimeline.styles.root` had `minHeight: 0` and inherited the default `flex-shrink: 1`. `RunTimeline`'s root (`data-testid="run-timeline"`) is a flex item inside `AgentSessionPanel`'s `tabBody` scroll container (`display:flex; flex-direction:column; overflow-y:auto; min-height:0`). Because the container is shorter than the total content, the flex algorithm shrank `run-timeline` to height 0 (verified live: `offsetHeight` 0, `scrollHeight` 1159). Its accordion content (overflow visible) then rendered outside the 0-height box, and the following sibling — the `timelineApprovals` approval card — flowed immediately after the collapsed box, visually overlapping the accordion content.

## Fix
Replace `minHeight: 0` with `flexShrink: 0` on `RunTimeline.styles.root`. RunTimeline is a content block that never scrolls itself — an ancestor owns scrolling — so it must always reserve its full content height. Verified live: injecting `flex-shrink:0` moved `offsetHeight` 0 -> 1159 and the approval card to top 1523 (below content), `overlap: false`.

## Why flex-shrink:0 over removing min-height:0
Either independently fixes it, but `flex-shrink: 0` states the intent directly (this block must never be compressed) and is robust regardless of future min-height changes. `minHeight: 0` was only meaningful as an enabler for internal scrolling, which RunTimeline does not have.

## Regression test
Added a test in `apps/web/src/__tests__/RunTimeline.test.tsx` asserting the `run-timeline` root computes to `flex-shrink: 0`. jsdom cannot compute pixel layout, so real overlap can't be asserted, but this directly guards the collapse enabler. Verified the test fails when the fix is reverted (negative check) and passes with the fix.

## Scope
`apps/web/src/components/RunTimeline.tsx` (styles.root) + test + changeset. Both consumers (AgentSessionPanel non-flat/embedded and AssistantRunPage flat) are content-sized, so the change is safe for both.
</body>
<references>["apps/web/src/components/RunTimeline.tsx", "apps/web/src/components/AgentSessionPanel.tsx", "apps/web/src/__tests__/RunTimeline.test.tsx"]</references>
</invoke>

---

## 2026-07-27T05-06-58: Fixed Visual Workflow Editor drag-to-connect inline (PR #556) rather than only filing an issue; flagged UI-harness drag gap
**By:** Trinity
**What:** Fixed Visual Workflow Editor drag-to-connect inline (PR #556) rather than only filing an issue; flagged UI-harness drag gap
**References:** #555, #556, PR #548, #540
**Why:** Task: triage @sabbour's report that the Visual Workflow Editor is unusable ("can't even drag connect nodes", "you'd be lucky to create a saveable workflow") on staging v0.11.6.

Findings:
1. ROOT CAUSE (release-blocking): The shared read-only `WorkflowNode` component (`workflowNodeTypes` in `apps/web/src/components/WorkflowGraphPanel.tsx:916`) renders all connection `<Handle>`s with `{ opacity: 0, pointerEvents: 'none' }`. `VisualWorkflowEditor` reuses this same component for its editable canvas. `pointer-events: none` swallows the pointerdown React Flow needs to START a connection drag, so the correctly-wired `onConnect` handler never fires and no edge can be authored by dragging. `opacity: 0` also removes the visual grab affordance. This is exactly Ahmed's complaint and it fully explains "can't create a saveable (meaningful multi-node) workflow": the YAML side-panel still works, but the visual canvas cannot express control flow.

Judgment call 1 — FIX INLINE vs FILE-ONLY: The task said to file issues for involved bugs but fix inline if the root cause is trivially obvious and low-risk. This qualified: a single well-scoped prop/style change, gated behind a new `connectable` flag so read-only surfaces (CoordinatorRunPage, WorkflowGraphPanel, LandingWorkflowDemo — all `nodesConnectable={false}`) are provably unaffected. Verified `VisualWorkflowEditor` is the ONLY connectable consumer. Filed issue #555 for traceability and opened fix PR #556 (branch fix-workflow-editor-drag-connect off dev, via isolated git worktree to avoid colliding with concurrent agents on the shared main checkout).

Judgment call 2 — HARNESS CANNOT TEST THIS: The agentweaver-ui-harness driver (`scripts/ui-harness/agent-driver-ui/tools.mjs`) exposes only goto/click/type-coordinator/capture/open-preview/resolve-approval — there is NO drag primitive. So the harness cannot positively reproduce or regression-test drag-to-connect; diagnosis was necessarily source-based. This is a real coverage gap worth a separate issue/enhancement so canvas interactions get automated evidence in future.

Verification: `npm --prefix apps/web run lint` passes; `WorkflowGraphPanel`(16) + `PodIndicator`(11) tests pass; full suite 900/901. The one failing SkillsPage case is a pre-existing FluentUI dialog-backdrop timing flake — it fails identically with my edits stashed and SkillsPage imports none of the changed files.

Assessment: With #555/PR #556, the core visual authoring path (add nodes -> drag-connect -> save -> reload) should be functional. Recommend Ahmed/Smith review + a manual staging pass once merged, since the harness can't automate the drag itself.

---

## 2026-07-27T00-01-25: Live-preview reachability verification on v0.11.5: start_preview call itself works (confirms #529 fix), but the returned preview_url stops resolving (404) within ~8-9 min once the subtask's sandbox pod is torn down despite keepalive_url — filed as new issue #542, likely root cause of "preview almost never works"
**By:** Trinity
**What:** Live-preview reachability verification on v0.11.5: start_preview call itself works (confirms #529 fix), but the returned preview_url stops resolving (404) within ~8-9 min once the subtask's sandbox pod is torn down despite keepalive_url — filed as new issue #542, likely root cause of "preview almost never works"
**References:** issue #542, issue #529 (confirmed fixed for the call itself), issue #536 (recurrence, not new), run f257d5d5-ad14-4a83-87ad-50270382158d, run 754e1c2f-d31f-4e66-a643-a90d7bf4fcde, project 26777f8e-2ba2-4fcd-a37c-a633ee61a1ef, scripts/api-harness/transcripts/oracle-live-2026-07-27T01-50-00Z.jsonl
**Why:** ## Context
Focused preview-reachability verification requested after #529 (start_preview 403) was
reportedly fixed but never independently proven to work end-to-end. Ran on v0.11.5
staging via a fresh Harness `PersonaActor` (Oracle), deliberately using a NEW, genuinely
runnable Node/Express app scenario (purpose-built blueprint) rather than the known-broken
static demo repo, so the test wouldn't just re-prove the already-documented preview-
heuristics gap.

## Result: real, evidence-backed FAIL on durable reachability (PASS on the call itself)

- **(a) Did `start_preview` succeed without 403/5xx?** Yes — the agent's own internal call
  succeeded cleanly (no 403, confirming #529's fix holds for a real runnable app).
- **(b) Was a preview URL/port actually returned?** Yes — real `preview_url`
  (`https://harbor-golden-delta-.../`) + `keepalive_url`, `target_port` 3000 (forwarded via
  port 5172).
- **(c) Did independent verification confirm reachable content?** **NO.** I (as Oracle)
  independently curled the exact returned `preview_url` myself ~8-9 minutes after it was
  issued and got a clean `HTTP 404` from istio-envoy — the route no longer resolved. My own
  attempt to call `POST .../sandbox/preview` again on the same run also got `409 no bound
  sandbox pod` — the run's single-turn subtask had already completed and its sandbox pod
  was already torn down.

**Root cause identified precisely**: the sandbox pod backing a preview is torn down as soon
as the originating single-turn subtask completes, and nothing in this flow calls
`keepalive` to extend it — so the preview URL is only reachable for a narrow window during
the agent's own turn, well before any human-review gate (which by design happens after that
turn ends) would ever get a chance to view it. This plausibly explains the long-standing
"live preview basically never works" experience, and is distinct from #529 (which is fixed
and not the problem here).

**Filed as new issue: https://github.com/sabbour/agentweaver/issues/542**

## Secondary, non-blocking observations (documented in #542, not filed separately)
- The first coordinator dispatch attempt stalled ~15 minutes in
  `coordinator.outcome_spec.drafting` with zero progress; required manual cancel+retry to
  unstick (not deeply investigated — noted as a real stall, not chased further this pass).
- The parent coordinator run's later build-test assembly gate also failed with
  `build_test_infra_agenthost_configure_unexpected_exception` — this matches the
  already-tracked **#536** (known residual/self-healing recurrence of #523's signature), not
  a new issue.

## Evidence
- Transcript (20 turns, full verbatim request/response):
  `scripts/api-harness/transcripts/oracle-live-2026-07-27T01-50-00Z.jsonl`
- Project `26777f8e-2ba2-4fcd-a37c-a633ee61a1ef`, coordinator run
  `f257d5d5-ad14-4a83-87ad-50270382158d` (retried from `86054257-7145-4139-aa78-f31a94e7ba59`),
  implementation subtask `754e1c2f-d31f-4e66-a643-a90d7bf4fcde`.
- New issue: https://github.com/sabbour/agentweaver/issues/542

## Left running / untouched
- Did not scale `agentweaver-api` (left at 1 replica).
- Did not touch other in-progress projects/runs on the dashboard.

---

# Trinity — Notification dismissal must be occurrence-aware

Date: 2026-07-28
Related issues: #594

## Finding

The in-app notification system persisted dismissals by notification id, but human-review notifications used a stable id of `review:{runId}`. If a user dismissed one review request, then the same run later returned to human review after more work or requested changes, the backend generated the same id again and the notification stayed filtered out forever.

## Decision

Key human-review notifications by the latest `coordinator.assembly_review_requested` occurrence timestamp, not just by run id.

## Why

Dismissals should hide one review occurrence, not permanently silence every future review cycle for that run. Using the latest review-request event keeps polling stable while a review is pending, but re-arms the notification when the run genuinely asks for review again.

---

## 2026-07-31T00-07-30: Publishing apps: one deployment primitive, two products — and workflow projection apps are blocked on a missing workflow input/output contract
**By:** Trinity
**What:** Publishing apps: one deployment primitive, two products — and workflow projection apps are blocked on a missing workflow input/output contract
**References:** #21 preview-sandbox-apps, #442 run-schedule-and-visually-author-workflows, #11 manage-workflow-library, #12 generate-and-save-workflows, #53 trigger-tasks-for-scheduled-and-event-workflows, #10 monitor-board-workflow-state, #50 browser-chat-control-console, #2 sign-in-and-carry-identity, #37 self-host-agentweaver
**Why:** ## Context

Ahmed asked to spec "publishing apps from Agentweaver" in two flavors: (a) persisted previews of any generated app, and (b) workflow projection apps — custom UIs over a workflow's input and output. This records the UX/product verdicts from the analysis, grounded in the current code.

## Verified starting position

- Previews today are Gateway-direct capability URLs `https://{token}-preview.{zone}` backed by a per-preview Service + HTTPRoute created at runtime in the `agentweaver` namespace (`apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewOptions.cs`, `k8s/base/gateway-preview.yaml`). The URL is explicitly UNAUTHENTICATED — possession grants access (`apps/Agentweaver.Api/Sandbox/Preview/PreviewToken.cs`). Idle timeout 30 min, hard cap 8 h, reaped (`SandboxPreviewReaperService`).
- The agent already publishes previews itself via the `start_preview` tool (`packages/Agentweaver.AgentRuntime/PreviewPublishTool.cs`); UI entry is the Sandbox Preview dialog in `apps/web/src/pages/CoordinatorRunPage.tsx` (~4557-4640).
- `WorkflowDefinition` (`apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:286-312`) carries Id/Name/Description/Version/Start/Nodes/Edges/Stages/Trigger. **There is no declared input or output schema.** Workflow input today is a free-text backlog task (`BacklogTaskDto`, `apps/web/src/api/types.ts:983`); output is a run (events, files, OutcomeSpec).

## Decisions

1. **One deployment primitive, two products.** "Published app" (image + long-lived Deployment + route + lifecycle + kill switch) is shared infrastructure with two entry points. It is NOT one feature. A workflow projection app additionally holds a workflow binding and a scoped credential that can start runs and spend budget; a persisted preview is inert. Shipping a single undifferentiated "Publish" button that grants (b)-level capability to (a)-level artifacts creates LLM-authored, publicly reachable, run-triggering endpoints with no review gate. The audience/capability model must be part of the publish dialog, not a settings afterthought.

2. **Flavor (b) is blocked on a data-model gap, not a UI gap.** Generated projection apps cannot be wired to "the workflow's input and output" schema-first because no such schema exists. Recommend adding `inputs:` (named, typed, required/optional, label + help text) and `outputs:` (named fields the workflow promises to emit) to `WorkflowDefinition`. This also retires the hand-waving in #53 about "preserving the target context the person supplied."

3. **All believable projection-app examples collapse to one shape**: a narrow, audience-specific façade over (start this workflow with typed input) + (render this run's output). Generic reviewer dashboards and generic scheduled-workflow status boards are worse copies of the existing board, notification bell, ApprovalGate, and Heartbeat surfaces — kill them. The versions that earn their keep are domain-shaped review surfaces and audience-facing status pages for people who do not and should not have Agentweaver accounts. The differentiator is the audience boundary, not the widgets.

4. **The model must not write auth, routing, or API-client code.** The scaffold ships a workflow-aware starter (React 19 + Fluent 2 reusing `apps/web/src/copilot-fluent-system`), a generated typed client for exactly two endpoints, an injected API base URL (never a baked-in literal, per Principle VI deployment parity), and a server-minted token scoped to (project, workflow, app) with only start-this-workflow and read-my-own-runs. The model writes fields, layout, and result rendering. Apps are regenerable via another run against the same binding, mirroring `edit-workflows-with-generation-prompt.md`.

5. **Publish is a run, not a new progress UI.** Build and deploy render through the existing `RunTimeline`; failures are failed runs with openable logs. Preserves Principle V and avoids a bespoke deploy-status surface.

6. **Audience model: Private / Project / Link / Public — default Project, do not ship Public in v1.** Link reuses the existing unauthenticated capability-URL posture but with an unbounded TTL, so links must be rotatable and the state must be visibly labelled. Anonymous audience combined with run-start capability requires a per-app rate limit and spend cap set in the publish dialog; no cap, no public publish.

7. **Platform-injected, non-removable provenance chrome on the published app** ("Built by Agentweaver, generated from run X on date Y, report a problem"). If the model renders provenance, it will eventually stop rendering it.

8. **Frame flavor (a) as a bounded pin, not hosting.** Most generated software-delivery output is destined for a PR in the user's own repo (`push-pr-as-execution-step.md`, `open-pull-request-action.md`), not for Agentweaver to host forever. Persisted previews are valuable as shareable review/demo artifacts with a longer bounded life (days), not as a PaaS. Becoming a hosting provider by accident buys patching, quota, abuse, TLS and egress burden with no product moat.

9. **Gate the feature on backend capability and keep CLI parity.** Publish must hide when the backend reports no publish support (same pattern as `showPreviewSandboxButton` gating on `sandboxBackend === 'kubernetes-sandbox-claim'`), and Principle IV requires equivalent CLI commands.

## Open for Ahmed

Workflow input/output schema (blocking); app object scope (project vs run); durable storage for apps that record decisions; run attribution and billing when an anonymous visitor triggers a run; local self-host behaviour with no Gateway; whether MCP already answers some of these use cases more cheaply than generated apps.

---

## 2026-07-27T01-06-55: RunCard task text: use CSS 3-line-clamp + native title attribute instead of Tooltip
**By:** Trinity
**What:** RunCard task text: use CSS 3-line-clamp + native title attribute instead of Tooltip
**References:** issue #549, PR: fix/run-card-task-text-truncation
**Why:** Bug: Board "Problems" panel (RunCard.tsx) rendered `card.task` — a raw, free-text prompt — directly as the card title with no truncation, so long multi-paragraph prompts (500+ words) blew up card height and broke the board's compact layout (reported via screenshot, filed as issue #549).

Fix applied in `apps/web/src/components/board/RunCard.tsx`:
1. Line-clamp value: chose 3 lines (`-webkit-line-clamp: 3`, `-webkit-box-orient: vertical`, `overflow: hidden`, `text-overflow: ellipsis`) to match the visual weight of a normal short task title plus a little headroom, while guaranteeing bounded card height regardless of prompt length. Went with `display: -webkit-box` (the standard cross-browser-supported line-clamp pattern, also used by Chromium/Firefox/Safari) rather than a fixed max-height + JS truncation, since it needs no measurement logic and degrades gracefully.
2. Full-text access: chose a native `title` attribute over a Fluent `Tooltip` component. Rationale: (a) the board area (RunCard.tsx, TaskCard.tsx, KanbanColumn.tsx/KanbanBoard.tsx) has no existing Tooltip usage to match/extend — Tooltip is used elsewhere in the app (LeftNav, NotificationBell, etc.) but not on the board, so there's no established board-local pattern to reuse; (b) potentially many run cards render on a board at once, and wrapping each in a Tooltip adds render/DOM overhead vs. a zero-cost native attribute; (c) native `title` gives the same "reveal full text on hover" affordance plus keyboard/AT-accessible exposure without extra bundle/JS.
3. Scope check: TaskCard.tsx (backlog task cards) was NOT touched — it renders `card.title`, a short human-authored title, not a raw prompt, so it does not exhibit this bug.

No disagreement or trade-off surfaced during implementation; flagging here per the task's request to record non-obvious design decisions.

---

## 2026-07-27T06-14-04: Schedule trigger in Visual Workflow Editor: file discoverability issue (#561), defer inline fix
**By:** Trinity
**What:** Schedule trigger in Visual Workflow Editor: file discoverability issue (#561), defer inline fix
**References:** issue #561, PR #556, PR #559, issue #555, issue #558, WorkflowsPage.tsx, VisualWorkflowEditor.tsx, utils/workflowYaml.ts, WorkflowScheduleTriggerService.cs
**Why:** Context: Ahmed asked "where's the schedule trigger, I thought you added a UI for it" while testing node connections in the Visual Workflow Editor. Investigation confirmed the schedule-trigger UI exists and is functional but is ONLY on the Workflows list page (WorkflowsPage.tsx), gated on `!wf.is_built_in`, and is not surfaced anywhere inside VisualWorkflowEditor.tsx.

Part 1 (regression check) — DONE. Verified the schedule save/persist path end-to-end on live staging v0.11.6 via an authenticated API round-trip on my own trinity-live test project (project 1d015020) with a temp workflow: create -> setScheduleTrigger(weekly/13:30/tuesday) -> saveWorkflowYaml (200) -> list summary exposes trigger{type:schedule,interval:weekly,time_of_day:13:30,day_of_week:tuesday} (exact badge data) -> yaml reload shows persisted trigger block -> cleanup removed the schedule (200, trigger=null) so the evaluator will not fire it. Feature works; the harness itself could not automate the dialog (navigate-and-snapshot only, about:blank between commands), so API round-trip was used as the live evidence.

Part 2 (editor affordance) — DECISION: file a well-scoped discoverability issue (#561) rather than an inline fix now. Rationale:
1. This is a UX/discoverability gap, not a functional regression — the save path is proven working, so no urgency/severity justifying a rushed third editor PR.
2. VisualWorkflowEditor.tsx already has TWO open PRs in flight (#556 drag-to-connect, #559 Add-node menu grouping); stacking a third overlapping change would create avoidable merge churn and complicate review.
3. Correct integration needs a small design decision, NOT a copy of WorkflowsPage.handleSaveSchedule: the editor holds a dirty `yamlText` buffer (useState, L392) and saves via handleSave (PUTs yamlText). WorkflowsPage does an independent getWorkflowYaml->setScheduleTrigger->saveWorkflowYaml round-trip; reusing that verbatim inside the editor would CLOBBER unsaved node edits. The issue documents the correct approach: mutate the buffer via setYamlText((t)=>setScheduleTrigger(t,...)), read current trigger via parseWorkflowYaml(yamlText) (already imported), header button + informative-tint indicator badge, and consider extracting a shared ScheduleTriggerDialog component consumed by both surfaces.

Side effects: one temp workflow `zz-trinity-schedule-regression-check` remains on my trinity-live test project (project 1d015020) as manual-only (schedule removed) because there is no DELETE-workflow API/endpoint (confirmed: no MapDelete route, no client method). Harmless and clearly labeled.

---

## 2026-07-27T00-55-56: Scoped the #540 viewport re-fit trigger to node-count growth, not node-id-set changes
**By:** Trinity
**What:** Scoped the #540 viewport re-fit trigger to node-count growth, not node-id-set changes
**References:** issue #540, PR #548, apps/web/src/components/VisualWorkflowEditor.tsx
**Why:** Fixing #540 (workflow visual editor doesn't re-center on newly-added nodes), I initially considered diffing the node-id set (any id not previously seen → re-fit) as the "was a node added" signal, per the issue's suggested direction. I switched to a simpler node-count-increase check instead.

Rationale: the editor's Inspector panel lets a user rename a node's *id* itself (handleRenameNode, bound to the "Node id" field's onBlur, calls renameNode() which changes the node's id in the YAML). Under an id-set diff, renaming a node's id looks identical to "old node removed + new node added" — a new id appears that wasn't previously known — which would incorrectly steal the user's pan/zoom on every id rename. A count-based check only fires when the node array actually grows, so id renames, label renames, edge reconnects, and node drags (all count-neutral) never trigger the re-fit; only genuine additions (handleAddNode/handleAddSpecialGate) do. Deletions (count decreases) also don't re-fit, which was fine since React Flow doesn't need to re-center to show fewer nodes.

Implementation: a `FitViewOnNodeAdded` component is rendered as a child of `<ReactFlow>` (not wrapped in an explicit `<ReactFlowProvider>`) and calls `useReactFlow().fitView(...)` inside a `useEffect` keyed on `nodes`, gated by `nodes.length > prevCountRef.current`. `<ReactFlow>` exposes its internal provider context to its own children, so this works without adding a top-level provider around the whole component tree.

---

## 2026-07-28T03-37-58: Shorten and clip the footer version badge at the text span level
**By:** Trinity
**What:** Shorten and clip the footer version badge at the text span level
**References:** #592, apps/web/src/components/shell/LeftNav.tsx, apps/web/src/components/shell/shell.css, apps/web/src/components/GitHubSignIn.tsx
**Why:** Follow-up on reopened issue #592 showed the original fix prevented outright overlap but still let the footer version text spill outside the Fluent Badge pill with a real staging dev string. The badge root had width constraints, but the rendered badge contents still needed their own ellipsis span, and the footer needed to bias more width toward the signed-in user.

Decision: in the left-rail footer, render the badge text through an inner `.aw-rail-footer__version-text` span with nowrap/ellipsis, cap the badge/meta widths, and shorten the displayed footer label to `v<version>` instead of `Alpha v<version>` while keeping the full alpha wording in the tooltip/title. Also let the GitHub sign-in button stretch left so a short username like `sabbour` stays fully visible at the 260px rail width.

---

# Trinity — tool category substring fix

- Date: 2026-07-27
- Area: apps/web timeline

## Decision
Replace `categorizeTool()` substring matching with segment-aware matching based on normalized tool-name parts.

## Why
Naive `.includes()` checks were misclassifying tools like `start_preview` as file-read actions because `preview` contains `view`. The same risk existed for other names such as `code_review`, `pr_review`, `overview`, and similar collisions across category buckets.

## Implementation
- Normalize tool names into lowercase segments split across underscores, hyphens, and camelCase boundaries.
- Match categories against whole segments / phrases instead of arbitrary substrings.
- Keep legitimate variants working (`view_file`, `str_replace_editor`, `search_design_system`, `get_file_contents`, `run_command`, etc.).
- Fall back to `deriveHumanTitle()` for read-category tools that have no file/path arguments so any future edge case degrades gracefully instead of showing a misleading generic `View file` title.

## Validation
- `npm --prefix apps/web run lint`
- `npx vitest run src/__tests__/runTimelineSteps.test.ts src/__tests__/RunTimeline.test.tsx src/__tests__/CoordinatorRunPage.coordUx.test.tsx --config vitest.config.ts`
- Full `npm --prefix apps/web run test` currently hits unrelated pre-existing failures in `SkillsPage.test.tsx` and `azureFluentSystem.test.tsx` in this environment.

---

## 2026-07-28T01-44-35: Treat raw report_intent tool calls as child timeline step boundaries
**By:** Trinity
**What:** Treat raw report_intent tool calls as child timeline step boundaries
**References:** #595, apps/web/src/timeline/runTimelineSteps.ts, apps/web/src/__tests__/runTimelineSteps.test.ts, apps/web/src/__tests__/AgentSessionPanel.test.tsx
**Why:** While fixing issue #595, I found the child/subtask timeline UI assumed every reported intent had already been translated into an `agent.intent` event. In practice, some child-run streams can still arrive with only the raw `tool.call` for `report_intent`, which made the timeline ignore the intent boundary and keep nesting all later activity under a single synthetic Step 1.

Decision: make the frontend timeline builder tolerant of both shapes. When a `tool.call` arrives for `report_intent` and no matching `agent.intent` has opened the current step, the UI now opens a real timeline step from that payload instead of discarding it. If both forms are present, the fallback dedupes against the already-open explicit intent so we do not double-create steps.

This keeps existing newer streams unchanged while making child/subtask chat rendering correct for older or mixed event streams.

---

---

## Processed decision inbox entries — 2026-07-31

---

<!-- Source: decisions/inbox/Morpheus-do-not-expose-coordinator-runs-via-an-openai-respo.md -->

### 2026-07-31T00-28-43: Do not expose coordinator runs via an OpenAI Responses/Conversations surface; if we ship an OpenAI-compatible endpoint at all, scope it to the read-mostly Operator/Assistant conversation and single-agent tasks in background mode, and keep MCP as the primary programmatic surface.
**By:** Morpheus
**What:** Do not expose coordinator runs via an OpenAI Responses/Conversations surface; if we ship an OpenAI-compatible endpoint at all, scope it to the read-mostly Operator/Assistant conversation and single-agent tasks in background mode, and keep MCP as the primary programmatic surface.
**References:** specs/orchestration-runs/coordinate-a-multi-agent-goal.md, specs/orchestration-runs/run-a-single-agent-task.md, specs/orchestration-runs/steer-and-recover-orchestrations.md, specs/review-merge/approve-request-changes-or-decline.md, specs/agent-execution-sandbox/govern-agent-tools-and-questions.md, specs/mcp-integrations/drive-agentweaver-through-mcp.md, specs/mcp-integrations/browser-chat-control-console.md, #14, #15, #16, #18, #19, #33, #50, #346, #394, .specify/memory/constitution.md
**Why:** ## Context

Ahmed asked whether Agentweaver coordinator workflows for a project could be exposed via the OpenAI Messages/Conversation API, and whether that helps. Tank covers wire protocol/auth; Trinity covers product/UX. This decision records the runtime/semantics verdict.

## Verified runtime facts (repo)

- Coordinator run lifecycle is a multi-phase workflow with a MAF RequestPort suspend/resume at the outcome-spec confirmation gate (apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs). No child work dispatches before confirmation (specs/orchestration-runs/coordinate-a-multi-agent-goal.md).
- RunStatus has 12 states including AwaitingReview, Committing, Merging, Merged, Declined, MergeFailed, AssembleReady, Idle (packages/Agentweaver.Domain/RunStatus.cs). A chat "response" has ~4.
- EventTypes.cs defines 76+ event types, ~40 of which are coordinator/subtask specific (coordinator.work_plan, coordinator.topology, subtask.dispatched, coordinator.assembly_*, coordinator.child_question, ...).
- Blocking human gates: IToolApprovalGate.WaitForApprovalAsync and IQuestionGate.AskAsync suspend the agent indefinitely-safe until a human answers.
- Steering is a first-class in-flight mutation: SteeringSignal + CoordinatorSteeringService with four conscious directions (in_place_steer, dispatch_fresh, proceed, advisory).
- Time bounds: agent TotalTurnTimeout 01:10:00; run watch loop timeout 4h; Coordinator:AssemblyReviewTimeoutMinutes default 60; Runs:ReviewTimeoutHours default 24.
- Side effects: git worktrees, sandbox pods, preview processes, and PR publication (specs/orchestration-runs/push-pr-as-execution-step.md).
- Constitution Principle IV: "There MUST be exactly two clients over the API: an MCP server and a Web UI." An OpenAI-compatible surface is a third client and needs an explicit constitutional amendment or a Complexity Tracking justification.

## Verified external facts (2026)

- Assistants API (Threads/Messages/Runs) is retired 2026-08-26; Threads -> Conversations, Runs -> Responses. https://developers.openai.com/api/docs/assistants/migration
- Responses API background mode: background:true, poll GET /v1/responses/{id} while queued|in_progress, POST /cancel, and resumable streaming via ?stream=true&starting_after=<sequence_number>. https://developers.openai.com/api/docs/guides/background
- Conversations API stores heterogeneous items (message, function_call, function_call_output, reasoning), not just messages.
- MCP 2026-07-28: Tasks graduates to a first-class extension (tools/call returns a task handle; tasks/get|update|cancel); elicitation gets Multi Round-Trip Requests with InputRequiredResult + requestState; sampling, roots and logging are DEPRECATED. https://blog.modelcontextprotocol.io/posts/2026-07-28-release-candidate/

## Decision

1. Do NOT model a coordinator run as a Response or a Conversation turn. The fatal mismatches are (a) no first-class blocking human-approval state, (b) no in-flight steering primitive, (c) 12 terminal/intermediate run states collapsing to ~4 response statuses, (d) irreversible side effects behind a surface whose clients retry aggressively.
2. If a compatibility surface is built, scope it to: the Operator/Assistant conversation (apps/Agentweaver.Api/Assistant/AssistantRunService.cs, #346 / #50) as a synchronous Responses endpoint, and single-agent tasks (#14) as background:true responses that terminate at AwaitingReview without merging.
3. Human gates are NEVER auto-approved on this surface. Recommended representation: background mode + out-of-band approval in the Agentweaver UI/MCP, with the pending gate surfaced as a non-authoritative status annotation. Reject "next user message = approval" (violates content-bound approval semantics in specs/review-merge/approve-request-changes-or-decline.md) and reject auto-approve (violates Constitution IX/X and #19).
4. Any such endpoint requires, before launch: per-token run budgets, idempotency keys on run creation, rate limits, a hard "no merge, no push, no PR" capability ceiling for OpenAI-compatible callers, and cost caps that fail closed.
5. MCP remains the primary programmatic surface. The 2026-07-28 Tasks extension plus MRTR elicitation is a strictly better semantic fit for coordinator runs than anything the Responses API offers today.

## What I will not do

- Not add a third first-class client without amending Principle IV.
- Not implement a shim that lies about run state (e.g. reporting completed when the run is AwaitingReview).
- Not allow an OpenAI-compatible caller to reach the merge/push boundary.
---

<!-- Source: decisions/inbox/Morpheus-publish-is-a-legal-workflow-node-but-only-as-a-dec.md -->

### 2026-07-31T00-49-41: publish is a legal workflow node, but only as a declarative post-approval tail node whose effect is converged by an external reconciler — never an inline mid-graph deployment
**By:** Morpheus
**What:** publish is a legal workflow node, but only as a declarative post-approval tail node whose effect is converged by an external reconciler — never an inline mid-graph deployment
**Why:** ## Verdict

`publish` IS a legal node kind. There is direct precedent: `WorkflowNodeType.OpenPullRequest` (apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:23-28) is already a platform-owned, non-agent, deterministic node whose effect (a GitHub PR) survives the run. `WorkflowNodeType.Merge` is documented as "an irreversible action gated by review". So node purity w.r.t. the outside world is NOT an executor invariant and never was.

BUT the graph's real invariant is structural, not type-based: in DefaultWorkflowTemplate.cs the two externally-side-effecting nodes (`merge`, `push-pr`) sit strictly in the **post-approval tail** — after the human-review gate, with only `scribe -> done` downstream. Nothing that can fail-and-loop-back exists after them. `publish` must inherit that placement rule.

## Execution guarantees today

- Runs start via `InProcessExecution.RunStreamingAsync(workflow, input, checkpointManager, runId, ct)` (RunWorkflowFactory.cs:1439) — checkpointed per superstep.
- Resume is `InProcessExecution.ResumeStreamingAsync` (RunWorkflowFactory.cs:1581) from the LATEST checkpoint. Superstep granularity ⇒ **at-least-once** for any executor in the un-checkpointed tail.
- WorkflowRestartService.RecoverAsync: stranded `InProgress` root runs are FAILED, never replayed ("Root turns remain non-replayable"); `Committing` and `Merging` are REVERTED to `AwaitingReview` and then resumed from checkpoint — so `merge`/`push-pr` CAN re-execute after a crash. Their safety comes from the external system being naturally re-entrant (git merge is a no-op when already merged; GitHub 422s a duplicate PR), plus `OpenPullRequestTurnExecutor` catching every exception and emitting a `failed` step event instead of throwing.
- Existing effectively-once primitive: `IRevisionEffectConfirmer` + `IRevisionCheckpointIndex` (apps/Agentweaver.Api/Infrastructure/) — a durable `(directiveId, attempt, runId)` marker committed in the SAME `SaveChanges` as the first checkpoint insert, with a monotonic checkpoint-watermark corroboration fallback on the file store. This is the pattern to reuse for publish.

## Consequences

1. Publish needs effectively-once. Gap closer = deterministic idempotency key `(publishedAppId, run.TreeHash)` persisted CAS-style (the existing `UPDATE ... WHERE status = <expected>` + rowcount pattern in IRunStore), plus deploy-by-digest so a retry converges on the identical object.
2. No compensating-action concept exists in the executor. `merge` proves the answer: once applied, it is NOT undone; the graph terminalizes. Therefore publish must NOT be rolled back by a later node failure. Enforce by placement: publish is a post-approval tail node, and a loader validation should reject a `publish` node with any downstream node other than scribe/terminal.
3. Lifetime mismatch is real (agent TotalTurnTimeout 1h10m; watch loop 4h; app lives indefinitely). Resolution: the node **declares intent** into `PublishedAppRevision` (desired digest + app-scoped config) and a `PublishedAppReconciler` BackgroundService converges Deployment/Service/HTTPRoute, exactly mirroring SandboxPreviewReaperService (~60s sweep, pure `PreviewReaper.Decide`, "entirely driven by cluster state ... no in-memory registry, so it is replica-safe"). Ownership transfers from the run to the reconciler at the DB write. Node completion == intent durably recorded, NOT rollout healthy.
4. Content refresh vs code regeneration is a REAL split and it dissolves the pinned-vs-tracking debate. A run that only produces new *data* for an already-published app needs no image build and no new revision — it needs a data-plane write to a volume/blob the running app reads. Only a run that changes the app's SOURCE requires a build + new revision. The headline bug-triage-report example is case (A): **no container build, so issue #582 does not block it.** That is the single most valuable finding here. It argues for two distinct node types (or one node with an explicit mode), not one node with a policy flag.
5. Inputs-as-nodes: a blocking input node is representable today (MAF RequestPort + `RequestInfoEvent` + `PendingRequestStore` + `RunStatus.AwaitingReview`; watchdog is Paused while parked). But for a SCHEDULED run it is a guaranteed deadlock — and the precedent already exists: `Runs:ReviewTimeoutHours` (default 24h, CoordinatorReconciler.cs:108) terminalizes an idle review as failed/abandoned. So an unattended run hitting a blocking input node would just die 24h later. Ruling: inputs must be BOUND AT TRIGGER TIME, not awaited mid-graph. Schedule/event triggers must supply values (or defaults) into the backlog task; a workflow whose input node has no default and no trigger-supplied value must be REFUSED at schedule-validation time, not deadlocked at runtime.
</body>
<parameter name="references">["Tank", "Trinity", "issue-582", "apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs", "apps/Agentweaver.Api/Runs/WorkflowRestartService.cs", "apps/Agentweaver.Api/Infrastructure/IRevisionEffectConfirmer.cs", "apps/Agentweaver.Api/Sandbox/Preview/PreviewReaper.cs", "packages/Agentweaver.AgentRuntime/Workflow/OpenPullRequestTurnExecutor.cs"]
---

<!-- Source: decisions/inbox/Squad-Coordinator-app-publishing-one-publish-verb-project-members-de.md -->

### 2026-07-31T01-15-32: App publishing: one Publish verb, project-members default audience, per-project namespaces; specs deferred
**By:** Squad-Coordinator
**What:** App publishing: one Publish verb, project-members default audience, per-project namespaces; specs deferred
**References:** specs/agent-execution-sandbox/build-images-with-rootless-buildkit.md, specs/agent-execution-sandbox/preview-sandbox-apps.md, apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs, k8s/base/rbac-api.yaml, k8s/base/gateway-preview.yaml, apps/Agentweaver.Api/Runs/WorktreeOperationsAdapter.cs, apps/web/src/utils/workflowYaml.ts
**Why:** Ahmed's decisions after the seven-agent exploration of app publishing (2026-07-31):

1. ONE "Publish" verb in the UI — NOT two ("Publish report" / "Publish app"). This overrides the recommendation from both Trinity and the rubber-duck review. Implication: the document-vs-container substrate choice becomes a SYSTEM decision, invisible to the user. This raises the stakes on the A/B discriminator, which the duck verified is NOT derivable from Run.TreeHash (root tree only) without a declared app-source path set.

2. Default audience for a published REPORT stays "project members". This overrules Trinity's revision (she argued link/org-scoped, on the grounds that an inside-only default rebuilds the internal dashboard she had already killed as strictly worse than the existing board UI). Ahmed has now overruled her twice on this axis. Open question to resolve: who the reader concretely is.

3. Namespace model for published apps = PER-PROJECT. This overrides Link's recorded phase-1 ledger entry specifying one shared 'agentweaver-published' namespace. Link's rationale was that per-app namespaces break the single-namespace RBAC model and the Gateway's allowedRoutes.namespaces.from: Same constraint — those costs are accepted, not dismissed.

4. Do NOT file the open_pull_request frontend bug separately (accepted by the server loader via TryParseNodeType, missing from apps/web/src/utils/workflowYaml.ts WORKFLOW_NODE_TYPES, so the visual editor cannot author a PR node). It should be folded into the publish node work instead.

5. No spec files or GitHub issues yet — exploration continues.

6. Approved a cleanup pass on .squad/decisions.md (404KB after 98 backlogged inbox entries were merged).

Settled earlier in the same session and unchanged: separate container registry; scoped public gateway endpoint for API access from published apps; indefinite hosting (no mandatory TTL); ACA-style immutable revisions with one active revision and pointer-move rollback; deploy-by-digest; retention ~10 minted revisions.
---

<!-- Source: decisions/inbox/Squad-Coordinator-published-app-revision-model-should-follow-azure-c.md -->

### 2026-07-31T00-24-34: Published-app revision model should follow Azure Container Apps semantics (Ahmed's preference)
**By:** Squad-Coordinator
**What:** Published-app revision model should follow Azure Container Apps semantics (Ahmed's preference)
**References:** Tank published-apps backend analysis 2026-07-31, Link publish topology analysis, Seraph publish security analysis, Trinity publish UX analysis, specs/agent-execution-sandbox/build-images-with-rootless-buildkit.md (#582)
**Why:** Ahmed reviewed Tank's proposed PublishedApp + PublishedAppRevision split for the "publish apps from Agentweaver" feature and endorsed it, explicitly citing Azure Container Apps as the reference model: "I like the revisions approach. Similar to what Azure Container Apps offers."

Implication for the spec: the revision design should be evaluated against ACA's concrete semantics rather than invented from scratch — immutable revisions with a name/suffix, active vs inactive revisions, single-revision vs multiple-revision mode, traffic splitting by weight across revisions, revision labels for stable per-revision URLs, and per-revision scale settings. Which of those to adopt vs deliberately drop is still open and should be decided in the spec.

Confirms Tank's core structural call: PublishedApp = stable identity + desired state; PublishedAppRevision = immutable build+deploy snapshot; rollback is a pointer move, not a rebuild.

Prior context from the same session: Ahmed also chose per-project namespaces, a separate registry for generated images, scoped-public API access for published apps, project-members-only default audience, and indefinite (not TTL-bounded) hosting.
---

<!-- Source: decisions/inbox/Tank-content-refresh-and-code-regeneration-are-distinct.md -->

### 2026-07-31T00-44-31: Content refresh and code regeneration are distinct publish mechanisms; content refresh needs no container, no BuildKit, and no #582 — it needs typed workflow outputs, an artifact store, a publish workflow node, and a platform-owned static renderer.
**By:** Tank
**What:** Content refresh and code regeneration are distinct publish mechanisms; content refresh needs no container, no BuildKit, and no #582 — it needs typed workflow outputs, an artifact store, a publish workflow node, and a platform-owned static renderer.
**References:** #582, #49, #394, #53, #11, #56, #397, #21, Trinity, Ahmed Sabbour
**Why:** ## Context

Ahmed asked whether an Agentweaver workflow can generate and publish its own app —
"blueprint runs a workflow that triages bugs and creates a report, click publish report,
app gets created and kept up to date with periodical runs." The coordinator's hypothesis
was that this conflates content refresh (fixed code, fresh data) with code regeneration
(run rewrites source, rebuild, new image). Backend scope: test that against the code.

## Verdict — hypothesis confirmed, with one refinement

Three mechanisms, not two:

- **A1 — content refresh, platform-owned renderer.** No per-app code at all. A run
  produces a typed artifact; a platform-owned renderer serves it. No LLM-authored code
  ever reaches production. No image, no registry, no BuildKit, no #582.
- **A2 — content refresh, app-owned renderer.** LLM-authored app built ONCE, reviewed,
  digest-pinned; scheduled runs push only data into the running app. Needs #582 for the
  first build; the recurring path is data-only.
- **B — code regeneration.** Every run rewrites source, rebuilds, mints a revision.
  Needs #582 plus a mandatory human gate per revision.

Ahmed's example maps to **A1**. "Click publish report" is a publish action on an
artifact, not a code-generation event.

Refinement to the hypothesis: it assumed (A) still has an image ("the app's code/image is
FIXED"). For A1 there need be **no image at all**.

## Evidence from the codebase

- Run output is already durably addressable by commit: `Run.WorktreeBranch` /
  `Run.TreeHash` (packages/Agentweaver.Domain/Run.cs:20-21); the API already serves file
  trees and blobs straight off `agentweaver/integration/{runId}` with LibGit2Sharp and no
  worktree (apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs:228-330).
- No blob-storage dependency exists in the repo (no Azure.Storage reference anywhere), so
  a new object store is a net-new dependency; git + EF are already present.
- Scheduled triggers are complete end-to-end and land as ordinary Ready backlog tasks with
  per-occurrence idempotency (apps/Agentweaver.Api/Workflows/WorkflowScheduleTriggerService.cs,
  WorkflowTriggerBacklogFactory.cs). Refresh cadence needs no new scheduler.
- The deterministic workflow-action family already exists:
  `WorkflowNodeType.OpenPullRequest` -> NodeClassifier -> NodeExecutorRegistry ->
  `IRunWorkflowWiringSupport.ResolveOpenPullRequestNode` ->
  packages/Agentweaver.AgentRuntime/Workflow/OpenPullRequestTurnExecutor.cs (pass-through,
  non-fatal failure, explicit skip semantics). `publish` is the next member of that family.
- Dynamic per-host routing already exists and is replica-safe: per-target Service +
  HTTPRoute on the wildcard preview Gateway
  (apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs, k8s/base/gateway-preview.yaml).
  A published app reuses this with a non-reaped `{slug}-app.{zone}` route.

## Decisions

1. **`publish` becomes a first-class workflow node type**, modelled exactly on
   `open_pull_request`. Slug is pre-declared in the definition (never agent-chosen at
   runtime — it is user-visible DNS and a governance surface); the app is created on first
   run and updated thereafter. Publish is pass-through: failure never fails the run and
   never moves the current-revision pointer.
2. **`runtime: container` + `gate: auto` is rejected at load/validation time.** Unattended
   LLM-authored code reaching production violates Principle IX. Static content refresh may
   be auto-gated; container publish always requires an explicit human approval event.
3. **The artifact store is a small EF index over content-addressed storage**, not a new
   object store: inline payload for small typed artifacts, plus a pinned commit sha in a
   protected `agentweaver/published/{slug}/{revision}` ref for file bundles. Revisions are
   immutable; the pointer move is the last, atomic step.
   Caveat recorded: git repos live on the shared RWX Azure Files PVC
   (k8s/base/pvc-workspace.yaml), which is a documented weak boundary — published commits
   must also be pushed to the connected remote so the PVC is a cache, not the record.
4. **Artifacts are typed, not opaque blobs.** A platform renderer cannot render an unknown
   shape. This promotes the typed-workflow-I/O prerequisite spec from "nice to have" to a
   hard dependency for the A1 path.
5. **Runtime delivery: projection over fetch.** For container apps the reconciler projects
   the artifact into the pod as a mounted Secret/ConfigMap (<= ~512 KiB, kubelet sync
   latency documented), so the app holds no credential and needs no egress. This reinforces
   the settled "never a NetworkPolicy hole" decision instead of eroding it. The scoped
   public endpoint becomes the fallback for large payloads, not the primary path.
6. **`RuntimeKind` of `static_artifact` vs `container` under one `PublishedApp`.** Stable
   identity, hostname, audience, freshness state, and current-revision pointer are shared;
   only the revision's pin field differs (artifact digest vs image digest). Rollback is a
   pointer move in both cases. A static app can later be re-published as a container under
   the same slug.
7. **Freshness state is application-scoped, not revision-scoped.** `LastRefreshRunId`,
   `LastRefreshStatus`, `LastRefreshedAt`, `LastSuccessfulRefreshAt`,
   `StalenessThresholdMinutes`. Rule: anything a rollback must NOT undo is
   application-scoped. `RefreshSchedule` is NOT duplicated on `PublishedApp` — the workflow's
   existing `trigger:` remains the single source of truth; the app stores only a derived,
   read-only `NextRefreshAt`.
8. **On refresh failure the app serves stale content with an exposed staleness banner** —
   never an error for a previously-good app, never a silent stale page. A never-successful
   app serves a provisioning placeholder, not 404/500.
9. **Overlapping runs: monotonic pointer guard.** A revision may only become current if its
   producing run started later than the current revision's; a late-finishing older run
   records a `superseded` revision without moving the pointer. Identical content records
   `no_change`, mirroring #394's no-publishable-change semantics.
10. **Blueprints need no new entity.** Add an optional `published_apps:` array to
    `BlueprintDto` (apps/Agentweaver.Api/Blueprints/BlueprintDtos.cs); package import
    (GitHubBlueprintPackageImportService.cs) validates slug collisions and must never
    auto-approve a container publish.

## Roadmap consequence

Ahmed's headline example ships entirely without #582. #582 stops being a blocker for the
demo and becomes a blocker only for genuinely generated interactive apps. The load-bearing
prerequisite is typed workflow I/O. Revised critical path: typed workflow I/O -> artifact
store -> PublishedApp schema -> publish node (static only) -> static renderer + routing ->
scheduled refresh (demo point) -> then, independently, #582 -> container runtime.

## Conflicts with settled decisions

None broken. Two refinements: "separate registry for generated images" and "namespace per
project" apply only to `RuntimeKind=container`; the scoped public callback endpoint becomes
optional rather than central because projection removes the app's need for any credential.
---

<!-- Source: decisions/inbox/Tank-if-we-expose-agentweaver-workflows-on-an-openai-co.md -->

### 2026-07-31T00-28-23: If we expose Agentweaver workflows on an OpenAI-compatible surface, target the Responses API (background+stream+function_call gates), not Chat Completions or Conversations
**By:** Tank
**What:** If we expose Agentweaver workflows on an OpenAI-compatible surface, target the Responses API (background+stream+function_call gates), not Chat Completions or Conversations
**References:** apps/Agentweaver.Api/Endpoints/RunEndpoints.cs, apps/Agentweaver.Api/Endpoints/OAuthServerEndpoints.cs, apps/Agentweaver.Api/Auth/OAuth/McpTokenService.cs, apps/Agentweaver.Api/Security/ProjectAuthorization.cs, apps/Agentweaver.Mcp/Tools/RunTools.cs, apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs, packages/Agentweaver.Domain/EventTypes.cs, specs/mcp-integrations/drive-agentweaver-through-mcp.md, Morpheus (semantic fit), Trinity (product framing), Seraph (auth ruling required)
**Why:** ## Context

Ahmed asked whether Agentweaver coordinator workflows for a project could be exposed via "the OpenAI Messages or Conversation API", and whether that helps. Scope of this decision: the wire contract / endpoints / persistence / auth shape only. (Morpheus owns the semantic fit question; Trinity owns whether this replaces the generated-projection-app idea.)

## Verified external facts (2026-07)

- The Assistants API (Threads/Messages/Runs) is **shut down 2026-08-26** — 26 days from now. It is not a viable target. https://developers.openai.com/api/docs/deprecations and https://developers.openai.com/api/docs/assistants/migration
- Migration mapping is: assistants -> prompts, threads -> conversations, runs -> responses, run steps -> items.
- **Responses API** is the first-class surface. It has `background: true` + `stream: true` with `sequence_number` on every event and resume via `GET /v1/responses/{id}?stream=true&starting_after=N`; plus `GET /v1/responses/{id}` polling and `POST /v1/responses/{id}/cancel`. Statuses: queued / in_progress / completed / incomplete / failed / cancelled. https://developers.openai.com/api/docs/guides/background
- **Conversations API** is durable item storage that pairs with Responses (`conversation` param, or stateless `previous_response_id` chaining). https://developers.openai.com/api/docs/guides/conversation-state
- **Chat Completions** is NOT deprecated and OpenAI intends to support it indefinitely, but gets no new features. It remains what most third-party clients speak.
- Function calling in Responses is a **client-executed loop**: the server returns a `function_call` output item; the client replies with a `function_call_output` item + `previous_response_id`. https://developers.openai.com/api/docs/guides/function-calling

## Decision

1. **Do not target Chat Completions or the Assistants API.** Chat Completions has no async/background mode, no resumable stream cursor, and no durable server-side item store — a 40-minute gated coordinator run has nowhere to live. Assistants is dead in 26 days.
2. **If we build this at all, target the Responses API in background+stream mode**, optionally paired with Conversations for multi-turn. Concretely:
   - `POST /v1/responses` with `background:true, stream:true` -> creates an Agentweaver run; returns `resp_<runId>`.
   - `GET /v1/responses/resp_<runId>` -> run status projection (queued/in_progress/completed/failed/cancelled/incomplete).
   - `GET /v1/responses/resp_<runId>?stream=true&starting_after=N` -> reuse the existing `IRunEventStream` cursor. `starting_after` maps 1:1 onto our existing `Last-Event-ID -> fromSequence` replay in `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:318`.
   - `POST /v1/responses/resp_<runId>/cancel` -> existing `POST /api/runs/{id}/cancel`.
3. **Human review gates map to `function_call` output items.** This is the single strongest technical argument for Responses over everything else: `review_run`, `confirm_outcome_spec`, `approve_tool`, `answer_question` become function tools the *client* must satisfy, and the client resumes with `previous_response_id` + `function_call_output`. This is the same shape the Assistants API expressed as `requires_action`. Our four existing gate endpoints (`/api/runs/{id}/review`, `/tool-approvals`, `/questions/{requestId}/answer`, `coordinator_outcome_spec_confirm`) already have exactly this arity.
4. **`model` = `agentweaver/{projectSlug}/{workflowId}`**, enumerated per-caller by `GET /v1/models` filtered by the caller's project RBAC. Do not invent a per-project base URL.
5. **Auth: reuse the existing OAuth 2.1 AS as-is.** `Authorization: Bearer <RS256 JWT>` is wire-identical to `Bearer sk-...`; no client cares about the token's internal shape. Requires a new scope alongside `mcp:invoke` (`McpTokenService.AccessTokenScope`). Project scoping comes from the `model` string, not the path, so `ProjectAuthorization.RequireAccessAsync` must be called with the project resolved from the model name. **Seraph must rule** on: (a) whether a bearer token that carries no project binding is acceptable when the project selector is attacker-controlled request body content, (b) whether we need per-project tokens (RFC 8707 `resource` binding, as already done for `{issuer}/mcp`) instead.
6. **Do not adopt the Conversations API in phase 1.** Adopting it means we own conversation storage, item ordering, retention, fork and delete on top of the run store. Use stateless `previous_response_id` chaining first — `previous_response_id: resp_<runId>` is enough to express "continue this run past a gate" and needs zero new tables.

## Non-decision / honest caveat

The capability delta over what already ships is small. `apps/Agentweaver.Mcp/Tools/RunTools.cs` already exposes `run_task` (submit, watch to gate, return artifacts), `run_watch`, `run_review`, and `CoordinatorTools.cs` exposes the outcome-spec gates — an MCP client already drives a gated workflow end-to-end today. The OpenAI surface buys **client reach** (Open WebUI, LibreChat, any OpenAI SDK) and nothing semantically new. Recommend treating it as a distribution/adapter question, not a platform capability, and gating the build on a named external consumer.

## Consequences

- No change to the run store, event stream, or gate endpoints — this is a projection layer, not a re-architecture.
- New: an `OpenAiCompatEndpoints.cs` translating the run event taxonomy (`packages/Agentweaver.Domain/EventTypes.cs`) into `response.*` streaming events; a model-name resolver; one new OAuth scope.
- Lossy by design: ~70 noun.verb event types collapse into `response.output_text.delta` + `response.output_item.added`. Anything a chat client can't render (topology graphs, subtask fan-out, diffs) is either dropped or smuggled into `metadata`.
---

<!-- Source: decisions/inbox/Tank-publish-belongs-in-the-graph-as-a-node-kind-preced.md -->

### 2026-07-31T00-51-18: Publish belongs in the graph as a node kind (precedent: open_pull_request); inputs do not — Trigger proves boundary contract lives outside nodes, so run parameters stay a top-level inputs: block while mid-run human input can later be a node.
**By:** Tank
**What:** Publish belongs in the graph as a node kind (precedent: open_pull_request); inputs do not — Trigger proves boundary contract lives outside nodes, so run parameters stay a top-level inputs: block while mid-run human input can later be a node.
**References:** #49, #394, #53, #582, Ahmed Sabbour, Trinity
**Why:** ## Context

Ahmed sharpened the publishing question into two: (1) can publish be a workflow NODE
declared in blueprint YAML and wired with edges, and (2) can INPUTS be nodes too, using
`WorkflowDefinition.Trigger` as the precedent — as an alternative to the `inputs:`/`outputs:`
JSON-Schema block the team had assumed. Answered against the code.

## What a node structurally is (verified)

- The discriminator already exists and is `WorkflowNode.Type` (`WorkflowNodeType`, 12 members),
  classified by `NodeClassifier.Classify` into an internal `NodeKind`. `WorkflowNode.Kind` is a
  RENDER hint ("live"/"action"/"gate"), NOT a discriminator — do not overload it.
- Not all nodes are agent dispatch: `OpenPullRequest` is a deterministic non-LLM action,
  `Merge`/`Scribe`/`Terminal` are platform-owned, `Check` is a gate.
- `RunWorkflowGraphBinder.WireFull` binds onto a MAF TYPED message-passing graph. Edges carry
  message types (`AgentTurnOutput`, `WorkflowReviewDecision`, `MergeInput`/`MergeOutput`,
  `ScribeTurnInput`). Every `(fromKind, toKind, when)` transition is ENUMERATED in
  `TryWireCanonicalEdge` + `CanBindTransition`; there is no generic fallthrough and an unmapped
  transition throws `WorkflowBindException` (fail-closed). `IRunWorkflowWiringSupport` is a
  catalogue of ~15 message adapters.
- `GetBindabilityErrors` rejects any non-terminal node with no outgoing edge.

## Decisions

1. **`publish` IS a node kind (option i).** The "it outlives the run" objection is already
   answered in this codebase: `open_pull_request` creates a PR that outlives the run and `merge`
   mutates the originating branch. The graph is already a graph of effects, not of LLM turns.
   Rejected: stage-level (WorkflowStageDefinition is `{Id,Label,Order}` — Kanban columns with
   ZERO execution semantics); output-binding-outside-the-graph (loses ordering relative to the
   review gate, which is the governance property that matters); trigger-shaped sink (a source
   needs no wiring, a sink does — it must consume a typed message and must be ordered after
   gates; putting it outside the graph forces re-deriving gate state from run state, exactly the
   drift the fail-closed binder exists to prevent).
2. **The node's effect stays transactional and small.** It mints an immutable revision and
   conditionally moves a pointer. A separate background reconciler (the SandboxPreviewService /
   reaper pattern) owns the long-lived Deployment/HTTPRoute. The node records intent; it does not
   own the outliving resource.
3. **Cheapest legal executor shape is `Executor<AgentTurnOutput, AgentTurnOutput>`** —
   pass-through, exactly `OpenPullRequestTurnExecutor`. Zero new message adapters needed.
   Empirical cost precedent: `OpenPullRequest` needed 4 enumerated transitions across two switch
   statements. `publish` needs a comparable handful, plus an outgoing edge to scribe (it cannot
   be a graph leaf).
4. **`Trigger` is NOT the precedent for input-as-node — it argues the OPPOSITE.** Verified:
   `Trigger` is a top-level document property, never a node, never a MAF executor, absent from
   `WORKFLOW_NODE_TYPES`, and the visual editor manipulates it as a document key
   (`workflowYaml.ts` `setScheduleTrigger`/`setEventTrigger` -> `doc.contents.set('trigger')`).
   It sits outside the graph precisely because it is a SOURCE with no incoming message and no
   predecessor. It is a precedent for "boundary concerns live outside `nodes:`", which supports a
   top-level `inputs:` block.
5. **Split the concept instead of picking a side.** Run PARAMETERS (known before the run) go in a
   top-level `inputs:` block — statically enumerable, trivially renderable as a start-form, which
   the projection-app idea requires. MID-RUN human input becomes a node kind later — it is
   positional by nature and the runtime primitive ALREADY EXISTS (MAF request port,
   `PendingRequestRecord`, checkpoint resume via `WorkflowRestartService`), so it is the
   human-review gate with a different payload. `outputs:` stays a top-level block naming typed
   artifacts that the `publish` node references by name.
   Rule that decides every case: **contract goes in the document, control flow goes in the graph.**
6. **Static form derivation is the decisive cost.** If inputs are nodes, "render a form from the
   definition alone" becomes "render from the nodes reached before the first suspension" — and
   with a `check` node upstream of an `input` node that subset is NOT statically decidable. The
   guarantee only survives if input nodes are constrained to be unconditionally reachable from
   `start`, which must then be validated and enforced.
7. **Idempotency: 40 scheduled runs must produce ~0-3 revisions, not 40.** Four rules:
   content-digest dedup (equal digest -> `no_change` event, no revision, no pointer move, but
   freshness timestamps still advance); a unique `(publishedAppId, runId, nodeId)` key so
   checkpoint-resume cannot double-publish; the monotonic pointer guard for overlapping runs; and
   retention of 10 counting only MINTED revisions, with `no_change` recorded as cheap
   `PublishedAppEvent` rows. Result: the rollback list shows the last 10 real changes, not the
   last 10 hours.
8. **Volatile-field exclusion is mandatory, not optional.** Any LLM-authored report embeds a
   generation timestamp, which defeats byte-level dedup and would mint a revision every run. The
   digest must be computed over the typed artifact's semantic payload with declared volatile
   fields (e.g. a reserved `generated_at`) excluded. You cannot do this with an opaque blob —
   this is an independent, decisive argument for typed artifacts.

## Drift found (pre-existing, worth fixing alongside)

`open_pull_request` is accepted by the server loader
(`WorkflowDefinitionLoader.TryParseNodeType`) but is MISSING from the frontend's
`WORKFLOW_NODE_TYPES` (`apps/web/src/utils/workflowYaml.ts:16-29`), so the visual editor cannot
author a PR node. Adding `publish` to only one list repeats the bug.

Related asymmetry to design around: an unknown node TYPE fails closed with a clear message, but
unknown node PROPERTIES are silently ignored (`IgnoreUnmatchedProperties()`). A mistyped
`aplication:` would publish to a null slug. The loader must explicitly require `app` and
`runtime` when type is `publish`. Also: do NOT reuse the shared DTO's generic `Title`/`Body`/
`Base`/`Head`/`Draft` keys — those belong to the PR node.
---

<!-- Source: decisions/inbox/Tank-published-apps-adopt-an-aca-style-revision-model-r.md -->

### 2026-07-31T00-30-14: Published apps adopt an ACA-style revision model: revision-scope vs app-scope split, single-active-revision mode in v1 (multi-revision deferred, schema-compatible), no traffic splitting, no revision labels/per-revision URLs in v1, Inactive revision state, and Agentweaver-owned registry retention (keep last 10 / 30d, digest-safe purge only)
**By:** Tank
**What:** Published apps adopt an ACA-style revision model: revision-scope vs app-scope split, single-active-revision mode in v1 (multi-revision deferred, schema-compatible), no traffic splitting, no revision labels/per-revision URLs in v1, Inactive revision state, and Agentweaver-owned registry retention (keep last 10 / 30d, digest-safe purge only)
**References:** Ahmed Sabbour, Link (k8s topology / registry GC), Seraph (security: digest pinning, per-project namespace), Trinity (UI), #21 preview-sandbox-apps, #582 build-images-with-rootless-buildkit, decisions.md 2026-07-31T00-09-10 published apps control-plane design, decisions.md 2026-07-30 missing Postgres migration
**Why:** Follows Ahmed's "I like the revisions approach. Similar to what Azure Container Apps offers." Verified ACA's current (2026) model against Microsoft Learn before mapping it.

VERIFIED ACA FACTS USED
- Revisions are immutable, versioned snapshots; changes split into revision-scope (properties.template: revisionSuffix, containers/images/env/resources/probes, scale rules) which DO create a revision, and application-scope (properties.configuration: activeRevisionsMode, ingress incl. traffic + labels, secrets, registry credentials, dapr) which do NOT.
  https://learn.microsoft.com/en-us/azure/container-apps/revisions#change-types
  https://learn.microsoft.com/en-us/azure/container-apps/azure-resource-manager-api-spec
- Modes: single (default; old revisions auto-deprovisioned, zero-downtime cutover only when the new revision passes startup/readiness and scales to match) vs multiple (weighted traffic, manual activate/deactivate). Deployment labels mode is preview.
- Labels give a revision a stable dedicated URL (app---label.<envdomain>); a label maps to one revision at a time and moves between revisions keeping the URL. Revision names are {app}--{suffix}; suffix is lowercase alnum/dashes, no "--", <=64 chars.
- Inactive revisions retained: default 100, tunable via --max-inactive-revisions (preview). Inactive revisions are free and can be reactivated.
- Scale rules are per revision and min replicas may be 0 (scale to zero).
- ACR: `acr purge` deletes tag references by default; deleting untagged manifests is explicitly unsafe for digest-pull consumers.
  https://learn.microsoft.com/en-us/azure/container-registry/container-registry-auto-purge

DECISIONS FOR AGENTWEAVER
1. ADOPT the revision-scope / application-scope split as a first-class, enforced rule. PublishedAppRevision carries everything that determines the running container: SourceCommitSha, SourceTreeHash, SourceRunId, DockerfilePath, DockerfileOrigin, BuildContextPath, ImageRef (digest), Port, EnvJson (non-secret), resource requests/limits, probe config, replica bounds. PublishedApp carries identity and policy: Slug, DisplayName, Kind, SourceMode, tracked run/workflow, Hostname, DesiredState, IdleSuspendMinutes, Visibility/audience, ShareLinkToken, OAuthClientId, CurrentRevisionId/TargetRevisionId, ObservedStatus/ObservedAt, audit fields.
2. Port moves from PublishedApp to PublishedAppRevision (it is a property of the built image, and an LLM can change it between revisions). Hostname stays app-scoped.
3. Secrets are app-scoped by reference only (SecretRefsJson on the app; values in a namespace Secret). Changing a secret value does NOT create a revision; it triggers a restart of the current revision, mirroring ACA.
4. SINGLE ACTIVE REVISION in v1. No weighted traffic splitting. Reason: LLM-generated, mostly single-container, mostly stateless apps whose audience is project members - canary percentages are ceremony without the telemetry to act on them. Multi-revision is additive later: PublishedApp gains RevisionMode plus a PublishedAppTrafficWeight child table; nothing already shipped has to change shape. CurrentRevisionId/TargetRevisionId remain the single-mode fast path.
5. NO revision labels / per-revision URLs in v1. AKS App Routing DefaultDomainCertificate does not support nested wildcards (live spike, k8s/base/gateway-preview.yaml), so ACA's {app}---{label}.{zone} form is unavailable; the only viable scheme is a flat single label such as {slug}-r{n}-app.{zone}. Deferred, not rejected outright: the cheap 80% substitute is to reuse the existing ephemeral preview surface to exercise a candidate revision before promotion.
6. ADOPT activate/deactivate as a revision-level status distinct from delete. Revision statuses: Building / BuildFailed / Ready / Active / Inactive / Purged. Inactive means the artifact and DB row survive with zero replicas and it can be promoted back. App-level Suspended is orthogonal: it forces every revision to zero replicas without changing which revision is Current.
7. RETENTION: keep the last 10 revisions per app plus any revision that is Current, Target, or was Current within 30 days; older revisions go to Purged (row retained as a tombstone, image tag deleted). Enforced by PublishedAppReconciler, not by an ACR task, so SQLite/self-host and Postgres/Azure behave identically. Because Seraph's rule is deploy-by-digest, GC must delete the tagged manifest for retired revisions explicitly and must never run an "untagged sweep" - per the ACR purge warning that would break digest pulls for still-live revisions.
8. NAMING: revision identifier {slug}--r{n} (ACA's double-dash convention, monotonic RevisionNumber rather than a random suffix, because Agentweaver owns the counter); Kubernetes objects {slug}-r{n} (Deployment/Service, DNS-1035, slug capped so the whole name stays <=63 chars); image tag registry/published/{projectId}/{appId}:r{n} pushed then resolved to a digest which is what gets deployed.
9. REJECT from ACA: KEDA scale rules and per-revision autoscaling (a fixed 0/1 replica plus IdleSuspendMinutes is the whole requirement); traffic weights; Dapr/service binds; sticky sessions; ipSecurityRestrictions (project-member auth at the gateway is the control); multi-container templates and init containers; revision copy; a 100-revision default (far too generous when every revision is a stored image); ACA's preview "deployment labels" mode.

MIGRATION DELTA (all four in one change - the 2026-07-30 auth-mode-epoch post-mortem is the reason)
- apps/Agentweaver.Api.Migrations.Postgres/Migrations/ - new EF migration for published_apps, published_app_revisions, published_app_events.
- apps/Agentweaver.Api/Infrastructure/SqliteDb.cs SchemaSql (const at line 551) - same DDL for SQLite.
- apps/Agentweaver.Api.Data/Memory/MemoryDbContext.cs - entity mapping plus a regenerated snapshot in BOTH apps/Agentweaver.Api/Migrations/MemoryDbContextModelSnapshot.cs and apps/Agentweaver.Api.Migrations.Postgres/Migrations/MemoryDbContextModelSnapshot.cs.
- apps/Agentweaver.Api/Tools/SqliteToPostgresMigrator.cs - copy the three new tables, revisions before apps is impossible (FK both ways), so insert apps with null Current/Target then backfill the pointers after revisions land.

OPEN FOR AHMED
- Retention numbers (10 revisions / 30 days) are a proposal, not a measurement.
- Whether a purged revision's row survives as a tombstone or is deleted outright.
- Whether "restart current revision" is a user-visible action or only an internal effect of a secret change.
- Whether tracking mode auto-promotes each new revision or builds it and waits for a human promote.
---

<!-- Source: decisions/inbox/Trinity-openai-compatible-chat-endpoint-is-a-fourth-door-n.md -->

### 2026-07-31T00-28-39: OpenAI-compatible chat endpoint is a fourth door, not a projection-app replacement; revised position — build workflow I/O schema first, MCP before OpenAI-compat, no v1 chat endpoint
**By:** Trinity
**What:** OpenAI-compatible chat endpoint is a fourth door, not a projection-app replacement; revised position — build workflow I/O schema first, MCP before OpenAI-compat, no v1 chat endpoint
**References:** specs/mcp-integrations/drive-agentweaver-through-mcp.md (#33), specs/mcp-integrations/provide-project-copilot-agent.md (#35), specs/mcp-integrations/browser-chat-control-console.md (#50), apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:286-306, apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:360-388, apps/Agentweaver.Mcp/Tools/*.cs, specs/personas/priya-customer-support-lead.md, specs/personas/nina-legal-compliance-counsel.md, specs/personas/devon-platform-engineer.md
**Why:** Product verdict on Ahmed's question "can coordinator workflows be exposed via the OpenAI Messages/Conversation API, does this help". Scope: product/UX only (Tank owns wire protocol, Morpheus owns semantic fit).

VERIFIED EXTERNAL FACTS (2026)
- Client ecosystem is real: Open WebUI (https://docs.openwebui.com/getting-started/quick-start/connect-a-provider/starting-with-openai-compatible/), LibreChat (https://github.com/danny-avila/LibreChat), Cherry Studio, plus BYOK model-provider fields in VS Code (https://code.visualstudio.com/blogs/2026/06/18/byok-vscode), JetBrains/Xcode Copilot (https://github.blog/changelog/2025-09-11-bring-your-own-key-byok-support-for-jetbrains-ides-and-xcode-in-public-preview/) and Copilot CLI (https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-byok-models).
- These clients overwhelmingly target /v1/chat/completions; Responses support is newer and partial (Open WebUI experimental, LibreChat yes). Conversations API has effectively zero third-party client demand. So the pragmatic target for "free clients" is the LEGACY endpoint, which is the opposite of where OpenAI itself is heading (Responses is now recommended; Assistants API shutdown Aug 2026).
- Precedent exists and works: n8n-openai-bridge exposes n8n workflows as "models" to Open WebUI/LibreChat (https://github.com/sveneisenschmidt/n8n-openai-bridge). Dify/Flowise/vLLM similar. Known limits: partial feature parity, tool-calling/streaming edge cases.
- MCP, not OpenAI-compat, is the 2026 lingua franca for non-model backends: native in ChatGPT connectors, Claude, VS Code, Cursor, Open WebUI, LibreChat (https://clickhouse.com/blog/llm-chat-mcp-support, https://workos.com/blog/everything-your-team-needs-to-know-about-mcp-in-2026).

VERIFIED REPO FACTS
- WorkflowDefinition (apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:286-306) still has no declared inputs/outputs: Id, Name, Description, Version, Start, Nodes, Edges, Stages, Trigger.
- POST /api/projects/{projectId}/workflows/{workflowId}/run (WorkflowDefinitionEndpoints.cs:363-388) takes NO request body; title/description are hardcoded ("Manual run: {Name}"). There is no parameterized start path today for any client, chat or otherwise.
- Agentweaver.Mcp already ships 14 tool groups (Backlog, Blueprint, Catalog, Coordinator, Diagnostics, GitHubAuth, Memory, Project, Run, SandboxPolicy, Skill, Team, Workflow, Workspace) and already supports remote HTTP transport with OAuth resource-server validation (Program.cs:13-96), i.e. the "drive Agentweaver from any assistant" job is largely BUILT, not hypothetical.

DECISIONS
1. An OpenAI-compatible chat endpoint does NOT replace the projection-app idea. Projection apps exist to give a narrow audience a domain-shaped facade (typed fields, validation, branding, structured output). A chat box is a GENERIC facade: no typed fields, no validation, no diff/table rendering, no approval affordances. It serves the "operator with a different client" job, not the "40 support agents who will never have an Agentweaver account" job.
2. It also does not replace the Agentweaver UI. Ranking for the narrow-audience-facade job: (i) generated projection app > (iii) MCP + existing assistant > (iv) improve Agentweaver UI > (ii) OpenAI-compatible chat endpoint (last).
3. REVISION of my earlier open question #7: MCP is NOT the cheaper answer to the projection-app job. MCP is the cheaper answer to the OPERATOR-FROM-ANOTHER-CLIENT job, which is a different job and is already mostly shipped. My earlier framing conflated them.
4. The workflow I/O schema is MORE necessary under a chat surface, not less. Without declared inputs, an OpenAI-compatible endpoint can only accept free text and must guess parameter extraction with an LLM. The schema is the shared prerequisite for projection apps, parameterized manual runs, MCP tool arguments, and any chat facade. STRENGTHENED recommendation: build the schema, and add a request body to the manual-run endpoint. This is the highest-leverage item and it is not blocked on any publishing decision.
5. Provenance is unrecoverable in a third-party client. My earlier requirement of platform-injected chrome the model cannot remove (provenance badge, cost display, kill switch, run link) cannot be enforced when Agentweaver renders zero pixels. Best available mitigation is in-band text (a run URL and provenance line in every response) which is cosmetic and strippable. Consequence: an OpenAI-compatible endpoint must be treated as a Private/Project-audience operator convenience only. It must never be the delivery mechanism for an untrusted or external audience, which is exactly the audience the projection app was for.
6. An OpenAI-compatible endpoint is a fourth door into the room already served by MCP (#33), the project Copilot agent (#35), and the browser chat console (#50). Say this plainly rather than shipping it. If chat-client reach is the actual want, the cheapest correct move is to make the existing remote MCP surface trivially connectable (one-click/paste connector, documented URL, OAuth flow), not to build a second protocol.
7. RECOMMENDED BUILD ORDER: (a) workflow input/output schema + parameterized run body; (b) polish the existing remote MCP connector story; (c) projection apps for the narrow-audience job on top of (a); (d) OpenAI-compatible endpoint only if a specific client that speaks ONLY /v1/chat/completions is a named blocker for a named user. Do not build (d) in v1.

RISK IF IGNORED: shipping (d) first produces a demo-friendly surface that leaks Agentweaver into contexts with no provenance, no gates rendering, and no cost visibility, while the actual blocker (no typed workflow I/O) stays unaddressed and blocks projection apps, MCP argument quality, and parameterized runs simultaneously.
---

<!-- Source: decisions/inbox/Trinity-self-publishing-workflow-outputs-the-living-report.md -->

### 2026-07-31T00-39-04: Self-publishing workflow outputs: the Living Report is a document, not a container — split the publish primitive in two, and blueprints are the real product
**By:** Trinity
**What:** Self-publishing workflow outputs: the Living Report is a document, not a container — split the publish primitive in two, and blueprints are the real product
**References:** #21 preview-sandbox-apps, #11 manage-workflow-library, #49 open-pull-request-action, #394 push-pr-as-execution-step, #53 trigger-tasks-for-scheduled-and-event-workflows, #7 cast-a-project-team, #6 browse-project-and-run-workspaces, apps/Agentweaver.Api/Blueprints/BlueprintDtos.cs, apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs
**Why:** ## Context

Ahmed asked whether an Agentweaver workflow can generate and maintain its own published app ("blueprint triages bugs -> click publish report -> app stays fresh via scheduled runs"). The coordinator's hypothesis was that this conflates content refresh with code regeneration. Verified against the repo.

## Verified starting position

- `WorkflowDefinition` (apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs) has node types Prompt, PeerReview, BuildTest, **OpenPullRequest**, Check, FanOut, FanIn, CoordinatorComposed, Serial, Merge, Scribe, Terminal — plus a first-class `WorkflowTrigger` (Schedule daily/weekly/monthly, or Event). Still **no `inputs:`/`outputs:` schema**.
- `OpenPullRequest` is the precedent: a platform-owned, deterministic, non-LLM action node with typed parameters (PrTitle/PrBody/PrBase/PrHead/PrDraft) and template placeholders. Spec: specs/workflows-automation/open-pull-request-action.md (#49); coordinator-owned sibling: specs/orchestration-runs/push-pr-as-execution-step.md (#394).
- **Blueprints already exist in code and are already a distribution unit**: `Blueprint` = roster + workflows[] + review policy + sandbox profile + skill bindings + bespoke roles (apps/Agentweaver.Api/Blueprints/BlueprintDtos.cs), with `GitHubBlueprintPackageImportService` / `GitHubBlueprintPackageClient` for acquisition from GitHub, and `BlueprintPicker.tsx` / `ProjectGalleryBlueprint` in apps/web. **Blueprints have zero coverage in specs/** — no spec file mentions the word.
- apps/web already ships `react-markdown` 10.1.0 + `rehype-sanitize` 6.0.0 + `remark-gfm` (apps/web/package.json).
- Run output today is a git worktree branch + events; there is no durable artifact store (see specs/projects-workspace/browse-project-and-run-workspaces.md #6; worktrees disappear from active choices).

## Decisions

1. **The hypothesis holds, with one refinement.** Content refresh and code regeneration are genuinely different products. But the axis that matters is not "does code change" — it is **what the reader's browser executes**. A Living Report executes *Agentweaver's* renderer over run-produced data. A projection app executes *model-authored code*. Refresh cadence is a secondary attribute of both.

2. **The Living Report needs no container and no image.** It is `PublishedDocument` (mutable head: slug, title, audience, source workflow binding, freshness policy) + `PublishedDocumentVersion` (immutable: run id, produced-at, content). Content is sanitized Markdown rendered by the existing `react-markdown` + `rehype-sanitize` stack. No registry push, no Deployment, no HTTPRoute, no rootless BuildKit build, no CVE/patching surface, no per-app namespace. Routing this class through the flavor-(a) container publish machinery would be the single biggest cost mistake available here.

3. **`PublishDocument` is the next member of the `OpenPullRequest` action family** — a platform-owned, deterministic, non-LLM workflow node with typed parameters (target document slug, title template, source path or upstream node output, audience). Same shape, same review posture, same "predictable failures reported without aborting the run" contract. Not a new subsystem.

4. **The button lives in three places with one owner.** Blueprint ships the `PublishDocument` node pre-wired (Ahmed's framing is correct); the workflow author sees and can remove it; the run page offers "publish this output" as a one-off that, on confirm, offers to add the node permanently. The publish *target* (which document, what audience) is owner-configured once, not decided per run by an agent. An agent may propose the first publish; a human confirms the binding — after that, refreshes are unattended because only data changes.

5. **Blueprints as the distribution unit is the actually-interesting idea in Ahmed's question**, and it is closer to done than anything else here. `Blueprint` already bundles roster + workflows + policy + skills, GitHub package import already exists, `WorkflowTrigger` already carries the schedule. Adding a published-document binding completes "install this and get a self-maintaining report on Monday." The gap is **product, not plumbing**: blueprints have no spec, no gallery narrative, and no notion of a bundled published surface.

6. **The bug-triage report passes the audience-boundary test only when the reader will never have an Agentweaver account.** For a team already on the board, this is a worse Kanban view — kill it. Keep the class for the executive/adjacent-team/customer reader. This is consistent with, not a revision of, the earlier finding that killed the generic reviewer dashboard and generic scheduled-workflow status board.

7. **Code regeneration on a schedule is refused.** Unattended LLM authorship reaching a reader's browser with no human between run and revision is not shippable. Regeneration produces a *proposed* revision; promotion is an explicit human act. This is the same gate `OpenPullRequest` respects by refusing to merge.

8. **Freshness is a stated contract, not a timestamp.** The document declares an expected cadence; the reader header states the promise and whether it was kept ("Weekly, Mondays. Updated 2 days ago." vs "Weekly. Last successful update 16 days ago; the last 3 attempts failed."). Failed refresh **never** replaces good content and **never** silently serves it as current — last-good content stays with a visible failure banner. Anomaly detection is out of scope for v1; a rendered-but-suspicious document (empty sections, zero rows) is reported by the run, not guessed at by the UI.

9. **Revise my earlier "bounded pin" position for this class only.** I argued flavor (a) should be a bounded pin because indefinite hosting makes Agentweaver a hosting provider by accident. Ahmed overruled that for containers. Separately, that objection **does not apply at all** to the document class: hosting a sanitized Markdown row indefinitely costs nothing and carries no patching or egress burden. The pinning argument was always an argument about containers; I overgeneralized it to "publishing."

## What I would build first

`PublishDocument` workflow action + `PublishedDocument`/`PublishedDocumentVersion` + a reader page with freshness contract + version history + "view the run" for project members. CLI parity per Principle IV. Nothing containerized, nothing regenerated.

## What I would not build

Container-backed living reports; scheduled code regeneration; a document builder UI; anomaly detection; a public blueprint marketplace with ratings/publishers before the first-party gallery proves the format.

## Open for Ahmed

Workflow `outputs:` schema (still blocking anything schema-first); who the anonymous reader actually is per class; whether a published document should also land in the connected repo via the existing PR action instead of being Agentweaver-hosted; whether blueprints get a spec before or after this.