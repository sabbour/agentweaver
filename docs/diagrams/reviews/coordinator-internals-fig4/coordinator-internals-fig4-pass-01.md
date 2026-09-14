# Pass 1: blocked visual upgrade

**Not publication-ready. No canonical source or PNG promoted.**

Input: the saved pitch PNG, opened at exported detail and in its 794px-wide print-scale
derivative. Output: the distinct pass-01 PNG, exported by Desktop 31.4.5 and opened at
both those scales. These screen inspections do not claim a physical print.

Added tiered warm group surfaces and evidence-backed annotations for quiescence,
Build & Test applicability, non-veto preview failure, persisted-review recovery,
decline and advisory steering. Replaced the wrongly rendered circular database icon
with native `cylinder`. Moved explicit revision to an outer marigold rail, eliminating
its snapshot crossing. No transparent/off-page cells or metadata padding were added.

The actual repository checker reports:

| Metric | Result |
| --- | --- |
| Metric identifier | `visible-semantic-canonical-xml-v1` |
| Pitch meaningful XML | 17,984 |
| Pass-1 meaningful XML | 19,926 |
| Growth | 1.107985x |
| Required minimum | 9x |
| Visible structures | 62 -> 69 |
| Invisible / off-page / duplicate / padding defects | 0 / 0 / 0 / 0 |
| Publication gate | **FAIL** |

Nine times this intact pitch would require 161,856 measured units. This attempt does
not meet that threshold. The baseline has not been shrunk and the measurement has not
been relaxed. This result establishes an unmet gate, not a proof that every possible
redesign is impossible.

The improved PNG has clear arrowheads and no connector crossing that requires a bridge.
At the 794px screen approximation, small metadata and connector labels need further
print-readability consideration. Named group titles and the full monospace metadata
hierarchy are not yet complete. The normal human-gate continuation versus recovered
safety-escalation approval also needs explicit separation before claiming an exhaustive
review state machine; see `research-coordinator.md`'s normal/recovery distinctions.

## Candidate arrow review, not the required final pass-4 trace

Source key A is `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs`;
C is `apps/Agentweaver.Api/Coordinator/CollectiveAssemblyPipeline.cs`.

| ID | Source -> target | Meaning and evidence | Current inspection |
| --- | --- | --- | --- |
| input | eligible -> integrate | Ordered branch assembly; A:910-981 | Attached, rightward |
| check | integrate -> gates | Aggregate snapshot to applicable gates; A:981-984,1586-1630 | Attached, rightward |
| finish-gates | gates -> merge | Complete-after-approval; A:1678-1698 | Left gutter, no crossing |
| review | gates -> human | RED safety park or authored human gate; A:1131-1136,1198-1234,3757-3795 | Downward; shared-lane abstraction needs refinement |
| approve | human -> gates | Normal authored human approval continues; A:1443-1464 | Upward; cannot imply every recovered escalation repeats remaining gates |
| merged | merge -> complete | Merge then Scribe/completion; A:1747-1775 | Leftward |
| revise | gates -> steer | RAI REVISE, not terminal block; A:1139-1146 | Right outer rail, attached |
| changes | human -> steer | Request-changes submits scoped steering; A:1467-1482,2232-2397 | Downward, attached |
| escalate | steer -> human | Proceed opens durable human review; A:2372-2388,2845-2958 | Upward, attached |
| revision | steer -> integrate | Explicit revision effects precede subsequent assembly; A:2334-2370 | Rerouted outside; not an unconditional reset edge |

Passes 2-4 have not been fabricated or run. The iteration instructions require the
pass-1 growth gate before correction-only passes. `iteration-manifest.json` deliberately
records only the attempted pass and fails publication validation. All 28 surviving
catalog diagrams still require qualified final authoring, not merely a passing drift check.
