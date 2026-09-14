# Two isolated pre-edit assessments

Both assessments were requested before any candidate XML or PNG was created.
Each agent opened the recovered original PNG and read the original React sources.
They performed no writes and did not see one another's findings.

## Design direction - calibration-design-direction (GPT-6 Astra)

Returned assessment:

> Retain the visual language, not legacy topology.
>
> Hierarchy: cards dominate; short group labels remain secondary; no giant heading/prose.
> Density: 400x116/148 cards; consistent columns; gutters expand only for routing.
> Typography: Segoe title 28/600/1.15; subtitle 20/1.2; meta 16 monospace.
> Card craft: padding18/left22; Fluent38; radius16; accent5; warm surfaces;
> restrained badges/shadows.
> Routing: thin warm-gray orthogonals; rounded elbows; unmistakable junctions;
> labels adjacent to their routes.
> Overall polish: quiet canvas, aligned baselines, balanced margins, no debug chrome.
>
> Demonstrable improvement: clarify crowded return rails and floating
> review/blocked labels without enlarging cards.

## Mechanical/layout/type - calibration-mechanical-layout (GPT-6 Astra)

Returned measurements and findings:

- Original PNG: 2140x2018. Raster cards approximately 680x208, x=334/1126,
  row tops 162/734/1229/1647; gaps364/287/210. Top/bottom margins162/163.
- Raster corresponds to approximately 340x104 logical cards at 2x, NOT the
  recovered source's 400x116 constants. Do not claim source and PNG are identical.
- Requested cards use border-box width400, padding18/left22, radius16, accent5,
  ordinary border1; content starts27 units from the outer edge, text starts79
  after the38-unit icon and14-unit gap.
- Title28/600/1.15; subtitle20/1.2 with2-unit topgap; meta16 monospace.
  Content is vertically centered; icons align to the complete text group.
- Source CARD_HEIGHT_3=148 is declared but unused by the estimator.
  Honor the requested 116/148 contract explicitly.
- Edge #746d68/1.8, elbow10, marker16; labels18/600/1.15,
  #3f3935 on #fdfbf8, stroke1/#ece7e3, padding5/9, radius6.
- Bridges radius7 with18-unit elbow clearance; junctionradius2.5.
  Keep the Human/Merge overpass; unrelated crossings are not junctions.
- No badge text, subtitles, metadata, group headings or title banner are
  actually drawn in this original. Do not add badge text merely because data
  declares Runtime/Service.
- Source bridge construction can lose rounded elbows on bridged routes;
  retain intended rounded routing rather than treating that artifact as authority.

## Reconciliation before editing

Use the actual PNG's two-column layout, raster margins and row centers as authority,
while honoring the explicitly requested 400-unit card and typography tokens.
A 1.7x export maps400 to the original680 raster pixels. The new116 height gives
197.2 rather than208 pixels, a small deliberate tightening, not a hidden scale claim.
Do not add a heading, metadata catalog, giant groups or badges.

Runtime truth differs from the legacy graph: DefaultWorkflowTemplate.cs:78-94,149-160
requires Merge -> publish/reuse PR -> Scribe. Preserve the visual reading order by
making the Merge card a concise two-line phase ("Merge", "Then publish / reuse PR")
at148 units high. This explicitly collapsed phase is not a claim that publication
is guaranteed, or a reason to omit PR from the current workflow.

This is a non-public visual calibration artboard with original-aspect comparison,
not a newly approved A5 publication. The public A5 artifacts remain frozen.
