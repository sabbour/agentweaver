---
name: "docs-feature"
description: "Full authoring playbook for documenting a NEW or EXISTING feature across ALL doc facets (deep-dive, reference, user guide, screenshots, landing card, nav, cross-links, diagrams, generated docs) in a thorough, consistent, code-grounded way. Triggers: 'add docs for a feature', 'document a new feature', 'update docs across all facets', 'write the docs for X', 'keep docs in sync with this feature', 'docs definition of done'."
domain: "documentation"
confidence: "high"
source: "team-decision"
---

## Context

The Agentweaver docs site (`docs/`, VitePress) documents every feature across **multiple facets**. Documenting a feature well means touching *all* the facets that apply — not just one page — in a consistent, code-grounded way. This skill is the authoring playbook.

It is the companion to the [`docs-sync`](../docs-sync/SKILL.md) skill, which owns **drift detection** and **generated docs** (`scripts/gen-docs.mjs`, the MCP tool index, and the CI check). Use `docs-feature` to *author* a feature's docs; use `docs-sync` to keep generated/derived docs in sync. See `.github/DOCS_SYNC.md` for the generated-vs-curated split and the CI workflow.

## Core principles (non-negotiable)

1. **CODE is the source of truth — not specs or markdown.** Read the actual C# / TS before writing a single claim. Every factual statement (routes, DTO fields, config keys, defaults, status codes, conditions) must trace to a real `file:line`. Do **not** paraphrase `specs/` or older docs — they drift; the code does not.
2. **Consistency.** Match the existing page structure, tone, frontmatter, section ordering, and the three-page pattern (deep-dive / reference / experience) of comparable features.
3. **Build gate.** `cd docs; npm run build` must stay green before you're done.
4. **Plan → execute → verify.** When invoked on a feature, first emit a per-facet plan/checklist for *that* feature, then execute each applicable facet, then build-verify.

## The doc-surface facets matrix

For a given feature, decide which facets apply, then do each. (Not every facet applies to every feature — a backend-only change may skip the landing card and screenshots.)

| # | Facet | Location | What to produce | Source to ground in |
| --- | --- | --- | --- | --- |
| 1 | **Deep Dive** | `docs/deep-dive/{feature}.md` | Concept + end-to-end flow + a **mermaid diagram** + a `## Source` file table. | The implementing C#/TS classes & services. |
| 2 | **Reference** | `docs/reference/{feature}.md` | Terse routes table, DTO field tables, config keys, status codes — all from source. | `*Endpoints.cs`, DTO records, `appsettings`/config keys, `apps/web/src/api/types.ts`. |
| 3 | **User Guide / Experience** | `docs/experience/{feature}.md` | Step-by-step user flow, "when available", "what to expect". | The web UI components (`apps/web/src/pages/*`, `client.ts`). |
| 4 | **Screenshots** | `docs/public/screenshots/{name}.png` (placeholder) + a row in `docs/experience/screenshot-plan.md` + a draft test in `tests/e2e/screenshots.spec.ts` | Placeholder image, a plan row (file → page → route → click-path → what it shows), and a `test.skip`-guarded capture test. | The real route + UI labels/aria-labels. |
| 5 | **Landing card** | `docs/index.md` `features:` list | A feature card (title + details + `See it in action →` link) **only if** it's a headline / value-prop feature. | The feature's user value. |
| 6 | **Nav wiring** | `docs/.vitepress/config.ts` | Sidebar entry under the right group (Getting Started / User Guide / Deep Dive / Reference) + top-nav if it's a new section. | Existing sidebar groups. |
| 7 | **Cross-links** | The new pages **and** related existing pages | `See also` / `Related reading` links both ways. | Related existing pages. |
| 8 | **Diagrams** | Inside the deep-dive (and others as needed) | Mermaid diagram following repo conventions (see below). | The real flow. |
| 9 | **Generated docs** | via `node scripts/gen-docs.mjs` | If the feature adds **MCP tools or API endpoints**, regenerate the derived reference and commit it. CI `--check`s it. | `apps/Agentweaver.Mcp/Tools/*.cs`, endpoints. Owned by [`docs-sync`](../docs-sync/SKILL.md). |

> **Ownership note.** `docs/.vitepress/config.ts` and `docs/index.md` are frequently owned/edited by others. If you cannot edit them, do facets 5–6 as a hand-off note in your PR description listing the exact entries to add, rather than blocking.

## Mermaid diagram conventions (this repo)

Diagrams are styled for both light and dark mode. Follow these exactly:

- **Per-diagram init string** (copy verbatim, it sets the Fluent-style palette):
  ```
  %%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'15px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
  ```
- Global `flowchart` defaults (`useMaxWidth:true`, `htmlLabels:true`, `padding:12`) come from `docs/.vitepress/config.ts` `mermaid:` — don't fight them.
- **Dark mode** is handled by `docs/.vitepress/theme/custom.css`, scoped under `html.dark .mermaid`. It already overrides flowchart chrome (`.cluster rect`, `.edgeLabel`, `.edgePaths .path`, markers) **and** sequence-diagram classes: `.messageText`, `.loopText`, `.labelText`, `.labelBox`, `.loopLine`, `.actor-line`, `.messageLine0/1`, `.sequenceNumber`.
- **Click-to-lightbox**: `docs/.vitepress/theme/index.ts` binds every `.mermaid` SVG to a fullscreen overlay (it strips the `useMaxWidth` inline style on the clone). No action needed per diagram.
- ⚠️ **A NEW diagram *type*** (e.g. `stateDiagram`, `classDiagram`, `gantt`) likely has classes **not** yet covered by the dark-mode overrides — text may render invisible in dark mode. If you introduce a new type, verify in dark mode and add the missing `html.dark .mermaid .<class>` rules to `custom.css` (that file is part of facet 6/diagrams; coordinate if you don't own it).
- Prefer `flowchart` and `sequenceDiagram` (already fully themed).

## VitePress / build gotchas

- **Bare `<word>` breaks Vue compile.** VitePress parses markdown through Vue, so a bare angle-bracket token like `<port>` or `<HOST>` is read as an HTML/Vue tag and fails the build. Wrap such tokens in inline-code `` `<port>` `` or use a brace placeholder like `{port}`. (This is why the gold-standard pages write `{target_port}`, `{pod_name}`, and `` `Forwarding from 127.0.0.1:<port> ->` `` inside code.)
- `ignoreDeadLinks: true` is set, so a typo'd link won't fail the build — **manually verify** your cross-links resolve.
- `markdown.attrs` is **disabled** — `{.class}` / `{#anchor}` attribute syntax does nothing; endpoint headings ending in `{id}` are safe.
- Screenshots are served from `docs/public/` at the site root: reference them as `/screenshots/{name}.png`.
- Always finish with `cd docs; npm run build` and confirm it's green.

## Definition of Done checklist

Copy this, delete the rows that don't apply, and check each off:

- [ ] **Read the code first** — listed the real `file:line` sources for every claim.
- [ ] **Deep Dive** page written with concept, end-to-end flow, a `## Source` table, and a themed mermaid diagram.
- [ ] **Reference** page written: routes / DTO / config keys / status codes, all from source.
- [ ] **User Guide (experience)** page written: step-by-step flow, availability conditions, what-to-expect.
- [ ] **Screenshots**: placeholder image committed under `docs/public/screenshots/`, row added to `screenshot-plan.md`, draft `test.skip` capture added to `tests/e2e/screenshots.spec.ts`.
- [ ] **Landing card** added to `docs/index.md` (if headline feature).
- [ ] **Nav** entries added to `docs/.vitepress/config.ts` in the correct sidebar group(s) (or handed off if not owned).
- [ ] **Cross-links** added on the new pages *and* on related existing pages (both directions).
- [ ] **Diagrams** follow the init-string + dark-mode conventions; new diagram types verified in dark mode.
- [ ] **Generated docs** regenerated (`node scripts/gen-docs.mjs`) and committed if MCP tools / endpoints changed.
- [ ] **Build green**: `cd docs; npm run build`.

## Worked example — the gold standard

The **sandbox browser preview** feature is the template to imitate. It documents one feature across the full surface:

- **Deep Dive** — `docs/deep-dive/sandbox-browser-preview.md`: concept, a themed `flowchart LR` mermaid diagram, numbered end-to-end flow, security notes, and a `## Source` table mapping each concern to a real file (`SandboxEndpoints.cs`, `PortForwardService.cs`, `IPodNameRegistry.cs`, `WorkflowRunPage.tsx`, …).
- **Reference** — `docs/reference/sandbox-browser-preview.md`: a **Routes** table, a `PortForwardSessionDto` field table, a **Status codes** table, a **Limits** table with exact config keys (`Sandbox:PortForward:MaxConcurrentSessionsPerRun`), and request/response examples — every value grounded in the service.
- **User Guide** — `docs/experience/sandbox-browser-preview.md`: when the **Preview** button appears, step-by-step dialog flow, "what to expect", two placeholder screenshots with `::: info Screenshot is a placeholder` callouts.
- **Screenshots** — placeholders `workflow-run-graph.png` and `sandbox-preview-dialog.png` under `docs/public/screenshots/`, rows #13/#14 in `screenshot-plan.md`, and matching `test('sandbox-preview-dialog.png', …)` (skipped) in `tests/e2e/screenshots.spec.ts`.
- **Landing card** — `docs/index.md` `features:` entry "Sandbox browser preview" with a `See it in action →` link.
- **Nav** — three `config.ts` sidebar entries: Reference, User Guide (Web), and Deep Dive (Execution & integration) groups.
- **Cross-links** — the three new pages link to each other, and the four related sandbox pages (`sandbox.md`, `sandbox-pod-execution.md`, `sandbox-pods.md`, `runs-board-watch.md`) link to the new pages and vice-versa.

Read those files before authoring a new feature's docs and mirror their structure, tone, and section ordering.

## Invocation flow

When asked to document a feature, do this in order:

1. **Locate & read the implementation** (endpoints, services, DTOs, UI components) and note `file:line` for every fact.
2. **Emit a per-facet plan** for *this* feature using the matrix above — mark which facets apply and the exact files you'll create/edit.
3. **Execute each facet**, grounding every claim in code and matching the gold-standard structure.
4. **Regenerate** generated docs if MCP tools / endpoints changed (`node scripts/gen-docs.mjs`).
5. **Build-verify**: `cd docs; npm run build` → green. Manually click-check cross-links and dark-mode diagrams.

## Quick reference

| Action | Command / path |
| --- | --- |
| Gold-standard pages | `docs/{deep-dive,reference,experience}/sandbox-browser-preview.md` |
| Mermaid theming | `docs/.vitepress/theme/custom.css`, `theme/index.ts`, `config.ts` `mermaid:` |
| Screenshot plan | `docs/experience/screenshot-plan.md` + `tests/e2e/screenshots.spec.ts` |
| Regenerate derived docs | `node scripts/gen-docs.mjs` (see [`docs-sync`](../docs-sync/SKILL.md)) |
| Build the site | `cd docs && npm run build` |
| Strategy / CI drift | `.github/DOCS_SYNC.md`, `.github/workflows/docs-drift.yml` |
