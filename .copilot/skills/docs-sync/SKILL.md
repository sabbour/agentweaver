---
name: "docs-sync"
description: "Playbook for keeping Agentweaver docs in sync with code when features change — which docs to touch, how to ground in source, how to regenerate and build."
domain: "documentation"
confidence: "high"
source: "team-decision"
---

## Context

Docs live in `docs/` (a VitePress site) and drift from the code as features land. This skill is the repeatable playbook so doc updates are deterministic instead of a one-off prompt. See `.github/DOCS_SYNC.md` for the full strategy.

Docs are split into two kinds:

- **Generated** — derived from source, never hand-edited (carry a `GENERATED — DO NOT EDIT` header).
- **Curated** — hand-written prose.

| Surface | Doc | Mode | Source of truth |
| --- | --- | --- | --- |
| MCP tools | `docs/reference/mcp-tools.md` | **Generated** | `apps/Agentweaver.Mcp/Tools/*.cs` |
| MCP tool params | `docs/reference/mcp.md` | Curated | hand-written |
| API endpoints | `docs/reference/api.md` | Curated | `apps/Agentweaver.Api/**/*Endpoints.cs` |
| Memory/decisions | `docs/reference/memory.md` | Curated | `apps/Agentweaver.Api/Endpoints/{Memory,Decisions}Endpoints.cs` |
| Blueprints/workflows | `docs/guide/workflows.md`, `docs/reference/*` | Curated | `apps/Agentweaver.Api/{Blueprints,Workflows}/`, `packages/Agentweaver.Squad/Catalog/Resources/blueprints/` |

## When to invoke

Invoke this skill whenever a change adds or alters: an **MCP tool**, an **API endpoint**, a **blueprint**, a **workflow**, or any user-facing **feature**. The CI workflow `.github/workflows/docs-drift.yml` also nudges PRs that change these paths without touching `docs/**`.

## Playbook

1. **Identify the surface that changed** and map it to its doc(s) using the table above.

2. **Regenerate generated docs** if you touched a generated surface (e.g. MCP tools):
   ```bash
   node scripts/gen-docs.mjs          # writes docs/reference/mcp-tools.md
   node scripts/gen-docs.mjs --check  # verify in sync (what CI runs)
   ```
   Never hand-edit a generated file — edit the source attribute/route and regenerate.

3. **Update curated docs** for the feature. Ground every statement in code:
   - Read the actual endpoint/tool/blueprint source — do not invent behavior.
   - Update both the **guide** page (how a user uses it) and the **reference** page (exact params/returns).
   - Keep the curated `mcp.md` parameter tables consistent with the generated `mcp-tools.md` names.

4. **Build to verify** (required whenever any `docs/*.md` changes):
   ```bash
   cd docs && npm ci && npm run build
   ```
   Confirm the build is green and there are no dead links.

5. **Commit docs in the same PR as the code.** The definition of done includes docs.

## Guardrails

- Do **not** edit files owned elsewhere: `README.md`, `install.*`, `docs/.vitepress/config.ts`, `docs/index.md`.
- Do **not** hand-edit `docs/reference/mcp-tools.md` — regenerate it.
- New curated pages must be added to the VitePress nav (`docs/.vitepress/config.ts`) by the owner of that file; generated-only index pages may be linked from a curated page instead.
- If you add a new generated surface, extend `scripts/gen-docs.mjs` and add its `--check` to the CI `generated-staleness` job.

## Quick reference

| Action | Command |
| --- | --- |
| Regenerate generated docs | `node scripts/gen-docs.mjs` |
| Check generated docs are in sync | `node scripts/gen-docs.mjs --check` |
| Build the docs site | `cd docs && npm run build` |
| Strategy / rationale | `.github/DOCS_SYNC.md` |
