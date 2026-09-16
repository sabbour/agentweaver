---
name: docs-diagram-audit
description: Audit Agentweaver docs diagrams with GPT-6 Astra, consolidation, integrity checks, and pitch/iterate orchestration. Use for repo-wide or cross-page visual impact, not one routine fix.
compatibility: Requires repository access, Python 3, GPT-6 Astra agents, image inspection, and draw.io Desktop CLI.
---

# Audit the Agentweaver documentation diagram catalog

Run this skill on demand for a repository-wide diagram audit or whenever a documentation
change may introduce, update, invalidate, consolidate, or remove architectural/process
visuals. This is a docs-only workflow. Do not change shipped product surfaces, runtime
APIs, product routing, `packages/Agentweaver.Squad` workflow definitions, product behavior,
or root dependencies needed outside documentation.

Read:

- `CONTRIBUTING.md`;
- `docs/guide/diagram-authoring.md`;
- `docs/diagrams/README.md`;
- `references/audit-checklist.md`;
- `references/audit-report.schema.json`.

## Evidence hierarchy

Research the repository implementation, configuration, tests, subject matter, and current
written documentation as ground truth. Existing diagrams are legacy artifacts to assess,
not factual evidence. When a diagram conflicts with current code or docs, the diagram is
wrong.

## 1. Create a complete machine inventory

Run:

```powershell
python .github/skills/docs-diagram-audit/scripts/diagram_audit.py discover `
  --repo . --output docs-diagram-audit.json
```

The scan inventories every:

- source under `docs/diagrams/src/`;
- PNG and hash under `docs/diagrams/`;
- Markdown reference to a documentation diagram;
- missing, ambiguous, or unreferenced artifact.

Refreshing an existing report preserves reviewed dispositions, ownership, evidence,
status, path-change history, and tombstone records for removed/reused/merged diagrams and
concepts that no longer exist on disk. Do not edit diagrams during discovery.

## 2. Orchestrate GPT-6 Astra research

Use GPT-6 Astra for the audit coordinator's research workers and for downstream diagram
work. Partition the inventory into independent docs areas such as `guide`, `deep-dive`,
`experience`, `reference`, and `shared`.

For each area, launch a bounded GPT-6 Astra research agent that:

1. reads current repository sources and current written docs;
2. identifies the concepts the area must explain;
3. maps legacy diagrams to those concepts;
4. reports duplication, contradictions, stale visuals, missing coverage, and reusable
   canonical candidates;
5. returns file/line evidence for every recommendation.

Agents may run in parallel only when their diagram ownership does not overlap. Shared
concepts have one `shared` owner. Reconcile area findings centrally before changing files.
Write intermediate area research, planning, and reconciliation JSON to
`.github/skills/docs-diagram-audit/reports/`. Generated JSON in that directory is ignored;
only the report schemas are tracked.

## 3. Classify concepts and legacy diagrams

Classify every concept and diagram as:

- `retain`: keep the concept and its canonical identity;
- `reuse`: satisfy it with another named canonical concept/diagram;
- `merge`: consolidate it into a named target;
- `remove`: eliminate it because current docs no longer need it;
- `redesign`: keep the concept but replace the legacy visual;
- `unreviewed`: temporary discovery state only.

Every `reuse` or `merge` decision names its target. Record rationale and repository/current
docs evidence. Classification applies to concepts first; legacy diagram files are then
mapped to the chosen canonical concept.

## 4. Consolidate the canonical plan

Before authoring:

1. choose one canonical diagram name and owner for each surviving concept;
2. identify all pages that will reuse it;
3. preserve each existing stable PNG path during redesign;
4. record any intentional path change for a merge/reuse/remove, including every consumer
   that must be updated;
5. ensure parallel workstreams have disjoint source, image, hash, review, and report-entry
   ownership.

Do not create near-duplicate diagrams merely to give each docs page a local image.

## 5. Revamp surviving concepts

For every concept not removed:

1. invoke `docs-diagram-pitch` using GPT-6 Astra to research the repository and current
   written docs and create the grounded A5 pitch;
2. invoke `docs-diagram-iterate` using GPT-6 Astra for the required four-plus review
   passes, 9x meaningful pass-1 XML gate, correction-only later passes, and final arrow
   trace;
3. promote the final source/PNG while preserving the stable canonical path unless the
   approved consolidation plan intentionally changes it;
4. update all references and the machine report.

Run different canonical concepts in parallel only across isolated area worktrees. Pitch
and iteration for one concept remain sequential and owned by one writer.

## 6. Verify catalog integrity

After all selected workstreams are integrated, rerun discovery and final validation:

```powershell
python .github/skills/docs-diagram-audit/scripts/diagram_audit.py discover `
  --repo . --output docs-diagram-audit.json

python .github/skills/docs-diagram-audit/scripts/diagram_audit.py validate `
  docs-diagram-audit.json --final
```

Final validation requires:

- no orphaned source, PNG, or hash;
- no broken Markdown image reference;
- no ambiguous source basename;
- no unreviewed concept or diagram;
- every surviving concept completed both pitch and iterate workflows;
- every merge/reuse target exists;
- historical tombstones still point to a surviving target when required;
- stable output paths are preserved or an intentional path-change reason is recorded.

Run selective render/check commands and the docs build after integrity validation.

## 7. Emit both reports

Keep the validated JSON report as the machine-readable source of truth. Generate the human
summary:

```powershell
python .github/skills/docs-diagram-audit/scripts/diagram_audit.py summary `
  docs-diagram-audit.json --output docs-diagram-audit.md
```

The summary must include ground-truth research, disposition counts, canonical
consolidations, area ownership, intentional path changes, unresolved blockers, and catalog
integrity results.

Do not claim the audit is complete until every diagram/reference is inventoried and final
validation passes.
