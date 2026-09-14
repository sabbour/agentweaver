---
name: docs-diagram-iterate
description: Refine existing Agentweaver draw.io diagrams through four-plus PNG passes, 9x pass-1 XML growth, correction-only later passes, and a final arrow trace. Use for polish or fixes, not new diagrams.
compatibility: Requires repository access, Python 3, image inspection, and draw.io Desktop CLI for PNG export.
---

# Iterate an Agentweaver documentation diagram

Treat the rendered PNG as the product and the uncompressed `.drawio` XML as its editable source. Never edit only the raster. Never claim to have inspected an image that you did not open.

Read `references/review-checklist.md` and use `references/iteration-manifest.schema.json` for the pass record.

## Inventory and parallel-work protocol

Start by reading the diagram's entry from the area-sharded inventory:

```text
node scripts/docs/inventory-diagrams.mjs --list --spec <name>
```

The entry fixes the stable source, editable draw.io, PNG, and hash paths and records
the current `retain`, `reuse`, `merge`, `remove`, or `redesign` disposition. Claim the
entry with `--set <name> --owner <agent-id>` before editing. Work only in the assigned
area shard or explicitly disjoint diagram names, so independent docs-area agents do
not contend on one inventory file.

Use repeated `--spec` selectors for a bounded batch. Use
`node scripts/docs/render-diagrams.mjs --area <area> --disposition redesign` only for
the area assigned to the current agent; never launch an unfiltered catalog render
while parallel diagram work is active. Preserve every `paths` value in the inventory.

## Hard repository boundary

Refine documentation artifacts only. Product surfaces are read-only references:

- do not edit coordinator or cluster topology, workflow editor/viewer, product routing
  helpers, runtime APIs, or other shipped application behavior;
- do not change `packages/Agentweaver.Squad` workflow definitions;
- do not remove root dependencies used by non-documentation tooling;
- a docs-only workflow-to-diagram generator may change, but product workflow semantics
  must remain unchanged.

## Bulk and parallel operation

When refining several diagrams, use the inventory created by `docs-diagram-pitch` and
select exact names or one `owner_area`:

```powershell
python .github/skills/docs-diagram-pitch/scripts/diagram_inventory.py run `
  docs-diagram-inventory.json --repo . --area deep-dive --status queued --action render
```

The helper passes each selected canonical name to the existing renderer as repeated
`--spec` arguments, so outputs stay at `docs/diagrams/<name>.png`. Process only diagrams
owned by the current area/worktree. Never let parallel agents edit the same diagram,
review directory, or inventory entry. A shared canonical diagram has one `shared` owner
and may be reused by many documentation areas.

Update the inventory status and disposition after each diagram. Do not perform unrelated
catalog migrations opportunistically during a selected batch.

## Preconditions

Require:

- an editable uncompressed pitch/current `.drawio` source;
- its matching PNG;
- the pitch's grounded facts, relationship evidence, visual research, and source/asset credits;
- one A5 page in portrait or landscape;
- the Agentweaver visual language and native/custom symbol inventory.

If the task is a brand-new visual rather than an existing draft, use `docs-diagram-pitch` first. If a source is compressed, convert it to uncompressed editable XML before measuring or editing.

Revalidate meaning against the repository implementation, configuration, tests, and
current documentation. Treat the diagram being refined and all other existing diagrams
as legacy artifacts to assess, not as ground truth. When a diagram conflicts with current
code or docs, correct the diagram and cite the authoritative source.

Preserve user edits only when they remain grounded. Native Azure, Kubernetes, C4, UML,
flowchart/BPMN, networking, database, and cloud symbols remain native when semantically
correct. Agentweaver custom cards remain limited to product-specific concepts or genuine
native-library gaps.

## Artifact naming

Keep every pass independently auditable:

```text
<name>-pitch.drawio
<name>-pitch.png
<name>-pitch.md
<name>-pass-01.drawio
<name>-pass-01.png
<name>-pass-01.md
...
<name>-pass-04.drawio
<name>-pass-04.png
<name>-pass-04.md
iteration-manifest.json
```

Never overwrite an earlier pass. A no-change pass still saves a distinct source copy, PNG, and record saying that inspection found no permitted defect.

For each pass: inspect the current PNG, edit the matching `.drawio`, export a new PNG, then inspect that actual new PNG at A5 print size and enlarged detail. Editing several times and exporting once counts as one pass, not several.

## Pass 1: visual upgrade

This is the only post-pitch redesign pass.

1. Measure the pitch source:

   ```powershell
   python .github/skills/docs-diagram-iterate/scripts/check_xml_growth.py <name>-pitch.drawio <name>-pass-01.drawio
   ```

2. Increase meaningful non-whitespace uncompressed draw.io XML to **at least 9 times** the pitch count. This is the article's "at least 800% increase": the pass-1 result must be at least 900% of the baseline total. The checker measures canonical visible semantic/visual structures, not raw bytes or arbitrary attributes; record the metric as `visible-semantic-canonical-xml-v1`.
3. Exclude XML comments and embedded image data from both counts.
4. Use the growth for meaningful visual detail: relevant licensed logos/native symbols, clearer hierarchy, richer but evidence-backed composition, intentional grouping, useful annotations, and deliberate effects.
5. Do not satisfy the threshold with invisible/transparent objects, off-page objects,
   duplicate/redundant cells, metadata padding, comments, whitespace, embedded image
   bytes, or invented facts. The checker rejects those cases.
6. Keep the result on the same one-page A5 canvas and readable at actual print size.
7. Complete the full visual redesign and pass the growth checker before pass 2.

Record baseline count, result count, ratio, changed visual details, asset credits, and the PNG inspections in `pass-01.md`.

## Passes 2 and 3: corrections only

Do not add content, change the visual language, or reopen composition. Inspect and correct only:

- **orientation:** reversed or inconsistent reading direction, wrong symbol/participant orientation, or misplaced directional cues;
- **overlap:** element/card/boundary collisions, clipped labels, line-label collisions, illegible crossings, or content outside the printable page;
- **arrows:** wrong direction, source, target, endpoint, arrowhead, label association, or routing.

After each correction, inspect affected neighboring elements and connectors. Save and inspect the new `.drawio`/PNG pair even when no defect remains.

## Pass 4: final correction and complete arrow trace

Pass 4 remains correction-only.

1. Repeat the orientation, overlap, and arrow checks on the latest PNG.
2. Trace **every arrow**, one by one, from source to target.
3. For each arrow verify:
   - source and target match the evidence map;
   - direction and arrowhead encode the intended relationship;
   - endpoints attach to the intended shapes rather than nearby containers;
   - orthogonal routing avoids cards and labels;
   - crossings use a real bridge when needed and never imply a false junction;
   - junction dots mark only genuine splits or merges;
   - dashed marigold outer rails represent only semantic revision/return flow.
4. Record every traced connector in `pass-04.md` or the iteration manifest, including connectors that needed no change.
5. Export and inspect the new pass-4 PNG at A5 print size and enlarged detail.

## Continue when defects remain

Four passes are a minimum, not a stopping condition. If pass 4 reveals any defect, continue with pass 5 and later **correction-only** passes. Trace affected arrows again and perform a full final arrow trace on the last pass. Stop only when the latest inspected PNG has no remaining orientation, overlap, endpoint, direction, or routing defect.

## Preserve the Agentweaver design contract

Corrections must retain:

- warm neutral Fluent palette and Segoe UI typography;
- rounded near-white cards, restrained shadows, 5 px semantic accents;
- icon/title/subtitle/metadata/pill-badge hierarchy and fixed badge tones;
- tiered group surfaces and group-title treatment;
- orthogonal rounded connectors, packed lanes, label backgrounds, true crossing bridges, and real split/merge junction dots;
- native draw.io technology/notation symbols when semantically correct;
- explicit `native:<library>` versus `custom:agentweaver` classification.

## Promote and validate

After the final clean pass:

1. Promote the final uncompressed `.drawio` to `docs/diagrams/src/<name>.drawio`.
2. Promote its matching inspected PNG to the stable published path.
3. Keep every pitch/pass source, PNG, and change record in the review artifacts.
4. Update the Markdown embed/provenance without changing a stable public image path.
5. Run the repository's focused diagram drift check and docs build when documentation changed.
6. Validate the iteration manifest against `references/iteration-manifest.schema.json`,
   including ordered unique passes and the actual latest final pass:

   ```powershell
   python .github/skills/docs-diagram-iterate/scripts/validate_iteration_manifest.py `
     iteration-manifest.json
   ```
7. Update the inventory disposition, replacement (for `reuse`/`merge`), owner, and
   concise notes. Do not rewrite another area's shard.

Do not call the result publishable if the four-pass minimum, 9x meaningful XML gate, per-pass artifacts, actual PNG inspections, or final every-arrow trace is missing.
