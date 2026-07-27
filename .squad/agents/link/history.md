# Link — History (Summarized)

## 2026-06-07–2026-06-30: foundations, docs, AKS, and sandbox platform work

Link helped establish the repo and docs foundations, then owned a large share of the AKS and sandbox platform work: cluster bring-up, Postgres and workload-identity wiring, sandbox network and token-delivery hardening, install/deploy documentation, and live staging diagnosis. Major durable lessons from this span: OAuth callback hosts must be substituted from the live environment; shared token delivery is unsafe compared to per-run scoped resources; and deployment/release docs must match the real operator path exactly.

## 2026-07-05–2026-07-18: release waves, staging operations, and Node Azure migration

Link contributed to the v0.7.11–v0.9.70 release waves, including preview/auth fixes, staging rollout verification, and the session-store regression/stale-image operational lessons recorded on 2026-07-16. Link also participated heavily in the Node Azure toolchain migration: foundations, upgrade/release/dev wiring, live staging verification, and the standing rule that shared-environment GitHub OAuth credentials must never be auto-filled from local secrets.

## 2026-07-20T12-01-24-07-00 — CI and contributor workflow batch

- Designed and implemented `.github/workflows/ci.yml` with dedicated .NET, Node-toolchain, web-test, web-lint, and docs checks; web lint remains advisory until the existing frontend lint backlog is cleared.
- Rewrote `CONTRIBUTING.md` and updated `RELEASING.md` so green `main` CI is the release bar and the issue/reviewer/rubber-duck/decisions-inbox/docs-as-you-go flow is explicit.
- Updated `scripts/azure/dev.mjs` with GitHub OAuth guidance, `appsettings.Development.json` scaffolding, and an npm-script-aligned dev-ready summary.
- Fixed the four real review findings from rubber-duck: missing docs CI coverage, a TOCTOU overwrite race in `dev.mjs`, CI lint-status masking, and conflicting secret-handling guidance.

## 2026-07-20T14-05-53-07-00 — Release/branching follow-through, skill reconciliation, and cluster-default cleanup

- Codified the durable release/docs governance follow-ups: `azure:release --resume`, ADR placement under `docs/architecture/decisions/`, the canonical future label taxonomy, and the split between in-repo `CHANGELOG.md` generation and GitHub Release notes.
- Reconciled stale agent/docs guidance by correcting WSL sandbox fallback language, documenting feature/bug issue workflows and real worktree layout, and making review-lockout / squad-triage behavior explicit.
- Clarified the local-dev versus Azure-verification versus protected-`main` flow, including the SQLite migration-ordering crash that blocked API startup on older local DBs.
- Renamed the active default AKS cluster name from `agentweaver-aks-2` to `agentweaver-aks` across live docs/config/tests while preserving historical records.
- Verified that Merge Queue is unavailable on the current personal-account repository, so all of the above was settled into a protected-`main`-only operating model.

## 2026-07-27 — Docs update round: model IDs, feature docs, specs index

- Requested by @sabbour after a coordinator audit found docs/specs drift; a prior attempt (link-7) had died mid-task leaving the branch empty, so this was a fresh pass on the existing `docs/update-round-model-ids-and-specs` branch/worktree.
- Fixed genuinely stale `Generation:Model` default docs (`gpt-5.4` → `gpt-5.6-sol`, confirmed against `GenerationModelOptions.DefaultModel`) in `docs/guide/configuration.md` and `docs/reference/api.md`, plus the two explicitly-flagged example values (`claude-sonnet-4.6` → `claude-sonnet-5` in `docs/guide/getting-started.md` and `docs/experience/projects.md`).
- Deliberately left `Providers:GitHubCopilot:Model`'s documented default (`claude-sonnet-4.6`) and the coordinator model-catalog table untouched after verifying both are still literally accurate to `CoordinatorModelDefaults.cs` — rewriting only the docs there would have made them describe a default the running code doesn't have. Recorded this reasoning in `decisions/inbox/link-docs-update-round.md`.
- Added a short "Add node" palette note to `docs/guide/workflows.md` for the grouped/deduped palette shipped in #558/#559 (found via `git log` diffing, not in the original task list).
- Determined the preview-sandbox TTL/autoscaler fixes (#560/#564/#570/#571/#574/#575) don't need spec or docs changes — they make already-specified behavior actually work, they don't change the user contract.
- Wrote a new design-only spec `specs/agent-execution-sandbox/build-images-with-rootless-buildkit.md` for issue #582 (AgentHost rootless-BuildKit image builds on Kubernetes) and linked it from `specs/README.md`; confirmed #452/#453 have no existing spec files to index.
- Durable lesson: when a docs task says "fix retired model ID X", always check the actual current code default/constant first — a doc that literally quotes a source-code constant's real value should not be "fixed" to a preferred ID without a corresponding code change, or the doc becomes newly inaccurate instead of newly accurate.
