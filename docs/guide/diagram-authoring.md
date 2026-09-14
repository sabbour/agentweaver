# Author documentation diagrams

Agentweaver documentation diagrams are editable, uncompressed draw.io XML exported to committed PNG files. Draw.io is the rendering engine; readers never need a live diagram viewer.

The authoring workflow uses two repository skills:

- `docs-diagram-audit` orchestrates a GPT-6 Astra repo-wide or cross-page inventory,
  disposition, consolidation, and integrity workflow.
- `docs-diagram-pitch` researches and creates the initial one-page A5 proposal.
- `docs-diagram-iterate` performs the mandatory publication refinement and correctness passes.

Use the audit skill when documentation changes may affect several diagrams or the canonical
catalog. Use pitch plus iteration for a new or substantially redesigned diagram. Use only
the iteration skill for a correction to an existing editable source.

## Scope boundary

This workflow affects documentation rendering and authoring only. The React coordinator
topology, cluster topology, workflow editor/viewer, product routing helpers, runtime APIs,
and every other shipped product surface are visual or factual references, not migration
targets.

Product workflow definitions under `packages/Agentweaver.Squad` and their runtime behavior
must not change as part of diagram work. A docs-only
`scripts/docs/workflows-to-graphspec.mjs` conversion path, or its docs-only replacement,
may change without changing product semantics. Root dependencies used by non-documentation
tooling must remain.

## Inventory and selective batches

Before a repository-wide audit, generate a planning inventory without changing diagrams:

```powershell
python .github/skills/docs-diagram-pitch/scripts/diagram_inventory.py discover `
  --repo . --output docs-diagram-inventory.json
```

Each entry records its canonical name, source, stable PNG output, documentation references,
owning docs area, status, rationale, and disposition:

- `retain`: keep the current diagram;
- `reuse`: replace its usage with a named canonical target;
- `merge`: combine its useful content into a named target;
- `remove`: delete it after all references are removed;
- `redesign`: rebuild it while preserving its canonical name and output path;
- `unreviewed`: discovered but not assessed yet.

Validate and select by exact name, area, disposition, or status:

```powershell
python .github/skills/docs-diagram-pitch/scripts/diagram_inventory.py validate `
  docs-diagram-inventory.json

python .github/skills/docs-diagram-pitch/scripts/diagram_inventory.py select `
  docs-diagram-inventory.json --area deep-dive --disposition redesign --status queued
```

The renderer accepts repeated names, and the helper can invoke that interface directly:

```powershell
npm run docs:render-diagrams -- --spec first-diagram --spec second-diagram

python .github/skills/docs-diagram-pitch/scripts/diagram_inventory.py run `
  docs-diagram-inventory.json --repo . --area deep-dive --status queued --action render
```

For parallel work, partition guide, deep-dive, experience, reference, and shared diagrams
into separate worktrees and branches. Each diagram has one `owner_area` and one active
writer. Cross-area diagrams use one `shared` owner and are reused by area agents; do not
edit competing copies or the same inventory entry.

Creating the inventory does not authorize the full migration. Review dispositions first,
then process explicit name/area selections.

## 1. Research and pitch

Invoke `docs-diagram-pitch`.

Before drawing, the skill launches exactly two or three separate bounded GPT-6 Astra
research agents over the repository implementation, configuration, tests, subject matter,
and current documentation. Multiple searches in one current-model context do not count as
separate agents. Those sources are ground truth. The skill reconciles the agents' findings
into source-backed components, boundaries, and directional relationships.

Existing diagrams are legacy references to assess for useful visual ideas, omissions, and
defects. They are not the research target or a factual source of truth and must not
override current code or documentation. The skill separately researches visual
references, official brand/logo assets, and native draw.io symbols that fit the subject.

The pitch must:

- fit one A5 sheet (`148 x 210 mm`) in portrait or landscape;
- use one draw.io page with all meaningful content inside the printable bounds;
- remain uncompressed, editable XML;
- preserve the Agentweaver visual language;
- export a PNG and inspect that actual image at A5 print size and enlarged detail;
- save a pitch `.drawio`, PNG, and research/change record.

The pitch is an initial proposal, not the final published artifact.

## 2. Use native and custom symbols deliberately

Use draw.io's native libraries whenever they accurately represent the thing shown:

| Subject | Native draw.io library |
| --- | --- |
| Azure resources | Azure |
| Kubernetes resources | Kubernetes |
| Architecture roles and boundaries | C4 |
| Software structure and interactions | UML |
| Process steps, decisions, events, gateways | Flowchart or BPMN |
| Routes, gateways, firewalls, network boundaries | Networking |
| Durable stores | Database |
| Provider-neutral cloud resources | Cloud |

Use Agentweaver custom components for product-specific concepts such as Coordinator, Team, Agent, Run, OutcomeSpec, Decision Inbox, Memory, and SandboxClaim, or where no correct native symbol exists. A custom Agentweaver card can frame a native symbol. Record each symbol as `native:<library>` or `custom:agentweaver`; do not replace a correct Azure or Kubernetes symbol with a generic imitation.

## 3. Preserve the Agentweaver visual language

The draw.io source carries forward the full React diagram style rather than only its colors:

- warm paper surfaces (`#efeae7`, `#f8f4f1`, and `#fdfbf8`) with warm ink and strokes;
- Segoe UI typography and monospace metadata;
- rounded cards, restrained warm shadows, and a 5 px semantic accent;
- icon, title, subtitle, metadata, and pill-badge hierarchy;
- fixed lavender, light-teal, green, marigold, and neutral semantic tones;
- tiered group surfaces with clear group titles;
- orthogonal rounded connectors routed through gutters and packed lanes;
- readable connector-label backgrounds;
- true bridge arcs for unavoidable crossings and junction dots only for logical splits or merges;
- dashed marigold outer rails only for semantic revision or return flow.

Start from:

```text
docs/diagrams/drawio/fluent-template.drawio
docs/diagrams/drawio/fluent-library.xml
```

## 4. Run the mandatory iteration passes

Invoke `docs-diagram-iterate` after the pitch export. Complete at least four post-pitch passes; the pitch does not count.

During every pass, verify meaning against the repository and current documentation rather
than trusting the legacy diagram. If they conflict, correct the editable diagram and
record the authoritative evidence.

### Pass 1: visual upgrade

Pass 1 is the only redesign pass. The meaningful non-whitespace XML in the result must be at least **9 times** the pitch count (the article's "at least 800% increase"). Comments and embedded image data are excluded.

Run:

```powershell
python .github/skills/docs-diagram-iterate/scripts/check_xml_growth.py `
  <name>-pitch.drawio <name>-pass-01.drawio
```

The checker counts canonical visible semantic/visual structures rather than raw XML bytes.
The added XML must produce visible, source-backed detail. Invisible or transparent
objects, off-page objects, duplicate/redundant cells, metadata padding, comments,
whitespace, embedded image bytes, and invented facts cannot satisfy the threshold. The
diagram must still fit and remain readable on one A5 sheet.

### Passes 2-4: corrections only

Passes 2, 3, and 4 may correct only:

- incorrect orientation;
- overlapping or clipped elements;
- wrong arrows, including direction, endpoints, labels, and routing.

Do not add content or reopen the visual design after pass 1.

In pass 4, trace every arrow from source to target and verify its evidence, direction, endpoints, routing, crossings, and junction semantics. If any defect remains, continue with pass 5 and later correction-only passes. Four is the minimum, not a forced stopping point.

## 5. Save every pass

Each pass exports and inspects a new PNG at A5 print size and enlarged detail. Keep every source, image, and short change record:

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

Store review artifacts together under `docs/diagrams/reviews/<name>/`. A pass with no correction still saves a distinct artifact set and records the clean inspection. Use the schema at `.github/skills/docs-diagram-iterate/references/iteration-manifest.schema.json`.

Validate ordered unique pass numbers, pass modes, pass-1 measurements, and the actual
latest final pass:

```powershell
python .github/skills/docs-diagram-iterate/scripts/validate_iteration_manifest.py `
  docs/diagrams/reviews/<name>/iteration-manifest.json
```

## 6. Promote and validate

After the latest PNG is clean:

1. Promote the final uncompressed source to `docs/diagrams/src/<name>.drawio`.
2. Promote the matching inspected image to `docs/diagrams/<name>.png`.
3. Preserve an existing public PNG filename and Markdown path during migrations.
4. Update the Markdown image embed, alt text, and source provenance when needed.
5. Export and check the canonical artifact:

```powershell
npm run docs:render-diagrams -- --spec <name>
npm run docs:check-diagrams -- --spec <name>
npm run docs:build
```

During JSON migration, keep the generated editable
`docs/diagrams/drawio/generated/<name>.drawio` with the JSON source, PNG, and hash.
When promoting canonical `.drawio`, retire the same-name JSON and transitional
generated copy so discovery has one source of truth. A drift hash verifies artifact
identity; it does not certify PNG appearance or the iteration review.

Commit the canonical source, PNG, hash, all required review artifacts, and documentation update together.

See [Architecture diagrams](../diagrams/README.md) for the source-control and publication contract.
