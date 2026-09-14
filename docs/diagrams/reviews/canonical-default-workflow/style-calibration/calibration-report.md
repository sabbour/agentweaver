# Default-workflow style calibration

**One non-public candidate completed. No public diagram assets were changed.**
The shared shard remains paused. Passing this calibration is not user approval
to resume mass authoring, nor an A5 publication claim.

## Deliverables

- Original: `original-react.png`, recovered byte-for-byte from
  `origin/dev:docs/diagrams/canonical-default-workflow.png` at commit
  `a46721ae77efeba505d2fbfa58c69cdae8b4f3eb`.
- Candidate: `candidate.drawio` and `candidate.png`, exported by draw.io31.4.5.
- Comparison: `comparison.png`, original left / candidate right.
  Both panels are exactly2140x2018; the composition is4280x2018.
  Neither panel was resized, stretched or resampled for comparison.

## Completed sequence

1. Recover original React PNG plus theme, node, edge, canvas, icon and type sources.
2. Two isolated GPT-6 Astra pre-edit assessments: design direction and mechanical/
   layout/type. Preserved in `pre-edit-assessments.md`.
3. Create one initial candidate using actual Fluent SVG primitives and the requested
   card/type/color tokens, not a generic native-symbol layout.
4. Inspect the actual initial PNG and equal-size comparison. Perform one batched
   correction: optical text-baseline alignment and rounded outer-canvas backing.
5. Export the corrected candidate. Record the owner's label-blinded comparison,
   then obtain a fresh-context blinded confirmation; only afterward reveal identities.

## Six-criterion result

Panel B was the draw.io candidate.

| Criterion | Owner | Independent confirmation |
| --- | --- | --- |
| Hierarchy | B preferred | B preferred |
| Density | Equivalent | Equivalent |
| Typography | Equivalent | B preferred |
| Card craft | Equivalent | B preferred |
| Routing | B preferred | B preferred |
| Overall polish | Equivalent | B preferred |

The owner recorded the assessment before reading the identity key or independent
result. Author familiarity limits complete perceptual blinding; that limitation
is explicit in `owner-blind-assessment.json`. The independent context saw only
the anonymous PNGs, without sources, metadata or earlier assessments.

No clipping or text overlap was found. Both versions preserve the broad inter-row
gaps of the reference; the candidate is not claimed to have dramatically increased
density. Return paths and label associations are clearer without a new layout.

## Deliberate, bounded differences

The PNG and recovered source do not encode the same historical metrics.
The PNG's cards measure about680x208 raster pixels; the requested400-unit width
at1.7x preserves680 pixels, while116-unit height yields197.2. Source title28 is
visibly larger than the original raster's title; it is not secretly downscaled
to fake identical typography. Row centers, margins and reading order are preserved.

Current runtime includes explicit PR publication between Merge and Scribe.
The sole extra copy is the short Merge subtitle "Then publish / reuse PR", with
a148-unit card. This is an explicitly collapsed sequential phase, not guaranteed
publication success. All other cards remain title-only; no metadata paragraphs,
badge inventory, oversized diagram title or native-library debug text was added.

The actual React icon primitives remain38-unit Fluent icons, not dominant generic
draw.io technology symbols. Editable card/text/connector cells remain separate.
There are11 external arrows; the Merge/PR phase intentionally collapses one internal
action boundary. All external source/target pairs and orthogonality were checked.

## Validation and freeze

`validation.json` verifies exact reference recovery, equal-size/pixel-exact comparison
panels, editable uncompressed XML, requested card/type/icon/accent/radius tokens,
11 external arrows, absent debug/prose blocks and unchanged hashes for all84
previously promoted public files. The calibration uses a custom original-aspect
artboard, not an A5 publication layout. The public A5 sources remain untouched.

The finalization decision applies only to SQL task `calibrate-diagram-style`.
`revamp-diagrams-shared` remains blocked pending approved shared calibration and
its existing consumer handoffs. No commit or promotion was performed.
