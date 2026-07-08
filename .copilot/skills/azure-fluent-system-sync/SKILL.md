---
name: azure-fluent-system-sync
description: Keep the standalone Azure Fluent-style component and pattern library synchronized with Azure UI Kit Figma evidence, IconCloud/Fluent icon sources, implementation recipes, DESIGN.md, and validation. Use this skill whenever the user mentions updating the Azure Fluent library, refreshing from Figma, completing rich design-context coverage, syncing IconCloud/Azure icons, expanding implementation recipes, fixing the Create Resource pattern gap, hardening the component library, or making the system drop-in ready for other projects.
---

# Azure Fluent System Sync

Use this skill to keep the portable Azure Fluent-style system current and usable as a standalone library. The system has five connected layers:

1. **Evidence cache** from Azure UI Kit / Fluent 2 Figma.
2. **Icon assets** from approved sources: Fluent system icons and IconCloud exports.
3. **Implementation recipes** that translate evidence into public React contracts.
4. **Library code** under `apps/web/src/azure-fluent-system`.
5. **Docs and validation** in `DESIGN.md`, package README, tests, build, and lint.

The goal is not a one-off Agentweaver visual pass. The goal is a high-fidelity, reusable system another React project can consume without reopening Figma.

## Source of truth map

Use these artifacts before making changes:

| Layer | Location |
|---|---|
| Design doctrine | `DESIGN.md`, section `Standalone Azure Fluent system contract` |
| Library implementation | `apps/web/src/azure-fluent-system/` |
| Library README | `apps/web/src/azure-fluent-system/README.md` |
| Figma cache root | `artifacts/figma-extraction/azure-ui-kit-fluent2/` |
| Component ledger | `comprehensive-component-cache-ledger.json` |
| Pattern ledger | `patterns.jsonl`, `pattern-node-index.json`, `distilled-pattern-contexts.json` |
| Recipes | `implementation-recipes.json`, `implementation-recipe-gap-list.json` |
| System plan | `standalone-system-plan.json` |
| Icon downloads | `artifacts/iconcloud/` |

If the artifact folders are missing or stale, regenerate or refresh `artifacts/figma-extraction/azure-ui-kit-fluent2/` and `artifacts/iconcloud/` with the sync workflow before proceeding. Do not commit machine-local artifact paths.

## Vocabulary

- **Rich design context** means Figma `get_design_context` output for a published component node, plus the component graph record. If the node is a sparse wrapper, use metadata and targeted child `get_design_context` files.
- **Graph snapshot** means node-level published component graph metadata only. It is useful for inventory, but it is not enough for high-fidelity implementation.
- **Recipe** means a structured implementation spec: evidence files, public React API, props, callbacks, slots, state model, variants, accessibility, examples, and implementation skeleton.
- **API** means the public React library contract: exported component/pattern names, prop types, events, slots/render props, controlled/uncontrolled state, and example usage. It does not mean a backend HTTP API.
- **Ready-to-use library** means consumers can import components, pass data/handlers, handle loading/error/disabled states, and get accessible Fluent React v9 UI without reading raw Figma dumps.

## Standard workflow

### 1. Classify the request

Map the user request to one or more workstreams:

| User asks for | Workstream |
|---|---|
| "update from Figma", "full component coverage", "rich context" | Figma evidence sync |
| "icons", "IconCloud", "Azure glyphs" | Icon source sync |
| "recipes", "API", "handoff docs" | Recipe/API sync |
| "component library ready", "high fidelity", "drop-in" | Library hardening |
| "Create Resource pattern" | Pattern gap investigation |
| "use it in Agentweaver" | Consumer migration |

Do not mix up completion claims. Evidence, recipes, code, and consumer migration are separate milestones.

### 2. Sync Figma evidence

1. Read `manifest.json` and `comprehensive-component-cache-ledger.json`.
2. Find ledger entries with graph-only or metadata-only evidence.
3. For each missing rich context entry:
   - Call Figma `get_design_context` for the exact node when available.
   - If sparse or wrapper-like, call metadata and then rich context on meaningful child frames/symbols.
   - Save raw output under `raw\design-context-comprehensive-<nodeid>-<slug>.txt`.
   - Update the ledger with request attempts, raw files, evidence type, and any failure.
4. Update `manifest.json` request count/status.
5. Create a new batch file named `batch-comprehensive-<N>-rich-context-<scope>.json`.

If rate limiting or Figma access fails, record the exact node IDs and stop gracefully. Do not claim full rich coverage unless every published node has rich context or an explicit unavailable record.

### 3. Investigate pattern gaps

For pattern issues such as **Create Resource**:

1. Read `pattern-node-index.json`, `patterns.jsonl`, and linked raw files.
2. Search the linked pattern file for child frames, variant names, nearby nodes, or alternate template names before spending new Figma calls.
3. If a usable child/template exists, cache rich context and update `patterns.jsonl`, distilled contexts, and `DESIGN.md`.
4. If only a notice/update node exists, create a root-cause record with node IDs, metadata evidence, and exact blocker. Mark the pattern blocked, not complete.

### 4. Sync icons

Use this source hierarchy:

1. **General system icons:** `@fluentui/react-icons`, from the `microsoft/fluentui-system-icons` family.
2. **Azure/resource glyphs:** IconCloud (`https://iconcloud.design/`) authenticated exports.
3. **Figma Community Microsoft Fluent System Iconography:** visual/reference guidance only unless assets are explicitly exported under acceptable terms.

IconCloud handling:

- Use Microsoft Edge/Playwright with a persistent profile when interactive login is needed.
- Never ask for or store credentials.
- Use visible export/download controls only; do not scrape hidden/private APIs.
- Download to session artifacts first, not directly into source.
- Normalize assets into a manifest with names, categories, format, source URL, and file path.
- Wire assets through `AzureIconProvider`, `AzureIcon`, and `createIconCloudRegistry`.
- Deduplicate extensionless SVG payloads and named SVG duplicates before copying assets into any project-owned static folder.

### 5. Complete recipes and public APIs

For every component/pattern in the standalone system surface:

1. Update `implementation-recipes.json` with:
   - `name`
   - `figmaNodeIds`
   - `rawEvidenceFiles`
   - `purpose`
   - `fluentReactMapping`
   - `layoutAnatomy`
   - `statesAndVariants`
   - `accessibilityAndInteraction`
   - `proposedApi`
   - `props`
   - `events`
   - `slots`
   - `stateModel`
   - `exampleUsage`
   - `implementationSkeleton`
   - `confidence`
   - `remainingUnknowns`
2. Resolve `summary-only` entries by either:
   - writing a dedicated full recipe,
   - folding the node into a named recipe with explicit evidence and rationale, or
   - marking it not part of the public library API with a reason.
3. Update `implementation-recipe-gap-list.json`.

Do not leave unexplained "summary-only" recipe coverage when the user asks for a drop-in system.

### 6. Harden library code

Work in `apps/web/src/azure-fluent-system/`.

High-fidelity components must provide:

- Fluent React v9 primitives and tokens, not Tailwind or copied Figma CSS.
- Default, hover/focus/active/disabled/loading/error states where applicable.
- Controlled data/value APIs and callback signatures.
- Accessible labels, table/list semantics, keyboard-friendly actions, and focus behavior.
- Responsive behavior without nested card chrome.
- Icon slots that accept Fluent icons or IconCloud registry entries.
- Examples in README and tests for critical interactions.

Prioritize:

- `BladeHeader`
- `ServiceMenu`
- `DataToolbar`
- `FilterBar`
- `AzureDataGrid`
- `ResourceTagEditor`
- `FormFooter`
- `Pager`
- `CopilotComposer`
- `CopilotResponse`
- `InlineCopilot`
- `AgenticProgress`
- `AzureTabList`
- `HelpPopover` / `CalloutPopover`
- pattern wrappers such as browse, filtering, form blade, delete, service overview, and Copilot workspace.

### 7. Validate

Run the smallest checks that prove the changed layer:

```powershell
Set-Location apps\web
npm run build
npx eslint src/azure-fluent-system
npm run test -- --run <new-or-focused-test-file>
git diff --check
```

If full tests fail outside the library, record the exact failure and continue with focused validation. Do not hide unrelated timeouts.

### 8. Report status precisely

Use this report structure:

```markdown
**Evidence:** X / 148 published nodes have rich design context; Y wrapper+child; Z unavailable.
**Patterns:** Create Resource verdict and files updated.
**Recipes:** complete / folded / not-public-API / blocked counts.
**Icons:** downloaded/imported/normalized counts and source.
**Library:** components hardened and validation results.
**Next:** whether Agentweaver migration can begin.
```

## Guardrails

- Do not claim all components are high-fidelity if some only have graph snapshots.
- Do not treat raw Figma-generated code as production code.
- Do not expose hidden chain-of-thought; show explainable progress/state rows instead.
- Do not scrape credentials, hidden IconCloud APIs, or private Figma internals.
- Do not migrate Agentweaver broadly until the standalone library is stable enough to consume.
- Do not copy icon glyphs directly from Figma. Use `@fluentui/react-icons`, `microsoft/fluentui-system-icons`, or IconCloud exports.
