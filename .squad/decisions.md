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
- This is a defaults-only change across active code, tests, params, docs, and diagrams. Historical records and descriptions of past live clusters are not retroactively rewritten.

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
