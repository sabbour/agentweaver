# Coverage audit — Azure Fluent System

This document answers a single question: **what does the library cover today, how does that map
back to the Figma sources, and how is fidelity guaranteed?** It synthesizes the three locked catalog
inventories ([`catalog/COMPONENTS.md`](./catalog/COMPONENTS.md),
[`catalog/PATTERNS.md`](./catalog/PATTERNS.md), [`catalog/ICONS.md`](./catalog/ICONS.md)) plus the
design doctrine in [`DESIGN.md`](./DESIGN.md) into one status view. Every number here is the same
value the test suite locks against `componentCatalogData.inventoryCoverage`; none of it is
hand-maintained prose.

---

## 1. What is in the library now

| Surface | Source module | Count | Notes |
| --- | --- | --- | --- |
| Theme provider | `provider.tsx` | 1 | `AzureFluentProvider` — Fluent v9 provider wired to the Azure token overrides |
| Fluent 2 foundations | `foundations.tsx` | ~75 | Direct, typed re-exports of upstream `@fluentui/react-components` primitives so downstream apps consume one dependency |
| Composed components | `components.tsx` | 40 (+3 aliases) | Azure-specific compositions (blade header, command bar, Copilot surfaces, resource lists, etc.) |
| Patterns | `patterns.tsx` | 12 | Multi-component page/blade recipes (stepped-form blade, browse resource, delete resource, Copilot workspace, …) + 2 library-authored composed scenarios (coordinator run, agentic approval) |
| Icon catalog | `icons.tsx` + `assets/icons/azure` | 1441 unique SVG assets · 27 collections · 1637 raw exports | Full vendored Azure icon set, surfaced by the showcase icon browser |
| Examples | `examples/*.example.tsx` | 24 | Checked-in, self-contained usage samples |
| Barrel | `index.ts` | — | Re-exports types, provider, icons, components, foundations, patterns (showcase app is intentionally not re-exported) |

The public entry point is the barrel `index.ts`. Downstream consumers get provider + foundations +
components + patterns + icons from a single import surface.

---

## 2. Figma coverage

Three Figma files feed this library:

| Figma file | Key | Feeds |
| --- | --- | --- |
| Azure UI Kit (Fluent 2) | `q2TdO4dVcMhNWYp0N6Bc05` | Component inventory (148-node roll-up below) |
| Azure Pattern Templates (Fluent 2) | `TXALL9CS0727dvGcZo84Bg` | 8 pattern families |
| Agentic chat / Copilot spec | `oqjy7GlpGqEQgUwMCs1wdq` | Copilot / coordinator-run surfaces |

### 148-node inventory roll-up (Azure UI Kit)

Counts below are on the **MCP-extraction axis** (how deeply each node has been extracted), which is
the axis the test suite locks. It is deliberately distinct from the finer per-row delivery-linking
status in [`catalog/COMPONENTS.md`](./catalog/COMPONENTS.md); do not expect a naive row tally of
that table to reproduce these numbers.

| MCP-extraction status | Count | Meaning |
| --- | --- | --- |
| implemented-rendered | 26 | MCP-extracted and rendered by a real library export |
| needs-mcp-extraction | 45 | Exact node identified; deeper MCP extraction not yet cached |
| showcase-placeholder | 77 | Linked into a delivered surface but not tracked as standalone full-fidelity |
| needs-implementation | 0 | — |
| local-only-needed | 0 | — |
| **Total** | **148** | |

Exact name+node audit against the raw Figma manifest: **105 covered / 43 missing** of the named
nodes. The 43 misses are enumerated per row in [`catalog/COMPONENTS.md`](./catalog/COMPONENTS.md).

### Pattern coverage (Azure Pattern Templates)

**8 pattern families, 25 unique tracked dev-mode nodes.** All 8 families render a live preview in
the showcase (see [`catalog/PATTERNS.md`](./catalog/PATTERNS.md)). Family design-source depth varies
(rich design context: 1, page index: 1, component inventory: 6) — that is an extraction-depth signal,
independent of the fact that every family is implemented and previewable.

---

## 3. Copilot / coordinator-run surfaces (the priority set)

Every Copilot surface below was rebuilt or verified directly against its Figma node via MCP
`get_design_context` + `get_variable_defs`, with the node ID cited in the component source comment.

| Component | Figma node(s) | Status |
| --- | --- | --- |
| ChainOfThought | `386:75088` (sub-content `386:75111`) | Verified — indent + pink-forward running loader corrected |
| CopilotComposer | `32382:38468` | Rebuilt — gradient 32px pill, inset field, "Message Copilot" |
| CopilotResponse | `32382:38129` | Rebuilt — left-aligned header, 10px badge, plain body |
| BladeHeader | `32615:9834` (menu label `35294:9320`) | Verified — menu label 24px regular, resource icon 28px, 24px pipe divider |
| InlineCopilot | `29192:8232` (loader `29192:8246`, disclaimer `29192:8267`) | Verified — shared Copilot loader bar + AI-content disclaimer |
| ArtifactPill | `27865:11293` | Verified — inline title+type, arrow-maximize, 34px pill |
| AgenticProgress | `27950:10571` / `27880:13472` (loader `386:75129`) | Verified — shared Copilot loader dot + bar |
| CopilotWorkspacePattern | composition (`patterns.tsx`) | Inherits fidelity from the primitives above |

---

## 4. Honest gaps

- **45 nodes are `needs-mcp-extraction`.** Their exact node is identified but a deeper MCP pass is
  not yet cached. They are safe to consume via the closest implemented composition, but are not
  claimed as standalone full-fidelity.
- **77 nodes are `showcase-placeholder`.** Linked into a delivered surface (so they contribute to a
  real screen) but not individually tracked as full-fidelity exports.
- **43 named manifest nodes are unmatched** by an exact name+node in the inventory. Listed per row in
  [`catalog/COMPONENTS.md`](./catalog/COMPONENTS.md) for follow-up.
- A few referenced nodes (`3203:24770`, `6672:54683`) resolve to not-found / update-notice in the
  current Figma files; the surfaces that referenced them are derived compositions, not 1:1 extracts.

These are refresh backlog, not blockers: the delivered surfaces (Copilot set, 8 patterns, icon
catalog) are complete and verified.

---

## 5. Are examples and the showcase up to date?

Yes. 24 checked-in examples exercise the composed components; the showcase renders (a) a component
preview browser built from [`catalog/COMPONENTS.md`](./catalog/COMPONENTS.md), (b) an icon browser
built from [`catalog/ICONS.md`](./catalog/ICONS.md), and (c) a pattern example browser built from
[`catalog/PATTERNS.md`](./catalog/PATTERNS.md). The showcase reads the same locked
`componentCatalogData`, so the numbers in this audit and the numbers on screen cannot drift apart —
the test suite fails if they do.

---

## 6. How fidelity is guaranteed

1. **MCP-first.** Every component's styling is pulled from Figma via `get_design_context` +
   `get_variable_defs` — never eyeballed from a screenshot.
2. **Node-ID citations.** Each composed component's source comment cites the Figma node it was
   extracted from, so fidelity is auditable at the code level.
3. **Verify gates on every change.** `tsc -b` (EXIT=0) + `azureFluentSystem.test.tsx` (21/21) +
   `validate-pattern-doctrine.mjs` must all pass. The doctrine validator locks the catalog to
   exactly three inventory files and locks the coverage counts so the docs cannot silently drift.
4. **Local-first downstream.** Per [`DESIGN.md`](./DESIGN.md), downstream consumption and review do
   not require Figma MCP — the checked-in catalog + tokens are the local source of truth. Figma MCP
   is used only when intentionally refreshing the catalog.

> "Coverage is not fidelity." — [`DESIGN.md`](./DESIGN.md). A node counted as covered is only
> claimed full-fidelity when it is `implemented-rendered` with a cited node and passes the gates.

---

## See also

- [`REBUILD-GUIDE.md`](./REBUILD-GUIDE.md) — how to point an LLM at this library and rebuild the
  Agentweaver app in this design language, with an archetype page→pattern mapping table and a
  worked `CoordinatorRunPage` recipe.
- [`DESIGN.md`](./DESIGN.md) — the enforceable usage contract and fidelity gate.
