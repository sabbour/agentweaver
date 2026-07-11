---
name: "azure-fluent-system-sync"
description: "Project skill for refreshing the Azure Fluent System library from Azure UI Kit / Fluent 2 via Figma MCP, mapping extracted nodes into checked-in React/CSS/docs/showcase artifacts, and validating the result without silent fallback."
domain: "frontend-design-system"
confidence: "high"
source: "team-decision"
---

# Azure Fluent System sync

Use this skill when work touches the checked-in Azure Fluent System library under `apps/web/src/copilot-fluent-system/` and the request depends on Azure UI Kit / Fluent 2 Figma extraction, component inventory updates, node-level extraction, showcase coverage, or portable traceability docs.

The goal is a **checked-in, portable deliverable**. Future consumers should be able to use the library, docs, examples, and the three checked-in catalog files **without** reopening Figma or recovering transient runtime artifacts.

## When to invoke

Invoke this skill whenever the user asks to:

- refresh Azure Fluent System components or patterns from Figma
- extract the main Azure UI Kit component list / inventory
- extract a specific Figma node from a dev-mode URL
- add or update a Figma-backed component in the React library
- refresh `COMPONENTS.md`, `PATTERNS.md`, or `ICONS.md`
- add/verify component or icon visibility in the showcase
- reconcile Figma extraction outputs with `components.tsx`, `types.ts`, `tokens.css`, examples, tests, or `DESIGN.md`

## Project constants

| Item | Value |
| --- | --- |
| Library root | `apps/web/src/copilot-fluent-system/` |
| Azure UI Kit / Fluent 2 file key | `q2TdO4dVcMhNWYp0N6Bc05` |
| Component catalog | `apps/web/src/copilot-fluent-system/catalog/COMPONENTS.md` |
| Pattern catalog | `apps/web/src/copilot-fluent-system/catalog/PATTERNS.md` |
| Icon catalog | `apps/web/src/copilot-fluent-system/catalog/ICONS.md` |

| Library README | `apps/web/src/copilot-fluent-system/README.md` |
| Durable doctrine | `apps/web/src/copilot-fluent-system/DESIGN.md` and repo `DESIGN.md` |
| Showcase app | `apps/web/src/copilot-fluent-system/showcase/AzureFluentShowcaseApp.tsx` |
| Focused test | `apps/web/src/__tests__/azureFluentSystem.test.tsx` |

## Durable outcome rule

Do not stop at transient Figma output. The final deliverable must live in checked-in project files:

- React implementation in `components.tsx`, `patterns.tsx`, `types.ts`, and `tokens.css`
- focused examples in `examples/*.example.tsx`
- component catalog in `catalog/COMPONENTS.md`
- pattern catalog in `catalog/PATTERNS.md`
- icon catalog in `catalog/ICONS.md`
- summary/link updates in `README.md` when coverage shape changes
- visible preview coverage in `showcase/AzureFluentShowcaseApp.tsx`

Downstream agents must not need Figma MCP for ordinary consumption.

## Source-of-truth map

| Need | File |
| --- | --- |
| Public component/pattern code | `apps/web/src/copilot-fluent-system/components.tsx`, `patterns.tsx`, `types.ts`, `tokens.css` |
| Public icon code and assets | `apps/web/src/copilot-fluent-system/icons.tsx`, `assets/icons/azure/` |
| Component catalog | `apps/web/src/copilot-fluent-system/catalog/COMPONENTS.md` |
| Pattern catalog | `apps/web/src/copilot-fluent-system/catalog/PATTERNS.md` |
| Icon catalog | `apps/web/src/copilot-fluent-system/catalog/ICONS.md` |
| Portable usage and summary | `apps/web/src/copilot-fluent-system/README.md` |
| Durable doctrine / anti-rules | `apps/web/src/copilot-fluent-system/DESIGN.md` |
| Showcase presence | `apps/web/src/copilot-fluent-system/showcase/AzureFluentShowcaseApp.tsx` |
| Example coverage | `apps/web/src/copilot-fluent-system/examples/` |
| Focused regression coverage | `apps/web/src/__tests__/azureFluentSystem.test.tsx` |

## Workflow

### 1. Extract the main component inventory with Figma MCP

Use `figma-list_file_components_for_code_connect` with the Azure UI Kit file key:

- `fileKey: q2TdO4dVcMhNWYp0N6Bc05`

Treat the result as the **inventory source of truth** for:

- `apps/web/src/copilot-fluent-system/catalog/COMPONENTS.md`
- any cross-links in `apps/web/src/copilot-fluent-system/README.md`

When pattern-family inventory or guidance changes, keep the pattern-side checked-in artifacts synchronized too:

- `apps/web/src/copilot-fluent-system/catalog/PATTERNS.md`

When icon assets, aliases, or import strategy change, keep the icon-side artifact synchronized too:

- `apps/web/src/copilot-fluent-system/catalog/ICONS.md`

Required handling:

1. Rebuild or update the explicit per-component inventory rows from the Figma response.
2. Preserve one row per inventory component/component set.
3. Record:
   - total inventory row count
   - per-category counts
   - exact name/node audit counts **only** when they come from an explicit comparison source
4. Keep showcase placeholders and related local mappings separate from exact-node coverage.
5. Do **not** claim `148/148 implemented` unless the table actually supports that claim.

Current durable outputs should remain conservative:

- exact inventory rows must remain explicit in the checked-in helper data used by the showcase
- summary counts must stay aligned between the helper data and the Markdown table
- the Markdown catalog file must reflect the same totals and categories

### 2. Extract a single item/node with Figma MCP

Use the exact dev-mode node URL whenever possible, for example:

- `https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/...?...node-id=30028-627&m=dev`

For a single node:

1. Parse the exact file key and node ID from the dev-mode URL.
2. Call `figma-get_design_context` with that exact `fileKey` and `nodeId`.
3. Call `figma-get_variable_defs` with that exact `fileKey` and `nodeId`.
4. Use `figma-get_motion_context` only when motion/animation fidelity matters.

Failure handling is mandatory:

- do not silently fall back from MCP failure to screenshots, vague grouped references, or guessed fidelity
- If extraction fails, record the exact failure and node ID in durable project artifacts.
- Do **not** silently fall back to screenshots, vague grouped references, or guessed implementation fidelity.
- Use conservative status in the catalog rows such as the already-recorded statuses (`implemented-rendered`, `showcase-placeholder`, `needs-mcp-extraction`, `needs-implementation`, `local-only-needed`, `not-in-inventory`) plus explicit error text in notes or `mcpNodes`.
- Screenshots may help polish the UI, but they do **not** count as successful MCP extraction.

Tool constraints:

- If the URL is missing `node-id`, get a node-specific dev URL before claiming extraction.
- For `/make/` URLs or unsupported URL shapes, follow the MCP tool constraints exactly.
- If the tool requires a node-specific design URL and you do not have one, stop and report the blocker instead of inventing fidelity.

### 3. Map extraction into the checked-in library

When a node is added or refreshed, update the checked-in library, not just the inventory records:

- `apps/web/src/copilot-fluent-system/components.tsx`
- `apps/web/src/copilot-fluent-system/types.ts`
- `apps/web/src/copilot-fluent-system/tokens.css`
- `apps/web/src/copilot-fluent-system/icons.tsx`
- `apps/web/src/copilot-fluent-system/assets/icons/azure/`
- `apps/web/src/copilot-fluent-system/examples/*.example.tsx`
- `apps/web/src/copilot-fluent-system/catalog/COMPONENTS.md`
- `apps/web/src/copilot-fluent-system/catalog/PATTERNS.md`
- `apps/web/src/copilot-fluent-system/catalog/ICONS.md`
- `apps/web/src/copilot-fluent-system/showcase/AzureFluentShowcaseApp.tsx`
- `apps/web/src/__tests__/azureFluentSystem.test.tsx`

Mapping rules:

- Use implemented React export names where present: e.g. `AzureAccordion`, `CodeSnippet`, `CopyButton`, `CopilotComposer`, `CopilotResponse`, `InlineCopilot`, `AgenticProgress`, `CopilotWorkspacePattern`.
- Use `Related local export <surface>` when the node can borrow implementation context from a broader checked-in surface but still lacks its own dedicated preview.
- Use `Needs mapping` or `Not mapped` when no checked-in export is present.
- Reuse the durable statuses already present in the checked-in catalog rows rather than inventing success.

Implementation rules:

- Use local `azf-` classes and the existing token contract in `tokens.css`.
- Do **not** paste Tailwind or generated Figma CSS into the library.
- Do **not** treat generated Figma React code as production code.
- Preserve portability: consumers must still work from local files only.

### 4. Update `DESIGN.md` carefully

Only write durable guidance into `DESIGN.md`:

- pattern doctrine
- local component usage notes
- anti-rules / guardrails
- durable source citations where they help future maintenance

Do **not** make `DESIGN.md` depend on:

- Figma MCP availability
- machine-local paths
- transient tool-output files
- copied raw extraction dumps

`DESIGN.md` should tell future agents **how to work locally** even when Figma MCP is unavailable.

### 5. Add and verify showcase coverage

Every newly mapped component or pattern must be reflected in the showcase:

- add it to the **Components** preview when it is a reusable library primitive
- add it to the **Patterns** browser when it is a composed pattern wrapper

If icon work changed:

- keep `catalog/ICONS.md` synchronized with the checked-in icon assets, aliases, and examples
- expose a minimal visible icon surface in the showcase when feasible, keeping it preview-oriented and aligned with `catalog/ICONS.md`

Verification rules:

- ensure the item is visibly present, not only mentioned in metadata
- keep showcase presence synchronized with `COMPONENTS.md`, `PATTERNS.md`, and `ICONS.md` when icon coverage is shown
- set showcase coverage explicitly in the mapping table:
  - `Yes` = the inventory row is visible and selectable in the showcase
  - `No` = not surfaced in the showcase

If a component reuses implementation context from another surface, the mapping table should say so plainly without implying the row is already implemented.

### 6. Validate without silent failure

Run the smallest set of existing checks that proves the changed layer, but do not silently skip required validation.

Required baseline validation for Azure Fluent System work:

```powershell
npm --prefix apps/web/src/copilot-fluent-system run showcase:validate-doctrine
```

When TypeScript, React, CSS, examples, catalog shape, or showcase behavior changed, also run:

```powershell
npm --prefix apps/web/src/copilot-fluent-system run showcase:build
npm --prefix apps/web run build
npm --prefix apps/web run test -- --run src/__tests__/azureFluentSystem.test.tsx
```

Also run a targeted consistency check for the durable mapping outputs:

- verify `COMPONENTS.md` remains a readable markdown table with the required columns and no embedded JSON block
- verify the component inventory row count in `COMPONENTS.md` stays aligned with its status summary
- verify the component status summary counts in `COMPONENTS.md` still match the table rows and any helper data used by the showcase
- verify extracted component rows in `COMPONENTS.md` still match the tracked MCP-backed component entries used by the showcase
- verify `PATTERNS.md` remains a readable markdown table with the required columns and no embedded JSON block
- verify the pattern-family row count in `PATTERNS.md` matches the tracked showcase/browser data
- verify `ICONS.md` remains a readable markdown table with the required columns and no embedded JSON or SVG dump
- verify the icon summary counts in `ICONS.md` stay aligned with the checked-in icon assets and import metadata
- verify `README.md` still links to `catalog/COMPONENTS.md`, `catalog/PATTERNS.md`, and `catalog/ICONS.md`

Run a forbidden-path / coupling search and scoped diff check:

```powershell
rg -n "transient-output markers|machine-local path markers" apps/web/src/copilot-fluent-system .copilot/skills/azure-fluent-system-sync
git --no-pager diff --check -- apps/web/src/copilot-fluent-system .copilot/skills/azure-fluent-system-sync
```

If validation fails:

- report the exact command
- report the exact error
- separate unrelated baseline failures from your change-specific failures
- do **not** claim the component/pattern is complete

## Reporting rules

When reporting completion, be precise:

- which file key and node IDs were used
- whether the inventory was refreshed
- which nodes were extracted successfully
- which nodes still need MCP extraction
- which exports were added or updated
- whether the showcase status is `Yes` or `No`
- which validations passed or failed

Never collapse `needs-mcp-extraction`, `showcase-placeholder`, and `implemented-rendered` into one misleading success claim.

## Guardrails

- Do **not** silently fall back from failed MCP extraction to guessed implementation fidelity.
- Do **not** claim high fidelity for nodes still marked `showcase-placeholder`, `needs-mcp-extraction`, `needs-implementation`, `local-only-needed`, or `not-in-inventory`.
- Do **not** commit machine-local paths, transient tool-output paths, or temp identifiers.
- Do **not** require downstream consumers to have Figma MCP.
- Do **not** paste Tailwind or raw generated design code into the library.
- Do **not** leave showcase visibility implicit; reflect it explicitly in `COMPONENTS.md` and `PATTERNS.md`.
- Do **not** update `DESIGN.md` with ephemeral extraction logs.
