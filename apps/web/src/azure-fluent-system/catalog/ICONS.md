# Azure Fluent System icon catalog

Checked-in icon inventory for `apps/web/src/azure-fluent-system`, sourced from IconCloud Azure Icons exports and local icon helpers. This catalog records vendored Azure assets, local alias strategies, and icon examples without dumping manifest JSON or SVG payloads.

## Status summary

| Measure | Value |
| --- | --- |
| Vendored Azure icon collections | 27 |
| Raw visible icon exports | 1637 |
| Unique checked-in SVG assets | 1441 |
| Duplicate alias payloads | 196 |
| Tracked local icon alias rows | 5 |
| Dedicated showcase icon surface | Yes |

## Tracked local icon aliases

| Icon name / alias | Source / upstream package or source reference | Extraction/import status | Extraction/import date | Local asset/component mapping | Showcase status | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| Azure icon collections (all vendored assets) | IconCloud visible authenticated exports from `https://iconcloud.design/browse/Azure%20Icons` | vendored | 2026-07-08 | `assets/icons/azure/azure-icons-manifest.json`, `assets/icons/azure/assets/`, `icons.tsx`, showcase icon card | Partial | Showcase surfaces a small checked-in subset while the full 27-collection manifest stays local and human-editable here. |
| `Compute/Virtual Machine` | IconCloud Azure Icons → Compute → `Virtual Machine` | vendored | 2026-07-08 | `azure-icons-manifest.json` row, `examples/icon-registry.example.tsx`, showcase icon card | Yes | Rendered in the showcase from the checked-in vendored SVG asset and manifest-backed registry key. |
| `Storage/Storage Accounts` | IconCloud Azure Icons → Storage → `Storage Accounts` | vendored | 2026-07-08 | `azure-icons-manifest.json` row, `examples/icon-registry.example.tsx`, showcase icon card | Yes | Rendered in the showcase from the checked-in vendored SVG asset and manifest-backed registry key. |
| `VirtualMachine` | Local alias convention for consumer-supplied Azure assets | local-alias | Not recorded | `createIconCloudRegistry()` in `icons.tsx`; sample wiring in `examples/icon-registry.example.tsx`; documented in showcase icon card | Partial | Demonstrates a simple alias-to-path convention; this repo does not vendor a same-name `/azure-icons/compute/VirtualMachine.svg` path. |
| `FluentFallback` | Fluent UI system icon package fallback (`BoxRegular`) | local-alias | Unknown | `examples/icon-registry.example.tsx` local registry entry; showcase icon card | Yes | Used when a registry entry is missing or a downstream app wants a non-Azure fallback glyph. |

## Vendored Azure icon collection coverage

| Collection | Raw aliases | Unique assets | Duplicate payloads | Source | Local mapping | Showcase status | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| AI + Machine Learning | 42 | 41 | 1 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Analytics | 10 | 10 | 0 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| App Services | 4 | 4 | 0 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Azure Ecosystem | 3 | 3 | 0 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Blockchain | 6 | 6 | 0 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Command | 254 | 186 | 68 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Compute | 38 | 34 | 4 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | Partial | Showcase icon card renders the `Virtual Machine` sample; the remaining Compute glyphs stay catalog-only until a larger browser is needed. |
| Containers | 8 | 8 | 0 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Databases | 23 | 23 | 0 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| DevOps | 13 | 13 | 0 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| General | 95 | 94 | 1 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Hybrid + Multicloud | 14 | 14 | 0 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Identity | 36 | 35 | 1 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Integration | 26 | 25 | 1 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Intune | 18 | 16 | 2 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| IoT | 26 | 26 | 0 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Management + Governance | 32 | 32 | 0 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Menu | 646 | 578 | 68 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Migrate | 3 | 3 | 0 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Mixed Reality | 2 | 2 | 0 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Networking | 57 | 56 | 1 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| New Icons | 14 | 14 | 0 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Other | 194 | 153 | 41 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Security | 14 | 14 | 0 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Status | 34 | 32 | 2 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |
| Storage | 18 | 18 | 0 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | Partial | Showcase icon card renders the `Storage Accounts` sample; the remaining Storage glyphs stay catalog-only until a larger browser is needed. |
| Web | 7 | 7 | 0 | IconCloud visible export | `assets/icons/azure/azure-icons-manifest.json` + `assets/icons/azure/assets/` | No | Checked-in normalized SVG assets. |

## Local-first workflow

1. Start with `icons.tsx`, this catalog, and `examples/icon-registry.example.tsx` before changing icon behavior.
2. Use vendored Azure assets from `assets/icons/azure/` for product and resource glyphs.
3. Use Fluent UI system icons for generic UI affordances and fallbacks.
4. Treat IconCloud as the source reference for vendored Azure assets; do not claim Figma MCP extraction for icons that came from IconCloud.
5. If icon assets or aliases change, update this catalog, the manifest/summary files, the showcase icon card, and any affected examples or tests together.
