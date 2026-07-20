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

- The Azure/AKS deploy toolchain is now the Node-based `azure:*` flow (`deploy`, `upgrade`, `release`, `verify`, `dev`); legacy `.sh`/`.ps1` deploy/install/release/start-dev scripts were intentionally removed once parity was proven.
- `azure:upgrade` must mint a new immutable tag from HEAD, refuse dirty trees by default, deploy **before** provenance verification, and treat warm-pool success as reapply-and-wait plus image verification.
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
