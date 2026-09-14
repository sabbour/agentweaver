# Architecture diagrams

Agentweaver documentation diagrams use a **draw.io-native, source-controlled pipeline**. The editable canonical source is uncompressed `.drawio` XML and the published artifact is a committed PNG. GitHub and VitePress do not require a live renderer or viewer.

Draw.io is the only rendering engine. Structured generators and Mermaid migration tools may produce draw.io XML, but their output remains editable and follows the same authoring, export, review, and drift rules.

The catalog migration is not yet complete: legacy JSON inputs still exist and the
renderer accepts them as migration sources. A matching hash proves artifact consistency,
not completion of the research, A5, native-symbol or pitch/iteration publication gates.

## Scope boundary

This pipeline is documentation-only. Existing product diagrams and routing implementations
in the web application are style and behavior references; they are not replaced or altered
by this work. In particular, do not change coordinator topology, cluster topology, workflow
editor/viewer, product routing helpers, runtime APIs, or another shipped product surface.

Workflow definitions in `packages/Agentweaver.Squad` and product workflow behavior remain
unchanged. The docs-only `scripts/docs/workflows-to-graphspec.mjs` path, or a docs-only
replacement, may evolve to emit draw.io XML. Root dependencies that support non-doc tooling
must remain even when the old documentation renderer no longer needs them.

## Inventory and disposition planning

Invoke `docs-diagram-audit` for repository-wide or cross-page work. It orchestrates GPT-6
Astra research, canonical consolidation, downstream pitch/iteration, integrity checks,
and both machine-readable and human summaries. The workflow starts with a non-destructive
inventory:

```powershell
python .github/skills/docs-diagram-pitch/scripts/diagram_inventory.py discover `
  --repo . --output docs-diagram-inventory.json
```

Each diagram has one canonical name, stable `docs/diagrams/<name>.png` output, source,
reference list, owner area, status, rationale, and disposition. Allowed review outcomes
are `retain`, `reuse`, `merge`, `remove`, and `redesign`; discovered entries start as
`unreviewed`. `reuse` and `merge` name their target.

Validate the inventory and process only explicit selections:

```powershell
python .github/skills/docs-diagram-pitch/scripts/diagram_inventory.py validate `
  docs-diagram-inventory.json

python .github/skills/docs-diagram-pitch/scripts/diagram_inventory.py run `
  docs-diagram-inventory.json --repo . --name first-diagram --name second-diagram `
  --action check
```

The helper delegates to the existing repeated `--spec <name>` interface. A redesign keeps
the same canonical name and published output path.

Parallel agents partition work by `owner_area` in separate worktrees and branches. One
diagram and inventory entry has one active writer. Cross-area diagrams use one `shared`
owner and are reused rather than forked into competing canonical copies.

The inventory is planning state, not permission to migrate the full catalog.

## Visual contract

The draw.io design system preserves the complete visual language established by the Agentweaver React diagrams:

- warm neutral Fluent palette and Segoe UI typography;
- rounded near-white cards with restrained shadows and a 5 px semantic accent;
- icon, title, subtitle, metadata, and pill-badge hierarchy;
- fixed lavender, light-teal, green, marigold, and neutral semantic tones;
- tiered group surfaces and clear group-title treatment;
- orthogonal rounded connectors routed through gutters and packed lanes;
- connector-label backgrounds, real crossing bridges, and genuine split/merge junction dots;
- dashed marigold outer rails only for semantic revision or return flow.

Reusable assets live in:

```text
docs/diagrams/drawio/fluent-template.drawio
docs/diagrams/drawio/fluent-library.xml
docs/diagrams/drawio/design-system.json
```

### React optical calibration and publication gate

`design-system.json` is the active token source for the browser-free graph and
sequence generators. The `react-optical-v1` profile follows the actual historical
PNG, not nominal constants from a later React source revision: 340 px cards,
104/132 px heights, 20 px semibold titles, 16 px subtitles, 12 px metadata and
28 px Fluent icons. Export at 2x preserves that optical scale. Badges are opt-in;
native symbols occupy the same icon footprint rather than replacing card chrome.
A5 paper and print scaling are explicit; a centered content group preserves
logical card dimensions, and a visible warm paper surface preserves export margins.

Run the non-publishing recovery planner before reconciling stopped migrations:

```powershell
node scripts\docs\plan-fluent-repair.mjs
```

It writes candidates and exact uncertainties under
`docs/diagrams/reviews/catalog-integration/`, never public images or consumers.
It reads the current workflow YAML/default template, final shard models and
existing XML endpoints. Missing groups, detailed constraints, sequence structure,
or endpoint disagreements remain blocked for explicit semantic review. A staged
projection is not an approved replacement.

For an already role-annotated source, use deterministic normalization:

```powershell
python -B scripts\docs\normalize-drawio.py input.drawio --output candidate.drawio --report validation.json
```

Unannotated sources require reviewed `--bindings` JSON mapping cell IDs to roles
or regeneration from a reviewed structured model. The normalizer preserves
named draw.io styles, geometry, IDs and semantic text; it never deletes copy,
guesses reflow or treats a table/sequence as a generic graph. It refuses output
when unresolved roles or geometric defects remain.

The exporter validates every selected source **before writing any batch output**.
Incorrect typography, card anatomy, oversized copy, unnormalized icons, overlapping
cards/labels, conflicting arrowheads, opposite-direction shared trunks, missing
bridges, false/missing junctions and weak density block publication. Python 3 is
required for this gate. These checks complement, not replace, source grounding
and inspection of the exported PNG.

**The Fluent visual contract is approved and catalog integration is complete.**
The approved comparison is
[comparison-final.png](reviews/canonical-default-workflow/style-calibration/comparison-final.png).
Both generators use its `classicThin`, 8 px arrow marker. All 121 public canonicals
have been inspected and exported through Desktop 31.4.5: 120 regenerated or
normalized diagrams, plus the preserved coordinator pilot. The final 20 execution
diagrams have separate concise pitches and four inspected passes; meaningful
pass-1 growth ranges from 13.516541x to 24.052930x. Scaled A5 page checks use
`pageWidth * pageScale` and `pageHeight * pageScale` as logical coordinate bounds.

The pilot's exact-source preservation profile keeps its approved card geometry,
copy and routes, with explicit roles, A5 metadata and calibrated arrow markers.
Its transparent re-export is not pixel-identical to the old marker version;
the measured differences are recorded in
`reviews/catalog-integration/pilot-compatibility/verification.json`.
The profile rejects source mutations and cannot be borrowed by other diagrams.
Historical manifests establish earlier process evidence, not approval of an
uninspected replacement. Do not stamp stale assets, add filler to satisfy XML
growth, or remove consumers to make checks green.
Exact publication, disposition and validation evidence lives in
`reviews/catalog-integration/integration-result.json` and
`reviews/catalog-integration/completion-reconciliation.json`.

## Native symbols and Agentweaver components

Use the native Azure, Kubernetes, C4, UML, flowchart/BPMN, networking, database, cloud, and architecture libraries when a symbol communicates the real resource or notation. This improves recognition and keeps standard semantics intact.

Use Agentweaver custom components only for product-specific concepts or when no semantically correct native shape exists. Coordinator, Team, Agent, Run, OutcomeSpec, Decision Inbox, Memory, and SandboxClaim are examples of custom concepts. A native symbol may appear inside Agentweaver card chrome.

Every diagram's research/change record identifies symbols as `native:<library>` or `custom:agentweaver` and records external asset sources and usage rights. Do not replace a correct native Azure or Kubernetes symbol with a generic custom drawing.

## Repository layout

```text
docs/diagrams/src/<name>.drawio       # canonical uncompressed editable source
docs/diagrams/drawio/                 # template, reusable library, design-system manifest
docs/diagrams/reviews/<name>/         # pitch and per-pass sources, PNGs, records, manifest
docs/diagrams/<name>.png              # stable published image
docs/diagrams/<name>.hash.txt         # canonical source hash at export time
docs/diagrams/email-exports/          # selected high-resolution external-use copies
```

Keep one canonical `.drawio` source per basename. Preserve existing PNG names, alt text, and Markdown paths when migrating a diagram.

## Authoring workflow

For a new or substantially redesigned diagram:

1. Invoke `docs-diagram-pitch`.
2. Complete two or three independent source-grounding threads.
3. Research the visual language, official logos, relevant references, and native draw.io libraries.
4. Create an uncompressed editable pitch on one A5 page in portrait or landscape.
5. Export and inspect the actual pitch PNG.
6. Invoke `docs-diagram-iterate`.
7. Complete at least four post-pitch passes.
8. Promote the final clean source and PNG to their canonical paths.

For a small correction to an existing diagram, invoke `docs-diagram-iterate` directly and preserve the established research and visual direction.

The full procedure is in [Author documentation diagrams](../guide/diagram-authoring.md).

## Mandatory post-pitch passes

The initial pitch is not a review pass.

1. **Visual upgrade:** grow meaningful non-whitespace uncompressed XML to at least 9 times the pitch count. The checker counts canonical visible semantic/visual structures, excludes comments and embedded image data, and rejects invisible cells, off-page objects, duplicates, and metadata padding.
2. **Corrections only:** fix orientation, overlaps, and arrow direction/endpoints/routing.
3. **Corrections only:** repeat those checks against the newly exported PNG.
4. **Final correction check:** repeat those checks and trace every arrow from source to target.

Continue with correction-only passes while defects remain. Every pass saves a distinct `.drawio`, PNG, and short change record, and every new PNG is inspected at A5 print size and enlarged detail.

Use:

```powershell
python .github/skills/docs-diagram-iterate/scripts/check_xml_growth.py `
  <name>-pitch.drawio <name>-pass-01.drawio
```

The iteration manifest schema is `.github/skills/docs-diagram-iterate/references/iteration-manifest.schema.json`.

## Connector convention

- Normal flow uses the warm neutral graph stroke.
- Semantic revision/return flow uses a dashed marigold outer rail.
- Orthogonal routes travel through card and group gutters rather than piercing shapes.
- Parallel routes use packed, separated lanes.
- Labels remain visibly associated with their connector and use a readable background.
- An unavoidable perpendicular crossing uses a true bridge arc, never a background-colored mask.
- A small solid junction dot marks only a real split, merge, or return join. An ordinary crossing, elbow, card endpoint, or container boundary is not a junction.

The final review traces every connector and verifies its source, target, direction, endpoint, route, label, crossing, and junction meaning against the evidence map.

## Export and validation

Export uses the official draw.io Desktop CLI. The command is discovered from a standard installation or `PATH`; set `DRAWIO_CLI` or pass `--drawio-cli <path>` when necessary.

```powershell
npm run docs:render-diagrams -- --spec <name>
npm run docs:render-diagrams -- --spec <first-name> --spec <second-name>
npm run docs:render-diagrams -- --spec <name> --drawio-format svg
npm run docs:check-diagrams -- --spec <name>
npm run docs:build
```

PNG is the required publication artifact. SVG or PDF is optional when another channel requires it. On headless Linux, export runs through `xvfb-run`.

The browser-free drift check compares the normalized canonical draw.io source hash with the committed hash and verifies that the PNG exists. It does not substitute for the required visual inspection.

Commit the canonical `.drawio`, PNG, hash, pitch/pass artifacts, iteration manifest, and any Markdown provenance update together.
