# Collective assembly pitch

Status: inspected draft, not publishable. Research completed by exactly three separately
launched GPT-6 Astra agents; no legacy diagram used as factual evidence.

## Contract

Audience: implementers tracing collective review, not standalone or child execution.
Takeaway: eligible child content passes applicable workflow-authored checks; collective
RED opens durable human review, while REVISE enters explicit steering.
Target: `docs/deep-dive/coordinator-internals.md`, reused by `review-merge.md`.
Stable image: `docs/diagrams/coordinator-internals-fig4.png`.
Canonical source after successful iteration only:
`docs/diagrams/src/coordinator-internals-fig4.drawio`.
Page: one A5 landscape page, 794 by 559 draw.io pixels, page scale 1.

## Grounding

The node/relationship table in `research-coordinator.md`, section
`coordinator-internals-fig4`, supplies current implementation and test evidence.
The two other independent bounded results are
`../canonical-workflow-invocation/research-workflows.md` and
`../team-casting-fig1/research-supporting.md`.

The cross-thread findings agree: collective RED is not standalone terminal safety
failure; approval continues remaining authored gates; effective workflow resolution
does not inject configurable project policy. No runtime/workflow edit is proposed.
The summary abstraction intentionally leaves gate-specific exceptions in the page.
The returned revision arrow means an explicit revision effect followed by subsequent
assembly, not a direct graph edge or an unconditional reset of every child.

## Visual reference and credits

Derived from repository `docs/diagrams/drawio/fluent-template.drawio`,
`fluent-library.xml`, and `design-system.json`; warm palette, rounded 16px cards,
5px accents, shadows, semantic badge tones, Segoe UI and orthogonal connectors.
Current product theme inspected: `apps/web/src/theme.ts:1-104`.
Template's invisible sample library marker and unrelated Azure/Kubernetes examples
were removed, not counted. No external logo, image payload or third-party asset added.
Built-in diagrams.net process, decision, terminator and database symbols use the
existing pinned Desktop distribution (drawio-desktop, Apache-2.0); no separate logo
license is implied.

Classification: eligible/integrate/merge icons are `native:flowchart` processes;
gates/steer use `native:flowchart` decisions; completion uses a native terminator;
human review attempts `native:database` inside `custom:agentweaver` review chrome.
All seven product-specific card envelopes are `custom:agentweaver`.
No Azure, Kubernetes, network or person icon is substituted for a different concept.

## Actual PNG inspection and handoff

Export: official draw.io Desktop 31.4.5, PNG, border 16, scale 2.
Opened the actual `coordinator-internals-fig4-pitch.png` in the image viewer.
The full image has readable hierarchy and labels. Review arrows correctly distinguish
RED from REVISE and approval from merge. The human-review icon rendered as a circle,
not a recognizable durable database: correct it in pass 1.
The revision return rail crosses the snapshot connector without a convincing bridge
and places its label in the central routing gutter: reroute it outside the composition.
Also opened `coordinator-internals-fig4-pitch-print.png`, the 794px-wide screen
approximation of A5. The hierarchy remained legible, with small secondary labels
needing further print-readability review. This is not a claim of physical printing.

Pass 1 must add meaningful source-backed detail, correct those defects, and run the
unaltered >=9x semantic XML checker. Do not pad XML, shrink the saved pitch baseline,
invent content, or promote a source with an unmet gate.
