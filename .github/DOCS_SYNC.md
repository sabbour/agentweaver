# Keeping docs in sync with code

**Problem:** Docs drift from the code as features land, and updating them by hand-prompting an agent every time is slow and error-prone.

**Strategy:** Split the docs into **generated** (derived from source, never hand-edited) and **curated** (hand-written prose), add a **CI drift check** that catches both kinds of staleness, and make doc updates a **repeatable skill** instead of a bespoke prompt. The "definition of done" for any feature now includes docs.

---

## 1. Generated vs. curated

| Doc | Mode | Source of truth |
| --- | --- | --- |
| `docs/reference/mcp-tools.md` (MCP tool index) | **Generated** | `apps/Agentweaver.Mcp/Tools/*.cs` — the `[McpServerTool]` / `[Description]` attributes |
| `docs/reference/mcp.md` (per-tool params) | Curated | Hand-written; links to the generated index |
| `docs/reference/api.md` | Curated *(candidate for generation — see below)* | `apps/Agentweaver.Api/**/*Endpoints.cs` |
| `docs/reference/memory.md`, `blueprint/*`, workflow lists | Curated | Hand-written prose |
| Guides, deep-dives, experience docs | Curated | Hand-written prose |

**Rule:** generated files carry a `GENERATED — DO NOT EDIT` header and are produced by `scripts/gen-docs.mjs`. Everything else is curated and owned by a human/agent.

### Generator

`scripts/gen-docs.mjs` (Node, dependency-free):

```bash
node scripts/gen-docs.mjs          # regenerate generated docs
node scripts/gen-docs.mjs --check  # exit 1 if a committed generated doc is stale (CI)
```

Today it generates the **MCP tool index** (79 tools, parsed straight from the server source — the most drift-prone, perfectly auto-derivable surface). It is structured so additional generators (e.g. an API endpoint table) can be added as new functions.

---

## 2. Drift detection in CI

`.github/workflows/docs-drift.yml` runs on every PR with two layers:

1. **`generated-staleness` (hard fail).** Runs `node scripts/gen-docs.mjs --check`. If a generated file is stale, the PR fails with the exact fix command. Deterministic and auto-fixable, so a hard fail is safe and helpful.
2. **`curated-drift-nudge` (non-blocking).** If a PR changes doc-relevant code paths (API endpoints, MCP tools, blueprints, workflows) **without** touching `docs/**`, it posts a sticky reminder comment and a workflow warning. It **never** blocks merge — it just nudges.

This intentionally does **not** hard-fail on curated drift: heuristics can't prove a prose doc is wrong, so blocking would create false friction. Generated drift *can* be proven, so it blocks.

---

## 3. Recommended workflow (definition of done)

When you add or change a feature:

1. **Make the code change.**
2. **Regenerate** if you touched MCP tools or other generated surfaces: `node scripts/gen-docs.mjs` and commit the result.
3. **Update curated docs** for the feature (guide + reference page). Use the `docs-sync` skill for the playbook on *which* pages to touch and how to ground them in code.
4. **Build:** `cd docs && npm run build` — confirm green.
5. **Open the PR.** CI confirms generated docs are in sync and reminds you if code changed without docs.

For agents/coordinator: invoke the **`docs-sync`** skill (`.copilot/skills/docs-sync/SKILL.md`) — it captures this whole playbook so doc updates are a repeatable skill, not a one-off prompt. To author a feature's docs across *all* facets (deep-dive, reference, user guide, screenshots, landing card, nav, cross-links, diagrams), use the companion **`docs-feature`** skill (`.copilot/skills/docs-feature/SKILL.md`).

---

## 4. Recommended next step: generate the API reference

`docs/reference/api.md` is the next high-value generation target. The API uses ASP.NET minimal-API endpoints (`MapGet`/`MapPost`/… in `apps/Agentweaver.Api/**/*Endpoints.cs`). Two viable approaches:

- **Build-time OpenAPI export** — emit `swagger.json` from the running API (or `dotnet swagger tofile`) and render an endpoint table from it. Most accurate, but requires building/booting the API in CI.
- **Source extraction** — parse `Map{Get,Post,Put,Delete}` calls (route + summary) the same way `gen-docs.mjs` parses MCP tools. No build required, but only as good as the inline metadata.

Recommended: start with **source extraction** for an endpoint index (low risk, no build dependency), and graduate to OpenAPI export if richer schema detail is needed. Wire either into the `generated-staleness` job exactly like the MCP index. This was deferred from the initial change to keep it low-risk; the MCP index proves the pattern end-to-end first.
