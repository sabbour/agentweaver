---
name: docs-diagram-pitch
description: Create or redesign Agentweaver docs diagrams. For new visuals, launch exactly 2-3 separate bounded GPT-6 Astra research agents, make an editable one-page A5 draw.io source, and inspect its PNG.
compatibility: Requires repository access, the ability to launch GPT-6 Astra agents, image inspection, and draw.io Desktop CLI.
---

# Pitch an Agentweaver documentation diagram

Create the initial evidence-backed visual proposal. Do not treat the pitch as publication-ready: hand it to `docs-diagram-iterate` for at least four post-pitch review passes.

Read `references/pitch-checklist.md` before drawing. Also read `CONTRIBUTING.md`, `docs/diagrams/README.md`, the target documentation, and the current Agentweaver diagram design-system assets.

## Inventory and parallel-work protocol

The source of truth for catalog coordination is the area-sharded inventory under
`docs/diagrams/drawio/inventory/`. Before researching or editing:

1. Run `node scripts/docs/inventory-diagrams.mjs --list --spec <name>` and confirm
   the stable source, editable draw.io, PNG, and hash paths.
2. Claim the diagram in its area shard:

   ```text
   node scripts/docs/inventory-diagrams.mjs --set <name> --owner <agent-id>
   ```

3. Work in one assigned inventory area. Independent agents may work in parallel
   only when they own different area shards or an explicitly disjoint set of names.
4. Set the intended disposition with `--disposition retain|reuse|merge|remove|redesign`.
   `reuse` and `merge` also require `--replacement <canonical-name>`.
5. Never rename the stable published PNG path. A replacement decision changes the
   inventory mapping and documentation references during the later catalog migration;
   it does not silently move or overwrite another diagram.

Use repeated `--spec` flags for a bounded batch, or `--area <area>` plus
`--disposition <value>` for an area queue. Do not run an unfiltered catalog render
while other area agents are active.

## Hard repository boundary

This skill changes documentation authoring and rendering artifacts only. Treat shipped
product code as read-only visual and factual reference:

- do not edit the web coordinator topology, cluster topology, workflow editor/viewer,
  product routing helpers, runtime APIs, or any other shipped product surface;
- do not change `packages/Agentweaver.Squad` workflow definitions or product workflow
  behavior;
- do not remove root dependencies used by non-documentation tooling;
- docs-only generation may change `scripts/docs/workflows-to-graphspec.mjs` or replace it
  with another docs-only generator, but that does not authorize product behavior changes.

## Inventory and selective operation

For a catalog audit or multi-diagram request, create or refresh an inventory before
pitching individual diagrams:

```powershell
python .github/skills/docs-diagram-pitch/scripts/diagram_inventory.py discover `
  --repo . --output docs-diagram-inventory.json
```

Use `references/diagram-inventory.schema.json` as the contract. Assign every diagram one
disposition: `retain`, `reuse`, `merge`, `remove`, `redesign`, or `unreviewed`. Preserve
the canonical `name` and `output_path` during a redesign. `reuse` and `merge` entries
must identify their target.

Select work by exact diagram name, docs area, disposition, or status:

```powershell
python .github/skills/docs-diagram-pitch/scripts/diagram_inventory.py select `
  docs-diagram-inventory.json --area guide --disposition redesign
```

The existing docs renderer accepts repeated `--spec <name>` selectors. The helper can
invoke it for the selected batch:

```powershell
python .github/skills/docs-diagram-pitch/scripts/diagram_inventory.py run `
  docs-diagram-inventory.json --repo . --area guide --action render
```

For parallel catalog work, partition by `owner_area` and use separate worktrees/branches.
One diagram has one owner at a time, including diagrams referenced by several docs areas.
Shared diagrams use `owner_area: shared`; dependent area agents consume them rather than
editing competing copies. Do not launch multiple writers against the same source, PNG,
review directory, or inventory entry.

## 1. Establish the artifact contract

Record:

- the audience and one-sentence takeaway;
- the target Markdown page and stable published PNG path;
- the canonical uncompressed `.drawio` source path;
- A5 portrait or landscape (`148 x 210 mm` or `210 x 148 mm`);
- the facts, relationships, and boundaries that need to be visible;
- whether an existing canonical diagram should be reused instead.

Use exactly one draw.io page. Keep every meaningful element inside the printable A5 page bounds. Do not make a larger canvas and hide overflow.

## 2. Run independent grounded research

Launch **exactly two or three separate bounded GPT-6 Astra research agents** before
drawing. One agent/current-model context doing several searches does not satisfy this
requirement. Give each separately launched agent a distinct bounded question, such as:

1. runtime components, ownership, and deployment boundaries;
2. request/data/control flow and relationship direction;
3. lifecycle, failure, trust, persistence, or assurance behavior.

Each thread must inspect the repository implementation, configuration, tests, and current
documentation as ground truth, then return file/line evidence or authoritative external
links. Existing diagrams are legacy references to assess for reusable ideas, omissions,
and defects; they are not factual evidence and must not override current code or docs.
Keep the threads independent until their findings are complete, then reconcile
disagreements. Separate facts, proposals, and assumptions. Do not invent a node or
connector to improve composition.

In parallel, research the visual language:

- inspect the Agentweaver React diagram sources and committed draw.io design-system assets;
- inspect existing diagrams only as legacy visual references, alongside official
  product/technology references;
- find official logos or native draw.io symbols and record their source and usage rights;
- identify the information hierarchy, grouping, orientation, palette, typography, and notation that fit this subject.

Never upload private repository material to a public research service.

## 3. Build the content model

Before editing XML, write a compact model containing:

- every node with its evidence and visual classification;
- every connector as `source -> target: verb`, with evidence;
- group and trust/deployment boundaries;
- the dominant reading direction;
- native-symbol choices and Agentweaver-specific custom components;
- source and asset credits.

Prefer the smallest model that communicates the takeaway. Detail should increase comprehension, not become an implementation inventory.

## 4. Apply the Agentweaver visual language

Preserve the complete React-based style:

- warm paper canvas `#efeae7` or `#f8f4f1`, near-white cards `#fdfbf8`;
- warm ink `#272320`, strong secondary `#3f3935`, muted text `#635c57`, metadata `#746d68`;
- warm strokes `#e2ddd9` and `#ece7e3`;
- Segoe UI with system fallbacks; Cascadia Code/Consolas for metadata;
- rounded 16 px cards, restrained warm shadows, and a 5 px semantic accent;
- icon, title, subtitle, metadata, and pill-badge hierarchy;
- lavender, light-teal, green, marigold, and neutral semantic tones;
- tiered group surfaces, with stronger outer hierarchy and clear group titles;
- orthogonal rounded connectors routed through gutters, readable label backgrounds, packed lanes, true bridge arcs at unavoidable crossings, and real junction dots only for logical splits/merges;
- dashed marigold outer rails only for semantic revision or return flow.

### Native versus custom symbols

Use draw.io's native libraries whenever the thing represented is actually that technology or notation:

| Meaning | Preferred classification |
| --- | --- |
| Azure resource | `native:azure` |
| Kubernetes resource | `native:kubernetes` |
| C4 person/system/container/component | `native:c4` |
| UML participant/component/class/relation | `native:uml` |
| Process, decision, event, gateway | `native:flowchart` or `native:bpmn` |
| Router, gateway, firewall, network boundary | `native:networking` |
| Database or durable store | `native:database` |
| Provider-neutral cloud resource | `native:cloud` |

Use Agentweaver custom components only for product-specific concepts such as Coordinator, Team, Agent, Run, OutcomeSpec, Decision Inbox, Memory, or SandboxClaim, or when no semantically correct native symbol exists. A custom card may frame a native symbol so the diagram keeps Agentweaver hierarchy and styling. Record every symbol as `native:<library>` or `custom:agentweaver` in the pitch/change record. Do not redraw an Azure or Kubernetes icon as a generic custom glyph.

## 5. Create the initial pitch

1. Start from `docs/diagrams/drawio/fluent-template.drawio` and load `docs/diagrams/drawio/fluent-library.xml`.
2. Keep the source uncompressed: the `<diagram>` element must contain editable XML, not a compressed text payload.
3. Set the page to one A5 sheet in the chosen orientation.
4. Compose a dense but readable diagram using source-backed detail, real/native symbols, explicit boundaries, and short labels.
5. Save the editable pitch source separately as `<name>-pitch.drawio`.
6. Export `<name>-pitch.png` with the repository's draw.io Desktop command.
7. Open and inspect the **actual exported PNG** at A5 print size and enlarged detail. XML inspection or draw.io's editor view does not substitute for PNG inspection.
8. Save `<name>-pitch.md` with the takeaway, orientation, research evidence, source/asset credits, native/custom symbol inventory, and visible issues handed to iteration.

The pitch artifacts must remain available after later passes. Do not overwrite them with the canonical final source.

## 6. Hand off to iteration

Invoke `docs-diagram-iterate` with:

- the pitch `.drawio`, PNG, and change record;
- the evidence map and source/asset credits;
- the selected orientation and one-sentence takeaway;
- known visual or semantic risks.

The final canonical source and published PNG are promoted only after the iteration skill completes its required passes.

## Pitch deliverables

- one uncompressed editable `<name>-pitch.drawio`;
- one matching inspected `<name>-pitch.png`;
- one `<name>-pitch.md` research and change record;
- two or three independent grounded research results;
- visual-reference, logo, license, and native/custom symbol records;
- explicit handoff to `docs-diagram-iterate`.

Do not claim the pitch is complete if the exported PNG was not opened and inspected.
