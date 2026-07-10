---
version: "1.0"
name: "Fluent 2 Design System"
description: >
  Design language for this project, implemented with @fluentui/react-components v9 (Fluent 2).
  All tokens are sourced from the microsoft/fluentui repository (web light theme, Communication Blue brand).
  Theme switching (dark / high-contrast) is handled automatically by FluentProvider.

colors:
  # ── Brand ─────────────────────────────────────────────────────────────────
  brand-primary: "#0078d4"          # brand[80]  colorBrandBackground / colorBrandForeground1
  brand-hover: "#106ebe"            # brand[70]  colorBrandBackgroundHover / colorBrandForeground2
  brand-selected: "#005a9e"         # brand[60]  colorBrandBackgroundSelected
  brand-pressed: "#004578"          # brand[40]  colorBrandBackgroundPressed
  brand-tint: "#eff6fc"             # brand[160] colorBrandBackground2
  brand-stroke: "#0078d4"           # brand[80]  colorBrandStroke1
  brand-stroke-subtle: "#c7e0f4"    # brand[140] colorBrandStroke2

  # ── Neutral Foreground / Text ─────────────────────────────────────────────
  colorNeutralForeground1: "#242424"     # grey[14] — primary text
  colorNeutralForeground2: "#424242"     # grey[26] — secondary text
  colorNeutralForeground3: "#616161"     # grey[38] — subtle / metadata
  colorNeutralForeground4: "#707070"     # grey[44] — decorative / tertiary
  colorNeutralForegroundDisabled: "#bdbdbd"   # grey[74]
  colorNeutralForegroundOnBrand: "#ffffff"    # white
  colorNeutralForegroundInverted: "#ffffff"   # white
  colorBrandForeground1: "#0078d4"            # brand[80]
  colorBrandForeground2: "#106ebe"            # brand[70]
  colorBrandForegroundLink: "#106ebe"         # brand[70]

  # ── Neutral Backgrounds ───────────────────────────────────────────────────
  colorNeutralBackground1: "#ffffff"     # white — cards, inputs (highest elevation)
  colorNeutralBackground1Hover: "#f5f5f5"
  colorNeutralBackground1Pressed: "#e0e0e0"
  colorNeutralBackground1Selected: "#ebebeb"
  colorNeutralBackground2: "#fafafa"     # grey[98] — page canvas
  colorNeutralBackground2Hover: "#f0f0f0"
  colorNeutralBackground3: "#f5f5f5"     # grey[96] — hover fills
  colorNeutralBackground4: "#f0f0f0"     # grey[94] — pressed fills
  colorNeutralBackground5: "#ebebeb"     # grey[92] — selected fills
  colorNeutralBackground6: "#e6e6e6"     # grey[90] — disabled fills
  colorNeutralBackgroundInverted: "#292929"   # grey[16]
  colorNeutralBackgroundDisabled: "#f0f0f0"   # grey[94]
  colorNeutralCardBackground: "#fafafa"       # grey[98]
  colorBrandBackground: "#0078d4"
  colorBrandBackgroundHover: "#106ebe"
  colorBrandBackgroundPressed: "#004578"
  colorBrandBackgroundSelected: "#005a9e"
  colorBrandBackground2: "#eff6fc"

  # ── Subtle / Transparent ─────────────────────────────────────────────────
  # colorSubtleBackground: transparent  (documented in prose)
  colorSubtleBackgroundHover: "#f5f5f5"
  colorSubtleBackgroundPressed: "#e0e0e0"
  colorSubtleBackgroundSelected: "#ebebeb"
  # colorTransparentBackground: transparent (documented in prose)

  # ── Strokes / Borders ─────────────────────────────────────────────────────
  colorNeutralStroke1: "#d1d1d1"          # grey[82] — default border
  colorNeutralStroke1Hover: "#c7c7c7"     # grey[78]
  colorNeutralStroke1Pressed: "#b3b3b3"   # grey[70]
  colorNeutralStroke2: "#e0e0e0"          # grey[88] — subtle divider
  colorNeutralStroke3: "#f0f0f0"          # grey[94]
  colorNeutralStrokeDisabled: "#e0e0e0"   # grey[88]
  colorNeutralStrokeOnBrand: "#ffffff"
  colorNeutralStrokeAccessible: "#616161" # grey[38]
  colorBrandStroke1: "#0078d4"
  colorBrandStroke2: "#c7e0f4"
  colorCompoundBrandStroke: "#0078d4"
  colorStrokeFocus1: "#ffffff"
  colorStrokeFocus2: "#000000"

  # ── Status — Warning (orange) ─────────────────────────────────────────────
  colorStatusWarningBackground1: "#fff9f5"
  colorStatusWarningBackground2: "#fdcfb4"
  colorStatusWarningBackground3: "#faa06b"
  colorStatusWarningForeground1: "#bc4b09"
  colorStatusWarningForeground2: "#de590b"
  colorStatusWarningForeground3: "#8a3707"
  colorStatusWarningBorderActive: "#f7630c"
  colorStatusWarningBorder1: "#fdcfb4"

  # ── Status — Danger (cranberry) ───────────────────────────────────────────
  colorStatusDangerBackground1: "#fdf3f4"
  colorStatusDangerBackground2: "#eeacb2"
  colorStatusDangerBackground3: "#dc626d"
  colorStatusDangerBackground1Hover: "#f6d1d5"
  colorStatusDangerForeground1: "#c50f1f"
  colorStatusDangerForeground2: "#b10e1c"
  colorStatusDangerForeground3: "#6e0811"
  colorStatusDangerBorderActive: "#c50f1f"
  colorStatusDangerBorder1: "#eeacb2"

  # ── Status — Success (green) ──────────────────────────────────────────────
  colorStatusSuccessBackground1: "#f1faf1"
  colorStatusSuccessBackground2: "#9fd89f"
  colorStatusSuccessBackground3: "#359b35"
  colorStatusSuccessForeground1: "#107c10"
  colorStatusSuccessForeground2: "#0e700e"
  colorStatusSuccessForeground3: "#094509"
  colorStatusSuccessBorderActive: "#218c21"
  colorStatusSuccessBorder1: "#9fd89f"

  # ── Status — Informative (royalBlue) ─────────────────────────────────────
  colorStatusInformativeBackground1: "#f0f6fa"
  colorStatusInformativeBackground2: "#9abfdc"
  colorStatusInformativeBackground3: "#286fa8"
  colorStatusInformativeForeground1: "#004e8c"
  colorStatusInformativeForeground2: "#00467e"
  colorStatusInformativeForeground3: "#002c4e"
  colorStatusInformativeBorderActive: "#125e9a"
  colorStatusInformativeBorder1: "#9abfdc"

typography:
  # Fluent 2 Web type ramp — size / line-height / weight / usage
  Caption2:
    size: "10px"
    lineHeight: "14px"
    weight: 400
    token: fontSizeBase100 / lineHeightBase100
  Caption1:
    size: "12px"
    lineHeight: "16px"
    weight: 400
    token: fontSizeBase200 / lineHeightBase200
  Body1:
    size: "14px"
    lineHeight: "20px"
    weight: 400
    token: fontSizeBase300 / lineHeightBase300
    note: default body text
  Subtitle2:
    size: "16px"
    lineHeight: "22px"
    weight: 600
    token: fontSizeBase400 / lineHeightBase400
  Subtitle1:
    size: "20px"
    lineHeight: "26px"
    weight: 600
    token: fontSizeBase500 / lineHeightBase500
  Title3:
    size: "24px"
    lineHeight: "32px"
    weight: 600
    token: fontSizeBase600 / lineHeightBase600
  Title2:
    size: "28px"
    lineHeight: "36px"
    weight: 600
    token: fontSizeHero700 / lineHeightHero700
  Title1:
    size: "32px"
    lineHeight: "40px"
    weight: 700
    token: fontSizeHero800 / lineHeightHero800
  LargeTitle:
    size: "40px"
    lineHeight: "52px"
    weight: 700
    token: fontSizeHero900 / lineHeightHero900
  Display:
    size: "68px"
    lineHeight: "92px"
    weight: 700
    token: fontSizeHero1000 / lineHeightHero1000

  font-families:
    base: '"Segoe UI", "Segoe UI Web (West European)", -apple-system, BlinkMacSystemFont, Roboto, "Helvetica Neue", sans-serif'
    monospace: 'Consolas, "Courier New", Courier, monospace'
    numeric: 'Bahnschrift, "DIN Alternate", "Franklin Gothic Medium", "Nimbus Sans Narrow", sans-serif-condensed, sans-serif'

spacing:
  None: "0px"
  XXS: "2px"     # spacingHorizontalXXS  / spacingVerticalXXS
  XS: "4px"      # spacingHorizontalXS   / spacingVerticalXS
  SNudge: "6px"  # spacingHorizontalSNudge
  S: "8px"       # spacingHorizontalS    / spacingVerticalS
  MNudge: "10px" # spacingHorizontalMNudge
  M: "12px"      # spacingHorizontalM    / spacingVerticalM
  L: "16px"      # spacingHorizontalL    / spacingVerticalL
  XL: "20px"     # spacingHorizontalXL   / spacingVerticalXL
  XXL: "24px"    # spacingHorizontalXXL  / spacingVerticalXXL
  XXXL: "32px"   # spacingHorizontalXXXL / spacingVerticalXXXL

rounded:
  None: "0px"         # borderRadiusNone
  Small: "2px"        # borderRadiusSmall
  Medium: "4px"       # borderRadiusMedium   (default for most controls)
  Large: "6px"        # borderRadiusLarge
  XLarge: "8px"       # borderRadiusXLarge
  Circular: "9999px"  # borderRadiusCircular

components:
  button-primary:
    background: "#0078d4"
    background-hover: "#106ebe"
    background-pressed: "#004578"
    color: "#ffffff"
    border: "none"
    border-radius: "4px"
    font-size: "14px"
    font-weight: 600
    padding: "5px 12px"

  button-outline:
    background: "transparent"
    background-hover: "#f5f5f5"
    background-pressed: "#e0e0e0"
    color: "#0078d4"
    border: "1px solid #d1d1d1"
    border-radius: "4px"
    font-size: "14px"
    font-weight: 600

  button-subtle:
    background: "transparent"
    background-hover: "#f5f5f5"
    background-pressed: "#e0e0e0"
    color: "#242424"
    border: "none"
    border-radius: "4px"
    font-size: "14px"
    font-weight: 600

  button-transparent:
    background: "transparent"
    background-hover: "transparent"
    color: "#0078d4"
    border: "none"
    border-radius: "4px"
    font-size: "14px"
    font-weight: 600

  button-warning-primary:
    background: "#f7630c"
    background-hover: "#bc4b09"
    color: "#ffffff"
    border: "none"
    border-radius: "4px"
    font-size: "14px"
    font-weight: 600

  button-warning-outline:
    background: "#fff9f5"
    background-hover: "#fdcfb4"
    color: "#bc4b09"
    border: "1px solid #fdcfb4"
    border-radius: "4px"
    font-size: "14px"
    font-weight: 600

  button-danger-outline:
    background: "#fdf3f4"
    background-hover: "#f6d1d5"
    color: "#c50f1f"
    border: "1px solid #eeacb2"
    border-radius: "4px"
    font-size: "14px"
    font-weight: 600

  button-success-outline:
    background: "#f1faf1"
    background-hover: "#9fd89f"
    color: "#107c10"
    border: "1px solid #9fd89f"
    border-radius: "4px"
    font-size: "14px"
    font-weight: 600

  card:
    background: "#ffffff"
    border: "1px solid #e0e0e0"
    border-radius: "4px"
    shadow: "0 0 2px rgba(0,0,0,0.12), 0 1px 2px rgba(0,0,0,0.14)"
    padding: "16px"

  card-filled:
    background: "#fafafa"
    border: "1px solid #e0e0e0"
    border-radius: "4px"
    shadow: "none"
    padding: "16px"

  badge-informative:
    background: "#f0f6fa"
    color: "#004e8c"
    border: "1px solid #9abfdc"
    border-radius: "9999px"
    font-size: "12px"
    font-weight: 600

  badge-warning:
    background: "#fff9f5"
    color: "#bc4b09"
    border: "1px solid #fdcfb4"
    border-radius: "9999px"
    font-size: "12px"
    font-weight: 600

  badge-danger:
    background: "#fdf3f4"
    color: "#c50f1f"
    border: "1px solid #eeacb2"
    border-radius: "9999px"
    font-size: "12px"
    font-weight: 600

  badge-success:
    background: "#f1faf1"
    color: "#107c10"
    border: "1px solid #9fd89f"
    border-radius: "9999px"
    font-size: "12px"
    font-weight: 600

  input:
    background: "#ffffff"
    background-disabled: "#f0f0f0"
    color: "#242424"
    color-placeholder: "#707070"
    border: "1px solid #d1d1d1"
    border-hover: "1px solid #c7c7c7"
    border-focus: "2px solid #0078d4"
    border-radius: "4px"
    font-size: "14px"
    padding: "5px 12px"

  tooltip:
    background: "#292929"
    color: "#ffffff"
    border-radius: "4px"
    font-size: "12px"
    padding: "4px 8px"
    shadow: "0 0 2px rgba(0,0,0,0.12), 0 4px 8px rgba(0,0,0,0.14)"

  # Compact selectable list-row (dense alternative to a template card grid).
  # Single fixed-height line: icon + name + one-line description + trailing meta.
  list-row:
    background: "transparent"
    background-hover: "#f5f5f5"
    color: "#242424"
    border: "1px solid #e0e0e0"
    border-radius: "4px"
    font-size: "14px"
    padding: "8px 12px"
    height: "54px"

  # Selected affordance shared by list-rows, the "No blueprint" control, and
  # selectable chips: brand-tint fill + 2px brand stroke. A same-color inset ring
  # (box-shadow, carried in prose) reinforces selection without changing box size.
  list-row-selected:
    background: "#eff6fc"
    color: "#242424"
    border: "2px solid #0078d4"
    border-radius: "4px"
    font-size: "14px"
    padding: "8px 12px"
---

# Fluent 2 Design System

**Implementation:** `@fluentui/react-components` v9  
**Theme:** Web Light (Communication Blue brand)  
**Source:** [microsoft/fluentui](https://github.com/microsoft/fluentui) — tokens are authoritative; do not invent values.

---

## Overview

This project uses **Fluent 2**, Microsoft's second-generation design system. All visual decisions — color, typography, spacing, motion — are expressed through design tokens exposed by `@fluentui/tokens`. `FluentProvider` resolves the correct CSS custom properties at runtime, so dark mode and high-contrast mode require zero additional code in components.

**Key principles:**
- **Semantic tokens over raw values.** Always reference alias tokens (e.g., `colorNeutralForeground1`) rather than global palette values (e.g., `grey[14]`).
- **Elevation communicates hierarchy.** Shadows and background layers indicate depth; use the correct layer for each surface.
- **Motion is purposeful.** Animations use the defined duration + easing curve tokens.
- **Accessibility first.** Color contrast, focus rings, and ARIA are built into Fluent components.

---

## Standalone Azure Fluent system contract


This section defines a reusable Azure Fluent-style product system that can be packaged for other React applications. Agentweaver is the first consumer and demo app, not the boundary of the system. The checked-in source of truth for the standalone kit is this `DESIGN.md` contract, the sync skill, and the library under `apps/web/src/azure-fluent-system/`. Supporting source reference artifacts such as extraction caches or icon manifests are generated inputs that may be refreshed, not machine-local dependencies.

Agentweaver's product web app follows the **Azure UI Kit (Fluent 2)** reference as an execution contract, not as a copied Figma export. The authenticated Figma captures available to the team identify the Azure UI Kit file in Web/Light + Fluent + Microsoft + Rounded modes, with `Copilot Flair` styles visible. Where exact Figma variables are unavailable, use Fluent 2 semantic tokens from `@fluentui/react-components`.

The standalone showcase is a pattern workbench, not a one-node screenshot deck: Resource Type may lead the first fold, but the navigation must still expose other Azure pattern families so coverage is never mistaken for fidelity.

**Authoritative Figma coverage:** use the full-frame table-of-contents exports as the source of truth, not the earlier click-through manifests:

- **Component Contents:** `artifacts/figma-exports/full-frame-exports/table-of-contents-components-full.jpg` (`1552×8386`) — authoritative for the component inventory/status below.
- **Pattern Contents:** `artifacts/figma-exports/full-frame-exports/table-of-contents-patterns-full.png` — authoritative for portal pattern inventory/status. Patterns are listed separately below and must not be conflated with component guidance.
- Earlier `component-sweep` and `component-sweep-missing` manifests are supplemental click-through source reference for visible Copilot layer language, not authoritative coverage lists.
- The full-frame export captures **visible Component Contents**. It does not prove every hidden internal Figma variant, state, or property is present/exported. Do not infer hidden variants or copy raw Figma values unless visible in artifacts; use Fluent semantic tokens.
- **Component Contents and Pattern Contents are distinct.** Component guidance below describes reusable UI building blocks. Pattern guidance describes broader Azure Portal page/task archetypes and should be applied only when implementing that kind of workflow.

**Authoritative MCP extraction cache:** structured extraction records live in `artifacts/figma-extraction/azure-ui-kit-fluent2/`. The cache contains `manifest.json`, `batch-high-impact-01.json`, `batch-representative-02.json`, `batch-representative-03.json`, `batch-representative-04.json`, `batch-representative-05.json`, `batch-representative-06.json`, `batch-representative-07.json`, `component-node-index.json`, `components.jsonl`, `representative-node-plan.json`, `pattern-node-index.json`, `patterns.jsonl`, `distilled-component-contexts.json`, `distilled-pattern-contexts.json`, and raw MCP responses under `raw/`. The current extraction budget ledger records the Azure UI Kit file `q2TdO4dVcMhNWYp0N6Bc05`, the Azure Pattern Templates file `TXALL9CS0727dvGcZo84Bg`, and the Figma request count. User-provided screenshots from the linked Pattern Templates file showed corrupted/overlapped Dev Mode rendering; use cached metadata and design-context text as source reference, not those visual screenshots. Treat this artifact tree as a portable, workflow-generated input surface: refresh it with the sync skill when needed, and do not require machine-local artifact paths in committed docs or consumer code.

Library-local catalog pack: `apps/web/src/azure-fluent-system/catalog/COMPONENTS.md`, `apps/web/src/azure-fluent-system/catalog/PATTERNS.md`, and `apps/web/src/azure-fluent-system/catalog/ICONS.md`. Use this pack before reopening Figma; page-level Pattern Templates nodes referenced there are indexes, not render targets, and IconCloud-backed glyph inventory is tracked locally without requiring Figma MCP.

Extraction guardrails:

- The bulk component graph found 148 published components, but only a high-impact representative subset has individual Dev Mode/MCP extraction cached. Do not claim per-component extraction coverage for all 148 components.
- For areas already cached and distilled below, future implementation should not require live Figma access. Use Figma again only for exact states or anatomy of unextracted components/patterns.
- Several image attachments were blank/tiny node renders; do not treat them as visual source reference.
- Collapse Figma's large variant matrices into practical Fluent recipes. Implementers should not expose every Figma variant as a product choice.

### Pattern guidance

- Treat the Azure Pattern Templates source reference as a pattern language, not a single screenshot target. The Resource Type node `4417:3962` is representative of one pattern family, not the whole showcase scope.
- Successful MCP inventory on page-level nodes is still source reference: it can identify the pattern family, page name, and dependent components even when design-context extraction cannot target the node directly. For `TXALL9CS0727dvGcZo84Bg` / `4417:3962`, the inventory source reference identifies pageName `↪ Browse Resource`, the `Location summary` component on the browse page, and related pattern families such as Browse Resource, Notifications, Delete A Resource, Manage A Resource, Service overview, Feedback/CES/CVA, and Table of contents.
- Use Pattern Templates source reference to extract reusable anatomy: portal shell hierarchy, blade/page hierarchy, command bars, filter/query surfaces, data grids, create/review flows, form density, empty/error/loading states, feedback surfaces, and recipe mapping.
- Coverage is not fidelity. A recipe is only complete when it maps to concrete Figma source reference and names the visible anatomy it covers.
- Each recipe entry should cite the Figma source reference that supports it and the production components/tokens that realize it.

#### Representative pattern families from MCP inventory

- **Browse Resource (`↪ Browse Resource`)** — includes `Location summary`. Treat Resource Type as one representative Browse Resource surface rather than as the entire system.
- **Notifications** — includes `.Notification pane body f2` and `.Context pane - grid + empty state`. These are source reference for pane-based feedback/state handling, not afterthoughts.
- **Delete A Resource** — includes `Delete footer`, `Implication delete content`, `Dependent delete content`, `Associated delete content`, `Bulk delete content`, and `Delete Dialog`. Delete is a structured review/implication pattern family.
- **Manage A Resource** — includes `.public network access`, `.restrict network access`, `.subnet address range`, `.edit subnet`, `.private endpoint`, `Accordion Header Content`, `Accordion Content`, and `.Status cell`. Management pages are compact editable surfaces with accordions and status cells.
- **Service overview** — includes `Overview Card`, `.Illustration Card`, `link List`, and `Footer_Overview card`. Overview pages summarize through constrained card + link-list anatomy, not dashboard sprawl.
- **Feedback / CES / CVA** — includes `.Next steps content`, `.Feedback footer`, `.Top radio`, and `.Feedback content`. Feedback is its own completion/follow-up pattern family.
- **Table of contents** — depends on `Navigation / Top nav`, `headline`, `buffer`, `component row`, `divider`, `link`, and `badge`. This is navigation scaffolding for the broader pattern language.

#### Page-level MCP inventory rule

- Page-level links are useful for inventory, pattern-family discovery, and dependency mapping.
- `get_design_context` and `get_variable_defs` may fail on page-level nodes with errors such as **"You currently have nothing selected."** That is a signal to drill into a concrete frame, layer, or component node, not to discard the inventory source reference. The page-level `list-components` result is still useful because it classifies the family and points to concrete child anatomy like `Location summary`, notification panes, delete footers, manage controls, overview cards, and feedback blocks.
- For `TXALL9CS0727dvGcZo84Bg` / `4417:3962`, treat the page-level response as valid inventory source reference for Browse Resource and adjacent families, while using child frames/components for renderable design-context extraction.
- Screenshot paste is not the primary workflow. Prefer: page-level MCP inventory → concrete child node extraction → pattern guidance/recipe mapping → implementation → visual validation.

#### Expected prompt workflow

- The canonical implementation prompt is:
  - `Implement this design from Figma. @https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates--Fluent-2-?node-id=3203-24770&m=dev`
- Treat `fileKey = TXALL9CS0727dvGcZo84Bg` and `nodeId = 3203:24770` as the concrete traceability target for this workflow.
- Expected sequence:
  1. Start from a **concrete Figma dev-mode node URL**.
  2. Run MCP `get_design_context` and `get_variable_defs` for that renderable node.
  3. Extract reusable pattern anatomy and map it to the checked-in catalog and library components.
  4. Implement in the standalone library showcase while keeping the workbench multi-pattern.
  5. Perform visual acceptance against the MCP-backed source reference.
- If the provided URL resolves to a page-level anchor instead of a renderable node, drill down to a concrete child frame/layer/component before treating design-context extraction as failed.
- Smith validation should confirm the implementation is traceable to node `3203:24770`, not to a pasted screenshot.

#### Portal shell anatomy

- Use the Azure portal shell as a stable page frame: brand top bar, global navigation affordances, search, utility controls, left portal rail, breadcrumb, and blade/page canvas.
- Shell chrome should be visually subordinate to the working surface: a low-noise white canvas inside the blue Azure frame, with compact hit targets and minimal ornament.
- Do not replace the shell with local demo chrome, marketing headers, oversized cards, or intro banners that crowd the first fold.

#### Blade and page hierarchy

- Treat the shell as level 0, the blade/page header as level 1, the command/query surface as level 2, and the working content as level 3.
- Blade headers must communicate identity first: glyph, title, tenant/subtitle, and a small action cluster. They should not collapse into a generic page hero.
- Resource Type is an example of this hierarchy, not the definition of every page. Other pattern families may swap the content region while preserving the hierarchy.

#### Command bars and query/filter surfaces

- Command bars are action-first rows that sit directly under the blade header. Primary commands belong left; view/grouping or secondary controls belong right.
- Query/filter surfaces are separate from command bars. Use search plus filter pills/chips/dropdowns as a horizontal refinement strip rather than stacking them inside cards.
- Do not merge command actions, filters, and explanatory copy into one noisy toolbar.

#### Data grids and list surfaces

- Azure data surfaces are dense, white, and scannable. Prefer link-style resource names, compact row heights, explicit column headers, and a trailing row-actions column.
- Grid composition should remain data-first: checkbox/selection, identity column, hierarchy metadata, and row actions. Decorative summary cards belong outside the grid.
- A recipe that claims data-grid coverage must name the anatomy it implements: selection, headers, link rows, metadata, actions, footer/pager, and state handling.

#### Create/review flows and form density

- Create/review patterns derive from Forms, Step Wizard, Manage Resource, and Create Resource source reference together. Keep them footer-driven, compact, and sequential.
- Forms should use dense Azure/Fluent row spacing, clear field labels, local validation, and explicit review checkpoints rather than card stacks.
- Do not treat Create Resource as solved just because one screenshot exists. Its recipe must document when it is direct source reference versus derived source reference.

#### Empty/error/loading states and feedback surfaces

- Empty, loading, error, notification, CES/CVA, and Copilot/agentic states are first-class pattern families. They must be shown as reusable surfaces, not hidden edge cases.
- Feedback surfaces should match the shell/blade hierarchy: local inline messaging for recoverable issues, page/blade errors only when the surface cannot continue.
- Do not add a persistent MessageBar or alert banner to every showcase view just to prove coverage.

#### Recipe mapping and fidelity gate

- Every public recipe must record: pattern family, concrete Figma source reference, visible anatomy, mapped library components, and known derived assumptions.
- If a surface is derived from multiple Pattern Templates nodes, say so explicitly in the recipe and in the design guidance.
- Coverage is not fidelity: if the visual anatomy is wrong, the recipe is incomplete even when the API surface is broad.

### Pattern rules and anti-rules

- **Do** build the library showcase as a pattern workbench/gallery that spans multiple Azure pattern families.
- **Do** let the first view be a representative pattern such as Resource Type only when the navigation clearly exposes other Azure patterns/recipes.
- **Do** keep portal shell, command bar, query/filter, data-grid, create/review, form, empty/error/loading, and feedback surfaces as distinct gallery surfaces.
- **Do not** present a single node as the full product scope.
- **Do not** convert visual-fidelity work into a generic recipe catalog.
- **Do not** claim coverage without Figma source reference and a documented visual anatomy.
- **Do not** use pasted screenshots as the primary source when cached MCP metadata, inventory output, or design-context text exists.
- **Do not** treat page-level links as direct extraction targets; they are inventory anchors, not render targets. Use concrete frames, layers, or components for design-context and variable extraction.
- **Do not** require machine-local/session-state paths in committed docs or consumer guidance.

### Showcase expectations

- Before merging showcase or design guidance updates, verify `DESIGN.md` contains this pattern guidance and that the showcase README describes multiple pattern families, not only Resource Type.
- The showcase must demonstrate at least one shell/pattern family, one data/query family, and one create/review or feedback family; otherwise it is too narrow.
- If the showcase update narrows back to one pattern, reject it and expand the navigation/workbench surfaces first.
- Verify this rule with focused Azure Fluent tests, build, and a real browser pass.

#### First viewport render integrity

- Build success is irrelevant until the first viewport is visually correct.
- The current showcase is a hard FAIL if it shows large dark navy/black panels covering content, repeated horizontal panes, clipped table/form content, or a footer floating over malformed layout.
- The first viewport must be a single coherent Figma-derived surface with no accidental overlays or duplicated panels.
- No decorative dark slabs unless the MCP context explicitly shows them.
- Footer/action strips must not overlap content.
- Screenshot or manual browser inspection is required before PASS.
- If Trinity reports success without visual inspection, mark FAIL.

### Canonical Figma workflow

1. Start from a concrete Figma dev-mode node URL, for example `https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates--Fluent-2-?node-id=3203-24770&m=dev`.
2. Use MCP `get_design_context` and `get_variable_defs` on a renderable node. If the supplied URL lands on a page-level node, drill down to a concrete frame/layer/component first.
3. Treat successful MCP inventory output as supporting source reference for pattern family coverage, page names, and dependencies.
4. Extract reusable anatomy into named pattern families and recipes.
5. Implement the pattern in the library showcase and supporting components.
6. Validate visually against the Figma source reference.

- Page-level links may help locate the right pattern family, but they are not always render targets.
- Traceability must point to the concrete node used for design-context extraction, not to a pasted screenshot.
- The canonical example node for this workflow is `3203:24770`; if that node is not renderable, drill to a child frame that is.

### Captured Azure UI Kit component inventory

#### Azure Copilot components

| Component Contents label | Design system | Code status | Progress | Agentweaver interpretation |
|---|---|---|---|---|
| Copilot for Azure | Azure Copilot | Code available | Complete | Hub/sidecar assistant surface with clear header, conversation body, contextual grounding, and command actions. |
| Chat input | Azure Copilot | Code available | Complete | Structured composer: placeholder/help text, attachment/tool affordances, send action, validation/message-bar area, latency/progress, prompt suggestions, and character count when applicable. |
| Chat output | Azure Copilot | Code available | Complete | Transcript/message output with readable markdown/code, metadata, status, and per-message actions. |
| Navigation & header | Azure Copilot | Code available | Complete | Copilot shell header and nav/drawer language: title, close/back, current context, and optional navigation. |
| Grounding menu | Azure Copilot | Code available | Complete | Selectable source/context menu with checked state and clear active-grounding feedback. |
| Chain of thought | Azure Copilot | Code available | Complete | Show agentic progress as explainable steps/status, not raw hidden reasoning; use timeline/step rows, expandable details, badges, and elapsed metadata. |
| Entry points | Azure Copilot | Code available | Complete | Page-level, component-level, inline, menu, command-bar, and tooltip entry affordances sized to their host surface. |
| Inline Copilot | Azure Copilot | Code available | Complete | Compact contextual assistance adjacent to target content, dismissible and expandable only when needed. |
| Top actions | Azure Copilot | Code available | Complete | Suggested-next-action card grouping likely actions with concise labels, icons, and one primary recommendation. |

#### Component Contents inventory

Progress is recorded only where the full-frame export shows a visible progress badge; blank progress cells mean no progress badge was visible in that artifact.

| Component Contents label | AKA / visible note | Design system | Code status | Progress |
|---|---|---|---|---|
| Accordion |  | Azure | Code available |  |
| Arm error list |  | Azure | Design only | Planning |
| Avatars |  | Fluent 2 | Code available |  |
| Badge |  | Fluent 2 | Code available |  |
| Breadcrumb |  | Fluent 2 | Code available |  |
| Button |  | Fluent 2 | Code available |  |
| Card | AKA Tile | Fluent 2 | Code available |  |
| Carousel |  | Fluent 2 | Code available |  |
| Checkbox |  | Fluent 2 | Code available |  |
| Code snippet |  | Azure | Design only | Complete |
| Copy button |  | Azure | Code available | Complete |
| Datagrid | AKA Details list | Azure | Code available | Complete |
| Date Picker |  | Fluent 2 | Code available |  |
| Dialog |  | Fluent 2 | Code available |  |
| Divider |  | Fluent 2 | Code available |  |
| Drawer | AKA Content pane, panel | Fluent 2 | Code available |  |
| Dropdown |  | Fluent 2 | Code available |  |
| Dropdown - Location |  | Azure | Code available | Complete |
| Dropdown - Resource group |  | Azure | Code available | Complete |
| Dropdown - Subscription |  | Azure | Code available | Complete |
| Empty state |  | Azure | Design only | Complete |
| Essentials |  | Azure | Code available | Complete |
| Field |  | Fluent 2 | Code available |  |
| Feedback link |  | Azure | Code available | Complete |
| File upload |  | Azure | Code available | Complete |
| Filterable combo box |  | Azure | Code available | Complete |
| Filter subscription |  | Azure | Code available | Complete |
| Filter pill |  | Azure | Code available | Complete |
| Filter pill - Group |  | Azure | Code available | Complete |
| Filter pill - Subscription |  | Azure | Code available | Complete |
| Form | AKA Section control | Azure | Code available | Complete |
| Info label |  | Fluent 2 | Code available |  |
| Input |  | Fluent 2 | Code available |  |
| Label |  | Fluent 2 | Code available |  |
| Link |  | Fluent 2 | Code available |  |
| Link - Resource |  | Azure | Code available | Complete |
| Link - Sanitized |  | Azure | Code available | Complete |
| Link - View |  | Azure | Code available | Complete |
| List | AKA List view | Fluent 2 | Code available |  |
| Material cards |  | Fluent 2 | Code available |  |
| Menu | AKA Context menu | Fluent 2 | Code available |  |
| Message bar | AKA Status bar, Info box | Fluent 2 | Code available |  |
| Nav |  | Fluent 2 | Code available |  |
| Nav for Azure Portal | Located in Azure Portal Shell | Azure | Design only | Complete |
| Pager |  | Azure | Code available | Complete |
| Persona |  | Fluent 2 | Code available |  |
| Popover |  | Fluent 2 | Code available | Complete |
| Progress bar |  | Fluent 2 | Code available | Complete |
| Radio group | AKA Segmented Box | Fluent 2 | Code available |  |
| Rating |  | Fluent 2 | Code available |  |
| Scrollbar |  | Azure | Design only | Complete |
| Search box |  | Fluent 2 | Code available |  |
| Secure input | AKA Password box | Azure | Code available | Complete |
| Shell for Azure Portal | Includes side/header/footer; do not improvise nav outside this language | Azure | Design only | Complete |
| Site header | Now consolidated in Shell for Azure Portal | Azure | Design only | Complete |
| Skeleton |  | Fluent 2 | Code available |  |
| Slider |  | Azure | Code available | Complete |
| Spin button |  | Fluent 2 | Code available |  |
| Spinner |  | Fluent 2 | Code available |  |
| Status indicator | AKA Inline message | Fluent 2 | Code available |  |
| Switch picker |  | Fluent 2 | Code available |  |
| Switch | AKA Toggle | Fluent 2 | Code available |  |
| Tablist for Azure | AKA Tab, Pivot | Azure | Code available | Complete |
| Tag & Interaction tag |  | Fluent 2 | Code available |  |
| Tag picker |  | Fluent 2 | Code available |  |
| Tags by resource |  | Azure | Code available | Complete |
| Teaching popover | AKA Teaching bubble | Fluent 2 | Code available |  |
| Text area | AKA Text box | Fluent 2 | Code available |  |
| Toast | AKA Notification toast | Fluent 2 | Code available |  |
| Toolbar | AKA Command bar | Azure | Code available | Complete |
| Tooltip | AKA Info balloon | Fluent 2 | Code available |  |
| Tree |  | Fluent 2 | Code available |  |

### Agentweaver consumption priorities

- **Use Fluent 2 components directly when the table says Fluent 2 + code available** (Button, Dialog, Drawer, Input, Field, Skeleton, Spinner, Tooltip, etc.). Style through tokens and app recipes; do not rebuild primitives.
- **Use Azure components as product recipes when marked Azure.** If code is available, prefer the closest Fluent implementation plus Azure-specific composition (for example command bars, portal dropdowns, filter pills, resource links). If design-only, implement only the visible language needed for Agentweaver and avoid claiming parity with the upstream component.
- **Do not improvise Azure Portal navigation.** Shell, site header, portal nav, context panes/drawers, and footer language belong to `Shell for Azure Portal`; Agentweaver's shell should remain a restrained interpretation of that component family.
- **Data-heavy surfaces** should follow Datagrid/List/Pager/Filter language: dense rows, clear headers, active filter pills, compact status cells, and paging/scrolling only when functionally needed.
- **Messaging and feedback** should follow Message bar, Status indicator, Toast, Teaching popover, Tooltip, and Feedback link language: local feedback first, toast for transient success, teaching popover only for first-run education.
- **Forms and input** should follow Field/Input/Text area/Secure input/Dropdown/Date Picker/Radio group/Switch/Slider/Spin button language: labels, helper/error text, keyboard support, visible values for numeric sliders/spinners, and fixed footer actions in long dialogs.

### Extracted component and shell source reference

These rules come from the cached MCP design-context outputs and supersede broad visual guesses from the earlier implementation pass. Coverage now includes a comprehensive node-level offline ledger through `batch-comprehensive-12-icons-and-gap-reopen.json`: all 148 published components have concrete raw source reference records, while design-context anatomy remains targeted to implementation-relevant nodes. Use `apps/web/src/azure-fluent-system/catalog/COMPONENTS.md`, `apps/web/src/azure-fluent-system/catalog/PATTERNS.md`, and `apps/web/src/azure-fluent-system/catalog/ICONS.md` for the consolidated package/module summary and durable inventory state, and `apps/web/src/azure-fluent-system/examples/` for checked-in TSX samples. Raw MCP generated code is reference source reference, not production code.

| Extracted target | Figma node / raw cache | Implementation guidance for Agentweaver |
|---|---|---|
| Azure Copilot grounding menu header | `32382:38901`, `raw\design-context-32382-38901-grounding-menu-header.txt` | Grounding selection uses a white, bordered header with `12px` top radii, `16px` horizontal padding, `12px` vertical padding, compact 32px pill tabs, a selected pill using `colorBrandBackground2` + `colorCompoundBrandStroke`, neutral unselected pill labels, an overflow button (`1 more`, `2 more`, etc.), and a rounded search box on the right. Agentweaver should model Copilot context/source selection as a commandable segmented header, not as decorative chips or a generic dropdown. |
| Azure iconography sources and slots | batch 12 search summaries, `28817:36284`, `35292:9094`, `31147:480` | No dedicated Azure UI Kit iconography page/component was found in the published graph or top-level metadata. Use the installed `@fluentui/react-icons` package / `microsoft/fluentui-system-icons` family for standard action/status/navigation glyphs; use IconCloud (`https://iconcloud.design/`) as the approved authenticated source for Azure resource or product identity exports. Treat the Figma Community Microsoft Fluent System Iconography file as visual/reference guidance only unless assets are explicitly exported under acceptable terms. Observed icon slots: 12px status captions, 16px header/menu controls, 18px grid resource links, 20px tab/button/menu/input controls, 24px generic AI shell button, and 32px service/grounding tiles or entity icons. Keep glyph color token-driven/currentColor except approved product/resource assets. |
| Command, header, and AI icons | `raw\design-context-28817-36284-grid-cell-icons-summary.txt`, `raw\design-context-35292-9094-header-icons-summary.txt`, `raw\design-context-31147-480-ai-button-summary.txt` | Dense grid row actions collapse behind a 32px overflow icon cell and Fluent `Menu` with 20px item icons; do not expose every row action inline. Blade header actions are optional 32px Pin/Star/More controls with 16px regular icons. Copilot uses the primary/rainbow product icon because color variants are deprecated; generic AI may use Sparkle only for explicit AI affordances. |
| Offline component handoff ledger | `comprehensive-component-cache-ledger.json`, `raw\component-node-source reference-*.json`, `raw\toc-no-published-node-*.json` | For handoff work, first consult the ledger. `directly-cached` entries have node-specific graph source reference; `directly-cached-wrapper-plus-children` entries pair sparse wrappers with child/subnode anatomy; `unavailable-or-inaccessible` entries document TOC labels with no published node. Do not treat earlier `low-priority`, `documentation-sufficient`, or `covered-by-parent` categories as missing unless the ledger lacks a node-level record. |
| Catalog overview | `apps/web/src/azure-fluent-system/catalog/COMPONENTS.md`, `apps/web/src/azure-fluent-system/catalog/PATTERNS.md`, and `apps/web/src/azure-fluent-system/catalog/ICONS.md` | Consolidated public-surface summary for package boundaries, portable API expectations, component/pattern/icon coverage snapshots, derived-surface notes, and guidance on how the checked-in catalog replaces the old split source reference/planning structure. |
| Component coverage ledger | `apps/web/src/azure-fluent-system/catalog/COMPONENTS.md` | Explicit component inventory, grouped implementation mappings, exact audit counts, and durable per-row extraction/mapping/showcase accounting. |
| Pattern coverage ledger | `apps/web/src/azure-fluent-system/catalog/PATTERNS.md` | Durable pattern-family design guidance, anti-rules, representative nodes, implementation files, and family-level extraction/mapping/showcase accounting. |
| Icon coverage ledger | `apps/web/src/azure-fluent-system/catalog/ICONS.md` | Durable icon inventory, IconCloud/source references, alias strategy, local asset mappings, and showcase visibility accounting. |
| Checked-in usage samples | `apps/web/src/azure-fluent-system/examples/` | TSX examples for provider/layout, BladeHeader, ResourceTagEditor, AzureDataGrid + filtering, Copilot composer/response, CreateResourcePattern, and icon registry usage. These are library-local reference files for other agents and future consumers. |
| Standalone implementation module | `apps/web/src/azure-fluent-system` | The first implementation lives as an isolated web source module because the repo has no workspace package convention yet. It exports provider, tokens/CSS, hardened ready-to-use components, patterns, icon registry adapters, focused tests, and a public index; it is structured to move later into `packages/azure-fluent-system` or `@org/azure-fluent-system` without migrating Agentweaver pages first. Priority surfaces now carry explicit loading/error/disabled/selection/interaction states rather than scaffold-only wrappers. |

### Public React API meaning

In this system, API means the reusable React library contract: exported component and pattern names, prop types, callbacks/events, slots/render props, controlled and uncontrolled state model, accessibility behavior, variants/states, example usage, and implementation skeletons. It does not mean a backend service API. The checked-in catalog files in `apps/web/src/azure-fluent-system/catalog/` plus the TSX samples in `apps/web/src/azure-fluent-system/examples/` are expected to give another project enough of this React API contract to implement or consume a component without reopening Figma.

| Azure Copilot grounding menu details | Grounding Menu `32382:38890`, GM Search `32382:38987`, GM Entity List Item `32382:38992`, GM ListItems `32382:38860` | Grounding menus are responsive source/context pickers with header tabs, search, and entity rows. Search rests as an icon button and focuses into a compact rounded SearchBox with a brand underline. Entity rows use a 32px resource/file icon, body heading, caption subheading, optional sync/right icons, and standard Rest/Hover/Pressed/Selected/Disabled/Focus states. Lists are 428px wide with 16px horizontal padding, 4px row gaps, 48px rows, and optional slim scrollbar. |
| Azure Copilot navigation item | `32382:39444`, cache in component extraction summary | Copilot navigation items are 268px rows with 3px active rail, 12px rounded containers, 16px horizontal padding, 8px vertical padding, 20px icon/status slots, strong text for selected/unread rows, and explicit hover/selected/focus/loading states. Agentweaver's left rail should use this density/state model; avoid custom boxed nav cards. |
| Azure Copilot workspace header and drawer | Nav Drawer `32382:39055`, Header `32382:40016` | Copilot workspaces use a 60px neutral header with chat title/subtitle, New chat, panel toggle, dismiss, and overflow actions. The 288px drawer is a chat-thread drawer with unread/review/in-progress/completed/no-chat variants; do not reuse it as app-wide primary navigation. |
| Azure Copilot entry/icon | `31000:461`, `raw\design-context-32382-40353-azure-copilot-large.txt` | Use the primary/rainbow Copilot icon. Rest, hover, pressed, and selected states use subtle button backgrounds with tooltip support; color variants are deprecated and should not become product options. |
| Inline Copilot prompt input | `29192:8358`, open start `29192:8232`, guided start `29192:8293`, `raw\design-context-29192-8358-inline-copilot-prompt-input-large.txt` | Supports generated, empty, error, open-start, and guided-start states; optional dismiss, stop/loading, focus, scroll, menu, section header, and menu item internals; and error circle feedback. Use only for concrete inline AI actions adjacent to target content, not as generic decorative AI chrome. |
| Copilot prompt ribbon | Prompt Ribbon `30909:48907` | Page-level prompt ribbons use a selected Copilot icon button plus suggested prompt pills with white fill, brand stroke, 8px radius, compact caption text, and 32px row height. Use only when clicking a prompt opens the sidecar and sends that exact prompt to Copilot. |
| Copilot entry and top action affordances | Button Entry `31316:1188`, Menu Entry `31330:9223`, Top Action `30046:9398`, Quick Actions `30289:2845` | Use compact Copilot entry buttons for one explicit action, and Copilot menu entry only when multiple related actions exist. Top action cards can surface a single recommendation with resource icon, title/metadata, concise body, and a Copilot action button; use sparingly and do not replace standard toolbar/list actions. Quick Actions is only a compact overflow/status affordance. |
| Azure Copilot chat composer and user turn | Chat Input `32382:38499`/`32382:38546`, User Message `32382:38151`, Input Footer LG/Sm `32382:38729`/`33526:118139`, Agent Toggle `32382:38689`, Send/Stop `32382:38835` | Composer action is a circular Send/Stop button: send uses brand background, stop uses `colorBrandBackground2`, disabled uses neutral fill, and focus uses the Fluent focus ring. The full composer is a bordered 12px-radius surface with textarea, Add, Agent toggle, optional attachments, and LG/Sm footers. User turns are right-aligned `colorBrandBackground2` bubbles with 16px horizontal and 8px vertical padding. Use inline message bars for composer-local validation or blocked send states; use Agent Toggle only as an explicit mode toggle, not as an AI badge. |
| Chat output code | `32382:38197`, `raw\design-context-38116-47202-code-snippet-large.txt` | Render Kusto/YAML/CLI or JSON/CLI snippets with Consolas/monospace text, syntax-colored spans, and max-height clipping. Use a semantic code block component with copy actions rather than arbitrary message cards. |
| Copilot response interactions | Response Element `32382:38154`, Single/Multiple Selection `32382:38161`/`32382:38159`, Confirmation Buttons `32382:38169`, Footeractions `32382:38177`, Request Count `32382:38434`, Latency `32382:38442` | Interactive assistant responses should use Fluent RadioGroup or Checkbox groups plus a submit button for disambiguation, compact primary/secondary buttons for confirmation, and like/dislike footer actions only under assistant output. Request-count is a rule-separated quota caption with info tooltip. Latency uses Copilot header, strong status text, 4px progress bar, and optional Cancel action. |
| Chain of thought and agentic list | `27880:12932`, reasoning `27865:7924`, artifact pill `27865:11293`, needs-input `27880:13471`, action swap `27887:13690`, show artifacts `27895:9234`, Agentic List `27950:11635`, `raw\design-context-27880-12932-chain-of-thought-large.txt` | Show explainable agent progress with collapsed/expanded states, action rows, attachment/artifact presence, and artifact overflow handling. Needs-user-input rows auto-expand to approval requests, include risk text, and offer Approve/Deny; denial stops reasoning. Artifact pills are 34px rows with icon, title, type metadata, and maximize action. Agentic List composes these into a structured progress timeline, not a generic checklist. Do not expose hidden reasoning or add decorative AI chrome. |
| Azure Portal shell/footer language | `35285:10476`, `raw\design-context-35285-10476-shell-footer-large.txt` | Shell examples use Portal hierarchy: site header, breadcrumb bar, blade header, service menu, content region, and footer/action bar. Agentweaver pages should flatten their custom card chrome into this sequence: global header, breadcrumb/project context, flat blade/page header, optional service menu/secondary nav, content with toolbar/filter/list, then fixed footer actions for long tasks. |
| Azure Portal service navigation | Service Menu `32610:9825`, menu item `32610:9731`, menu search `32610:9943`, menu group `32610:9931`, L1 Portal Menu `35399:10130`, L1 element `35360:9984`, service tile `41544:8562` | Use Service Menu as the local/secondary navigation recipe: 264px open rail, compact local SearchBox, grouped resource rows, resource icons, optional favorite star, sub-item indent, explicit hover/pressed/focus, and selected 2px brand rail. L1 Portal Menu is only for global portal sections and uses section/favorite/expand-collapse elements; do not replace local project/resource nav with it. Service tiles are for global search/category menus only. |
| Blade header, site header, and global search | Blade header `32630:8970`, Site Header `31147:440`, global search `40971:40679`, `raw\design-context-32630-8970-blade-header-large.txt`, `raw\design-context-31147-440-site-header-wide-large.txt`, `raw\design-context-40971-40679-azure-global-search-large.txt` | Blade headers support responsive title/subtitle, optional resource icon/menu context, restrained pin/favorite/context actions, dismiss, and optional Copilot prompt ribbon. The wide Site Header is a 40px shell bar containing home/menu, global search, AI entry, persona, and actions. Global search belongs in shell/header context; do not duplicate it as page-level search. |
| Data grid and filtering composition | representative nodes `28752:53376`, search filter pills `40971:32871`, filter popover `27774:7950`, `27119:16070`, pattern node `3273:15356`, `raw\design-context-pattern-3273-15356-filtering-content-footer.txt` | Lists/grids use a horizontal command/data surface: Toolbar (Azure), then SearchBox plus filter pill group, then `F2-Data Grid`, then footer/pager. Global search filter pills use a brand selected category pill, neutral category pills, and optional overflow menu; page/resource filters use the filter pill dropdown. Do not stack filters into cards. Data cells carry explicit text/icon/resource/persona slots. |
| Azure F2 data grid default/editable | wrapper `28093:32728`, child frames `28093:32729` and `28784:55430`, resource/status/persona cells `28093:48359`/`28093:48297`/`28093:48328`, `raw\design-context-28093-32729-data-grid-default-large.txt`, `raw\design-context-28784-55430-data-grid-editable-large.txt` | The top grid node is only sparse wrapper source reference; use child frames for anatomy. Default grids use selectable rows, sortable headers, hierarchy/resource/persona/tag/status cells, and icon/action columns. Resource links use an 18px resource icon plus brand link text; status links use a compact 12px status icon plus brand link text; persona links use a 24px avatar plus brand link text. Editable grids keep table context and allow TextBox, Dropdown, and Date Picker cells only where validation can remain local. |
| Grid cell tags | `28752:53376` | Grid tag cells align to dense rows with 24px tags, 4px gaps, `colorBrandBackground2` + `colorBrandForeground2`, and ellipsis overflow. |
| Resource tag editor | `29807:5989`, `36787:12057`, row `29804:6858`, `raw\design-context-29807-5989-tags-by-resource-large.txt`, `raw\design-context-36787-12057-tags-by-resource-medium.txt` | Use table/grid row editor anatomy, not cards: Name, Value, Resource, and Delete columns; Combobox/Input fields; selected resource combobox; colon separator; divider; delete icon button. Large rows use 40px fields with Body 2 text; Medium shares the same anatomy at tighter density. |
| Forms, labels, and validation | form node `27181:1280`, representative Form label node `27878:1838`, pattern nodes `3203:15419` and `3203:24770`, `raw\design-context-27181-1280-form-large.txt` | Forms sit inside blade/menu-page content with optional header, label, description, message bar, repeated input rows, and footer actions. Use Fluent `Field`, `Label`, `InfoLabel`, `Input`, `Textarea`, and `Dropdown/Combobox`; required marks use danger foreground; `InfoLabel` is only for nonessential explanatory info. Create Project, workflow generation, casting, and settings should use local validation and footer actions instead of ad hoc stacked cards. |
| Filterable combo box | `25248:8173` | Implement with Fluent `Combobox` plus a filter affordance only where filtering is needed. Support practical Small/Medium/Large sizing and rest/pressed/focus/selected/multi-select/showing-filter states; do not expose every matrix combination as a product choice. |
| Filter pill dropdown | `25378:3066`, `raw\design-context-25378-3066-filter-pill-dropdown-large.txt` | Resource-scope filter pills use a Tag trigger with Default/Selected/removable states, a popover header with Label/InfoLabel/Input, multi-select list items, divider, and Apply/Cancel actions. Use for subscription/resource filters, not arbitrary settings. |
| Pager and form footer | Pager narrow open `27162:1910`, Form footer `35285:10489`, `raw\design-context-27162-1910-pager-narrow-open-first-large.txt`, `raw\design-context-35285-10489-footer-form-summary.txt` | Use responsive pager anatomy with item count, rows-per-page dropdown, and pagination counter; avoid custom chip pagination. Long forms use a top-border footer with primary Save, secondary Cancel, and a feedback link on the right. |
| Progress/status badges | `27218:35412` | Use compact 32px status badges for Complete, Work in progress, and Planning. Use Fluent `Badge` with success/warning/danger colors, 12px strong caption, and pill radius. |
| Progress bar with labels | static `28174:7417`, animated `28209:4563` | Use Fluent `ProgressBar` with adjacent label/caption text for static Azure Fluent 2 progress. Animated progress uses a 2px rail with optional Label/InfoLabel for long-running nonblocking tasks; use Skeleton instead when loading known-structure content. |
| Tab lists | Horizontal TabList `29553:14762`, Horizontal Tab validation `29167:8291`, Vertical tabs `29195:8155`, `raw\design-context-29195-8155-vertical-swap-summary.txt` | Use Fluent `TabList` only for closely related categories. Horizontal tabs are 44px high in medium density with optional 20px icons, semibold selected label, 3px rounded brand bottom selector, and More overflow when needed. Validation icons belong only on tabs with actionable content errors/success. Vertical tabs retain the earlier 32px row and 3px left indicator guidance. |
| Scrollbar | `27777:16820` | Treat default/transparent rail, thumb fill, and thumb position as native scrollbar styling guidance. Do not build a custom scrollbar unless a product requirement demands it. |
| Copy button | `25260:8600` | Use a small wrapper around Fluent v9 `Button` that copies a provided clipboard value. Place near IDs, code snippets, commands, and resource identifiers. |
| File upload | `25412:31783` | Supports input or drag/drop, single or multiple files. Use for import and workspace flows. |
| Essentials | `25412:8797` | Use as a resource metadata block with links, descriptions, IDs, and concise metadata on service overview/resource pages. |
| Popover content variants | Light `27965:13711`, Brand `28024:14416`, Dark `28035:15353`, `raw\design-context-28035-15353-popover-content-dark-large.txt` | Optional title/body/image/buttons support contextual help or callouts. Light popovers can include primary/secondary actions for structured help; Brand popovers invert action colors and should be reserved for high-emphasis product callouts. Regular Fluent `Popover` or `Tooltip` remains the default for common product UI. |
| Toolbar, feedback, and slider targeted source reference | Toolbar `29553:7574`/`29553:7575`, Feedback `35182:762`/`35182:765`, Slider `28472:10335`-`28472:10337` | Toolbar has Top of Page Yes/No variants at 884px wide and ~40-41px high with neutral background/stroke; use it as command bar/toolbar language, not card chrome. Feedback has footer and in-page placements with a 20px Person Feedback icon, link text, and an in-page 14px/20px semibold heading; Link text must not wrap. Slider numbers can be Leading, Trailing, or Both; use sliders only for imprecise ranges, and use numeric input for precise values. |
| Message bars, notifications, and empty states | `28644:76791`, multi-line upsell `28644:76783`, Search No Results `40971:35678`, Mobile Search No Results `41153:24673`, hidden Empty state `29232:42433`, Pattern Templates `1024:309`, `5707:60107` | Use in-context message bars and inline status first. Toasts are for transient confirmation; full-page/blade errors are reserved for unrecoverable states. Purple upsell message bars are only for non-intrusive adoption prompts. Mobile/global search no-results may include filter pills, scope-change link, Entra continuation, and feedback footer. Standalone Empty state remains metadata-only/hidden/generic-only; search no-results is contextual source reference, not standalone Empty state parity. |
| Form input row status | `.Input row` `27293:520`, `raw\design-context-27293-520-input-row-status-summary.txt` | Azure form rows use a fixed label column and flexible field column, optional secondary hierarchy indicator, optional create link, and optional informational status line with 12px icon/caption. Use Fluent `Field` helper/validation slots to implement this anatomy. |
| Documentation-sufficient Fluent primitives | Component Contents labels for Field/Input/Textarea/Dropdown/Date Picker/Radio group/Switch/Spin button/Dialog/Drawer/Tooltip/Toast/Spinner/Skeleton/Tree/Tag picker | These are generic Fluent 2 code-available primitives with adequate public guidance and no newly extracted Azure-only anatomy in this pass. Use `@fluentui/react-components` directly and compose with the extracted Azure recipes above only when a product flow needs them. |
| TOC-only labels not found in published graph | Arm error list, Link - Resource/Sanitized/View, Dropdown - Location/Resource group/Subscription, Secure input, Breadcrumb, Status indicator, Nav for Azure Portal | These visible Component Contents labels did not appear as separate published component nodes in the cached component graph or batch 08 gap analysis. Until exact nodes are discovered, use Fluent primitives plus extracted Azure recipes for resource links, dropdown/filter pills, service menu, header, error messaging, and form validation. |

### Captured Azure UI Kit Pattern Contents

Pattern Contents are Azure Portal workflow/page archetypes. They are not components, and they should not be used to justify rebuilding Fluent primitives. Use them when a whole Agentweaver page maps to a portal task pattern.

| Pattern group | Pattern | Progress |
|---|---|---|
| Portal pattern | CES/CVA | Complete |
| Portal pattern | Error Messages | Complete |
| Portal pattern | Notifications | Complete |
| Resource management patterns | Browse Resource | Complete |
| Resource management patterns | Delete a Resource | Complete |
| Resource management patterns | Manage a Resource | Planning Changes |
| Resource management patterns | Create a Resource | Work in progress |
| Page designs patterns | Filtering | Complete |
| Page designs patterns | Forms | Complete |
| Page designs patterns | List and Grids | Complete |
| Page designs patterns | Step Wizard | Complete |
| Page designs patterns | Service Overview | Complete |

Agentweaver mapping:

- **Error Messages / Notifications:** apply to API errors, run failures, status indicators, message bars, and toast feedback.
- **Filtering / List and Grids:** apply to project lists, orchestration lists, workspace browsers, diagnostics tables, and observability tables.
- **Forms / Step Wizard:** apply to project creation, workflow generation, casting, and long setup dialogs.
- **Service Overview:** apply to Overview and Dashboard pages: command strip first, then status metrics, recent activity, and attention surfaces.
- **Resource management patterns:** use as task-flow inspiration only; Agentweaver resources are projects, runs, workflows, agents, memories, and workspace files.

Extracted pattern guidance from `pattern-node-index.json` / `patterns.jsonl`:

| Pattern | Extracted status | Agentweaver guidance |
|---|---|---|
| CES/CVA | Cached via child frame `4750:18318` | Treat as feedback-entry guidance only. Feedback belongs in a low-emphasis footer/link or local command area; do not turn it into a primary product CTA. |
| Error Messages | Cached metadata and design context | Choose the smallest effective error pattern: field-level validation for invalid input, inline/message-bar errors for recoverable page issues, dialog confirmation for destructive or data-loss actions, and full blade/page error only when the page cannot continue. |
| Notifications | Cached metadata and design context | Use notification surfaces for actionable status and next steps, with consistent placement and reflow. Avoid decorative alert stacks; status should be local to the affected blade, grid, run, or workflow unless it is a global event. |
| Browse Resource | Cached metadata and design context | Browse pages use top navigation, a page headline/description, then resource discovery with list/grid/search/filter affordances. Agentweaver's project/workspace browsers should start with search/filter/list, not metric-card grids. |
| Delete a Resource | Cached via child frame `5706:33046` | Destructive delete flows require explicit object naming, consequences, soft-delete/recovery language when available, danger styling only on the destructive action, and a clear cancel path. |
| Manage a Resource | Cached via child frame `6355:66884`; Pattern Contents marks it "Planning Changes" | Use cautiously as an evolving pattern. Current source reference still favors Portal shell + service menu + toolbar/grid/content sections for project/run/workflow management pages. |
| Create a Resource | Notice-only/unavailable; linked node shows a `.Design System Update Notice` and Pattern Contents marks it "Work in progress" | Do not treat as authoritative yet. For create flows, use Forms + Step Wizard source reference instead. |
| Filtering | Cached via targeted child frame `3273:15356`, `raw\design-context-pattern-3273-15356-filtering-content-footer.txt` | Use the horizontal sequence shown in the cached pattern: Toolbar (Azure), SearchBox plus filter pill group, `F2-Data Grid`, then footer bar. Do not stack filters into cards. Context panes keep their own local toolbar/filter/grid/footer stack. |
| Forms | Cached via metadata and representative child frame `3203:15419` | Forms live inside a blade/content region with stable shell, service menu when applicable, and footer actions. At narrow widths, the form narrows while retaining labels, validation, and footer affordances. |
| List and Grids | Cached via metadata and representative child frame `3715:20982` | Use toolbar + essentials + grid on overview-style pages; use toolbar + filter pills + grid for operational lists; use empty state below the grid header when no rows match. |
| Step Wizard | Cached via metadata and representative child frame `3203:24770` | Keep the shell and blade header stable across first/final/error steps. Use horizontal tab/step context only when it helps orientation; footer actions carry progression. |
| Service Overview | Cached via child frame `4654:83587` | Overview pages should combine command actions, overview card/details, essentials/status, and follow-up sections. Avoid the current hero-metric template; use Azure overview card anatomy and table/detail blocks. |

### Product tone

- **Restrained Azure product UI.** Dense enough for operational work, but keep rows readable and controls discoverable.
- **Neutral layers first.** The app canvas uses `colorNeutralBackground2`; product surfaces use `colorNeutralBackground1`; nested strips/cards use `colorNeutralBackground2` or `colorNeutralBackground3` only when hierarchy needs it.
- **Azure blue is stateful.** Reserve brand blue for primary actions, current navigation selection, active state indicators, focus, and links. Do not use brand blue as decoration.
- **Rounded, practical shapes.** Use `borderRadiusXLarge` for page-level panels and command strips, `borderRadiusLarge` for cards/list rows, `borderRadiusMedium` for controls.
- **Standard Fluent affordances.** Prefer Fluent Button, Badge, MessageBar, Dialog, Drawer, TabList, Table/DataGrid-like tables, Dropdown/Combobox, Tooltip, Popover, and Skeleton-style loading over bespoke controls.

### Shared app recipes

| Recipe | Implementation | Guidance |
|---|---|---|
| App shell | `AppShell`, `LeftNav`, `TopBar` | Persistent Azure portal frame: left navigation, top command/status bar, scrollable content canvas. Keep app chrome below Fluent overlay z-index. |
| Page header | `PageHeader` | White page-title surface with breadcrumb, title/subtitle, and right-aligned commands. One per route. |
| Page container | `AzurePage` | Max-width operational canvas (`1480px`) with token gaps and optional full-height mode for split workspaces. |
| Surface/panel | `AzureSurface` | White or subtle neutral panel with token border/radius/shadow. Use raised surfaces for command centers and flat surfaces for headers. |
| Command strip | `AzureCommandStrip` or equivalent styles | A page-level action/status band: leading status copy, middle metrics, trailing decision/actions. |
| Section header | `AzureSectionHeader` or equivalent styles | Title/subtitle on the left, compact actions/filter controls on the right. |
| Empty state | `AzureEmptyState` or equivalent styles | Calm neutral placeholder with one icon, one title, optional body, and at most one action group. |
| Work item cards | Kanban `TaskCard`/`RunCard` | White cards on neutral column panels, large radius, subtle border, `shadow2`; hover may lift to `shadow4`. |

### Page patterns

- **Overview / Dashboard:** Use a command strip first, then metric cards, recent activity, and attention panels. Keep numeric metrics tabular and label them with `fontSizeBase200`.
- **Board:** Intake sits in a raised command surface. Columns are neutral panels; cards stay white for contrast. Drag/drop affordances are dashed neutral/brand borders, not saturated fills.
- **Workspace:** Use a full-height split view. Tree and file viewer are independent white panels with scroll inside each panel, not the whole route.
- **Orchestrations / Workflows / Agents:** Prefer dense list rows or card grids with clear status badges and one primary action per item.
- **Dialogs / drawers:** Use Fluent surfaces and actions. Large two-column dialogs scroll inside columns and keep headers/footers fixed.

### Implementation guardrails

- Import from `@fluentui/react-components` and `@fluentui/react-icons`; do not replace Fluent React.
- Prefer token references (`tokens.colorNeutralStroke2`) over raw `hex`, `rgba`, or pixel literals. Fixed dimensions are allowed only for structural constraints (nav width, split-pane widths, touch targets).
- Keep route structure, data fetching, and API behavior unchanged when applying UI recipes.
- If a Figma variable is not present in the captured artifacts, do **not** invent or copy a raw value. Use the nearest Fluent semantic token.

---

## Colors

### Brand palette (Communication Blue)

The web brand maps to the `blue` color family. These tokens adapt automatically across themes.

| Token | Light value | Usage |
|---|---|---|
| `colorBrandBackground` | `#0078d4` | Primary button fill, selected indicators |
| `colorBrandBackgroundHover` | `#106ebe` | Primary button hover |
| `colorBrandBackgroundSelected` | `#005a9e` | Primary button selected/active |
| `colorBrandBackgroundPressed` | `#004578` | Primary button pressed |
| `colorBrandBackground2` | `#eff6fc` | Light brand tint backgrounds |
| `colorBrandForeground1` | `#0078d4` | Brand-colored text, icon on light bg |
| `colorBrandForeground2` | `#106ebe` | Brand-colored text hover |
| `colorBrandForegroundLink` | `#106ebe` | Hyperlinks |
| `colorBrandStroke1` | `#0078d4` | Brand border, compound checkbox stroke |
| `colorBrandStroke2` | `#c7e0f4` | Subtle brand border |
| `colorCompoundBrandStroke` | `#0078d4` | Checkbox / toggle compound border |
| `colorNeutralForegroundOnBrand` | `#ffffff` | Text/icons rendered on brand fill |

### Global grey scale

The full grey scale underpins all neutral tokens. Reference only via alias tokens.

| Step | Value | Step | Value | Step | Value |
|---|---|---|---|---|---|
| grey[2] | `#050505` | grey[38] | `#616161` | grey[74] | `#bdbdbd` |
| grey[4] | `#0a0a0a` | grey[40] | `#666666` | grey[76] | `#c2c2c2` |
| grey[6] | `#0f0f0f` | grey[44] | `#707070` | grey[78] | `#c7c7c7` |
| grey[8] | `#141414` | grey[46] | `#757575` | grey[80] | `#cccccc` |
| grey[10] | `#1a1a1a` | grey[50] | `#808080` | grey[82] | `#d1d1d1` |
| grey[12] | `#1f1f1f` | grey[52] | `#858585` | grey[84] | `#d6d6d6` |
| grey[14] | `#242424` | grey[54] | `#8a8a8a` | grey[86] | `#dbdbdb` |
| grey[16] | `#292929` | grey[56] | `#8f8f8f` | grey[88] | `#e0e0e0` |
| grey[18] | `#2e2e2e` | grey[58] | `#949494` | grey[90] | `#e6e6e6` |
| grey[20] | `#333333` | grey[60] | `#999999` | grey[92] | `#ebebeb` |
| grey[22] | `#383838` | grey[62] | `#9e9e9e` | grey[94] | `#f0f0f0` |
| grey[24] | `#3d3d3d` | grey[64] | `#a3a3a3` | grey[96] | `#f5f5f5` |
| grey[26] | `#424242` | grey[66] | `#a8a8a8` | grey[98] | `#fafafa` |
| grey[28] | `#474747` | grey[68] | `#adadad` | grey[99] | `#fcfcfc` |
| grey[30] | `#4d4d4d` | grey[70] | `#b3b3b3` | white | `#ffffff` |
| grey[32] | `#525252` | grey[72] | `#b8b8b8` | black | `#000000` |
| grey[34] | `#575757` | | | | |
| grey[36] | `#5c5c5c` | | | | |

### Neutral foreground / text tokens

| Token | Value | Usage |
|---|---|---|
| `colorNeutralForeground1` | `#242424` | Primary text — body copy, headings |
| `colorNeutralForeground2` | `#424242` | Secondary text — labels, secondary copy |
| `colorNeutralForeground3` | `#616161` | Subtle / metadata — timestamps, captions |
| `colorNeutralForeground4` | `#707070` | Decorative / tertiary — less emphasis |
| `colorNeutralForegroundDisabled` | `#bdbdbd` | Disabled state text |
| `colorNeutralForegroundInverted` | `#ffffff` | Text on dark/inverted surfaces |
| `colorNeutralForegroundOnBrand` | `#ffffff` | Text/icons on brand-colored fills |
| `colorNeutralStrokeAccessible` | `#616161` | Meets AA contrast for borders |

### Neutral background tokens

| Token | Value | Usage |
|---|---|---|
| `colorNeutralBackground1` | `#ffffff` | Cards, dialogs, inputs — highest layer |
| `colorNeutralBackground1Hover` | `#f5f5f5` | Hovered state of bg1 |
| `colorNeutralBackground1Pressed` | `#e0e0e0` | Pressed state of bg1 |
| `colorNeutralBackground1Selected` | `#ebebeb` | Selected state of bg1 |
| `colorNeutralBackground2` | `#fafafa` | Page canvas, panel backgrounds |
| `colorNeutralBackground2Hover` | `#f0f0f0` | Hovered state of bg2 |
| `colorNeutralBackground3` | `#f5f5f5` | Hover fills (list items) |
| `colorNeutralBackground4` | `#f0f0f0` | Pressed fills |
| `colorNeutralBackground5` | `#ebebeb` | Selected fills |
| `colorNeutralBackground6` | `#e6e6e6` | Disabled fills |
| `colorNeutralBackgroundInverted` | `#292929` | Inverted/dark surfaces |
| `colorNeutralBackgroundDisabled` | `#f0f0f0` | Disabled control backgrounds |
| `colorNeutralCardBackground` | `#fafafa` | Card surface fill |
| `colorSubtleBackground` | `transparent` | Default for ghost/subtle elements |
| `colorSubtleBackgroundHover` | `#f5f5f5` | Ghost element hover |
| `colorSubtleBackgroundPressed` | `#e0e0e0` | Ghost element pressed |
| `colorSubtleBackgroundSelected` | `#ebebeb` | Ghost element selected |
| `colorTransparentBackground` | `transparent` | Fully transparent surfaces |

### Stroke / border tokens

| Token | Value | Usage |
|---|---|---|
| `colorNeutralStroke1` | `#d1d1d1` | Default control borders |
| `colorNeutralStroke1Hover` | `#c7c7c7` | Border on hover |
| `colorNeutralStroke1Pressed` | `#b3b3b3` | Border on press |
| `colorNeutralStroke2` | `#e0e0e0` | Subtle dividers |
| `colorNeutralStroke3` | `#f0f0f0` | Very subtle structural lines |
| `colorNeutralStrokeDisabled` | `#e0e0e0` | Disabled borders |
| `colorNeutralStrokeOnBrand` | `#ffffff` | Border on brand fill |
| `colorStrokeFocus1` | `#ffffff` | Inner focus ring |
| `colorStrokeFocus2` | `#000000` | Outer focus ring |

**Shadow color primitives** (used in `box-shadow` only, not as fills):

| Token | Value |
|---|---|
| `colorNeutralShadowAmbient` | `rgba(0,0,0,0.12)` |
| `colorNeutralShadowKey` | `rgba(0,0,0,0.14)` |

### Status semantic tokens

#### Warning (orange palette)

| Token | Value |
|---|---|
| `colorStatusWarningBackground1` | `#fff9f5` |
| `colorStatusWarningBackground2` | `#fdcfb4` |
| `colorStatusWarningBackground3` | `#faa06b` |
| `colorStatusWarningForeground1` | `#bc4b09` |
| `colorStatusWarningForeground2` | `#de590b` |
| `colorStatusWarningForeground3` | `#8a3707` |
| `colorStatusWarningBorderActive` | `#f7630c` |
| `colorStatusWarningBorder1` | `#fdcfb4` |

#### Danger (cranberry palette)

| Token | Value |
|---|---|
| `colorStatusDangerBackground1` | `#fdf3f4` |
| `colorStatusDangerBackground1Hover` | `#f6d1d5` |
| `colorStatusDangerBackground2` | `#eeacb2` |
| `colorStatusDangerBackground3` | `#dc626d` |
| `colorStatusDangerForeground1` | `#c50f1f` |
| `colorStatusDangerForeground2` | `#b10e1c` |
| `colorStatusDangerForeground3` | `#6e0811` |
| `colorStatusDangerBorderActive` | `#c50f1f` |
| `colorStatusDangerBorder1` | `#eeacb2` |

#### Success (green palette)

| Token | Value |
|---|---|
| `colorStatusSuccessBackground1` | `#f1faf1` |
| `colorStatusSuccessBackground2` | `#9fd89f` |
| `colorStatusSuccessBackground3` | `#359b35` |
| `colorStatusSuccessForeground1` | `#107c10` |
| `colorStatusSuccessForeground2` | `#0e700e` |
| `colorStatusSuccessForeground3` | `#094509` |
| `colorStatusSuccessBorderActive` | `#218c21` |
| `colorStatusSuccessBorder1` | `#9fd89f` |

#### Informative (royalBlue palette)

| Token | Value |
|---|---|
| `colorStatusInformativeBackground1` | `#f0f6fa` |
| `colorStatusInformativeBackground2` | `#9abfdc` |
| `colorStatusInformativeBackground3` | `#286fa8` |
| `colorStatusInformativeForeground1` | `#004e8c` |
| `colorStatusInformativeForeground2` | `#00467e` |
| `colorStatusInformativeForeground3` | `#002c4e` |
| `colorStatusInformativeBorderActive` | `#125e9a` |
| `colorStatusInformativeBorder1` | `#9abfdc` |

### Palette colors (shared accent / avatar / badge fills)

50 palette families are available as structured tokens. Each family exposes the pattern:

```
colorPalette{Name}Background1   — lightest tint (icon container background)
colorPalette{Name}Background2   — mid tint (badge background)
colorPalette{Name}Background3   — strong fill (icon background)
colorPalette{Name}Foreground1   — dark shade (primary foreground on tint)
colorPalette{Name}Foreground2   — mid shade
colorPalette{Name}Foreground3   — darkest shade
colorPalette{Name}BorderActive  — primary color (active border)
colorPalette{Name}Border1       — tint border
```

**Available palette families:**

| Group | Families |
|---|---|
| Reds | darkRed, burgundy, cranberry, red |
| Oranges | darkOrange, bronze, pumpkin, orange, peach, marigold |
| Yellows | yellow, gold, brass |
| Browns | brown, darkBrown |
| Greens | lime, forest, seafoam, lightGreen, green, darkGreen |
| Teals | lightTeal, teal, darkTeal |
| Blues | cyan, steel, lightBlue, blue, royalBlue, darkBlue, cornflower, navy |
| Purples | lavender, purple, darkPurple, orchid, grape, berry, lilac |
| Pinks | pink, hotPink, magenta |
| Neutral/Meta | plum, beige, mink, silver, platinum, anchor, charcoal |

**Example usage:**

```tsx
// Cranberry badge
<Badge
  style={{
    backgroundColor: tokens.colorPaletteCranberryBackground2,
    color: tokens.colorPaletteCranberryForeground1,
    borderColor: tokens.colorPaletteCranberryBorder1,
  }}
>
  Error
</Badge>
```

---

## Typography

### Font families

| Token | Value | Usage |
|---|---|---|
| `fontFamilyBase` | `"Segoe UI", "Segoe UI Web (West European)", -apple-system, BlinkMacSystemFont, Roboto, "Helvetica Neue", sans-serif` | All UI text |
| `fontFamilyMonospace` | `Consolas, "Courier New", Courier, monospace` | Code blocks, technical values |
| `fontFamilyNumeric` | `Bahnschrift, "DIN Alternate", "Franklin Gothic Medium", "Nimbus Sans Narrow", sans-serif-condensed, sans-serif` | Tabular numbers, financial data |

### Font weights

| Token | Value | Usage |
|---|---|---|
| `fontWeightRegular` | `400` | Body text, captions |
| `fontWeightMedium` | `500` | Emphasized body, secondary labels |
| `fontWeightSemibold` | `600` | Subtitles, titles, button labels |
| `fontWeightBold` | `700` | Display, Title 1, Large Title |

### Type ramp

| Fluent name | Size token | Size | Line-height token | Line-height | Weight | Use for |
|---|---|---|---|---|---|---|
| Caption 2 | `fontSizeBase100` | `10px` | `lineHeightBase100` | `14px` | 400 | Fine print, legal |
| Caption 1 | `fontSizeBase200` | `12px` | `lineHeightBase200` | `16px` | 400 | Labels, tags, tooltips |
| Body 1 | `fontSizeBase300` | `14px` | `lineHeightBase300` | `20px` | 400 | **Default body text** |
| Subtitle 2 | `fontSizeBase400` | `16px` | `lineHeightBase400` | `22px` | 600 | Section headings, prominent labels |
| Subtitle 1 | `fontSizeBase500` | `20px` | `lineHeightBase500` | `26px` | 600 | Panel headings |
| Title 3 | `fontSizeBase600` | `24px` | `lineHeightBase600` | `32px` | 600 | Card headings, dialog titles |
| Title 2 | `fontSizeHero700` | `28px` | `lineHeightHero700` | `36px` | 600 | Page section titles |
| Title 1 | `fontSizeHero800` | `32px` | `lineHeightHero800` | `40px` | 700 | Page titles |
| Large Title | `fontSizeHero900` | `40px` | `lineHeightHero900` | `52px` | 700 | Hero headings |
| Display | `fontSizeHero1000` | `68px` | `lineHeightHero1000` | `92px` | 700 | Marketing / splash |

**Usage example:**

```tsx
import { Text, makeStyles, tokens } from "@fluentui/react-components";

const useStyles = makeStyles({
  heading: {
    fontSize: tokens.fontSizeHero800,
    lineHeight: tokens.lineHeightHero800,
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorNeutralForeground1,
  },
});

// Or use Fluent's <Text> with built-in ramp variants:
<Text as="h1" size={800} weight="bold">Page Title</Text>
```

---

## Layout

### Spacing scale

Horizontal and vertical spacing tokens mirror each other exactly.

| Level | Token (H) | Token (V) | Value |
|---|---|---|---|
| None | `spacingHorizontalNone` | `spacingVerticalNone` | `0px` |
| XXS | `spacingHorizontalXXS` | `spacingVerticalXXS` | `2px` |
| XS | `spacingHorizontalXS` | `spacingVerticalXS` | `4px` |
| SNudge | `spacingHorizontalSNudge` | `spacingVerticalSNudge` | `6px` |
| S | `spacingHorizontalS` | `spacingVerticalS` | `8px` |
| MNudge | `spacingHorizontalMNudge` | `spacingVerticalMNudge` | `10px` |
| M | `spacingHorizontalM` | `spacingVerticalM` | `12px` |
| L | `spacingHorizontalL` | `spacingVerticalL` | `16px` |
| XL | `spacingHorizontalXL` | `spacingVerticalXL` | `20px` |
| XXL | `spacingHorizontalXXL` | `spacingVerticalXXL` | `24px` |
| XXXL | `spacingHorizontalXXXL` | `spacingVerticalXXXL` | `32px` |

**Usage rules:**
- Use **S (8px)** for internal component padding (icon gaps, badge padding).
- Use **M (12px)** for compact form field padding.
- Use **L (16px)** for card padding, section gaps.
- Use **XL–XXL (20–24px)** for layout column gaps.
- Use **XXXL (32px)** for major section separators.

### Grid

Fluent 2 does not mandate a fixed grid. Recommended approach:
- Fluid 12-column grid with `spacingHorizontalXXL` (24px) gutters.
- Content max-width: `1280px` (with `spacingHorizontalXXXL` (32px) horizontal padding on mobile).
- Use CSS Grid or Flexbox; Fluent provides no layout primitives.

### Z-index / layer system

Fluent 2 defines a logical stacking order. Use these values for custom overlays:

| Layer | z-index | Surfaces |
|---|---|---|
| Base | `0` | Page content, cards |
| Raised | `1` | Raised cards, sticky headers |
| Overlay | `1000` | Drawers, side panels |
| Dialog | `1000` | Modal dialogs, popups |
| Flyout | `1000` | Dropdowns, menus, tooltips |
| Toast | `1000` | Notification toasts |
| Overlay (critical) | `9999` | Full-screen blocking overlays |

> Fluent components manage their own z-index internally via the `Portal` component.

**Overlay must be the true top layer.** Fluent's `Dialog`/`Popover` portal above app content by default. When app chrome (a fixed left nav rail, a floating action button) sets its own high z-index or forms its own stacking context, it can paint over the modal scrim, leaving parts of the window undimmed. Keep app-chrome z-index below the Fluent overlay layer so the dialog and its dimmed scrim sit above the nav rail and the FAB — do not raise a single chrome element past the dialog. Verify by opening a dialog and confirming the entire viewport (nav rail and FAB included) is dimmed.

### Modal dialog surface

Convention for the Create Project dialogs (`ProjectGalleryPage`), applicable to any large two-column modal:

- **Centered, bounded surface.** Let Fluent's default `DialogSurface` centering stand (do not pin it to the top with a `marginTop`/`top`/`alignSelf` override). Bound its height with `maxHeight: calc(100vh - <comfortable margin>)` so it sits centered with roughly equal space above and below on a laptop viewport.
- **Per-column scroll, not full-modal scroll.** In a two-column body (source on the left, blueprint on the right), give each column its own bounded scroll region rather than letting the whole modal scroll. The header and footer stay fixed; only the column that overflows scrolls.
- **Full-width footer.** Use `DialogActions` as a single full-width row: secondary/utility affordance and its helper text on the left (e.g. the "No blueprint" control + a one-line tip), primary actions on the right (Cancel + Create). Do not let the footer controls collapse into a narrow column.
- **Dimmed backdrop over the full viewport.** The scrim must cover `100vw × 100vh` (`inset: 0`) regardless of surface size; keep the surface's own max-height/margin from shrinking the backdrop, and see the overlay/top-layer note under Z-index above.

---

## Elevation & Depth

### Shadow tokens

Shadow tokens communicate elevation. Higher shadow = higher perceived layer.

| Token | Value | Use for |
|---|---|---|
| `shadow2` | `0 0 2px rgba(0,0,0,0.12), 0 1px 2px rgba(0,0,0,0.14)` | Cards resting on page |
| `shadow4` | `0 0 2px rgba(0,0,0,0.12), 0 2px 4px rgba(0,0,0,0.14)` | Cards on cards, dropdowns |
| `shadow8` | `0 0 2px rgba(0,0,0,0.12), 0 4px 8px rgba(0,0,0,0.14)` | Menus, tooltips |
| `shadow16` | `0 0 2px rgba(0,0,0,0.12), 0 8px 16px rgba(0,0,0,0.14)` | Dialogs, panels |
| `shadow28` | `0 0 8px rgba(0,0,0,0.12), 0 14px 28px rgba(0,0,0,0.14)` | High-elevation panels |
| `shadow64` | `0 0 8px rgba(0,0,0,0.12), 0 32px 64px rgba(0,0,0,0.14)` | Full-screen overlays |

### Stroke width tokens

| Token | Value | Usage |
|---|---|---|
| `strokeWidthThin` | `1px` | Default borders |
| `strokeWidthThick` | `2px` | Focus indicators, selected indicators |
| `strokeWidthThicker` | `3px` | Emphasis borders |
| `strokeWidthThickest` | `4px` | Heavy structural dividers |

### Surface layer hierarchy (light theme)

```
colorNeutralBackground2 (#fafafa)  — page canvas / app shell
  └─ colorNeutralBackground1 (#ffffff) — cards, panels
       └─ shadow2 / shadow4            — floating elements (dropdowns, combobox)
            └─ shadow16                — dialogs
                 └─ shadow28           — high-elevation overlays
```

---

## Shapes

### Border radius

| Token | Value | Applied to |
|---|---|---|
| `borderRadiusNone` | `0px` | Flush / square elements (dividers) |
| `borderRadiusSmall` | `2px` | Tags, compact chips |
| `borderRadiusMedium` | `4px` | **Default** — buttons, inputs, cards |
| `borderRadiusLarge` | `6px` | Large cards, dialogs |
| `borderRadiusXLarge` | `8px` | Panels, sheets |
| `borderRadiusCircular` | `9999px` | Badges, avatars, pills, FABs |

**Rule:** Nested elements use the same or smaller radius than their container.

---

## Motion

### Duration tokens

| Token | Value | Use for |
|---|---|---|
| `durationUltraFast` | `50ms` | Micro interactions (checkbox tick) |
| `durationFaster` | `100ms` | Icon swaps, color transitions |
| `durationFast` | `150ms` | Tooltip appear, badge pop |
| `durationNormal` | `200ms` | **Default** — most state changes |
| `durationGentle` | `250ms` | Expand/collapse (accordion) |
| `durationSlow` | `300ms` | Drawer slide-in, page transitions |
| `durationSlower` | `400ms` | Complex choreography |
| `durationUltraSlow` | `500ms` | Full-screen transitions |

### Easing curve tokens

| Token | Value | Use for |
|---|---|---|
| `curveLinear` | `cubic-bezier(0, 0, 1, 1)` | Looping animations (spinners) |
| `curveEasyEase` | `cubic-bezier(0.33, 0, 0.67, 1)` | **Default** — neutral state changes |
| `curveEasyEaseMax` | `cubic-bezier(0.8, 0, 0.2, 1)` | Emphasis interactions |
| `curveDecelerateMax` | `cubic-bezier(0.1, 0.9, 0.2, 1)` | Elements entering the screen |
| `curveDecelerateMid` | `cubic-bezier(0, 0, 0, 1)` | Mid-screen element entrances |
| `curveDecelerateMin` | `cubic-bezier(0.33, 0, 0.1, 1)` | Subtle entrances |
| `curveAccelerateMax` | `cubic-bezier(0.9, 0.1, 1, 0.2)` | Elements leaving the screen |
| `curveAccelerateMid` | `cubic-bezier(1, 0, 1, 1)` | Fast exits |
| `curveAccelerateMin` | `cubic-bezier(0.8, 0, 0.78, 1)` | Subtle exits |

### Motion principles

1. **Direction matters.** Entering elements decelerate (`curveDecelerate*`); exiting elements accelerate (`curveAccelerate*`).
2. **Default to `durationNormal` + `curveEasyEase`** for most hover/focus/active transitions.
3. **Reduce motion.** Honor `prefers-reduced-motion`; Fluent's `useMotion` hooks do this automatically.
4. **No gratuitous animation.** Motion should communicate state, not decorate.

**CSS example:**

```css
.button {
  transition: background-color var(--durationNormal) var(--curveEasyEase);
}
```

**React + Fluent example:**

```tsx
import { tokens } from "@fluentui/react-components";
import { makeStyles } from "@fluentui/react-components";

const useStyles = makeStyles({
  panel: {
    transitionProperty: "transform, opacity",
    transitionDuration: tokens.durationSlow,
    transitionTimingFunction: tokens.curveDecelerateMax,
  },
});
```

### Earned-moment motion (delight)

Motion is restrained and reserved for earned moments — a state that changed, a result that arrived, a fresh item worth noticing. Delight spread across the page reads as noise. Every pattern below is written with Griffel keyframes (`animationName: { '0%': {...}, '100%': {...} }` + `animationDuration`) and **every one ships a `@media (prefers-reduced-motion: reduce)` fallback that sets `animationName: 'none'`** and neutralizes transform/opacity. Reduced motion is not optional.

| Moment | Motion | Duration / curve | Reduced-motion fallback |
|---|---|---|---|
| Work in flight (blueprint generation) | A "breathing" sparkle icon (subtle scale + opacity pulse) beside rotating status lines | `~1.8s` loop, `curveEasyEase` | Static icon, plain status text |
| Result arrives (generated preview) | One-time rise + fade (`translateY(8px)`→`0`, `opacity 0`→`1`) | `~340ms`, `curveDecelerateMid`, `fillMode: both` | Instant, fully visible |
| Result badge | One-time pop (`scale(0.7)`→`1` with a slight overshoot) | `durationGentle`, `curveDecelerateMid` | Instant, full scale |
| New item created (project card) | One-time entrance: brand ring + gentle rise, cleared after it plays so it never replays | `curveDecelerateMid` | Instant, no ring animation |
| Success confirmation | Fluent `Toast` (`intent="success"`) dispatched from a `Toaster` | Fluent default | Fluent handles it |

Rules for these:
- **One-time entrances must clear themselves** (e.g. drop the highlight state after the animation duration) so a later re-render doesn't replay them.
- **Loops are for genuine in-progress state only** (generation working state), never decoration.
- Prefer `transform`/`opacity`; a brand ring uses an inset `box-shadow`, not an animated border-width, to avoid reflow.

---

## Components

All components must be imported from `@fluentui/react-components` and wrapped in `<FluentProvider theme={webLightTheme}>`.

### Button

Fluent 2 Button has five visual appearances. Use the `appearance` prop.

| Appearance | Token mapping | When to use |
|---|---|---|
| `primary` | `colorBrandBackground` fill, white text | Primary CTA — one per view |
| `outline` | transparent fill, `colorNeutralStroke1` border, brand text | Secondary actions |
| `subtle` | transparent fill, neutral text | Tertiary / ghost actions |
| `transparent` | fully transparent, brand text | Link-like inline actions |
| `secondary` (default) | `colorNeutralBackground1` fill, border, neutral text | General-purpose secondary |

**Status-colored buttons** are achieved by overriding tokens via `makeStyles`:

```tsx
const useStyles = makeStyles({
  dangerButton: {
    backgroundColor: tokens.colorStatusDangerBackground1,
    color: tokens.colorStatusDangerForeground1,
    borderColor: tokens.colorStatusDangerBorder1,
    ":hover": {
      backgroundColor: tokens.colorStatusDangerBackground1Hover,
      borderColor: tokens.colorStatusDangerBorderActive,
    },
  },
});
```

**Focus ring:** All buttons render a double-ring focus indicator using `colorStrokeFocus1` (white inner) and `colorStrokeFocus2` (black outer) with `strokeWidthThick` (2px).

### Input / TextField

| State | Background | Border |
|---|---|---|
| Default | `colorNeutralBackground1` (`#ffffff`) | `colorNeutralStroke1` (`#d1d1d1`) |
| Hover | `colorNeutralBackground1` | `colorNeutralStroke1Hover` (`#c7c7c7`) |
| Focus | `colorNeutralBackground1` | `colorBrandStroke1` (`#0078d4`) 2px |
| Disabled | `colorNeutralBackgroundDisabled` (`#f0f0f0`) | `colorNeutralStrokeDisabled` (`#e0e0e0`) |
| Error | `colorNeutralBackground1` | `colorStatusDangerBorderActive` (`#c50f1f`) |

Placeholder text: `colorNeutralForeground4` (`#707070`)  
Input text: `colorNeutralForeground1` (`#242424`)  
Border radius: `borderRadiusMedium` (4px)

### Card

| Variant | Background | Border | Shadow |
|---|---|---|---|
| `filled` | `colorNeutralCardBackground` (`#fafafa`) | `colorNeutralStroke2` (`#e0e0e0`) | none |
| `filled-alternative` | `colorNeutralBackground2` (`#fafafa`) | none | none |
| `outline` | `colorNeutralBackground1` (`#ffffff`) | `colorNeutralStroke1` (`#d1d1d1`) | none |
| `subtle` | `colorSubtleBackground` (transparent) | none | none |
| Default (raised) | `colorNeutralBackground1` (`#ffffff`) | `colorNeutralStroke2` | `shadow4` |

Padding: `spacingHorizontalL` / `spacingVerticalL` (16px)  
Border radius: `borderRadiusMedium` (4px)

### Badge

| Appearance | Background | Foreground | Border |
|---|---|---|---|
| `filled` | color-family based | white | none |
| `ghost` | transparent | color-family based | none |
| `outline` | transparent | color-family based | color-family based |
| `tint` | lightest tint | dark shade | light tint border |

**Status badge tokens:**

```tsx
// Informative tint badge
<Badge appearance="tint" color="informative">Info</Badge>
// Uses colorStatusInformativeBackground1 + colorStatusInformativeForeground1 + colorStatusInformativeBorder1

// Warning
<Badge appearance="tint" color="warning">Warn</Badge>

// Danger
<Badge appearance="tint" color="danger">Error</Badge>

// Success
<Badge appearance="tint" color="success">Done</Badge>
```

Shape: `borderRadiusCircular` (9999px)  
Font: `fontSizeBase200` (12px), `fontWeightSemibold` (600)

### Tooltip

Background: `colorNeutralBackgroundInverted` (`#292929`)  
Text: `colorNeutralForegroundInverted` (`#ffffff`)  
Font: `fontSizeBase200` (12px)  
Border radius: `borderRadiusMedium` (4px)  
Shadow: `shadow8`  
Padding: `spacingVerticalXS` / `spacingHorizontalS` (4px 8px)  
Motion: `durationFast` + `curveDecelerateMax` (in), `durationFaster` + `curveAccelerateMin` (out)

### Spinner / Progress

Active color: `colorBrandBackground` (`#0078d4`)  
Track color: `colorNeutralBackground6` (`#e6e6e6`)  
Animation: linear rotation, `durationUltraSlow` per cycle

### Checkbox & Toggle

| State | Indicator fill | Border |
|---|---|---|
| Unchecked | `colorNeutralBackground1` | `colorNeutralStrokeAccessible` (`#616161`) |
| Checked | `colorBrandBackground` (`#0078d4`) | `colorBrandBackground` |
| Indeterminate | `colorBrandBackground` | `colorBrandBackground` |
| Disabled | `colorNeutralBackgroundDisabled` | `colorNeutralStrokeDisabled` |

Compound border: `colorCompoundBrandStroke` (`#0078d4`)

### Compact list-row (dense selectable list)

The preferred dense alternative to a template card grid. Each option is one fixed-height row (`54px`) instead of a tall card: leading icon, primary name, a one-line truncated description (`colorNeutralForeground2`, ellipsized), and trailing compact meta. It lets many options fit without scrolling and removes the chip-wrapping that inflates card height.

- **Structure:** `role="radio"` on the row, `aria-checked`, `aria-label={name}`, `tabIndex={0}`, Enter/Space selects. Group behaves as a radio set.
- **No reflow:** all content is single-line (`whiteSpace: nowrap`); the main column has `min-width: 0` + ellipsis; trailing meta is `flex-shrink: 0`. Selection uses an inset `box-shadow` ring, not a border-width change, so the row never changes size.
- **Trailing meta:** compact, tokenized pills — e.g. a workflow indicator (`FlowchartRegular` + name, or a `N workflows` count when a blueprint bundles several) and an `N agents` count (`PeopleTeamRegular`). The fuller detail lives in the roster tooltip, not inline.
- Frontmatter tokens: `list-row`, `list-row-selected`.

### Blueprint descriptor (BlueprintMeta)

One shared component renders a blueprint's identity the same way everywhere it appears — the Templates roster tooltip, the Suggested recommendation preview, and the Generate result preview. Do not fork this formatting per surface.

- Canonical form: `N agents` · `Workflow: X` (single) or `Workflows: a, b, c` (many) · `Review: Y`, as space-separated spans in `colorNeutralForeground3`.
- **A blueprint bundles one or more workflows** (backend `Blueprint.Workflows` is a list; the singular `workflow` is just the default = first entry). Render 0 → omit the workflow span, 1 → `Workflow: X`, many → `Workflows: …`. The compact row shows the single name or an `N workflows` count; the full list belongs in the descriptor / tooltip and the pill's `aria-label`.

### Selected state (rows, chips, options)

The standard selection affordance across the picker: `colorBrandBackground2` (`#eff6fc`) tint fill + a `2px` `colorBrandStroke1` (`#0078d4`) stroke + a same-color inset ring (`box-shadow`, so selection reads clearly without resizing the element). Used by selected template rows and by the "No blueprint" control. Selection is mutually exclusive — selecting "No blueprint" clears any highlighted template and vice-versa, and the choice shows a checked/selected style plus a confirming line so it never reads as a no-op.

### Roster / detail tooltip

The pattern for revealing per-item detail (e.g. the full agent roster) without inflating a dense row: a portaled Fluent `Tooltip` with `relationship="description"`. It opens on hover **and** keyboard focus, wires the content as `aria-describedby` (the roster is real, announced content — not a `title` attribute), and because it portals it is never clipped by the panel's bounded `overflow: auto`. Never hand-roll a `position: absolute` popover inside a scroll container.

### Empty state

Progressive disclosure: reveal suggestions only after the input they depend on exists (e.g. the Suggested tab before a repository is chosen). One calm, centered placeholder — a single neutral icon (no illustration), a concise primary line, and at most a muted secondary pointer. **Do not restate actions the adjacent tabs already own** (no "Browse templates" / "Generate" links sitting directly under Templates/Generate tabs). Guidance text must wrap fully (no truncation) and meet ≥4.5:1 — use `colorNeutralForeground2`, not a washed-out gray.

### Project card

`Card` with a `CardHeader` (name + availability badge), a meta row (origin badge + repository), the working directory, and an `Open` action. For a project connected to a GitHub repository (`origin === 'github'`), place the GitHub logo mark (a shared inline-SVG `GitHubIcon`, `fill: currentColor`, `colorNeutralForeground1`) in the `CardHeader` `image` slot, with an accessible label (`Connected to GitHub: {org/repo}`). Blank projects show no mark. The GitHub logo is a brand mark, not an emoji — its use here is allowed.

---

## Do's and Don'ts

### Colors

✅ **Do** use semantic alias tokens (`colorNeutralForeground1`) — they adapt to light/dark/HC automatically.  
✅ **Do** ensure text on `colorBrandBackground` uses `colorNeutralForegroundOnBrand` (white).  
✅ **Do** use `colorStatusDangerForeground1` on `colorStatusDangerBackground1` for status messaging.  
❌ **Don't** hardcode hex values — use `tokens.colorNeutralForeground1` from `@fluentui/react-components`.  
❌ **Don't** use foreground-1 tokens on foreground-1 backgrounds (no contrast).  
❌ **Don't** mix palette global tokens directly into components; use alias tokens.

### Typography

✅ **Do** use the Fluent `<Text>` component with `size` + `weight` props for the type ramp.  
✅ **Do** default to Body 1 (`fontSizeBase300`, 14px) for body copy.  
❌ **Don't** use font sizes outside the 10-level type ramp.  
❌ **Don't** set `font-family` manually — it is inherited from `FluentProvider`.

### Spacing

✅ **Do** use spacing tokens for all padding, margin, and gap values.  
✅ **Do** use `L` (16px) for card/panel internal padding.  
❌ **Don't** use arbitrary pixel values like `13px` or `7px`.

### Motion

✅ **Do** use `durationNormal` (200ms) + `curveEasyEase` as the default transition.  
✅ **Do** test with `prefers-reduced-motion` — Fluent hooks handle this, but custom CSS must too.  
✅ **Do** ship a `@media (prefers-reduced-motion: reduce)` fallback for **every** custom keyframe animation (set `animationName: 'none'`, neutralize transform/opacity). No exceptions.  
✅ **Do** reserve motion for earned moments (state change, arriving result, fresh item); clear one-time entrances so they never replay.  
❌ **Don't** animate layout properties (width, height) — prefer `transform` and `opacity`.  
❌ **Don't** use durations above `durationSlow` (300ms) for interactive feedback.  
❌ **Don't** spread animation across the page for decoration — delight everywhere reads as noise.

### Components

✅ **Do** use one `primary` button per view.  
✅ **Do** wrap everything in `<FluentProvider theme={webLightTheme}>`.  
✅ **Do** use `makeStyles` + `mergeClasses` for all custom styles (Griffel CSS-in-JS).  
✅ **Do** prefer a compact list-row over a card grid for dense, scannable option lists; move per-item detail into a portaled hover/focus tooltip.  
✅ **Do** reuse one shared descriptor (BlueprintMeta) so the same entity is described identically across every surface.  
✅ **Do** apply the shared selected-state affordance (brand-tint + 2px brand stroke + inset ring) to selectable rows/chips, and keep selection mutually exclusive with clear feedback.  
❌ **Don't** apply inline `style` props for token-based values — use `makeStyles`.  
❌ **Don't** create custom components for things Fluent already provides (Button, Badge, Card, Input, Tooltip, Dialog, Menu, etc.).  
❌ **Don't** reach for tall vertical cards or identical card grids by reflex — they are the lazy answer and read as slop; use them only when they are genuinely the best affordance.  
❌ **Don't** hand-roll a `position: absolute` popover inside a scroll container (it gets clipped); use a portaled Fluent `Tooltip`/`Popover`.  
❌ **Don't** duplicate, in an empty state, the actions the adjacent tabs already own.

### Voice & register

✅ **Do** keep the restrained developer-terminal register: dense, fast, information-rich; delight only at earned moments.  
✅ **Do** write plain, human copy; label and explain in the user's terms.  
❌ **Don't** use emojis anywhere in UI labels, copy, or generated text (Constitution VII). A vendor brand mark such as the GitHub logo is not an emoji and is allowed.  
❌ **Don't** use AI-filler words ("seamless", "genuine", "real", "delve", and similar).  
❌ **Don't** hardcode values that a Fluent token already covers — tokens over hard-coded hex/px, always.

---

## Dark Mode & High Contrast

`FluentProvider` handles theming transparently. To support dark mode:

```tsx
import {
  FluentProvider,
  webLightTheme,
  webDarkTheme,
  webHighContrastTheme,
} from "@fluentui/react-components";

function App({ prefersDark }: { prefersDark: boolean }) {
  return (
    <FluentProvider theme={prefersDark ? webDarkTheme : webLightTheme}>
      {/* All components automatically use correct tokens */}
    </FluentProvider>
  );
}
```

**Dark theme token shifts (informative — do not hardcode):**
- `colorNeutralBackground1` shifts from `#ffffff` → `#292929`
- `colorNeutralForeground1` shifts from `#242424` → `#ffffff`
- Brand tokens shift to lighter shades for contrast on dark backgrounds

**High contrast:** `webHighContrastTheme` maps tokens to system colors (`ButtonText`, `Highlight`, etc.) per Windows HC mode. Never override these.

---

## Appendix: Token Quick Reference

### All foreground tokens
`colorNeutralForeground1` · `colorNeutralForeground2` · `colorNeutralForeground3` · `colorNeutralForeground4` · `colorNeutralForegroundDisabled` · `colorNeutralForegroundOnBrand` · `colorNeutralForegroundInverted` · `colorBrandForeground1` · `colorBrandForeground2` · `colorBrandForegroundLink` · `colorNeutralStrokeAccessible`

### All background tokens
`colorNeutralBackground1` · `colorNeutralBackground1Hover` · `colorNeutralBackground1Pressed` · `colorNeutralBackground1Selected` · `colorNeutralBackground2` · `colorNeutralBackground2Hover` · `colorNeutralBackground3` · `colorNeutralBackground4` · `colorNeutralBackground5` · `colorNeutralBackground6` · `colorNeutralBackgroundInverted` · `colorNeutralBackgroundDisabled` · `colorNeutralCardBackground` · `colorSubtleBackground` · `colorSubtleBackgroundHover` · `colorSubtleBackgroundPressed` · `colorSubtleBackgroundSelected` · `colorTransparentBackground` · `colorBrandBackground` · `colorBrandBackgroundHover` · `colorBrandBackgroundPressed` · `colorBrandBackgroundSelected` · `colorBrandBackground2`

### All stroke tokens
`colorNeutralStroke1` · `colorNeutralStroke1Hover` · `colorNeutralStroke1Pressed` · `colorNeutralStroke2` · `colorNeutralStroke3` · `colorNeutralStrokeDisabled` · `colorNeutralStrokeOnBrand` · `colorNeutralStrokeAccessible` · `colorBrandStroke1` · `colorBrandStroke2` · `colorCompoundBrandStroke` · `colorStrokeFocus1` · `colorStrokeFocus2`

### All motion tokens
**Duration:** `durationUltraFast` · `durationFaster` · `durationFast` · `durationNormal` · `durationGentle` · `durationSlow` · `durationSlower` · `durationUltraSlow`  
**Curves:** `curveLinear` · `curveEasyEase` · `curveEasyEaseMax` · `curveDecelerateMax` · `curveDecelerateMid` · `curveDecelerateMin` · `curveAccelerateMax` · `curveAccelerateMid` · `curveAccelerateMin`

### Create Resource pattern fix

The direct Azure Pattern Templates node for Create Resource (`6672:54683`) is not implementable as direct Figma parity: it is a 0x0 wrapper containing only `.Design System Update Notice` (`6744:54790`). The standalone library therefore provides `CreateResourcePattern` as a pragmatic derived pattern from cached Forms, Step Wizard, Browse Resource, and Manage Resource source reference. Its provenance is `derived-from-related-patterns`; revisit the direct Figma node when Microsoft publishes usable Create Resource anatomy.
