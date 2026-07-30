---
name: "agentweaver-demo-creative-direction"
description: "Direct Agentweaver demos, sizzle reels, and walkthroughs. Use for pacing, camera moves, speed ramps, transitions, captions, music, DOM capture cues, or polishing a recording."
domain: "video-production"
confidence: "high"
source: "verified research and repository analysis 2026-07-30"
allowed-tools: Bash(ffprobe:*) Bash(ffmpeg:*)
---

# Agentweaver demo creative direction

Use this skill to turn real Agentweaver UI activity into a concise, readable product
story. It governs editorial intent, capture evidence, and post-production decisions.

For authentication, staging login, `playwright-cli`, and recording mechanics, read
[`agentweaver-demo-recording`](../agentweaver-demo-recording/SKILL.md). Do not duplicate
or improvise its auth workflow.

For detailed research, citations, system timing evidence, and schema rationale, read:

- [Initial creative-director research](../../../.squad/decisions/inbox/link-creative-director-shot-directing-scheme-for-demo-s.md)
- [Grounded capture-first design](../../../.squad/decisions/inbox/link-creative-direction-v2-grounded.md)

Do not copy a previous video's beat order or timings. Apply these rules to the new
story, target runtime, and measured capture.

## Core workflow

Work in this order:

1. Define the narrative arc and evidence each beat must prove.
2. Author soft output budgets, DOM cue definitions, and framing intent.
3. Record complete real behavior and generated cue evidence.
4. Inspect actual cue timestamps, rectangles, frame timing, and dead intervals.
5. Author the take-specific director cut.
6. Render, review, and adjust against the real footage.

Do not promise source durations or screen coordinates before runtime-dependent behavior
has occurred. The final runtime is an editorial constraint; it is not a capture timeout.

**Verified 2026-07-30:** real Agentweaver child work is designed for multi-minute runs,
preview readiness includes variable build/approval/publication waits, and trace loading
does not continuously poll. Capture-first/direct-after is therefore required for
trustworthy timing.

## Pacing arc

Start with this reusable 100% budget:

| Arc | Starting share | Purpose |
|---|---:|---|
| Hook | 5% | Show immediate product proof; avoid logo pre-roll. |
| Orient | 25% | Establish project, actors, goal, and governing context. |
| Accelerate | 28% | Move briskly through setup, navigation, and composition. |
| Payoff | 38% | Hold on execution, running output, observability, and shipped result. |
| CTA | 4% | Resolve on one stable branded action or promise. |

Treat these as a starting model, not fixed law:

- Keep the hook around 5–8% and the CTA around 3–5%.
- Give the payoff roughly one-third to two-fifths of the runtime.
- Reclaim time from route changes, menus, and repeated setup before shortening proof.
- When runtime expands, add time to readable results and live state changes; do not
  linearly stretch every beat.
- When runtime shrinks, remove redundant steps or whole intermediate shots before
  accelerating every moment.
- Vary rhythm inside dense beats: establish, act, reveal, hold.

Calculate each beat's output budget from the target runtime, then revise after capture.
Use a preferred/minimum/maximum range for variable beats rather than a single promise.

## Camera direction

### Default framing

Use full-frame by default. It preserves navigation context, relationships, graph
structure, board columns, and evidence that a preview is genuinely running.

Zoom only when the viewer otherwise cannot read the proof:

- Use approximately **1.15–1.35×** for most UI emphasis.
- Allow up to **1.5×** only for genuinely small text, badges, or controls.
- Keep enough surrounding UI visible to identify the page and action.
- Return to full-frame when relationships or simultaneous state matter.

### Pans

Pan while remaining zoomed only when attention must move between related regions in one
continuous explanation—for example, across a visual workflow or from a coordinator node
to an active child cluster.

- Keep graph-wide pans restrained, commonly around **1.05–1.12×**.
- Use roughly **400–700ms** eased movement, scaled to distance.
- Prefer one sustained zoom with pan points over repeated zoom-out/zoom-in churn.
- Do not pan merely to make a static screen feel cinematic.

### Capture versus post camera

Never apply both a browser-body capture zoom and a post-production camera move to the
same shot. For director-cut profiles, record an unwarped full viewport and apply camera
movement in post from captured DOM rectangles.

Use cue-anchored targets, never guessed pixel coordinates:

- full-frame requires no target;
- zooms target one captured cue rectangle;
- pans interpolate between captured cue rectangles;
- moving targets may emit multiple rectangle samples.

## Speed and holds

Keep these moments at **1×**:

- the beginning and ending of meaningful typing;
- visible state transitions and the first readable result;
- approvals and confirmations;
- drag/drop and other causal pointer actions;
- live response onset when “in action” matters;
- trace inspection and other proof that must be read.

Candidates for acceleration:

- route changes, tabs, menus, and repeated navigation: usually **2×**;
- the middle of long typing or repetitive setup: usually **2×**;
- short spinners or visibly repetitive progress: up to **4×** when still legible;
- multi-minute work: select meaningful activity windows and cut empty waits.

For measured long intervals:

- up to 4× continuous footage is normally reviewable;
- 4–12× requires visual review to confirm state changes remain legible;
- if more than 12× would be required, prefer activity-window selection and hard cuts.

These are editorial guardrails, not system-performance claims. Derive the required ratio
from actual source cues.

Use approximately **180–250ms** ramps for 1×↔2× changes and **250–400ms** for stronger
changes. Return to 1× around 0.75–1 second before the reveal. Hold a meaningful result
long enough to read—commonly 1.5–3 seconds, longer for traces or dense output.

## Transitions

Use this hierarchy:

1. **Hard cut** — default for product-to-product progression.
2. **Semantic match cut** — use only when geometry, status, or cause visibly connects the
   two shots.
3. **Native UI motion** — let panels, dialogs, graphs, and previews provide continuity.
4. **Directional transition** — rare; only when real captured motion supports it.

Do not use body cross-dissolves as generic polish. They soften responsive UI changes and
were not supported as a dominant convention by the grounded reference review. A final
fade-to-black after the CTA is acceptable because it closes the video rather than
blending two product states.

Avoid decorative wipes, cube transitions, gratuitous whip-pans, and effects that draw
attention away from the product.

## Headlines, subtitles, and safe framing

Marketing headlines and accessibility subtitles serve different purposes:

- A headline is optional, short—usually 2–7 words—and expresses the benefit or proof.
- Do not repeat narration word-for-word.
- Enter with restrained motion such as a 150–200ms fade-up; exit cleanly.
- Keep headlines in a consistent safe zone, usually top-left, after checking that they
  do not cover navigation, dialogs, graph nodes, or status badges.
- Accessibility subtitles reproduce spoken content, follow subtitle timing/line-length
  conventions, and should also be delivered as a sidecar even when a social export burns
  them in.

## Music and sound

Use narration plus a restrained instrumental bed for a sizzle reel. Let music rise in
nonverbal proof moments and duck beneath narration. Use SFX only for meaningful actions
such as approval, ready, completion, or selection; avoid cartoon clicks and swooshes.

Choose music with:

- no lead vocal;
- enough spectral space for speech;
- clear build, lift, lower-density section, and resolve;
- alternate mixes or stems when available.

A 105–120 BPM instrumental electronic/minimal brief is a useful search starting point,
not a requirement. Measure and cut to the selected track's real accents.

Do not render external music without a license manifest containing at least:

```json
{
  "trackId": "vendor-stable-id",
  "title": "Exact title",
  "artist": "Exact artist",
  "source": "Vendor or archive",
  "sourceUrl": "https://...",
  "licensePlan": "Exact plan or CC license",
  "licenseVersionOrDate": "YYYY-MM-DD",
  "downloadedAtUtc": "ISO-8601",
  "project": "Agentweaver demo name",
  "permittedSurfaces": ["website", "youtube", "social", "events"],
  "attributionRequired": false,
  "attributionText": null,
  "assetSha256": "...",
  "evidenceFiles": ["receipt-or-certificate", "saved-license-terms"]
}
```

Prefer a commercial synchronization license that explicitly covers the intended
distribution. For zero-budget music, use CC0 or CC BY with correct attribution. Avoid
NC, ND, and SA music for product promotion unless separate written permission and legal
review make the use acceptable. Fail closed when music is configured without evidence.

## Three-layer storage format

Keep the existing beat markdown as the narrative source. Store direction beside it:

```text
scripts/demo-recording/plans/<name>-beats.md
scripts/demo-recording/plans/<name>.capture.json
scripts/demo-recording/plans/<name>.direction.json
recordings/raw/<name>/<take-id>/capture-cues.json
```

### `<name>.capture.json`

Authored before recording. Contains:

- beat IDs and soft output budget ranges;
- required/optional DOM cues;
- framing and pacing intent;
- required holds and allowed treatments;
- prerequisites and failure cues.

It must not contain source timestamps, predicted live coordinates, or backend event
subscriptions.

### `capture-cues.json`

Generated during/after recording. Contains immutable evidence:

- take ID, source hash, viewport, DPR, and capture clock;
- actual cue timestamps;
- selector/attribute/text/predicate evidence;
- CSS-pixel and normalized rectangles;
- actual video frame PTS extracted with `ffprobe`;
- pointer/click tracks when useful.

### `<name>.direction.json`

Authored or generated after inspecting the take. Contains:

- resolved source segments and output durations;
- cuts, holds, and playback rates;
- camera keyframes targeting cue rectangles;
- transitions, headlines, subtitles, music, ducking, and SFX;
- the take/cue manifest it was resolved against.

Keep camera keyframes and speed/edit segments separate. A full-frame wait may accelerate,
while a zoomed proof moment may remain 1×.

### Take analysis

Run the read-only analyzer after capture and before approving direction:

```text
node scripts/demo-recording/cli.mjs analyze-take \
  --video <raw.webm> \
  --capture-plan <scenario.capture.json> \
  --cues <capture-cues.json> \
  --activity-log <activity.json> \
  --beat-id <beat-id> \
  --out <take-analysis.json> \
  --draft-direction <scenario.direction.draft.json>
```

The analyzer maps DOM cues to actual `ffprobe` frame PTS, warns rather than rejects when
cues are missing or out of order, flags cue-to-frame drift above 500ms, measures budget
pressure, and classifies intervals as `action`, `wait`, or `dead-time`. It may seed a
`draft-suggestion` direction file, but that file is never approved automatically.

Keep action and readable proof legible. Accelerate waits only up to 12× continuously;
above that, select meaningful activity windows and hard-cut the gaps. Remove dead-time.

## DOM-only cue detection

The harness observes rendered UI only. Do not tap Agentweaver SSE, coordinator events,
run logs, or backend state types.

Allowed cue sources:

- `selector`: first visible match;
- `attribute`: first target attribute value/change;
- `text`: first declared text match;
- `predicate`: validated DOM operations such as `count-gte`,
  `any-attribute-in`, or `all-attribute-in`.

### Blocking cues

Extend existing `waitFor` and `waitText` capture steps with an optional named `cue`.
When the wait resolves:

1. preserve the generic activity mark used by idle trimming;
2. emit the semantic cue;
3. capture `getBoundingClientRect()` for the matched or explicitly targeted element;
4. store CSS pixels, normalized coordinates, viewport, and DPR.

Require a selector or explicit rectangle target when a text cue needs a rectangle.

### Passive cues

Install one lightweight in-page `MutationObserver` through the existing
`addInitScript` bootstrap pattern:

- evaluate declared watchers immediately;
- observe child, attribute, and text mutations;
- coalesce bursts before evaluating unfired watchers;
- emit only the first match for each globally unique cue;
- optionally require `stableForMs`;
- report missing required cues during take validation rather than serially blocking
  every other watcher.

Send cues to a Node-side Playwright binding so the log survives reloads and cross-origin
navigation. Reconfigure page-local watchers after explicit `goto` operations.

Use one shared capture clock and map cue times to actual video frame PTS. Keep browser
observation time for diagnostics, but use the calibrated take timeline for editing.

### Markup contract

Inspect current frontend markup before authoring selectors. Never invent a `data-*`
attribute in a capture plan and assume it exists.

If rendered state lacks stable selectors, add nonvisual frontend observability attributes
as a prerequisite—for example stable graph/node/status attributes or trace
panel/span/selection attributes. Prefer these over generated CSS classes, localized text,
React Flow internals, or backend state access.

**Verified 2026-07-30:** current topology and trace components expose visible/accessibility
content but lack the full stable `data-*` contract needed for reliable status and
selection watchers. The detailed gaps and proposed attributes are recorded in the
grounded decision linked above.

## Review checklist

Before approving a cut, verify:

- the runtime budget emphasizes proof rather than navigation;
- variable beats were directed from measured cues;
- full-frame remains the default and every camera move has a reason;
- no shot combines capture-time and post-production zoom;
- speed changes preserve causal boundaries at 1×;
- transitions follow the hard-cut/match-cut hierarchy;
- headlines do not duplicate narration or cover product evidence;
- subtitles remain available as an accessibility sidecar;
- music has a complete license manifest and evidence;
- required DOM cues fired exactly once with valid rectangles;
- source frame timing, narration, music, and rendered output remain synchronized.
