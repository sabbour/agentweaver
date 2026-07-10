---
name: "Azure Fluent System"
description: "Task-first Azure Portal and Fluent 2 design system for dense product UI."
colors:
  portal-blue: "#0078d4"
  fluent-brand: "#0f6cbd"
  brand-hover: "#005a9e"
  foreground: "#292827"
  secondary-foreground: "#323130"
  canvas: "#ffffff"
  selected-surface: "#f3f2f1"
  border: "#d6d6d6"
  field-border: "#d1d1d1"
  footer-border: "#cccccc"
  disabled-foreground: "#bdbdbd"
  disabled-background: "#f0f0f0"
typography:
  title:
    fontFamily: "az_ea_font, Segoe UI, az_font, system-ui, -apple-system, BlinkMacSystemFont, Roboto, Helvetica Neue, sans-serif"
    fontSize: "24px"
    fontWeight: 600
    lineHeight: "32px"
  subtitle:
    fontFamily: "az_ea_font, Segoe UI, az_font, system-ui, -apple-system, BlinkMacSystemFont, Roboto, Helvetica Neue, sans-serif"
    fontSize: "16px"
    fontWeight: 600
    lineHeight: "22px"
  body:
    fontFamily: "az_ea_font, Segoe UI, az_font, system-ui, -apple-system, BlinkMacSystemFont, Roboto, Helvetica Neue, sans-serif"
    fontSize: "13px"
    fontWeight: 400
    lineHeight: "18px"
  body-strong:
    fontFamily: "az_ea_font, Segoe UI, az_font, system-ui, -apple-system, BlinkMacSystemFont, Roboto, Helvetica Neue, sans-serif"
    fontSize: "14px"
    fontWeight: 600
    lineHeight: "20px"
  label:
    fontFamily: "az_ea_font, Segoe UI, az_font, system-ui, -apple-system, BlinkMacSystemFont, Roboto, Helvetica Neue, sans-serif"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: "16px"
rounded:
  control: "4px"
  small: "2px"
  panel: "2px"
  composer: "12px"
spacing:
  xxs: "4px"
  xs: "8px"
  sm: "12px"
  md: "16px"
  lg: "20px"
  xl: "24px"
  row: "32px"
  topbar: "40px"
  tab: "44px"
components:
  button-primary:
    backgroundColor: "{colors.portal-blue}"
    textColor: "{colors.canvas}"
    rounded: "{rounded.control}"
    height: "32px"
    padding: "5px 12px"
  button-subtle:
    backgroundColor: "{colors.canvas}"
    textColor: "{colors.foreground}"
    rounded: "{rounded.control}"
    height: "32px"
    padding: "5px 12px"
  field-row:
    backgroundColor: "{colors.canvas}"
    textColor: "{colors.foreground}"
    rounded: "{rounded.control}"
    height: "32px"
    padding: "0 10px"
  portal-surface:
    backgroundColor: "{colors.canvas}"
    textColor: "{colors.foreground}"
    rounded: "{rounded.panel}"
    padding: "20px"
---

# Azure Fluent System — usage contract

## 1. Overview

**Creative North Star: "The Portal Workbench"**

Azure Fluent System is the product UI contract for product surfaces that need
to feel at home in Azure Portal and Fluent 2. It serves dense authenticated work:
resource review, automation run supervision, approval gates, configuration forms,
browse/filter grids, contextual Copilot support, and operator recovery. The
system is intentionally familiar. A user fluent in Azure Portal should trust the
interface immediately, not pause to decode invented affordances.

The visual language is restrained, flat, and task-first. White and neutral
surfaces carry most of the experience; Azure blue appears only when it
communicates action, focus, selection, link, or state. Density is a feature:
compact rows, direct labels, command bars, side navigation, and readable tables
are the default. Decorative brand-landing patterns are rejected because this is
a product UI design system, not a marketing page.

**Key Characteristics:**

- Azure Portal anatomy: top bar, rail, breadcrumb, blade header, command/filter
  region, task body, optional footer.
- Fluent 2 primitives first: the package wraps and composes
  `@fluentui/react-components` v9 instead of inventing controls.
- Local-first portability: `DESIGN.md`, `tokens.css`, catalogs, examples, and
  showcase must be enough without Figma MCP.
- Honest coverage: catalog status is traceability, not a public UX promise.
  Coverage is not fidelity.
- Consumer-facing showcase: no internal jargon, no readiness badges, no
  standalone child-slot primitives, and no screenshot-backed previews outside
  Icons.

**The Barrel-Only Rule.** Product code imports from
`apps/web/src/azure-fluent-system` through `index.ts`. Do not import from
`showcase/`, `catalog/`, or `examples/` in product bundles.

**The Provider Rule.** Wrap each product surface once with
`AzureFluentProvider`. Use `density="compact"` for portal blades, grids,
command-heavy workspaces, and agent run views; use `cozy` only when a task needs
more breathing room.

**The Local-First Rule.** Ordinary implementation and review work starts with
the checked-in docs, catalogs, examples, showcase, components, patterns, and
tokens. Figma dev-mode URLs are citations unless intentionally refreshing
provenance or closing a design-source gap.

## 2. Colors

The palette is Azure Portal restrained: flat white work surfaces, neutral text
and borders, and one scarce Azure blue accent. Color is a state and hierarchy
tool, not decoration.

### Primary

- **Portal Blue** (`portal-blue`): primary actions, selected rails,
  focus-visible emphasis, and key links. Use sparingly enough that the user's
  eye can find the next action.
- **Fluent Brand Blue** (`fluent-brand`): upstream Fluent brand role for
  selected and brand token states when components already expose Fluent
  variables.
- **Brand Hover Blue** (`brand-hover`): command/link hover and active states
  when the interaction needs a darker blue response.

### Neutral

- **Canvas White** (`canvas`): blade bodies, tables, cards, form surfaces,
  popover bodies, and showcase preview surfaces.
- **Portal Ink** (`foreground`): default readable product text. Do not lighten
  body text for elegance.
- **Secondary Ink** (`secondary-foreground`): metadata, descriptions, helper
  labels, and compact secondary rows.
- **Selected Surface** (`selected-surface`): restrained selected row, nav item,
  or rail background.
- **Portal Border** (`border`): standard dividers, card outlines, grid strokes,
  and shell separation.
- **Field Border** (`field-border`): inputs and form-control chrome.
- **Footer Border** (`footer-border`): docked footer and command-region
  separators.
- **Disabled Pair** (`disabled-foreground`, `disabled-background`): inactive
  controls and unavailable actions.

### Semantic color

Use Fluent semantic tokens for success, warning, danger, error, info, hover,
active, disabled, selected, and focus states. `StatusIconText`, message bars,
progress, badges, and confirmation flows should expose state through standard
Fluent semantics and compact text. Do not invent a second status palette.

### Named Rules

**The Blue Scarcity Rule.** Blue belongs to primary action, link, focus,
selected, and state indicator roles only. If blue is used as background
decoration, the surface is already drifting.

**The No Raw Color Rule.** New product styling uses Fluent tokens or `--azf-*`
tokens. Literal colors may remain in catalogs, extracted notes, SVG assets, or
documented token definitions; they must not appear as one-off component styling.

**The Neutral Legibility Rule.** Body text stays near Portal Ink on light
surfaces. Gray-on-blue or pale-gray-on-tinted-white fails unless contrast
remains at least 4.5:1.

## 3. Typography

**Display Font:** none. Product surfaces use the same UI family at larger
Fluent sizes.
**Body Font:** `az_ea_font`, Segoe UI, `az_font`, system UI fallback.
**Label/Mono Font:** labels use the UI family; code snippets may use the app's
existing monospace token inside `CodeSnippet`.

**Character:** compact, readable, and system-native. Typography should
disappear into the task while preserving Azure Portal rhythm.

### Hierarchy

- **Title** (600, 24px, 32px): blade titles, selected resource headings, and
  major pattern headers.
- **Subtitle** (600, 16px, 22px): section headings, breadcrumb-adjacent labels,
  and compact overview headings.
- **Body Strong** (600, 14px, 20px): table headers, field emphasis, selected
  labels, and command group labels.
- **Body** (400, 13px, 18px): dense portal regions, grid cells, command labels,
  browse rows, and default product text.
- **Label** (400, 12px, 16px): metadata, helper copy, status descriptions, and
  secondary nav text.

### Named Rules

**The One UI Family Rule.** Do not add display fonts, serif accents, or
brand-page type pairings. Product UI earns trust through standard Segoe/Fluent
typography.

**The Fixed Scale Rule.** Do not use fluid `clamp()` headings for product
surfaces. Use fixed Fluent/Portal sizes so blades, grids, and side panels remain
predictable.

**The Copy Boundary Rule.** Rendered showcase and product UI use plain customer
language. Catalog status, file paths, node IDs, extraction terms, MCP/tooling
names, and implementation references belong in docs or collapsed developer
reference, not in primary UX.

## 4. Elevation

Azure Fluent System is flat by default. Depth comes from tonal layering,
borders, sticky regions, selected states, and flyout shadows only where a layer
must float above the work surface. Cards are not the default answer; use them
when content is a true contained object such as an overview item, approval
checkpoint, output artifact, or focused sample.

### Shadow Vocabulary

- **Flat Surface** (`box-shadow: none`): default for blades, grids, forms,
  command bars, overview items at rest, and showcase preview surfaces.
- **Flyout Shadow** (`0 6.4px 14.4px rgb(0 0 0 / 13.3%), 0 1.2px 3.6px
  rgb(0 0 0 / 11%)`): notification panes, popovers, settings panes, and true
  overlay layers.
- **Fluent Shadow 16** (`var(--shadow16)`): upstream Fluent overlay components
  when the primitive already owns elevation.

### Named Rules

**The Flat Workbench Rule.** A blade or grid at rest has a border and neutral
background, not a glow. If everything is elevated, nothing is actionable.

**The Real Overlay Rule.** Shadows are reserved for flyouts, popovers, dialogs,
drawers, and panes that spatially sit above the current task.

**The No Nested Cards Rule.** Do not wrap a whole page in a card and then place
more cards inside it. Keep the content region flat and use sections, dividers,
toolbars, and tables for structure.

## 5. Components

All public UI comes from the package barrel. The source modules are
`provider.tsx`, `components.tsx`, `patterns.tsx`, `icons.tsx`,
`foundations.tsx`, and `types.ts`; `showcase/`, `catalog/`, and `examples/` are
documentation and verification surfaces only.

### Provider and tokens

- **Provider:** `AzureFluentProvider` applies Fluent theme, density, and the
  `.azf-theme` token scope.
- **Token class:** `.azf-theme` defines Portal density,
  `--azf-service-menu-width` (264px), `--azf-form-label-width`
  (`clamp(168px, 22vw, 220px)`), `--azf-grid-row-min-height` (32px),
  `--azf-portal-topbar-height` (40px), `--azf-tab-height` (44px),
  `--azf-control-icon-size` (20px), and `--azf-entity-icon-size` (32px).
- **Utility classes:** `.azf-stack`, `.azf-row`, `.azf-wrap`, `.azf-gap-xs`,
  `.azf-gap-s`, `.azf-gap-m`, `.azf-surface`, `.azf-muted`, and
  `.azf-linkish` are approved composition helpers.
- **Measured values:** 264px service menu, 32px rows, 44px tabs, 52px rail,
  728/770px form constraints, icon sizes, and the 2.2734375px blade-header
  divider are allowed when centralized as tokens or isolated component CSS and
  tied to recurring Azure/Fluent anatomy.

### Buttons and command surfaces

- **Shape:** compact rounded Fluent controls (4px), 32px high in compact
  density.
- **Primary:** blue only for the dominant action. Destructive actions stay gated
  and typically use outline/confirmation treatments before execution.
- **Secondary/subtle:** neutral background, neutral text, thin border or Fluent
  subtle treatment.
- **Command bars:** `CommandBar`, `PortalCommandBar`, `AzureToolbar`, and
  `DataToolbar` are flat command strips. They should sit near the object they
  act on.
- **States:** default, hover, focus-visible, active, disabled, loading, and
  destructive states must be present through Fluent props or local tokens.

### Inputs and forms

- **Field rows:** `FormFieldRow`, `AzureForm`, `FormFooter`, `FileUpload`,
  `FilterableComboBox`, `AzureSlider`, and `ResourceTagEditor` preserve Azure's
  label-column rhythm.
- **Form width:** create/edit forms stay narrow inside wide blades. Common
  anchors are 728px form content and 168-220px labels.
- **Validation:** error/help text stays adjacent to the field or review step.
- **Footers:** primary and secondary actions sit in `FormFooter`; do not move
  Next/Previous above the form body.
- **Wizard use:** `StepWizardPattern` and `CreateResourcePattern` are for staged
  create or irreversible review flows only, not routine settings.

### Navigation and shell

- **Shell order:** `PortalTopNav`, optional `PortalRail`, breadcrumb row,
  `BladeHeader`, command/filter region, task body, optional footer.
- **Navigation:** `ServiceMenu` groups service navigation; `PortalRail` handles
  high-level destinations. Selected state is a restrained surface plus rail, not
  a decorative gradient.
- **Header:** `BladeHeader` uses compact horizontal padding, title/menu lockup,
  resource icon, optional command row, and pinned close/action controls.
- **Responsive:** collapse structure deliberately. Keep commands and selected
  state discoverable instead of shrinking type fluidly.

### Data, status, and feedback

- **Browse stack:** `DataToolbar` + `FilterBar` + `AzureDataGrid` + `Pager` stay
  visually adjacent.
- **Empty/loading:** `AzureEmptyState` explains recovery or filter reset;
  skeletons mirror content. Avoid spinners floating in the middle of a blank
  surface.
- **Status:** `StatusIconText`, `ProgressBarWithLabel`, `NotificationPane`,
  message bars, badges, and progress controls use semantic state and compact
  labels.
- **Notifications:** `NotificationPattern` and `NotificationPane` are
  contextual panes or inline updates, not modalized notification walls.

### Copilot and automation

- **Family:** `CopilotComposer`, `CopilotResponse`, `InlineCopilot`,
  `CopilotPromptRibbon`, `ArtifactPill`, `AgenticProgress`, `ChainOfThought`,
  `CopilotWorkspacePattern`, `AgenticApprovalPattern`,
  `CopilotTriagePanelPattern`, `CoordinatorRunPattern`, and
  `ResourceOperationHeaderPattern` form one coherent automation vocabulary.
- **Copy:** frame these as Copilot help, run activity, generated outputs,
  approvals, and recovery. Do not expose hidden reasoning as product value.
- **Reviewability:** approval checkpoints must show the consequence, artifact,
  next action, and gating state clearly.

### Icons

- **AzureIconProvider and AzureIcon:** use registered Azure resource glyphs,
  direct image sources, and Fluent icon fallbacks.
- **Registry helpers:** `useAzureIconRegistry`, `createIconCloudRegistry`, and
  `createIconCloudRegistryFromManifest` are helper-only/non-visual and should be
  documented rather than rendered as standalone cards.
- **Icon previews:** Icons may use local image-backed SVG assets because
  `catalog/ICONS.md` and vendored assets are the icon source. Other public
  previews should not use arbitrary screenshots or image-backed mockups.

### Patterns and catalogs

- **Patterns:** use `patterns.tsx` when the task is larger than a single
  component. Patterns are thin compositions over public components and must not
  introduce a second visual language.
- **BrowseResourcePattern / FilteringPattern:** resource finding, filtering,
  grids, empty/loading/error, and paging.
- **ManageResourcePattern:** service navigation plus management forms, grids,
  accordions, and routine settings.
- **FormBladePattern / StepWizardPattern / CreateResourcePattern:** staged
  create work with validation and review.
- **NotificationPattern / ErrorPattern:** actionable messages paired with next
  steps.
- **ServiceOverviewPattern:** service health, recommendations, and next actions;
  no hero-metric dashboards.
- **DeleteResourceDialog / DeleteConfirmationDialog:** consequence review before
  destructive action.
- **Catalogs:** `catalog/COMPONENTS.md`, `catalog/PATTERNS.md`, and
  `catalog/ICONS.md` are traceability layers. They may include Figma URLs, node
  IDs, extraction status, MCP notes, and source terminology; the primary
  showcase UI must not.
- **Catalog refresh:** Refresh a catalog row from Figma MCP only when intentionally updating provenance or closing a design-source gap. Ordinary implementation, review, and downstream consumption should work from local files.

### Showcase and examples

- **Components view:** every public Azure Fluent UI Kit component has a visible
  representation, is grouped under a visible parent preview, or is explicitly
  helper-only/non-visual.
- **Patterns view:** task-focused flows and composed scenarios belong here.
- **Icons view:** searchable local Azure icon examples and vendored icon set.
- **Examples directory:** source-level API guidance. Do not duplicate the same
  flow as both a public component card and a pattern scenario unless it serves a
  distinct use case.
- **Public showcase rules:** primary UI must not display status/readiness chrome
  such as "Live preview", "Related preview", "Design review", "Planned preview",
  or "Review planned". Use focused tests, build, browser QA, and this design
  contract as the normal package quality bar.

## 6. Do's and Don'ts

### Do:

- **Do** import product UI only from the Azure Fluent System barrel and keep
  `tokens.css` loaded once through `index.ts`.
- **Do** wrap product surfaces with `AzureFluentProvider` and choose compact
  density for portal-like work.
- **Do** preserve Portal anatomy: top bar, rail, breadcrumb, blade header,
  command/filter region, task body, and optional footer.
- **Do** use Fluent tokens and `--azf-*` tokens before adding a new value.
- **Do** keep browse/filter/grid tasks as toolbar, filters, grid,
  empty/loading/error, and pager.
- **Do** document helper-only exports as helper-only instead of inventing public
  visual cards.
- **Do** keep Copilot and automation controls grouped so prompt, response,
  artifact, progress, reasoning, and approvals read as one family.
- **Do** keep catalogs honest: distinguish implemented-rendered,
  needs-mcp-extraction, showcase-placeholder, needs-implementation,
  local-only-needed, and not-in-inventory.
- **Do** run focused tests, build, and browser QA after changing docs, catalogs,
  source, examples, or preview visibility.

### Don't:

- **Don't** import `showcase/`, `catalog/`, or `examples/` into product runtime
  code.
- **Don't** add status chips, live-preview pills, design-readiness badges,
  catalog status values, file paths, node IDs, or tooling terms as primary
  showcase content.
- **Don't** expose child slot primitives as standalone public cards when they
  only make sense inside a larger component.
- **Don't** use arbitrary screenshot/image-backed previews outside Icons.
- **Don't** use non-Azure or non-Fluent decorative language, decorative
  gradients, glass panels, giant dark surfaces, screenshot mosaics, or
  hero-metric dashboards.
- **Don't** replace resource browse/filter/grid flows with marketing card
  galleries.
- **Don't** use side-stripe borders (`border-left` or `border-right` greater
  than 1px) as accent decoration.
- **Don't** use gradient text, default glassmorphism, repeated identical
  icon-card grids, or tiny uppercase tracked eyebrows as section scaffolding.
- **Don't** invent modals, pickers, tabs, status treatments, custom scrollbars,
  or form controls when Fluent primitives already solve the task.
- **Don't** claim high fidelity for nodes marked needs-mcp-extraction,
  showcase-placeholder, needs-implementation, local-only-needed, or
  not-in-inventory.
- **Don't** rebuild product UI from screenshots or raw generated Figma code. Use
  MCP only for intentional refresh work, then map results into local React,
  tokens, catalogs, examples, and showcase.
