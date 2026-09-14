# MCP lifecycle: choose one launch path: pitch review

Reviewer/model: GPT-6 Astra (`gpt-6-astra`). Record compiled after actual inspection;
earlier pitch/pass sources and exports are preserved, not overwritten.

Takeaway: Queue pickup and explicit starts are alternatives; inspection and approvals remain explicit.

The two-card pitch establishes the principal relationship without using legacy
images as truth. Actual `guide-example-scenarios-fig3-pitch.png` and its half-size `-pitch-print.png`
were opened and inspected. A5 landscape (827 x 583 draw.io units) fits the later
left-to-right lanes; portrait would compress tool names and create longer returns.
The pitch is deliberately an overview, not a padded denominator or final detail view.

## Content and research

`content-model.json` records source-backed nodes, edges, native/custom roles and
layout. Exactly three bounded Astra research threads cover the four diagrams:
`../canonical-aks-network/research-runtime.md`,
`../canonical-aks-network/research-network.md`, and
`../canonical-aks-network/research-workflow.md`.
No fourth research thread or live deployment mutation was needed.

## Visual system and asset rationale

Preserve Agentweaver Fluent: warm canvas #efeae7, surface #fdfbf8, group #f8f4f1,
Segoe UI titles/body, Cascadia metadata, fixed colored pills, 5-unit accent rails,
rounded cards, shadows, orthogonal gray arrows and bridge arcs. Marigold dashed
return is reserved for the retained next turn.

Native draw.io Kubernetes pod/Service/ServiceAccount/CRD icons represent those
resources; Gateway API and HTTPRoute use CRD, not a misleading Azure/Cisco device.
Azure Managed Identities and Key Vaults use bundled Azure2 SVG library assets.
Native UML actor, flowchart process/decision, cylinder and cloud primitives cover
their actual roles. Product-specific coordinator/broker/turn concepts use custom
hexagons. Do not force C4 or a network appliance stencil onto unrelated concepts.
Icons retain the bundled draw.io Desktop library attribution/terms; Azure product
icons are Microsoft assets, Kubernetes icons are Kubernetes-project assets.
No copied UI screenshots, external hotlinks or generated screenshot substitutes.

## Renderer

Official draw.io Desktop 31.4.5 Windows ZIP, SHA256
`d2c6f1eb4ed39fac9bb70fe6a1d359b8b9b01778145d65a68d29554994414207`,
checked against the release checksum and EXE product version.
Export: `--export --format png --border 16 --scale 2`.
Print evidence is the actual exported PNG downsampled by two (100 dpi), not a
separate rendering or a claimed physical-printer test. Uncompressed editable XML
is the source. Pass 1 growth: 2352 ->
26392 = 11.221088x; see `growth.json`.
