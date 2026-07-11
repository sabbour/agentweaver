---
name: "Copilot Fluent System"
description: "Copilot warm-monochrome design language for dense product UI, implemented on Fluent 2 (React v9)."
register: product
colors:
  canvas: "#faf8f5"
  sidebar: "#f5f2ec"
  surface: "#fcfcfa"
  ink: "#242424"
  ink-secondary: "#3c3c3c"
  ink-tertiary: "#707070"
  primary: "#242424"
  primary-foreground: "#faf8f5"
  selected: "#f3f1ed"
  subtle: "#f5f5f5"
  border: "#dedede"
  border-ring: "rgb(36 36 36 / 10%)"
  success: "#16a149"
  danger: "#a62147"
typography:
  family: "Segoe UI, ui-sans-serif, system-ui, -apple-system, Roboto, Helvetica Neue, sans-serif"
  title:
    fontSize: "24px"
    fontWeight: 600
    lineHeight: "30px"
  subtitle:
    fontSize: "16px"
    fontWeight: 600
    lineHeight: "22px"
  body:
    fontSize: "14px"
    fontWeight: 400
    lineHeight: "20px"
  label:
    fontSize: "12px"
    fontWeight: 400
    lineHeight: "16px"
rounded:
  control: "8px"
  card: "10px"
  panel: "16px"
  full: "9999px"
spacing:
  xxs: "4px"
  xs: "8px"
  sm: "12px"
  md: "16px"
  lg: "20px"
  xl: "24px"
---

# Copilot Fluent System — usage contract

## 1. Overview

**Creative North Star: "The calm workbench."**

Copilot Fluent System is the product UI contract for dense operator apps. It
renders dense,
authenticated operator work — projects, orchestrations, agent runs, approvals,
skills, observability — in the **Microsoft Copilot** visual language: warm,
quiet, monochrome, and soft-cornered, with the tool disappearing into the task.

It is **not** the Azure Portal, and the consuming app is **not** an Azure
service. We
borrow Copilot's *look and feel*, not Azure chrome and not Azure-service
surfaces (no subscriptions, tenant scopes, waffle nav, or "Microsoft Azure"
branding).

**Implementation:** the system wraps and composes `@fluentui/react-components`
v9 and exposes everything through one barrel (`index.ts`). Fluent is the
*vehicle*; the *design language* is Copilot. We keep our compact, product
density — Copilot's desktop app is spacious, we are a denser web tool.

**Source of truth for the visual language:**
- The Microsoft "M" / Scout desktop app design tokens — `github.com/gim-home/m`,
  `src/index.css` (shadcn theme vars mapped to Figma design tokens). This is the
  authoritative palette/radius/type source; see §10.
- `copilot.com` live computed styles, used as a sanity check.

**The Barrel-Only Rule.** Product code imports from
`apps/web/src/copilot-fluent-system` through `index.ts`. Never import from
`showcase/`, `catalog/`, or `examples/` in product bundles.

**The Provider Rule.** Wrap each product surface once with `AzureFluentProvider`
(the provider name is retained for import stability). Use `density="compact"`
for dense blades, grids, and run views.

**The Light-Only Rule.** Light mode only for now. Do not add a dark theme until
it is explicitly requested; keep tokens dark-ready but do not build dark.

## 2. Colors

The palette is **warm-neutral monochrome**. Warm off-white surfaces carry the
experience; ink is a near-black neutral; there is **no blue accent**. The only
saturated colors are the Copilot gradient logo (identity only), semantic green
(success / toggles), and semantic crimson (danger).

### Tokens (authoritative, from M `src/index.css` light mode)

| Role (M Figma token) | Value | Used for |
| --- | --- | --- |
| surface-default (`--background`, `--sidebar`) | `rgb(250 248 245)` | warm paper canvas |
| sidebar (slightly darker) | `rgb(245 242 236)` | left rail + shell frame |
| surface-subtle (`--card`) | `rgb(252 252 250)` | cards, blades, flyouts |
| foreground-primary (`--foreground`, `--primary`) | `rgb(36 36 36)` | ink + primary buttons |
| foreground-secondary | `rgb(60 60 60)` | strong secondary text |
| foreground-tertiary (`--muted-foreground`) | `rgb(112 112 112)` | metadata, helper text |
| bg-subtle (`--secondary`, `--muted`) | `rgb(245 245 245)` | subtle fills |
| brand-subtle (`--accent`) | `rgb(243 241 237)` | hover / selected fill |
| border-subtle (`--border`) | `rgb(222 222 222)` | dividers, control chrome |
| border-ring (M `ring-foreground/10`) | `rgb(36 36 36 / 10%)` | soft card/blade ring |
| success (`--chart-3`) | `rgb(22 161 73)` | switch "on", success |
| danger (`--destructive`) | `rgb(166 33 71)` | destructive text/tint |

### Named rules

- **The No-Blue Rule.** There is no blue accent anywhere. Primary actions,
  links, focus, and selection are near-black / warm-neutral. If blue appears,
  something is drifting back toward the Azure theme.
- **The Near-Black Primary Rule.** Primary buttons use `--foreground`
  (`rgb(36 36 36)`) with warm-paper text; hover lightens (`--primary/80` feel);
  press nudges to black. Links are near-black + underline on hover.
- **The Soft-Ring Rule.** Flat surfaces (cards, blades, summary/property cards)
  use a `1px` `rgb(36 36 36 / 10%)` ring, not a hard neutral border. Hard
  `border-subtle` is for dividers and control chrome only.
- **The Warm-Neutral Legibility Rule.** Body text stays near ink on warm
  surfaces; keep ≥4.5:1. Never lighten body text for elegance.
- **The Semantic-Only Saturation Rule.** Green and crimson appear only as
  status (success/toggle, danger). The Copilot gradient is identity only, never
  decoration.

## 3. Typography

**Family:** Copilot uses `Ginto`; M uses `Segoe Sans`; both fall back to
`ui-sans-serif, system-ui`. We ship **Segoe UI** as the licensed stand-in with a
system fallback. One family, multiple weights — no display or serif pairing.

**Density:** Copilot's desktop body is airy (16px/26px). We run denser: base
**14px / 20px**. Headings are **fixed** (not fluid `clamp()`), so blades, grids,
and panels stay predictable.

### Hierarchy

- **Title** (600, 24px/30px): blade titles.
- **Subtitle** (600, 16px/22px): section headings.
- **Body-strong** (600, 14px/20px): table headers, field emphasis.
- **Body** (400, 14px/20px): default product text, rows, command labels.
- **Label** (400, 12px/16px): metadata, helper, secondary nav.

### Named rules

- **One-Family Rule.** No display fonts, serif accents, or brand-page pairings.
- **Fixed-Scale Rule.** No fluid `clamp()` headings on product surfaces.
- **Plain-Copy Rule.** Rendered UI uses plain customer language. No invented
  marketing copy, no Azure-service jargon, no tooling/implementation terms in
  primary UX.

## 4. Elevation & shape

Flat and soft. Depth comes from tonal layering, soft rings, the floating content
panel, selected states, and real flyout shadows — not decorative glows or
gradients.

- **Radius:** control `8px`, card `10px`, shell content panel `16px`, pills
  `9999px` (chips, floating actions, avatars).
- **Shadow vocabulary:**
  - Flat surface (`box-shadow: none`) — default for cards, grids, forms.
  - Soft float — the content panel and floating pills use a soft, low shadow
    (`0 1px 2px / 0 4px 16px` at ~4–10% ink).
  - Flyout shadow — popovers, dialogs, drawers, panes only.
- **No decorative gradients / glassmorphism / hero-metric templates.**

## 5. Layout & chrome

Copilot single-surface shell. **There is no top bar.**

- **One left rail** on the slightly-darker warm sidebar, borderless. Grouped nav
  (heading + items), fully-rounded warm hover/selected fill (no accent bar).
- **Rail header slot:** project/scope switcher + the operator-dock trigger.
- **Rail footer slot:** the signed-in persona menu, pinned to the bottom.
- **Content is a lighter rounded panel**, inset on all sides (radius `16px`,
  soft shadow), floating on the sidebar tone — no hard divider.
- **Primary "new work" action floats top-right** as a warm-white pill with a
  soft glow (Copilot "Temporary"-button placement), not in a top bar.
- **Per-blade anatomy:** breadcrumb (optional) → flat title → flat command bar
  (icon+text row, thin rule) → optional filter row → task body → optional
  footer. No boxed header card, no decorative resource icon.

## 6. Components

All public UI comes from the barrel. Source modules: `provider.tsx`,
`components.tsx`, `patterns.tsx`, `icons.tsx`, `foundations.tsx`, `types.ts`.

- **Shell:** `PortalLayout` (app shell + rounded content panel), `PortalRail`
  (single rail with header/footer slots), `ServiceMenu`.
- **Header/command:** `BladeHeader`, `CommandBar` / `PortalCommandBar` /
  `AzureToolbar` / `DataToolbar` (flat command strips), `FilterBar`, `Pager`.
- **Data & status:** `AzureDataGrid`, `AzureEmptyState`, `AzureSummaryCard`
  (icon + title + right-aligned metric rows), `AzurePropertyList` (uppercase
  label + value rows), `StatusIconText`, `ProgressBarWithLabel`, badges.
- **Forms:** `FormFieldRow`, `AzureForm`, `FormFooter`, `FileUpload`,
  `FilterableComboBox`, `AzureSlider`, `ResourceTagEditor`.
- **Copilot & automation:** `CopilotComposer`, `CopilotResponse`,
  `InlineCopilot`, `CopilotPromptRibbon`, `ArtifactPill`, `AgenticProgress`,
  `ChainOfThought`, plus patterns (`CopilotWorkspacePattern`,
  `AgenticApprovalPattern`, `CoordinatorRunPattern`).
- **Icons:** `AzureIcon` / `AzureIconProvider` and Fluent icon exports.

Every interactive component ships default, hover, focus-visible, active,
disabled, and (where relevant) loading and error states. Buttons nudge
`translateY(1px)` on press; subtle/ghost hover is a ~6% ink tint. Reduced-motion
safe.

## 7. Patterns

Use `patterns.tsx` when a task is larger than one component. Patterns are thin
compositions over public components and must not introduce a second visual
language.

- **Browse:** `BrowseResourcePattern` / `FilteringPattern` — toolbar + filters +
  grid + empty/loading/error + pager.
- **Manage:** `ManageResourcePattern` — service menu + management content.
- **Create/forms:** `FormBladePattern`, `StepWizardPattern`,
  `CreateResourcePattern` — staged create with validation and review.
- **Overview:** compose `AzureSummaryCard` + `AzurePropertyList` in a card flow
  (varied sizes, not a rigid equal-tile grid) for dashboard/overview blades.
- **Copilot:** `CopilotWorkspacePattern`, `CoordinatorRunPattern`,
  `AgenticApprovalPattern`, `CopilotTriagePanelPattern`.
- **Messages:** `NotificationPattern` / `ErrorPattern` — actionable message +
  next step, inline, not modal walls.

### The Page-Pattern Rule (M list is the default, Azure grid is the exception)

Item-centric pages (workflows, projects, orchestrations, skills, agents,
memories) follow the **M "list" pattern**, not the Azure dense command strip:

1. **Header:** title on the left; on the right, **one primary action**
   (near-black) plus optional **subtle** secondary actions (Import, Refresh). Not
   a long command strip.
2. **Filter row** (only when the list is long enough to need it): **segmented
   tabs** (All / Active / Paused) + a **functional** search. Never a dead search.
3. **Content = a borderless rich-row list.** Each row: leading status dot, bold
   title + inline status, a description line, a meta line (`schedule · N steps`),
   and **per-row actions revealed on hover** (run/pause, edit, ⋯). Hairline
   separators, generous row height, warm hover fill.

Reserve the **Azure dense command bar** (`+ Create · Manage View · Refresh ·
Export · Delete … Group by`) only for a true multi-select data grid with bulk
operations. Most Agentweaver surfaces are item-centric and must use the list
pattern — a wall of command buttons is intimidating and cold, which we reject.

## 8. Motion

- 150–250 ms on most transitions; motion conveys state, not decoration.
- Button press `translateY(1px)`; hover fills/lightens.
- No orchestrated page-load choreography. Every animation has a
  `prefers-reduced-motion` alternative.

## 9. Do's and Don'ts

### Do
- Import product UI only from the barrel; keep `tokens.css` loaded once.
- Preserve the single-rail shell, floating content panel, and bottom persona.
- Use near-black primary, warm-neutral surfaces, soft rings, and `--azf-*` /
  theme tokens before adding any value.
- Keep browse/filter/grid as toolbar → filters → grid → empty/loading/error →
  pager, at compact density.
- Keep every control wired to real behavior and real data.

### Don't
- **Don't** reintroduce blue accents, Azure Portal chrome, or Azure-service
  surfaces (subscriptions, tenant scope, waffle nav, "Microsoft Azure").
- **Don't** add a top bar. There is no top bar.
- **Don't** ship non-functional UI (dead search boxes, decorative filters,
  fake affordances).
- **Don't** invent marketing copy or expose tooling/implementation terms in
  primary UX.
- **Don't** paste M/shadcn (Tailwind + Radix) markup into the library. Borrow
  the *vocabulary*, keep our Fluent primitives at our density.
- **Don't** use decorative gradients, glassmorphism, hero-metric dashboards,
  side-stripe borders, or gradient text.
- **Don't** build a dark theme until explicitly requested.

## 10. Source & traceability

- **`github.com/gim-home/m`** — `src/index.css` (authoritative light-mode
  tokens, Figma-mapped), `src/components/ui/*` (shadcn primitives),
  `src/features/chat/components/*` (chat surface). Traceability only; downstream
  work must not require access to this repo.
- **`copilot.com`** — live computed styles used to sanity-check palette, type,
  and radius.
- Local sources of truth remain the checked-in `tokens.css`, `components.tsx`,
  `patterns.tsx`, `catalog/`, `examples/`, and `showcase/`.

## 11. Component gap analysis (M → us)

Patterns M ships that we do not yet have, with web-density adaptation notes.
Candidates, not commitments — build on Fluent at our density when a real surface
needs them, then document in `catalog/` and surface in the showcase.

| M component | Have equivalent? | Gap / adaptation note |
| --- | --- | --- |
| `command` (Cmd/Ctrl-K palette) | No | High value; the *functional* replacement for the removed fake search — a real command/resource palette. Keyboard-first, compact. |
| `kbd` / `ShortcutKbd` | No | Shortcut hints in menus, command bars, tooltips. Small, high leverage for a power-user tool. |
| `segmented-control` | Partial (Tabs) | Compact toggle for 2–4 mutually exclusive dense views; better than tabs in tight spaces. |
| `hover-card` | No | Rich hover previews for resource rows without navigating. |
| `WelcomeState` + `ChatInput` | Partial (`CopilotComposer`/`CopilotResponse`) | The Operator dock should become a full chat surface: greeting + large composer + suggestion chips. Tracked redesign. |
| `ModelPicker` / `PersonalityPicker` | No | Inline composer-footer pickers → orchestration mode + workflow selectors. |
| `PermissionCard` / `InlineQuestion` | Partial (`AgenticApprovalPattern`) | Richer human-in-the-loop gate + inline-question cards for the Coordinator. |
| `EntityPill` / `AttachmentPillList` / `SkillSlashMenu` | No | Composer affordances: @-entity pills, attachment pills, slash menu. |
| `section-header` / `panel-header` | Partial (`BladeHeader`) | Lighter sub-section header (title + optional action + thin rule) for dense content. |
| Fluent primitives (input, textarea, select, switch, dialog, popover, menu, tabs, tooltip, avatar, badge, progress, skeleton, separator, scroll-area, toast) | Yes | Already covered by Fluent v9 wrappers. Keep our compact versions; do not re-import shadcn equivalents. |
