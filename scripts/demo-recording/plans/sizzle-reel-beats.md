# Sizzle Reel — Master Beat Plan

This is the **single committed source of truth** for the third Agentweaver demo — the
short **sizzle reel** assembled from real captured footage taken from the finalized
blueprint demo and Azure/AKS live-repo demo. It is parsed by `lib/beats.mjs`'s
`loadBeatPlan`.

Each `## Beat X.Y — Title` heading starts a beat. `Narration: "..."` is the voiceover
script for that beat.

This plan is intentionally **editorial, not capture-time predictive**:

- **No music.**
- **No dissolves or cross-fades.**
- Use **hard cuts** or native UI motion only.
- Every cut should land on a **DOM-grounded visual cue**, not a guessed timestamp or a
  project-specific backend event.
- Treat cue timing with the creative-direction skill's lenient policy: preserve action,
  accelerate waits only when readable, remove dead-time, and tolerate up to about **0.5s**
  cue drift before treating it as a mismatch.

Because this reel is assembled from Scenario 1 and Scenario 2 footage rather than one
continuous live run, each beat includes a **Continuity** note naming the source beat(s)
and the DOM-visible state that justifies the cut.

## Beat 0.1 — Hook the viewer

Narration: "This is Agentweaver — a team of agents working in the open."

DOM cue: Cut in when a live run view is already visible and at least one topology node is visibly active, selected, or changing state.

On screen: Open on the most visually active topology shot from the finalized footage — moving status, live graph structure, and coordinator context already on screen. Hold just long enough to establish that the work is happening now.

Continuity: Source footage from Scenario 1 beat 2.3 or Scenario 2 beat 3.1. Start on a fully rendered run page; do not pre-roll through navigation. Hard cut out on the next stable rendered UI state.

## Beat 1.1 — Create the project

Narration: "Start with a project, then point the team at the job."

DOM cue: Cut in when the create-project or repo-to-project dialog is fully rendered and the primary form fields are already visible.

On screen: Use the cleanest create-project footage from Scenario 2's repo-to-project flow, with the project form, repository target, and blueprint path readable without retyping any setup more than once.

Continuity: Source footage from Scenario 2 beats 0.1 and 1.1. Cut from the hook to the first fully rendered create-project state; cut out once the project creation confirmation visibly lands.

## Beat 1.2 — Show the team

Narration: "Every role is specialized, and every role starts with the right skills."

DOM cue: Cut in when the Agents or Skills view is fully loaded and at least one agent card or skill tile is clearly readable.

On screen: Show the roster, then the skills surface — enough to prove role, model, and skill assignment without lingering on any one modal. Prefer the footage that best shows both the team cast and the skill source options.

Continuity: Source footage from Scenario 1 beat 1.3 plus Scenario 2 beat 1.2 or 2.3. Hard cut from project creation once the project shell is visible; cut out on a stable Agents/Skills state rather than mid-modal dismissal.

## Beat 1.3 — State the goal

Narration: "Describe the work in plain language."

DOM cue: Cut in when the assistant or task entry surface is focused and the prompt area is visibly ready for input.

On screen: Use the live typing shot where the goal or task brief is entered. Keep the start and end of meaningful typing at 1×; if the middle of the typing run is long, trim or accelerate only the repetitive middle.

Continuity: Source footage from Scenario 1 beat 2.1 or Scenario 2 beat 1.1. Enter on a ready text field, not during route navigation. Cut out once submission is visibly committed.

## Beat 2.1 — Confirm the plan

Narration: "Before the team starts, review the plan and confirm it."

DOM cue: Cut in when the OutcomeSpec view is rendered and the confirm action is visible or becomes enabled.

On screen: Show the coordinator's proposed plan, one visible revision or clarification if available, then the final confirm action that unblocks the run.

Continuity: Source footage from Scenario 1 beat 2.2. Hard cut from the typed goal to the first readable OutcomeSpec state; cut out only after the confirmed state is visually acknowledged.

## Beat 2.2 — Generate the workflow

Narration: "Workflows are editable, so the team can run the same play again."

DOM cue: Cut in when the workflow editor graph or trigger configuration is fully rendered and individual nodes or trigger controls are visibly present.

On screen: Use the Scenario 2 workflow-generation footage: generated graph, workflow structure, then the trigger setup that shows this is a reusable system rather than a one-off run.

Continuity: Source footage from Scenario 2 beat 2.2 and, if needed, beat 3.1 for the finished workflow state. Hard cut on rendered graph visibility; do not dissolve between editor states.

## Beat 2.3 — Watch the graph run

Narration: "Then follow the graph as the work moves from agent to agent."

DOM cue: Cut in when the topology graph is fully drawn and at least one node or edge is visibly active, expanding, or changing status.

On screen: Stay full-frame by default so the graph relationships remain readable. If the graph is wider than the viewport, use one restrained DOM-anchored pan between related node clusters instead of repeated zoom churn.

Continuity: Source footage from Scenario 1 beat 2.3 or Scenario 2 beat 3.1. Keep causal actions at 1×, accelerate only readable waits, and hard cut dead gaps rather than pushing beyond the 12× threshold.

## Beat 2.4 — Open the preview

Narration: "When the work produces a real app, open the live preview."

DOM cue: Cut in when the preview gate, preview button, or preview surface is visibly available; cut out only after the preview is visibly rendered.

On screen: Show the human approval gate if it is on screen, then the real preview opening and holding long enough to prove the page is running rather than mocked.

Continuity: Source footage from Scenario 1 beat 2.5 or 4.5. Hard cut in on the first preview-ready state; hard cut out only after the rendered preview settles.

## Beat 3.1 — Turn output into work

Narration: "Artifacts become tasks, and tasks move across the board."

DOM cue: Cut in when the Board view is fully loaded and at least one column label and task card are visibly present.

On screen: Show the board with a work item being created or moved between columns. Keep the drag or card move at 1× because that visible action explains the state change.

Continuity: Source footage from Scenario 1 beat 2.4. Cut from preview to board on a stable page render, not while navigation is still animating.

## Beat 3.2 — Trace the run

Narration: "If you need proof, open the trace and inspect what actually happened."

DOM cue: Cut in when the Observability or Traces view is fully rendered and the span tree is visibly populated.

On screen: Use the trace shot that shows the run's span tree, agent activity, model calls, or timing detail. Keep this readable and full-frame; this beat is proof, not scenery.

Continuity: Source footage from Scenario 1 beat 2.7. Hard cut from the board to a loaded trace view; do not accelerate through the first readable populated trace state.

## Beat 3.3 — Put it on a schedule

Narration: "Some work should run on its own, so schedule it once and keep going."

DOM cue: Cut in when the workflow schedule or trigger editor is fully visible and the schedule controls are already rendered.

On screen: Show the scheduling surface from the finalized workflow footage, using the shortest readable path from editing the workflow to saving the recurring trigger.

Continuity: Source footage from Scenario 1 beat 3.1 or Scenario 2 beat 2.2. Hard cut on the loaded scheduling UI; cut out once the saved schedule state is visibly confirmed.

## Beat 4.1 — Ask from chat

Narration: "You can also hand the next job to the assistant directly."

DOM cue: Cut in when the assistant session is open and the composer is visibly ready, or when the submitted prompt and reply are both already on screen.

On screen: Use the shortest readable assistant sequence: ask for the next task, show the request in context, then hold on the assistant's visible response or kickoff state.

Continuity: Source footage from Scenario 1 beats 4.1 and 4.2. Keep the beginning and end of typing at 1×; cut away once the assistant's response is visibly established.

## Beat 4.2 — Review the result

Narration: "When the work is ready, review the change before it moves forward."

DOM cue: Cut in when a review gate, diff view, PR banner, or resulting pull request surface is visibly rendered.

On screen: Use the cleanest review-result footage available: file diff, approval gate, or PR-ready state. Show enough of the review surface to prove human approval stays in the loop.

Continuity: Source footage from Scenario 1 beats 2.6 and 4.7, or Scenario 2 beat 3.2. Hard cut in on the fully rendered review state; cut out once the approved or PR-ready result is visible.

## Beat 5.1 — Close on the shared surface

Narration: "The same workspace is available in the UI, through workflows, and from your own tools."

DOM cue: Cut in when the Settings or MCP connection surface is fully rendered and the connection details area is visibly present.

On screen: End on the settings-based connection surface that proves the same project and team context can be reached from external clients. Hold the final frame steady long enough to read before the end fade-to-black.

Continuity: Source footage from Scenario 1 beat 5.1 or Scenario 2 beat 5.1. This is the only place where a final fade-to-black is acceptable, because it closes the video rather than blending two product states.
