# Shared diagram execution: blocked, not publication-ready

Plan owner and authoring model: **shared / GPT-6 Astra**. No commits.
The completed coordinator pilot was not edited. Global audit, reconciliation,
inventory and other plan shards were not written.

## Counts and persistent results

| Disposition | Planned existing identities | Result |
| --- | ---: | --- |
| Retain | 10 | Canonical draw.io/PNG/hash migrations exist; initial-pitch contract still needs review |
| Redesign | 16 | 8 canonical migrations; 8 review-only candidates |
| Merge | 6 | All deferred until target publication and consumer gates are satisfied |
| Remove | 4 | Source, generated source, PNG and hash removed for all four |
| Reuse | 0 diagram identities | Concept-level reuse still appears in the audit contract |
| Proposed | 1 | Provider-admission pitch and four-pass candidate exist; no public canonical yet |

There are **27 current review manifests, 135 pitch/pass triples and 212 final
manifest arrow traces**. Eighteen canonical families have editable sources, stable
PNGs and pinned-renderer stamps; nine remain review-only. These are artifact counts,
not a claim that the full publication contract is complete.

`shared-concept-status.json` enumerates **all 96 owned concept IDs** with their
reconciled dispositions, document paths, targets and exact execution status:
17 written contracts corrected, 13 written integrations still diagram-gated,
and 66 retained-text concepts awaiting independent completion.

The four removed orphan identities are `a2a-bridge-fig2`, `events-observability-fig3`,
`landing-product-feature` and `token-usage-monitoring-fig1`. Their 16 existing
source/generated/PNG/hash files are gone. Tombstones are confined to the shared plan.
The independent tracked/nonignored non-Markdown scan and a final Markdown-image
scan found no consumers. Ignored/external consumers were not claimed to be known.

## Research and semantic corrections

Exactly three separate bounded Astra research agents grounded components, directional
flows and assurance against implementation/config/tests/current docs. Their original
component and flow results are preserved in `research-thread-01.md` and
`research-thread-02.md`; `research-thread-03.md` is an explicitly abridged preservation
of the third agent's returned result. Earlier worker notes saying the original
transcripts were not preserved are historical; this blocker is now resolved.

Shared-page changes correct Entra versus optional GitHub capabilities, current
OpenIddict/MCP endpoint classification, normative-versus-shipped sandbox credentials,
provider-bound execution semantics, durable cursor streaming/sequencing, all-inline
promotion fallback, private worktree dependencies, harness verdict fields and
workflow catalog/binder/selection facts. They do not change runtime code.

In particular, the no-repository-token sandbox contract is **not** certified as
implemented. Trusted configuration currently supplies repository material into
AgentHost/tool options for restricted direct commands. Historical GitHubLegacy,
raw GitHub product authentication and old organization middleware are explicitly
not current authentication. Collective coordinator merge is not mislabeled as
proven universal PR publication.

## Changed-path summary

- Eighteen `docs/diagrams/src/<name>.drawio` files and corresponding stable PNG/hash
  families, with their obsolete JSON/generated copies retired.
- `docs/diagrams/reviews/<name>/` for all 27 selected survivors/proposal:
  research, content models, pitch/pass pairs, records, manifests and validation.
- Three derived `docs/diagrams/email-exports/*.png` copies match their canonicals.
- Four orphan families removed; five target-gated merge families and the
  consumer-gated replay-tail family remain intact.
- Plan-owned pages: `CONTRIBUTING.md`, `docs/api-test-harness-plan.md`,
  `docs/architecture/decisions/0001-agent-worktree-isolation.md`,
  `docs/catalog-groupings.md`, the promotion/auth/two-App design documents,
  `docs/diagrams/README.md`, `docs/diagrams/email-exports/README.md`,
  `docs/mcp-oauth.md`, `docs/run-event-stream.md`, `docs/workflow-binder.md`,
  `docs/workflow-library.md`, `docs/workflow-selection.md`.
- Only the owned `plan-shared.json` execution entry/status/tombstones were updated.

## Validation

- Focused Node diagram pipeline suite: **27 passed**.
- Pitch/iteration validator regressions: **19 passed**.
- All 27 current manifests pass JSON Schema and the skill's cross-field validator.
- All 135 selected XML/PNG/record triples exist; XML is uncompressed, one-page A5,
  with no invisible/off-page/duplicate/padding findings. Every pass-1 ratio is >=9
  using `visible-semantic-canonical-xml-v1`.
- All 212 final manifest arrows match XML endpoints; UML activation endpoints are
  checked against their named participant's lifeline center. Lifelines without
  arrowheads are not falsely counted as messages.
- Eighteen selective canonical drift checks pass, including actual source/XML/PNG
  hashes, pinned 31.4.5 stamps and pixel identity with the inspected final pass.
  Derived email exports match. All selected image links on owned pages resolve.
- `canonical-workflow-authoring` initially used an invalid 1123x1587 page. The
  coordinator preserved that history, created `a5-final/` at 583x827, performed
  fresh ordered pitch + four exports with actual print/enlarged inspections,
  corrected native glyph/title overlap, retraced all 13 arrows and re-promoted it.
  Corrected meaningful growth is **4032 -> 37564 (9.316468x)**. The first failed
  HTML-scaling probe is retained and is not counted as a successful pass.
- The standard `npm run docs:build` failed on foreign
  `diagrams/reviews/reference-a2a-fig1/docs-build/agents/agentweaver.agent.md`.
  That other owner's output was not changed. A VitePress API build with
  `srcExclude: ['diagrams/reviews/**']` succeeded in 62.32 seconds. No global
  configuration or pipeline edits were used. After the foreign temporary output
  disappeared and this task's own temporary build directory was removed, a fresh
  standard **`npm run docs:build` passed in 86.56 seconds**. Build logs are preserved
  here; the transient build failure is no longer a blocker.
- The bounded link check still reports three pre-existing local-only contributor
  links: `.squad/templates/issue-lifecycle.md`, `.worktrees/` and
  `.squad/templates/worktree-reference.md`. They were not introduced by this change.

## Exact publication blockers / residual work

1. **Nine unpromoted candidates:** assistant-runtime-fig1, auth-security-fig1,
   auth-security-fig4, canonical-aks-components, canonical-memory-context,
   canonical-sandbox-boundary, canonical-sandbox-experience,
   sandbox-browser-preview-fig1 and canonical-provider-admission. Their sparse
   initial pitches lack required native/full-hierarchy composition. Preserve them,
   create compliant replacement lineages, then repeat ordered export/inspection and
   all gates before canonical promotion. Provider admission has no published path yet.
2. **Initial-pitch review of the other 17 migrated families:** numeric growth and
   final PNG checks do not establish full skill compliance. The sampled
   coordinator-journey pitch is a sparse generic-box overview; PM-discovery's
   pitch has native topology but lacks full Fluent hierarchy. Do not retroactively
   certify those pitches or call the migrated catalog publication-ready.
3. **Boundary visual defects:** the sandbox-boundary candidate visibly clips
   Execution boundary/Credential handling titles; AKS/resource notation still needs
   semantically appropriate native Azure/Kubernetes symbols rather than generic
   router/process substitutions. The required semantic/native redesign belongs in
   a new pitch/pass-1 lineage, not correction-only content expansion.
4. **Six pending merges:** five await compliant targets; replay-tail also awaits
   edits to `docs/deep-dive/frontend.md` and `docs/deep-dive/orchestration.md`,
   which are outside this plan's document scope. Retire both root AKS Excalidraw
   sources only with the AKS consolidation. No competing target or external
   consumer page was edited.
5. **Remaining concept integration:** finish the plan's concept-by-concept
   verification, including shared memory-entity/inbox reconciliation and consumers.
   Existing prose/table/inline concepts must not be converted to invented diagrams.
6. **Coordinator integration:** global inventory is intentionally unchanged. The
   standard docs build now passes; three pre-existing contributor local-link
   findings remain recorded rather than silently hidden.

`revamp-diagrams-shared` must remain **blocked**, not done.
