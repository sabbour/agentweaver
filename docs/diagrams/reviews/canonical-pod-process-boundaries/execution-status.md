# Deep-dive execution plan: blocked, not publication-ready

This is a partial execution record, not a completed diagram migration.
The authoritative plan and global audit reports remain unchanged.

## Counts and outcomes

| Item | Result |
| --- | --- |
| Independent bounded research agents | Exactly 3, all GPT-6 Astra |
| Plan-owned consumer pages updated | 13 |
| Concept dispositions recorded | 54; completion is not certified |
| Merge consumer migrations | 4 |
| Obsolete asset sets removed | 4 sets, 16 files; originals preserved under `initial` |
| Intended surviving diagrams | 20: 19 existing plus the proposed process-boundary diagram |
| Pitches exported and inspected | 20 |
| First-upgrade exports and inspections | 20; all opened enlarged and on A5-scale contact sheets |
| Editable A5 draw.io / PNG pairs checked | 40 |
| Qualifying first-upgrade passes | 0 |
| Measured meaningful XML growth | 3.304030x-4.095160x; required 9x |
| Correction passes 2-4 / final every-arrow trace | Not performed |
| Promoted canonical diagrams | 0 |
| Incomplete iteration manifests | 20; fail publication validation |
| Focused diagram tests | 27 passed, 0 failed |
| Follow-up docs build | Passed with an execution-owned temporary SSR directory |
| Owned-page relative file links | 132 checked, 0 missing; fragment anchors not checked |
| Existing canonical drift | 19 passed; proposed canonical diagram still absent |

The failed first-upgrade gate prevents moving to correction-only passes.
Adding redundant cells, hidden content, artificial metadata or invented facts
would not be an acceptable workaround. The frozen pitch baselines were not
replaced to lower the denominator.

## Files

The changed consumer pages are `a2a-bridge.md`, `agent-communication.md`,
`agent-runtime.md`, `agent-token-delivery.md`, `distributed-execution-scaling.md`,
`events-observability.md`, `infra-deployment.md`, `live-preview-provisioning.md`,
`sandbox-browser-preview.md`, `sandbox-pod-execution.md`, `sandbox.md`,
`sandboxed-execution.md`, and `token-usage-monitoring.md`, all under
`docs/deep-dive`.

The four reconciled merges are:

| Removed owned diagram | Replacement |
| --- | --- |
| `agent-communication-fig5` | `canonical-agent-communication-shared` |
| `distributed-execution-scaling-fig1` | `canonical-sandbox-pod-evolution` |
| `infra-deployment-fig1` | Stable shared `canonical-aks-components` reference |
| `sandbox-pod-execution-fig3` | `sandbox-fig2` |

For each removed name, the owned source JSON, generated draw.io, PNG and hash
were removed only after checking consumer references. Legacy copies remain in
that name's owned review directory. No shared replacement was edited.

All pitch/pass sources, PNGs, inspection records, change records and incomplete
manifests are under the corresponding `docs/diagrams/reviews/<name>` directory.
The proposed `canonical-pod-process-boundaries` exists only as a review draft,
not as a published source, embed, PNG or hash.

## Evidence

- `research-runtime.md`, `research-events.md`, `research-preview.md`: three
  independent source/config/test-grounded findings with citations.
- `content-models.json`: intended nodes, relationships and evidence for 20 drafts.
- `validation-results.json`: exact growth counts, XML/PNG checks, SHA-256 hashes,
  inspection truth values, manifest rejection and 132 relative-link checks.
- `canonical-drift.json`: focused drift results for the 20 intended targets.
- `concept-progress.json`: all 54 owned concepts, without false completion flags.
- `focused-tests.log`: successful 27-test diagram suite.
- `docs-build.log`: initial build failed with temporary screenshot cleanup
  `ENOTEMPTY`; `docs-build-retry.log` records the successful isolated-TEMP retry.
- `docs-build-final.log`: final build after remaining prose/link corrections.
- `docs-build-followup.log`: build after the continued first-upgrade inspections
  and their updated review records; failed on a missing shared `.vitepress/.temp`
  page module, consistent with a concurrent build collision.
- `docs-build-followup-isolated.log`: successful retry through the same VitePress
  build API and unchanged repository configuration. The `onAfterConfigResolve`
  hook redirected only temporary SSR output into this owned review directory;
  the final site output remained in session storage. No pipeline edit was needed.
- `pitch-print-sheet-1.png` through `pitch-print-sheet-5.png`: inspected A5-scale
  contact sheets. Individual pitch PNGs were also opened enlarged.
- `pass-01-inspections.json` and `pass-01-print-sheet-1.png` through
  `pass-01-print-sheet-5.png`: continued inspection of all 20 first upgrades,
  with individual findings and the first-sample progress notification.

`author-execution.py` is a review-only draft builder, not a replacement pipeline.
`audit-progress.py` reproducibly measures the saved artifacts and records the
blocked state; its zero exit code means evidence collection succeeded, not that
the publication gates passed. `correct-owned-prose.py` records the one-time
assertion-checked prose migration and must not be rerun against corrected text.

Exports used the official portable draw.io Desktop **31.4.5**, executable product
version `31.4.5.0`, with PNG border 16 and scale 2. XML is uncompressed, one-page
A5 landscape at 793.70 by 559.37 draw.io units. All 40 PNG files decode and their
source/PNG hashes are recorded; they are not approved canonical drift stamps.

## Remaining blockers

1. Every first-upgrade draft fails the mandatory 9x meaningful XML gate.
   Current generic-grid composition is not the requested final visual design.
2. Initial enlarged inspection found connector/card collisions and incorrect
   resource classifications, including generic Pod symbols for other Kubernetes
   objects. The proposed process diagram lacks the required nested container
   and mount-view boundaries. Native notation and asset credits are not approved.
3. First-upgrade PNG inspection is complete and records remaining defects;
   correction-only passes 2-4 and a final every-arrow trace do not exist.
   The incomplete manifests must fail.
4. Existing published survivor images are still legacy visuals. Prose corrections
   do not certify those images; all 20 intended survivors still need final
   source/PNG/hash promotion and owned consumer provenance reconciliation.
5. Research identified shared-owner concerns for `canonical-aks-components` and
   `sandbox-browser-preview-fig1`. Their current owner must reconcile those
   findings; this session did not change or certify shared assets.
6. Inventory shards/global audit reports are outside the explicit write scope.
   Their merge/removal coordination remains with the catalog owner.

No product/runtime code, pipeline, skills, global audit files, other plans,
coordinator pilot or shared-owned assets were edited by this execution.
The shared worktree already contained unrelated changes, which were preserved.
No commits were created.
