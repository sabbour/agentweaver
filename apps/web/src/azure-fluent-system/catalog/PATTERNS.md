# Azure Fluent pattern catalog

Checked-in pattern inventory for `apps/web/src/azure-fluent-system`, sourced from Azure Pattern Templates / Fluent 2 (`TXALL9CS0727dvGcZo84Bg`). This catalog tracks two orthogonal signals per family: **implementation readiness** (is it built with a live preview in the showcase?) and **design source** (how much Figma design context was extracted). All 8 families are implemented with live showcase previews, library mappings, and checked-in examples; the design-source depth still varies by family and is preserved below as provenance.

## Status summary

| Measure | Value |
| --- | --- |
| Pattern families | 8 |
| Implemented · live preview | 8 |
| Unique tracked dev-mode nodes | 25 |
| Design source · rich design context | 1 |
| Design source · page index | 1 |
| Design source · component inventory | 6 |

## Pattern inventory table

> Readiness note: every family below is implemented with a live showcase preview (the `showcase` column is `Yes` for all rows). The **MCP extraction status** column records design-source provenance only — how much Figma design context was cached — not whether the family is built.

| Figma node reference / dev-mode URL | MCP extraction status | extraction date | where extracted from | implemented component/pattern mapping | showcase |
| --- | --- | --- | --- | --- | --- |
| **Create / stepped form blade**<br>Page / [3203:24770](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=3203-24770&m=dev)<br>Representative nodes / [3203:24770](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=3203-24770&m=dev), [6747:133457](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6747-133457&m=dev), [3203:24781](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=3203-24781&m=dev) | rich-context | Unknown | Figma `TXALL9CS0727dvGcZo84Bg` · `get_design_context` + `get_variable_defs` on `3203:24770` | `BladeHeader`<br>`CreateResourcePattern`<br>`FormFooter`<br>`AzureTabList` | Yes |
| **Browse Resource**<br>Page / [4417:3962](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=4417-3962&m=dev)<br>Representative nodes / [4570:40874](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=4570-40874&m=dev) | page-index-only | Unknown | Figma `TXALL9CS0727dvGcZo84Bg` · page/index + representative dev-mode citations in row | `BrowseResourcePattern`<br>`DataToolbar`<br>`FilterBar`<br>`AzureDataGrid`<br>`Pager` | Yes |
| **Notifications**<br>Page / [5707:60107](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5707-60107&m=dev)<br>Representative nodes / [5760:12271](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5760-12271&m=dev), [5760:12325](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5760-12325&m=dev) | component-inventory | Unknown | Figma `TXALL9CS0727dvGcZo84Bg` · page/index + representative dev-mode citations in row | `NotificationPattern`<br>`AzureDataGrid` | Yes |
| **Delete A Resource**<br>Page / [5649:6163](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5649-6163&m=dev)<br>Representative nodes / [5706:113870](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5706-113870&m=dev), [5706:110040](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5706-110040&m=dev), [5747:42979](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5747-42979&m=dev) | component-inventory | Unknown | Figma `TXALL9CS0727dvGcZo84Bg` · page/index + representative dev-mode citations in row | `DeleteResourceDialog`<br>`AzureDataGrid`<br>`FormFooter` | Yes |
| **Manage A Resource**<br>Page / [6331:13976](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6331-13976&m=dev)<br>Representative nodes / [6432:43439](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6432-43439&m=dev), [6710:173923](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6710-173923&m=dev), [6710:115802](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6710-115802&m=dev) | component-inventory | Unknown | Figma `TXALL9CS0727dvGcZo84Bg` · page/index + representative dev-mode citations in row | `ManageResourcePattern`<br>`ServiceMenu`<br>`AzureDataGrid` | Yes |
| **Service overview**<br>Page / [4625:1737](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=4625-1737&m=dev)<br>Representative nodes / [5163:12001](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5163-12001&m=dev), [8195:9103](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=8195-9103&m=dev) | component-inventory | Unknown | Figma `TXALL9CS0727dvGcZo84Bg` · page/index + representative dev-mode citations in row | `ServiceOverviewPattern` | Yes |
| **Feedback / CES / CVA**<br>Page / [4493:21](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=4493-21&m=dev)<br>Representative nodes / [5080:12885](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5080-12885&m=dev), [5080:12891](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5080-12891&m=dev), [5080:12902](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5080-12902&m=dev) | component-inventory | Unknown | Figma `TXALL9CS0727dvGcZo84Bg` · page/index + representative dev-mode citations in row | `FormFooter`<br>`NotificationPattern` | Yes |
| **Table of contents / pattern index**<br>Page / [1024:66](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=1024-66&m=dev)<br>Representative nodes / [7947:112498](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=7947-112498&m=dev) | component-inventory | Unknown | Figma `TXALL9CS0727dvGcZo84Bg` · page/index + representative dev-mode citations in row | Pattern doctrine only (`showcase navigation`) | Yes |

## Source-reference rules

1. Treat page-level Pattern Templates nodes as indexes unless this pack explicitly says rich design context succeeded on that exact node.
2. If MCP is available during refresh work, prefer MCP text extraction (`get_design_context`, `get_variable_defs`, `list_file_components_for_code_connect`) over screenshots. Screenshots confirm layout; they are not the primary source of truth.
3. Convert Dev Mode output into Fluent v9 primitives and the local `azf-` CSS/token contract. Do not preserve Figma-exported Tailwind classes.
4. Keep all implementation artifacts self-contained under `apps/web/src/azure-fluent-system`.
5. Downstream consumers should be able to inspect examples, catalog tables, docs, CSS, library components, and assets without any MCP access.
6. When a page node fails design-context extraction but succeeds for inventory, use that page node to classify the family, then pivot to a concrete child node from this catalog.

## Local-first workflow

Use this workflow in downstream projects where Figma MCP may not exist:

1. Start with the pattern inventory table in this file to identify the family, extraction status, local examples, and implementation mappings.
2. Inspect the checked-in pattern rows and notes here before opening code.
3. Inspect `showcase/` and `examples/` before changing code.
4. Reuse `patterns.tsx`, `components.tsx`, and `tokens.css` before creating anything new.
5. Treat dev-mode URLs in this file as citations only.
6. If Figma MCP is available, use it only as an optional refresh path for deeper extraction context.

## Shared token anchors

These values repeated across the successful concrete extraction and should anchor future implementations unless a deeper node proves otherwise:

- Page canvas: `#ffffff`
- Brand/action blue: `#0f6cbd` (site header / selected rail) and `#0078d4` (primary CTA)
- Font family: `Segoe UI`
- Core text: body `14/20`, body-strong `14/20 semibold`, subtitle `16/22 semibold`, title `24/32 semibold`
- Control radius: `4px`
- Input control height: `24px` chrome with `10px` content padding
- Default borders: `#d1d1d1`; command/footer border: `#cccccc`
- Portal shadow level: `Drop Shadow - Level 2`
- Disabled footer/button colors: foreground `#bdbdbd`, background `#f0f0f0`, stroke `#e0e0e0`

## Pattern family table

| Family | Source file + dev-mode nodes | Extraction status | Anatomy | Variables / tokens | Implementation notes | Anti-rules |
| --- | --- | --- | --- | --- | --- | --- |
| Create / stepped form blade | File: `TXALL9CS0727dvGcZo84Bg`<br>Worked example: [3203:24770](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=3203-24770&m=dev)<br>Key child anchors: [6747:133457](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6747-133457&m=dev), [3203:24777](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=3203-24777&m=dev), [3203:24781](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=3203-24781&m=dev) | Rich design context + variable defs succeeded on the concrete node. Safe render target. | Portal shell, site header, breadcrumb, blade header, 3-step horizontal tablist, intro copy, 728px form column, docked footer. | White page canvas, brand blue header, title `24/32`, body `14/20`, radius `4`, input chrome `24`, border `#d1d1d1`, footer border `#ccc`, shadow level 2. | Map to `BladeHeader`, `CreateResourcePattern`, `FormFooter`, and a step/tab surface that preserves numbered icons plus the 3px selected rail. Keep breadcrumb separate from the blade header. | Do not collapse this into a generic modal wizard. Do not move Next/Previous above the form. Do not replace the tab underline with pill chips or cards. |
| Browse Resource | File: `TXALL9CS0727dvGcZo84Bg`<br>Page index: [4417:3962](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=4417-3962&m=dev)<br>Concrete component: [4570:40874](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=4570-40874&m=dev) `Location summary` | Page inventory succeeded; page-level design context failed. Use `4417:3962` as an index only, then jump to concrete children. | Browse shell, filter/query surfaces, location summary, map/data summary, grid-driven selection flow. | Shared portal shell tokens plus browse surfaces that stay on white/subtle neutrals with restrained brand highlights. | Map to `BrowseResourcePattern`, `DataToolbar`, `FilterBar`, `AzureDataGrid`, and `Pager`. Treat `Location summary` as a supporting reference for summary/preview regions, not as the whole page. | Do not treat `4417:3962` as a render target. Do not replace browse/filter/grid structure with a marketing card gallery. |
| Notifications | File: `TXALL9CS0727dvGcZo84Bg`<br>Page index: [5707:60107](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5707-60107&m=dev)<br>Representative nodes: [5760:12271](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5760-12271&m=dev), [5760:12325](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5760-12325&m=dev) | Component inventory only. Good family classifier; no rich context cached in this pack. | Notification pane body, contextual pane, toolbar, data grid, and empty state working together. | Shared portal shell tokens; lean on neutral surfaces and semantic status colors instead of bespoke notification chrome. | Map to `NotificationPattern` for message treatment plus `AzureDataGrid` and empty-state slots for the pane body/context pane composition. | Do not modalize the notification pane. Do not add glass or toast-wall decoration that overpowers the task surface. |
| Delete A Resource | File: `TXALL9CS0727dvGcZo84Bg`<br>Page index: [5649:6163](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5649-6163&m=dev)<br>Representative nodes: [5706:113870](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5706-113870&m=dev), [5706:110040](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5706-110040&m=dev), [5706:110052](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5706-110052&m=dev), [5747:42979](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5747-42979&m=dev) | Component inventory only. Family is well-indexed by footer/content/dialog variants. | Delete footer, implication/dependent/associated/bulk delete content, resource identity row, recoverable/permanent dialog variants. | Shared tokens plus semantic danger/warning colors; confirmation input/footer states matter more than decorative red fill. | Map to `DeleteResourceDialog`, `AzureDataGrid`, inline dependency lists, and footer confirmation affordances. Keep copy explicit about recoverable vs permanent. | Do not flood the entire surface with danger styling. Do not enable destructive primary actions before the confirmation contract is satisfied. |
| Manage A Resource | File: `TXALL9CS0727dvGcZo84Bg`<br>Page index: [6331:13976](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6331-13976&m=dev)<br>Representative nodes: [6432:43439](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6432-43439&m=dev), [6492:120446](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6492-120446&m=dev), [6710:173923](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6710-173923&m=dev), [6710:115802](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6710-115802&m=dev) | Component inventory only. Strong enough to recover layout anatomy. | Service navigation + content grid, public/restricted network access controls, subnet/private endpoint forms, accordion header/content, status cell density. | Shared shell tokens, `4px` controls, restrained status colors, dense form/grid spacing. | Map to `ManageResourcePattern`, `ServiceMenu`, accordions, `AzureDataGrid`, and task-focused forms. Prefer structural layout over decorative containers. | Do not flatten management tasks into stacked marketing cards. Do not turn routine manage flows into multi-step wizards unless the cataloged source reference shows a wizard. |
| Service overview | File: `TXALL9CS0727dvGcZo84Bg`<br>Page index: [4625:1737](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=4625-1737&m=dev)<br>Representative nodes: [5163:12001](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5163-12001&m=dev), [5158:16431](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5158-16431&m=dev), [5154:14810](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5154-14810&m=dev), [8195:9103](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=8195-9103&m=dev) | Component inventory only. Good card/footer taxonomy. | Overview cards with icon/illustration/hero content modes, link list, footer CTA/navigation choices. | Shared shell tokens; white cards on subtle neutrals; links/actions carry the accent. | Map to `ServiceOverviewPattern` with reusable overview card/footer composition. Keep action hierarchy clear and product-oriented. | Do not import the generic SaaS hero-metric template. Do not add gradient hero cards or decorative KPI counters. |
| Feedback / CES / CVA | File: `TXALL9CS0727dvGcZo84Bg`<br>Page index: [4493:21](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=4493-21&m=dev)<br>Representative nodes: [5080:12885](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5080-12885&m=dev), [5080:12891](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5080-12891&m=dev), [5080:12902](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5080-12902&m=dev) | Component inventory only. Family is clear; use for survey/feedback task surfaces. | Next steps content, feedback footer, top radio affordance, message bar + textarea feedback content. | Shared text/control tokens plus semantic feedback colors; emphasize clarity, not decoration. | Map to task-local feedback surfaces using Fluent radios, textarea, message bars, and `FormFooter`-style action placement. | Do not build a decorative survey microsite. Do not bury the primary action under extra card chrome or oversized illustration. |
| Table of contents / pattern index | File: `TXALL9CS0727dvGcZo84Bg`<br>Representative node: [7947:112498](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=7947-112498&m=dev) | Component inventory only. Useful as a taxonomy reference, not a product task surface. | Top navigation, headline, component rows, dividers, links, badges. | Shared shell and text tokens; badge/link treatments matter more than bespoke layout. | Use this to organize the showcase/workbench and to classify patterns before implementing them elsewhere. | Do not ship the index surface as if it were an end-user workflow. Do not replace pattern source references with screenshot mosaics. |

## Worked example: `3203:24770` (`Isolated` → `First step`)

**Dev mode URL:** <https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=3203-24770&m=dev>

### Extraction summary

- Root node: `Isolated` (`3203:24770`)
- Main frame: `First step` (`3203:24771`) at `1920×1080`
- Proven successful child references in the extracted context:
  - `Site Header` (`3203:24773`)
  - `Breadcrumb` (`3203:24774`)
  - `Blade header` (`6747:133457`)
  - `Azure Horizontal TabList` (`3203:24777`)
  - two 728px form sections (`3258:7786`, `3260:9526`)
  - `Footer bar` (`3203:24781`)
- Variable extraction succeeded for the same node and confirmed the shared token anchors listed above.

### Anatomy to preserve

1. **Portal shell first.** The outer example is a full portal view, not just a form. Preserve the blue Azure site header, breadcrumb row, blade header, and docked footer.
2. **Breadcrumb is separate from the blade header.** Breadcrumb uses `16/22` Segoe UI styling above the `24/32` blade title.
3. **Steps are a horizontal tablist, not pill chips.** The selected step uses a numeric icon plus a `3px` blue underline. Inactive steps are neutral text with the same rhythm but no rail.
4. **Form body is narrow inside a wide shell.** Content padding is `20px`; form content stays at a `728px` max with a `230px` label column and `250px`+ input column.
5. **Inputs are compact Azure controls.** Keep `4px` radii, white fields, neutral borders, and the thin accessible underline behavior.
6. **Footer actions are docked and asymmetric.** `Previous` is disabled on the left, `Next` is the primary action, and `Give feedback` is right-aligned in the footer bar.

### Library mapping

| Figma source reference | Library surface | Acceptance target |
| --- | --- | --- |
| `Blade header` (`6747:133457`) | `BladeHeader` | Separate title region from breadcrumb; preserve 24/32 semibold title scale and close affordance. |
| `Azure Horizontal TabList` (`3203:24777`) | `CreateResourcePattern` step surface or `AzureTabList` composition | Numeric step markers + selected underline remain visible; do not downgrade to plain buttons. |
| Form sections (`3258:7786`, `3260:9526`) | `CreateResourcePattern` body + Fluent form fields | Preserve the narrow 728px reading column, 20px vertical grouping rhythm, and compact Azure input density. |
| `Footer bar` (`3203:24781`) | `FormFooter` | Docked bottom bar, muted top border, disabled Previous, primary Next, right-aligned feedback link. |

### Implementation acceptance checklist

- [ ] Uses Fluent v9 primitives plus `azf-` styling; no Tailwind export classes remain.
- [ ] Preserves the full portal shell hierarchy instead of rendering only a floating form.
- [ ] Keeps breadcrumb above the blade header.
- [ ] Uses a numbered step/tab pattern with a visible selected rail, not chip buttons.
- [ ] Keeps form content constrained to the narrow column inside the wide shell.
- [ ] Uses Segoe UI body/title scales backed by Fluent tokens and the guide token anchors.
- [ ] Keeps footer actions docked at the bottom with disabled `Previous`, primary `Next`, and right-aligned `Give feedback`.
- [ ] Uses screenshots only as a final parity check, not as the primary implementation source.

## Future refresh checklist

When a future agent refreshes a pattern with Figma MCP, keep the process lightweight:

1. Start with this catalog row and the linked local files in `showcase/`, `examples/`, `patterns.tsx`, `components.tsx`, and `tokens.css`.
2. Use the listed page node only as an index when the row says `page-index-only`; jump to the representative child node before implementing.
3. If MCP is available, extract the concrete node with `get_design_context` and `get_variable_defs`; record failures explicitly instead of guessing.
4. Map the result onto existing library surfaces before creating anything new.
5. Validate shell hierarchy, spacing, token use, active/disabled states, and footer/action placement against the cited node.

## How agents should use this pack

1. **Start with this catalog.** It is faster to classify a node/family from the inventory table in this file than to reopen raw extraction output.
2. **Use page nodes as indexes.** If the catalog row says `page-index-only`, jump to a representative child node before implementing.
3. **Reuse the library first.** `BladeHeader`, `BrowseResourcePattern`, `ManageResourcePattern`, `CreateResourcePattern`, `ServiceOverviewPattern`, `DeleteResourceDialog`, `NotificationPattern`, and `FormFooter` already capture the expected product vocabulary.
4. **Use local artifacts for ordinary consumption.** The checked-in showcase, examples, catalog tables, CSS tokens, and source files should be enough in another project even without Figma MCP.
5. **Escalate to fresh Figma extraction only for missing states and only when MCP is available.** This pack is intended to cover the family classification, token anchors, anti-rules, and implementation file pointers needed for most work.
