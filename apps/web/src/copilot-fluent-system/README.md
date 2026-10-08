# Azure Fluent System module

Reusable Azure Fluent-style React components and patterns built on `@fluentui/react-components` v9. Import product UI from `apps/web/src/copilot-fluent-system` while this repository does not have a packages workspace. The module is isolated so it can move later to a standalone package such as `@org/copilot-fluent-system`.

```tsx
import {
  AzureFluentProvider,
  BladeHeader,
  ServiceMenu,
  AzureDataGrid,
  CopilotComposer,
} from './copilot-fluent-system';
```

`index.ts` imports `tokens.css` as a side effect, so normal app bundles pick up the CSS automatically. If a downstream build strips CSS side effects or copies individual modules instead of the barrel, include the stylesheet once near the app root:

```ts
import './copilot-fluent-system/tokens.css';
```

## Public surface vs. dev-only showcase

The barrel (`index.ts`) is the **only** public entry point. It exports library
symbols exclusively — types, provider, icons, components, foundations, and
patterns — and imports `tokens.css` as a side effect. It deliberately does
**not** re-export `./showcase`, which is a dev-only preview app bundled with a
large catalog dataset. Keeping the showcase out of the barrel avoids a circular
dependency and prevents the showcase + catalog from leaking into product
bundles. If tooling or tests need the showcase, import it directly from
`./copilot-fluent-system/showcase/AzureFluentShowcaseApp`.

## Checked-in catalog and examples

The library now owns a compact checked-in catalog so other agents can work locally without reopening Figma:

- `apps/web/src/copilot-fluent-system/DESIGN.md`
- `apps/web/src/copilot-fluent-system/catalog/COMPONENTS.md`
- `apps/web/src/copilot-fluent-system/catalog/PATTERNS.md`
- `apps/web/src/copilot-fluent-system/catalog/ICONS.md`
- `apps/web/src/copilot-fluent-system/COVERAGE.md`
- `apps/web/src/copilot-fluent-system/REBUILD-GUIDE.md`
- `apps/web/src/copilot-fluent-system/examples/README.md`
- `apps/web/src/copilot-fluent-system/examples/*.example.tsx`
- `apps/web/src/copilot-fluent-system/showcase/README.md`

Use `DESIGN.md` as the portable design-system addendum, `catalog/COMPONENTS.md` for component inventory and local mappings, `catalog/PATTERNS.md` for pattern inventory and design guidance, `catalog/ICONS.md` for icon inventory and asset mappings, `examples/` for focused samples, and `showcase/` for the standalone component, pattern, and icon browser.

For the two cross-cutting root docs: read [`COVERAGE.md`](./COVERAGE.md) for the honest high-fidelity-vs-placeholder coverage ledger and how fidelity is guaranteed, and [`REBUILD-GUIDE.md`](./REBUILD-GUIDE.md) for the end-to-end process of pointing an LLM at this library to rebuild the Agentweaver app (including a worked `CoordinatorRunPage` recipe).

## Component catalog snapshot

Review [`catalog/COMPONENTS.md`](./catalog/COMPONENTS.md) for the durable Azure UI Kit → local implementation catalog. It is a checked-in component inventory and mapping table, not exhaustive high-fidelity implementation coverage.

| Snapshot | Count |
| --- | --- |
| Inventory components/components sets | 148 |
| Exact name/node audit | 104 covered / 44 missing |
| implemented-rendered | 25 |
| needs-mcp-extraction | 45 |
| showcase-placeholder | 78 |
| needs-implementation | 0 |
| local-only-needed | 0 |

## Pattern catalog snapshot

Review [`catalog/PATTERNS.md`](./catalog/PATTERNS.md) for the durable Azure Pattern Templates → local implementation catalog.

| Snapshot | Count |
| --- | --- |
| Pattern families | 8 |
| Unique tracked dev-mode nodes | 25 |
| rich-context | 1 |
| page-index-only | 1 |
| component-inventory | 6 |

## Icon catalog snapshot

Review [`catalog/ICONS.md`](./catalog/ICONS.md) for the durable IconCloud → local asset/import catalog.

| Snapshot | Count |
| --- | --- |
| Vendored Azure icon collections | 27 |
| Raw visible icon exports | 1637 |
| Unique checked-in SVG assets | 1441 |
| Duplicate alias payloads | 196 |

## Local-first downstream workflow

Downstream agents should be able to consume this library without Figma MCP.

1. Inspect the checked-in showcase, examples, and catalog files first.
2. Read `DESIGN.md` as the package-local design contract.
3. Use `catalog/COMPONENTS.md`, `catalog/PATTERNS.md`, and `catalog/ICONS.md` to find exports, examples, implementation files, coverage notes, and optional design references.
4. Import primitives from `apps/web/src/copilot-fluent-system`, then compose the target pattern with the checked-in React + CSS sources.
5. Treat Figma dev-mode URLs as traceability citations only.
6. Use Figma MCP only if it is available and you are intentionally refreshing the catalog or investigating a gap.

## Sanitized Portal capture note

A sanitized July 2026 Azure Portal DOM/computed-style capture refreshed the local token guidance. The reusable deltas are intentionally structural only: 13px Segoe UI density, neutral foreground near `rgb(41 40 39)`, Azure blue near `rgb(0 120 212)`, a 40px top bar, 32px command/list rows, flat white surfaces, thin neutral borders, and compact flyout/list anatomy. No visible text, screenshots, tenant/resource identifiers, emails, secrets, or customer data are checked in.

## Showcase app

`apps/web/src/copilot-fluent-system/showcase/` is the library-local standalone showcase. It has three primary experiences: a component preview, a pattern example browser, and an icon browser. It is self-contained for downstream projects: the checked-in catalogs, examples, React components, CSS tokens, and local assets are enough for ordinary usage without Figma MCP. Run it from `apps/web/src/copilot-fluent-system/` with `npm run showcase:dev`, which serves the standalone app at `http://127.0.0.1:4174/`.

The root project design guidance still lives in the repository `DESIGN.md`, but this library also ships a portable addendum in `./DESIGN.md`. Downstream projects should copy or merge the library-local addendum so the component, pattern, token, icon, and anti-rule guidance stays close to the package.
