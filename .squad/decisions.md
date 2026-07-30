# Squad Decisions

## 2026-07-20T12-01-24-07-00 — Active decisions reset after size gate

**By:** Scribe  
**What:** Archived the previous active snapshot to `decisions/archive/2026-07-20T12-01-24-07-00-pre-inbox-archive.md`, then rebuilt the active file from the current high-signal decisions plus today's processed inbox.  
**Why:** `decisions.md` had grown to 147846 bytes and was functioning as a long-form archive instead of a working decision index. The full prior detail is preserved in the archive snapshot; the active file now carries the current operating decisions.

---

## 2026-07-16T17-19-26-07-00 — Assistant session-store regression hotfix and rollout/provenance lessons

- Re-enabling the Copilot SDK session store for `OperatorAssistantAgent` caused a live SQLite lock P0 because the agent creates a fresh SDK session on every turn instead of resuming one. The session-store flags were reverted in v0.9.69 and must stay off until real session resumption exists.
- `scripts/aks/30-deploy` can false-fail under transient scheduling pressure; confirm `kubectl rollout status` before treating the deployment as broken.
- Provenance verification correctly caught a stale frontend image after a post-build docs merge; rerun provenance checks after **any** merge to `main` that lands after image build, even if the change looks unrelated.

---

## 2026-07-16T19-15-00Z — Assistant conversation recall and sessions UI shipped

- Durable assistant run rehydration now rebuilds conversation history from persisted run events on cache miss.
- `GET /api/assistant/runs` and the Sessions UI shipped together with delete support.
- Released as v0.9.68; later session-store hotfixes did **not** change the durable rehydration approach.

---

## 2026-07-17T08-44-16-07-00 — Cross-user approval authorization fix shipped

- Persistent tool-approval authorization was fixed and approved for cross-user correctness.
- The approval path must continue to enforce owner scoping while preserving the durable run-control behavior already on `main`.

---

## 2026-07-18T02-58-30-07-00 — Staging OAuth credential incident and standing directives

- Never auto-resolve local GitHub OAuth credentials into staging or other shared environments; staging credentials must be supplied explicitly.
- For the sandboxed Operator Assistant, continue using the current repo-capable OAuth token inside the one-turn sandbox rather than adding a separate GitHub App credential or switching to Foundry, while keeping the token in memory only and containing authority through scoped capabilities and restricted ambient access.
- Normal team workflow is restored: after verification, push directly to `origin/main`; no separate "commit locally, hold pushes" directive remains in force.

---

## 2026-07-19T06-05-00 — Node Azure toolchain migration: final operating decisions

**Scope sources merged:** `Squad-Coordinator-full-node-port-of-deploy-toolchain-drop-c3-c4-upgr.md`, `Link-phase-1-node-engine-foundation-for-the-azure-deplo.md`, `Tank-phase-2-30-deploy-mjs-ported-the-full-apply-path-n.md`, `Morpheus-phase-3-p3-node-port-build-provenance-parity-image.md`, `Link-phase-4-upgrade-mjs-warm-pool-cycle-remaining-prov.md`, `Link-phase-5-installer-cli-release-dev-verify-npm-wiring.md`, `Smith-phase-6-smith-s-half-deleted-all-legacy-aks-instal.md`, `Link-phase-7-staging-e2e-verification-critical-sequencing-bug-found.md`, `Link-phase-7-reverify-bug2-fix-confirmed-two-minor-findings.md`, `Link-phase-7-final-single-command-upgrade-success.md`, `Squad-Coordinator-no-remote-curl-bash-one-liner-replacement-git-clon.md`, `Trinity-p6-docs-half-done-readme-docs-guide-updated-for-ne.md`, `tank-v0971-merge-resolutions.md`.

- The Azure/AKS toolchain is the Node-based `azure:*` flow for infrastructure provisioning, local deployment, published-release deployment, release orchestration, verification, and local development; legacy `.sh`/`.ps1` deploy/install/release/start-dev scripts were intentionally removed once parity was proven.
- `azure:deploy-from-local` must mint a new immutable tag from HEAD, refuse dirty trees by default, deploy **before** provenance verification, and treat warm-pool success as reapply-and-wait plus image verification.
- `azure:deploy-from-commit` resolves any committed ref and runs that same SHA deployment pipeline from a temporary detached worktree, without switching or using dirty state from the caller's checkout.
- Build/provenance share one declarative image spec; watched-path and build-arg bugs fixed during the migration stay part of the contract.
- `git clone` is the only install/bootstrap entry point; there is no replacement remote `curl|bash` or `curl|iex` installer.
- The v0.9.71 integration kept the Node toolchain as canonical, preserved the current Assistant resume/send protections, wired `RemoteApiBaseUrl` through `RemoteAgentProxy`, and removed obsolete `Assistant__McpEndpoint` Kubernetes config.

---

## 2026-07-18T14-49-00-07-00 — Frontend private 1JS dependency removal

- `apps/web` no longer depends on private `@1js/*` packages.
- The final runtime usage was replaced with native Fluent UI components, the private feed override was removed, and public-npm lockfile/docs/build wiring were updated accordingly.
- This keeps plain clone/install flows aligned with the repository rule against private dependencies.

---

## 2026-07-20T11-11-57-07-00 — CI, contributing, releasing, and local dev setup policy

**Scope sources merged:** `link-contributing-releasing-process.md` plus Link's `link-oauth-setup`, `link-appsettings-scaffold`, `link-devready`, and the coordinator's final ship note.

- Added a real GitHub Actions CI workflow at `.github/workflows/ci.yml` for pull requests, pushes to `main`, and manual dispatch. It runs the repo's real .NET, Node-toolchain, and web test commands on dedicated runners; web lint is currently advisory until the existing frontend lint backlog is cleared.
- CONTRIBUTING and RELEASING now treat green `main` CI as the release bar, document the issue/reviewer/rubber-duck/decisions-inbox workflow, and require docs-as-you-go updates for shipped behavior.
- `scripts/azure/dev.mjs` now includes GitHub OAuth setup guidance, scaffolds `appsettings.Development.json` when needed, and prints a dev-ready summary aligned to the supported npm scripts.
- The docs home page now includes a local/Azure Quick Start hero block. After rubber-duck approval, the full batch was verified (220/220) and committed/pushed to `origin/main` as `95a855a0`.

---

## 2026-07-20T12-08-00-07-00 — Release/versioning policy, recovery semantics, and ADR/label governance

**Scope sources merged:** `link-release-process-design.md`, `link-release-process-docs-and-tag-predicate.md`, `link-release-adrs.md`.

- While Agentweaver remains `0.x`, patch bumps are for backwards-compatible bug fixes only; minor bumps carry both new features and breaking changes; major is reserved until the project intentionally declares `1.0.0` stability.
- `azure:provision-infra` and `azure:deploy-from-local` are SHA-identified environment operations, not release cuts. `release:publish` creates repository release identity, `azure:deploy-from-release` deploys an existing published tag, and `azure:release` composes both for the first shipment.
- Changesets generates `CHANGELOG.md` during `release:prepare`; GitHub Release notes are copied from the exact matching section. The authoritative release boundary remains the annotated `vX.Y.Z` tag, using `^v\d+\.\d+\.\d+$`.
- `azure:release --resume vX.Y.Z` is the supported recovery path after publication. Preparation, publication, and deployment are separate reusable stages.
- Durable cross-cutting architecture decisions now belong in numbered ADRs under `docs/architecture/decisions/`; routine operational/team decisions remain in `.squad/decisions.md`.
- `.github/labels.json` is the canonical future label taxonomy. `workstream:*` is deprecated in favor of the smaller `area:*` vocabulary without rewriting historical issue labels.
- Docs/process corrections from the same review set are durable: missing `bubblewrap` inside WSL means fallback to fully unsandboxed passthrough (not a weaker `unshare` path), CONTRIBUTING now documents explicit new-feature and bug-fix issue/spec workflows, and agent worktree guidance must reflect the real `.worktrees/{branch-slug}` layout.

---

## 2026-07-20T12-20-00-07-00 — Local dev, Azure verification, and protected-main clarity

**Scope source merged:** `link-devtest-clarity.md`.

- Local `npm run dev` is branch-agnostic and never interacts with GitHub protection. The normal flow is: feature worktree/branch → local verification → optional Azure verification → PR → protected-branch admission on `main`.
- `azure:provision-infra` is first/full idempotent provisioning, `azure:deploy-from-local` is normal current-HEAD iteration on an existing environment, and `azure:verify` is read-only live verification. `--allow-dirty` is for personal/throwaway use only, not shared validation.
- The local-dev API readiness failure was a real SQLite migration-ordering bug, not slow startup: existing databases missing `backlog_tasks.parent_prd_run_id` crashed because schema setup tried to create `idx_backlog_tasks_parent_promotion_key` before the idempotent `ALTER TABLE` migrations ran. The index must only be created in the post-column migration sequence; Vite must not start unless API health succeeds.
- There is no supported long-lived local integration/staging branch. The audited local-only refs (`main-staging`, `integration`, `integration-v0.9.71`, `release-staging`, `release/v0.9.71-foundation-integration`, `main-tip`, `localmain/main`, `merge-docs-landing-main`) are safe to delete once any attached worktree is removed.

---

## 2026-07-20T14-05-53-07-00 — Branching strategy settled: protected `main` only

**Scope sources merged:** `morpheus-branching-strategy-design.md`, `Morpheus-use-protected-trunk-based-github-flow-with-a-seria.md`, plus the Merge Queue availability correction recorded in `link-devtest-clarity.md`.

- Settled decision: keep `main` as the only long-lived branch. Do not add `dev`, `preview`, release-candidate, or routine release-maintenance tiers for current Agentweaver development.
- Every change, including docs-only changes and release `VERSION` bumps, must land through a short-lived PR. Required blocking checks are `.NET tests`, `Node toolchain tests`, `Web tests`, and `Docs build`; `Web lint` stays advisory until its existing backlog is cleared.
- Protect `main` for maintainers too: squash-only merge, automatic source-branch deletion, and only a narrow, documented, audited emergency/admin bypass.
- Release flow stays tag-centric: merge a normal protected PR for the `VERSION` bump, then create annotated tag `vX.Y.Z` on that exact green merged SHA and publish/deploy from the tag.
- GitHub Merge Queue is unavailable today because `sabbour/agentweaver` is a personal-account repository. The earlier merge-queue-of-one design and the later dev/preview/main promotion alternative are retained only as audit trail; the enforceable current design is strict protected-PR admission on `main`.

---

## 2026-07-20T12-25-00-07-00 — Review outcome auditability and deterministic Squad triage

**Scope source merged:** `link-skills-reconcile.md`.

- Ordinary GitHub `Changes requested` feedback does not lock out the original author. Lockout applies only when a reviewer explicitly records `REJECTED — requires independent rewrite`; that PR marker is the durable audit trail.
- Feature and bug issue templates apply the existing `squad` label by default so `squad-triage.yml` runs deterministically. Issues created outside those templates must receive `squad` manually.
- Operating expectation: triage P0 Squad issues the same business day and route other new Squad issues within a few business days.

---

## 2026-07-20T12-30-00-07-00 — Default AKS cluster name and active-doc defaults

**Scope source merged:** `link-rename-cluster.md`.

- The configured/documented default AKS cluster name is now `agentweaver-aks` instead of `agentweaver-aks-2`.
- This is a defaults-only change across active code, tests, docs, params, and diagrams. Historical records and descriptions of past live clusters are not retroactively rewritten.

---

## 2026-07-20T12-35-00-07-00 — Sandbox containment, persistence fallback, and merge-resolution follow-ups

**Scope sources merged:** `smith-ci-red-investigation.md`, `tank-v0971-merge-resolutions.md`.

- The recurring red `.NET tests` runs were real product failures, not flakes. Linux containment must reject Windows-style absolute, UNC, and device paths before relative normalization, and sandbox roots must be validated as non-symlink/non-reparse before they are trusted.
- Real-sandbox Linux/WSL end-to-end tests should early-exit when the required backend is unavailable instead of trying to dynamically skip through a failure path.
- Coordinator persistence must retain the direct `MemoryDbContext` RunEvents fallback when `IRunEventStream` is not registered (as in test harness construction), while still preferring durable event-stream append in production.
- Remaining Windows PodLocal workspace failures are an environment/path-length limitation on this machine, not evidence that the v0.9.71 merge changed pod-local workspace behavior.

---

## 2026-07-20T14-36-47-07-00 — Branch Topology Activation Plan

**Scope source:** `decisions/inbox/niobe-branching-growth-review.md` (permanent supporting analysis; retained in place).

- **Final verdict:** retain protected `main` as the only long-lived integration branch now. This supplements, rather than deletes, the 2026-07-20 branching-strategy settlement above, preserving its audit trail.
- **Branch Topology Activation Plan:** replace the prior vague “revisit later” posture with these checkable activation conditions:
  - **Trigger A — Merge Queue:** when the repository is organization-owned and either **at least 5 PRs in a rolling 14-day period** rerun blocking CI solely because another PR merged first, **or** the median time from all review/check requirements being satisfied to merge exceeds **one business day for two consecutive weeks** because of update/retest serialization. Enable Merge Queue (with `merge_group` CI) while retaining `main` as the sole integration branch.
  - **Trigger B — protected maintenance branch:** when the project makes its **first commitment to patch an older minor after an incompatible newer minor lands on `main`**. Create and protect `release/X.Y`; patch from it and forward-port applicable fixes to `main`.
  - **Trigger C — full `dev → release → main` promotion tier:** when **two consecutive releases** each require **at least 3 business days** of RC soak while **at least two independent next-version changes** must keep integrating in parallel, **or** the project formally commits to a durably-diverging externally consumed `next` channel.
- These measurable conditions explicitly prevent the earlier short-sighted reasoning that a topology is unnecessary merely because the repository does not need it today. Full rationale, boundaries, and the migration playbook remain in `decisions/inbox/niobe-branching-growth-review.md`.

---

## 2026-07-20T15-05-18-07-00 — Trigger C promotion topology deliberately activated

**Source:** Ahmed (@sabbour) explicit directive in the 2026-07-20 migration conversation.

- Trigger C was activated deliberately as a strategic room-to-grow choice, not because its prior automatic soak/next-channel metrics threshold was measured as met.
- `dev` is now the default protected integration branch; normal PRs target it. `release/vX.Y.Z` is an ephemeral soak branch cut from green `dev`, and `main` is stable/published-only, receiving only soaked release promotions or audited emergency hotfixes.
- The migration updates CI, ruleset documentation, contributor/release guidance, and agent workflow instructions. GitHub ruleset activation for `dev` remains an explicit manual owner action.

---

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
