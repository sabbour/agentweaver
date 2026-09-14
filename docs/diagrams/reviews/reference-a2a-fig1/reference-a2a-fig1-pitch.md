# reference-a2a-fig1: pitch

Show platform callers on the left, the configured AgentHost boundary on
the right, and durable checkpoint ownership outside the pod. Transport controls
are annotations, not fictitious gateway services. Separate one-time configure,
card auth, turn auth, bridge dispatch, and worker-owned persistence. Keep
deployment-specific mTLS and additive ingress caveats visible without implying
SPIFFE/workload identity. The small initial pitch establishes that composition;
pass one adds the actual source-grounded structure.

Initial PNG opened and inspected enlarged and at A5 scale before post-pitch work. It is a sparse composition baseline, not the final design.

Exactly three bounded, read-only research agents ran as **GPT-6 Astra
(`gpt-6-astra`)**. No nested/additional research agents were used:

1. A2A/context: `reference-a2a-fig1/research-a2a.md`.
2. Persistence/sandbox: `reference-scaling-data-layer-fig1/research-scaling.md`.
3. Reference contracts/UI: `reference-a2a-fig1/research-contracts.md`.

Paths above are relative to `docs/diagrams/reviews`. Code, configuration, tests,
and current documentation supplied the factual model, not historical diagrams.
Their complete source maps are saved in those reports.

## Style, symbol classification and credits

The editable A5 landscape page is 794 x 559 logical pixels (210 x 148 mm).
The implementation follows `apps/web/src/theme.ts` and the repository's
`docs/diagrams/drawio/fluent-template.drawio`, `fluent-library.xml`, and
`design-system.json`: warm paper, elevated rounded cards, Segoe UI labels,
Cascadia metadata, colored accent rails/badges, muted boundaries, and rounded
orthogonal connectors. The template/library were loaded before authoring.
Hidden template markers were removed; no invisible padding was added.

Agentweaver-specific aggregate cards use custom Fluent composition because no
native library symbol represents a configured turn gate, credential snapshot,
checkpoint ownership boundary, or verified-writeback lifecycle. Kubernetes
pods/volumes use native `mxgraph.kubernetes.pod`/`vol`; process, document,
decision and database symbols use native `process`, `mxgraph.flowchart.document`,
`mxgraph.flowchart.decision`, and `cylinder3` as applicable. Scaling uses the
bundled Azure Fileshare image `img/lib/azure2/storage/Azure_Fileshare.svg`,
not a fabricated Azure logo. Symbols remain editable library shapes/images.

The [draw.io upstream license](https://github.com/jgraph/drawio/blob/dev/LICENSE)
is Apache-2.0. Microsoft retains its icon rights; the
[Azure architecture icon terms](https://learn.microsoft.com/en-us/azure/architecture/icons/)
permit architectural diagrams/documentation. The bundled Fileshare artwork is
used for that purpose without changing its design. No external icon download
was incorporated; a failed stencil-URL probe was not treated as verification.

All PNGs were exported by verified **draw.io Desktop 31.4.5**, using the official
recipe (PNG, scale 2, border 16), not a browser renderer. The executable under
the canonical-api-host review directory was used read-only; runtime cache was
isolated under the reference review directory.
