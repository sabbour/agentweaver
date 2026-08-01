# Squad Decisions

> Archive pointer: the full pre-cleanup working ledger and the 2026-07-31 processed inbox entries were moved verbatim to `decisions-archive-2026-07.md`. Do not read that archive whole in normal agent prompts; grep for the date/topic you need.

## 2026-07-31T03:40:59+03:00 — Publish-apps exploration working decisions

**Context:** Ahmed requested discussion-only exploration of a proposed "publish apps" feature. No code or spec files changed.

- **One user verb:** the UI should expose one **Publish** verb, not separate "Publish report" and "Publish app" actions. The system chooses the document vs container substrate.
- **Audience/defaults:** default audience for published reports/apps is **project members**. Published app infrastructure uses **per-project namespaces**, a separate registry for generated images, scoped-public API access from published apps, indefinite hosting, and deploy-by-digest.
- **Workflow graph:** `publish` is a legal workflow node. Precedent: `open_pull_request` and `merge` are deterministic platform-owned side-effecting nodes. `publish` must be a post-approval tail action that records durable intent; a reconciler converges long-lived infrastructure.
- **Inputs boundary:** run parameters belong in a top-level `inputs:` block. `trigger:` is a top-level document boundary, not a graph node; it argues for document-level inputs rather than input nodes. Mid-run human input can be a later node kind, but scheduled/event run parameters must be bound before execution.
- **Document vs container path:** the living report / published document path is distinct from generated container apps. A document path needs typed workflow outputs, an artifact/version store, a publish node, freshness state, and a platform-owned sanitized renderer; it does **not** need BuildKit, a container, or issue #582.
- **Container path:** generated app containers use a `PublishedApp` + immutable `PublishedAppRevision` model inspired by Azure Container Apps. V1 is single-active-revision, no traffic splitting, no revision labels/per-revision URLs, inactive revision state, digest-pinned deploys, and retention around last 10 minted revisions / 30 days unless revised.
- **Refresh semantics:** content refresh and code regeneration are separate. Content refresh can update data/artifacts without rebuilding an image; code regeneration mints a proposed revision and requires explicit human promotion before readers see new model-authored code.
- **Idempotency:** scheduled refreshes must dedupe by semantic content digest, exclude declared volatile fields, use a unique `(publishedAppId, runId, nodeId)` guard, and prevent late older runs from moving the current pointer.
- **OpenAI-compatible surface:** unanimously rejected for v1. MCP remains the programmatic surface; if an OpenAI-compatible endpoint is ever built, it is an operator convenience only, not a coordinator-run or projection-app replacement.
- **Issue #582:** does not block the document/living-report path. The rubber-duck review found the claim survives with caveats: container publishing and code regeneration still need the image-build work.
- **Frontend drift:** `open_pull_request` is server-accepted but missing from the web visual editor node type list; fold that fix into publish-node work rather than filing it separately.

## 2026-07-31T03:40:59+03:00 — Active auth and platform decisions retained by reference

The detailed Entra/auth-mode, RBAC, preview, workflow, and release decisions from 2026-07-26 through 2026-07-30 are archived verbatim in `decisions-archive-2026-07.md`. The current load-bearing summaries are:

- Agentweaver is Entra-first for platform sign-in, with GitHub identity decoupled from login and used as a linked source identity where needed.
- Auth mode is a deployment-level switch; Entra is the default and GitHubLegacy is opt-in.
- Project RBAC uses explicit Entra OID role assignments and preserves last-owner invariants atomically.
- Shared auth-mode epoch invalidates cross-pod mode switches; the Postgres migration for the epoch table was added after the 2026-07-30 gap was found.
- PR #640 shipped after the final Operator tool-policy fix and staging verification.
- Preview durability remains governed by the sandbox/preview decisions and related issue trail; use the archive for exact evidence.
