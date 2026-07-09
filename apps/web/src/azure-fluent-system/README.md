# Azure Fluent System module

Reusable Azure Fluent-style React components and patterns built on `@fluentui/react-components` v9. Import from `apps/web/src/azure-fluent-system` while this repository does not have a packages workspace. The module is isolated so it can move later to a standalone package such as `@org/azure-fluent-system`.

```tsx
import {
  AzureFluentProvider,
  BladeHeader,
  ServiceMenu,
  AzureDataGrid,
  CopilotComposer,
} from './azure-fluent-system';
```

## Public surface vs. dev-only showcase

The barrel (`index.ts`) is the **only** public entry point. It exports library
symbols exclusively — types, provider, icons, components, foundations, and
patterns — and imports `tokens.css` as a side effect. It deliberately does
**not** re-export `./showcase`, which is a dev-only preview app bundled with a
large catalog dataset. Keeping the showcase out of the barrel avoids a circular
dependency and prevents the showcase + catalog from leaking into product
bundles. If tooling or tests need the showcase, import it directly from
`./azure-fluent-system/showcase/AzureFluentShowcaseApp`.

## Checked-in catalog and examples

The library now owns a compact checked-in catalog so other agents can work locally without reopening Figma:

- `apps/web/src/azure-fluent-system/DESIGN.md`
- `apps/web/src/azure-fluent-system/catalog/COMPONENTS.md`
- `apps/web/src/azure-fluent-system/catalog/PATTERNS.md`
- `apps/web/src/azure-fluent-system/catalog/ICONS.md`
- `apps/web/src/azure-fluent-system/COVERAGE.md`
- `apps/web/src/azure-fluent-system/REBUILD-GUIDE.md`
- `apps/web/src/azure-fluent-system/examples/README.md`
- `apps/web/src/azure-fluent-system/examples/*.example.tsx`
- `apps/web/src/azure-fluent-system/showcase/README.md`

Use `DESIGN.md` as the portable design-system addendum, `catalog/COMPONENTS.md` for component inventory/mapping/extraction status, `catalog/PATTERNS.md` for pattern inventory/mapping/doctrine, `catalog/ICONS.md` for icon inventory/import status, `examples/` for focused samples, and `showcase/` for the standalone pattern browser plus component preview with an inline icon catalog surface.

For the two cross-cutting root docs: read [`COVERAGE.md`](./COVERAGE.md) for the honest high-fidelity-vs-placeholder coverage ledger and how fidelity is guaranteed, and [`REBUILD-GUIDE.md`](./REBUILD-GUIDE.md) for the end-to-end process of pointing an LLM at this library to rebuild the Agentweaver app (including a worked `CoordinatorRunPage` recipe).

## Component catalog snapshot

Review [`catalog/COMPONENTS.md`](./catalog/COMPONENTS.md) for the durable Azure UI Kit → local implementation catalog. It is a checked-in component inventory and mapping table, not exhaustive high-fidelity implementation coverage.

| Snapshot | Count |
| --- | --- |
| Inventory components/components sets | 148 |
| Exact name/node audit | 105 covered / 43 missing |
| implemented-rendered | 26 |
| needs-mcp-extraction | 45 |
| showcase-placeholder | 77 |
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
3. Use `catalog/COMPONENTS.md`, `catalog/PATTERNS.md`, and `catalog/ICONS.md` to find exports, examples, implementation files, extraction or import status, and citations.
4. Import primitives from `apps/web/src/azure-fluent-system`, then compose the target pattern with the checked-in React + CSS sources.
5. Treat Figma dev-mode URLs as traceability citations only.
6. Use Figma MCP only if it is available and you are intentionally refreshing the catalog or investigating a gap.

## Showcase app

`apps/web/src/azure-fluent-system/showcase/` is the library-local standalone showcase. It has exactly two primary experiences: a component preview and a pattern example browser, plus an inline icon catalog surface inside the Components experience. It is self-contained for downstream projects: the checked-in catalogs, examples, React components, CSS tokens, and local assets are enough for ordinary usage without Figma MCP. Run it from `apps/web/src/azure-fluent-system/` with `npm run showcase:dev`, which serves the standalone app at `http://127.0.0.1:4174/`.

The root project doctrine still lives in the repository `DESIGN.md`, but this library also ships a portable addendum in `./DESIGN.md`. Downstream projects should copy or merge the library-local addendum so the component, pattern, token, icon, and anti-rule guidance stays close to the package.
