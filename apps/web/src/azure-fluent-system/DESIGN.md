# Azure Fluent System — usage contract

How to build product UI with this library. It is an Azure Portal / Fluent 2
component system built on `@fluentui/react-components` v9. Treat it as a product
UI system, not a demo kit: dense, task-first, neutral-first, explicit actions,
minimal decoration.

## 1. Import

The barrel is the only public entry point. Import library symbols from it:

```tsx
import {
  AzureFluentProvider,
  BladeHeader,
  ServiceMenu,
  AzureDataGrid,
  CommandBar,
  CopilotComposer,
} from '@/azure-fluent-system'; // or a relative path while there is no packages workspace
```

Do not import from `./showcase` in product code — it is a dev-only preview app
plus a large catalog dataset and is intentionally excluded from the barrel.

## 2. Provider setup

Wrap every product surface once, near the app root, in `AzureFluentProvider`.
It applies the Fluent theme, the `azf-theme` token scope, and the density class,
and imports `tokens.css` as a side effect.

```tsx
<AzureFluentProvider density="cozy">
  <App />
</AzureFluentProvider>
```

- `density` — `'cozy'` (default) or `'compact'`. Use `compact` for dense data
  surfaces (grids, blades with many rows).
- `theme` — defaults to `webLightTheme`. Pass a Fluent `Theme` to override.
- All other `FluentProvider` props pass through.

## 3. Tokens and styling

Style with tokens, never hardcoded values.

- Use Fluent v9 design tokens (`var(--colorNeutralForeground1)`,
  `var(--spacingHorizontalM)`, etc.) and the library's `azf-` tokens/utility
  classes (`.azf-surface`, `.azf-stack`, `.azf-row`) defined in `tokens.css`.
- All `azf-` tokens are backed by Fluent tokens, so they follow the active
  theme automatically. There are zero hardcoded colors in the library.
- Radii come from named tokens (`--azf-radius-control`, `--azf-radius-panel`,
  `--azf-radius-small`); do not introduce raw `px` radii.
- Do not copy Tailwind or raw Figma export output into product code.

## 4. Component vocabulary

Compose from existing primitives before inventing new APIs:

- **Shell / chrome:** `BladeHeader`, `ServiceMenu`, `CommandBar`, breadcrumb,
  tab lists, footers.
- **Data:** `AzureDataGrid`, dense tables, status surfaces, empty states.
- **Forms:** `Field`, `Input`, `Label`, compact form rows, step lists. Keep
  form columns narrow inside wide shells — no full-width slabs.
- **Feedback:** `MessageBar`, notifications (pane-based, contextual, non-modal),
  dialogs (confirmation-gated, restrained danger styling).
- **Copilot:** `CopilotComposer`, `CopilotResponse`, `CopilotWorkspacePattern`,
  `AgenticProgress`, `ChainOfThought` (reasoning panel with Activity/Artifacts
  tabs, an actions-completed summary, and approval-gated steps).
- **Primitives:** Fluent re-exports (`Button`, `Card`, `Link`, `Text`,
  `Slider`, `ProgressBar`, …) plus `patterns.tsx` for composed Azure tasks.

Browse `catalog/COMPONENTS.md` and `catalog/PATTERNS.md` for the full inventory,
exports, and examples; `examples/*.example.tsx` for focused samples.

## 5. Pattern rules

- **Create / stepped form blade:** portal shell, breadcrumb above blade title,
  horizontal numbered steps, narrow form column, docked footer.
- **Browse resource:** command bar + filters + dense grid, not card galleries.
- **Delete flows:** implication-first, confirmation-gated.
- **Service overview:** concise overview cards, not marketing hero dashboards.
- **Feedback:** lightweight prompt + local input + restrained footer action.

## 6. Do / don't

Do:
- Wrap surfaces in `AzureFluentProvider` and style with `azf-` + Fluent tokens.
- Reuse primitives and patterns; keep actions explicit and task-local.
- Use `@fluentui/react-icons` for general UI and checked-in Azure assets for
  product/resource glyphs.

Don't:
- Pull the showcase or catalog dataset into product bundles.
- Turn portal tasks into dashboard-first marketing layouts.
- Use giant dark panels, oversized cards, blurred chrome, or screenshot mosaics.
- Couple this library to app-specific routes, state containers, or session paths.

## 7. Traceability

The checked-in catalogs (`catalog/`) are the authoritative day-to-day handoff.
Figma dev-mode URLs in the catalogs are origin citations only — not a runtime,
build, or review dependency. Refresh a catalog row from Figma MCP only when
intentionally closing a gap; never rebuild from screenshots.

## Azure Fluent System design addendum

**Local-first workflow.** This library is self-contained and consumable
downstream without Figma tooling.
Do not require Figma MCP for ordinary implementation or review.
The checked-in `catalog/COMPONENTS.md`, `catalog/PATTERNS.md`, and
`catalog/ICONS.md` are the source of truth; refresh them from Figma MCP only
when intentionally re-syncing the catalog from the Azure UI Kit.
