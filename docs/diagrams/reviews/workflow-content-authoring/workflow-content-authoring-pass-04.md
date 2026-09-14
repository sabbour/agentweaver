# Content Authoring workflow

Audience: workflow authors and operators. Takeaway: Authored graph • all YAML branches retained.
One editable, uncompressed A5 portrait page, 583 × 827 units at 100 units/inch.
Stable image: `docs/diagrams/workflow-content-authoring.png`; source: `docs/diagrams/src/workflow-content-authoring.drawio`.
Target documentation: docs/guide/workflows.md and docs/deep-dive/workflow-engine.md.

## Evidence and assets

Full node and connector evidence, line anchors, classifications, ownership and research handoff are in `evidence.json`. Source-backed YAML graphs are facts, not proposals. The seven catalogs preserve all authored edges and add no merge, PR or Scribe stages. Evaluation task steps remain prompts. Default publication creates/reuses a PR; it does not prove git push or successful publication. The selection diagram is trigger-agnostic and distinguishes overrides, silent count handling, bounded model retries and separate fallbacks. An all-code-review selector set ultimately uses its first entry; the main fallback badge abbreviates this last-resort detail.

Started with the committed Fluent template and loaded its library; removed invisible template metadata. Native flowchart process, decision, document and terminal shapes are framed by the repository's warm Fluent card system. Native shapes are bundled diagrams.net assets (Apache-2.0); no downloaded logos. Visual references: docs/diagrams/drawio/fluent-{template.drawio,library.xml}, docs/diagrams/README.md, apps/web/src/components/WorkflowGraphPanel.tsx.

## Research provenance

The parent supplied findings from three already-completed independent GPT-6 Astra research threads. Those findings were reconciled with direct reads of the current YAML, DefaultWorkflowTemplate, WorkflowSelector, CoordinatorOrchestratorExecutor and current documentation. No new agents were launched. The two full output files supplied under Temp were not accessed because this execution forbids any temporary-directory file operations; their full text remains a handoff residual, not a claimed local copy. The supplied assurance result independently reports zero catalog (from,to,label) differences, with 8/15/11/7/14/6/17 edges; evaluation is in the gate theory but not the bindability theory.

## Export and actual image inspection

Official draw.io Desktop 31.4.5, --export --format png --border 16 --scale 2. The actual exported PNGs were opened in the image tool. Print-size derivatives (583 × 827) were opened in three-diagram contact sheets; full exported PNGs were also opened for enlarged detail. These were raster inspections, not XML/editor proxies. Native bridge arcs were checked at rail crossings. No page, inventory, global report, product or pipeline edits were made by this author.

## Pass 4: correction-only

Correction-only final audit. No permitted defect remained after pass three, so this independently saved source is byte-identical and was freshly exported by the official CLI. The new PNG was opened at print size and enlarged. Every connector below was traced from its actual source port, across its route and any native bridge, to its target arrowhead. No false junctions, reversed arrows, clipped labels or card-crossing routes remained.

## Complete final arrow trace (11 connectors)

| Connector | Source → target | Relationship | Evidence | Direction / endpoints / route | Result |
|---|---|---|---|---|---|
| edge-01 | research → draft | unconditional advance | `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:82–83` | source bottom → target top; downward gutter; block arrow at target | clean |
| edge-02 | draft → edit | unconditional advance | `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:85–86` | source bottom → target top; downward gutter; block arrow at target | clean |
| edge-03 | edit → rai-check | unconditional advance | `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:89–90` | source bottom → target top; downward gutter; block arrow at target | clean |
| edge-04 | rai-check → draft | revise | `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:93–95` | source left → dedicated marigold outer rail → target bottom; separate arrowhead; native bridges at crossings | clean |
| edge-05 | rai-check → terminal-safety-failed | safety-failed | `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:97–99` | source right → distinct outcome rail → intended target side; block arrow; native bridges, no junction implication | clean |
| edge-06 | rai-check → done | no-changes | `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:101–103` | source right → distinct outcome rail → intended target side; block arrow; native bridges, no junction implication | clean |
| edge-07 | rai-check → human-review | review | `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:105–107` | source bottom → target top; downward gutter; block arrow at target | clean |
| edge-08 | human-review → publish | approved | `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:109–111` | source bottom → target top; downward gutter; block arrow at target | clean |
| edge-09 | human-review → draft | request-changes | `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:113–115` | source left → dedicated marigold outer rail → target bottom; separate arrowhead; native bridges at crossings | clean |
| edge-10 | human-review → terminal-declined | declined | `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:117–119` | source right → distinct outcome rail → intended target side; block arrow; native bridges, no junction implication | clean |
| edge-11 | publish → done | unconditional advance | `packages/Agentweaver.Squad/Catalog/Resources/workflows/content_authoring.yaml:121–122` | source right → distinct outcome rail → intended target side; block arrow; native bridges, no junction implication | clean |
