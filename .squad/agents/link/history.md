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
