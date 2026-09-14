# Reference plan execution review

**Status: blocked on generated catalog source wording, not diagram publication.**

Two diagrams were redesigned, one obsolete preview diagram was merged/retired,
and 21 of 23 owned documents were corrected. Of 74 assigned concepts, 73 are
complete and one generated-catalog concept remains blocked. Dispositions:
24 reuse, 46 retain, two redesign, one remove, and one merge. The unassigned
provider-context visual remains a shared placeholder; its consumer uses API
prose instead of inventing a bitmap.

## Published evidence

| Diagram | Meaningful pass-one growth | Post-pitch passes | Final trace |
| --- | --- | --- | --- |
| reference-a2a-fig1 | 1,686 to 20,854; 12.368921x | 4 | 5 connectors |
| reference-scaling-data-layer-fig1 | 1,703 to 20,413; 11.986494x | 5 | 9 connectors |

Both surviving sources are uncompressed editable A5 landscape draw.io, with
the full Agentweaver Fluent composition and native library symbols. Initial
pitch and every post-pitch PNG were opened enlarged and at A5 scale. Pass one
was the substantial visual upgrade; passes two through four were correction
only. Scaling's final enlarged inspection caught the temporary-ref label close
to the adjacent fetch elbow. A fifth correction-only pass moves it down 25
logical pixels, clear of the unrelated connector. Label text/placement
corrections are the only post-pass-one XML changes.
All final orientation, overlap and arrow defect counts are zero.

Exactly three bounded GPT-6 Astra research agents supplied independent
code/configuration/test/current-document evidence. The saved research reports,
pitch records, pass records, XML, PNGs and print-scale companions preserve the
review history. Historical diagrams did not supply factual authority.

- [A2A iteration manifest](./iteration-manifest.json)
- [Scaling iteration manifest](../reference-scaling-data-layer-fig1/iteration-manifest.json)
- [Preview merge tombstone](../reference-sandbox-pods-fig1/merge-tombstone.json)
- [Per-concept execution and validation](./execution.json)

Canonical sources are `docs/diagrams/src/reference-a2a-fig1.drawio` and
`docs/diagrams/src/reference-scaling-data-layer-fig1.drawio`. PNGs and version-2
hash stamps retain those basenames in `docs/diagrams/`. The old preview consumer
was changed before its four source/generated/image/stamp assets were removed.
The replacement `sandbox-browser-preview-fig1` assets were never modified.

## Validation and limits

All 27 diagram-pipeline tests and 14 iteration tests passed. Both manifests pass
JSON Schema and cross-field publication gates. Existing meaningful-XML checks
pass without invisible/off-page/duplicate/padding findings. Independent
draw.io Desktop **31.4.5** exports are byte-identical to the inspected final
PNGs; canonical source/raster/stamp drift checks pass.

All 190 local links, including 45 heading fragments, across the 23 owned pages
resolve using the site's VitePress heading rules. Nine external URLs were not
network-probed by that local link validator. The full VitePress site builds
with its existing config/theme and no page exclusions; output, temporary
bundles and caches were redirected into this owned review directory.
Existing Fluent UI directive, large-chunk and PromQL highlighting warnings
are recorded in the build log. Transient build/cache directories are removed
after validation; logs and result records remain.

The generated-doc check reports all five outputs in sync. This does **not**
prove their source wording is factually current: `mcp-tools.md` still excludes
preview from safe auto-approval and promises a full parameter reference in
the curated MCP page. Fixing those statements durably requires
`apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs` and `scripts/gen-docs.mjs`,
which are outside this task's write scope. The generated file was not hand
edited to manufacture a passing result.

No global inventory/audit/reconciliation, plans, pipeline/skills, product code,
shared/pilot/deep-dive assets, or commits were changed. Reference report entries
retain their historical audit evidence and append bounded execution outcomes;
the unassigned report concept remains unchanged.
