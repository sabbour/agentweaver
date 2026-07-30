# Sizzle Reel — Master Beat Plan

This is the **single committed source of truth** for the third Agentweaver demo — the
short **sizzle reel** cut together from the already-locked footage plan in
`blueprint-demo-beats.md` (**Scenario 1**) and `azure-aks-demo-beats.md`
(**Scenario 2**). It is parsed by `lib/beats.mjs`'s `loadBeatPlan`.

Each `## Beat X.Y — Title` heading starts a beat. `Narration: "..."` is the voiceover
script for that beat.

This reel is an **editorial highlight cut**, not a third standalone product walkthrough:

- **No music.**
- **No dissolves or cross-fades.**
- Use **hard cuts** or native UI motion only.
- Every cut should land on a **DOM-grounded visual cue**, not a project-specific timer or
  backend event.
- Each beat below names the exact **source beat(s)** it is lifted from so the edit stays
  anchored to the finalized Scenario 1 / Scenario 2 scripts rather than inventing new UI
  business.

Because this is assembled from existing capture plans, every beat preserves the source
beat's locked intent and only trims for pace.

## Beat 0.1 — Open on the live graph

Narration: "This is the topology view — a live graph of every agent involved and how they connect."

Source: Scenario 1 Beat 2.3

DOM cue: Cut in only once the topology graph is already rendered and at least one node is visibly active, selected, or changing state.

On screen: Open on the strongest live topology shot from the Scenario 1 work-plan run: graph already drawn, coordinator context visible, real motion on screen.

Continuity: This is a direct lift from Scenario 1 Beat 2.3's topology reveal. Use a hard cold-open straight onto the rendered graph; do not pre-roll through menus or navigation.

## Beat 1.1 — Create from a blueprint

Narration: "Before I create it, I want to start from a blueprint. A blueprint sets up the team roster, the workflows, the review policy, and the sandbox — so I'm not starting from a blank slate."

Source: Scenario 1 Beat 1.2

DOM cue: Cut in when the create-project flow is already open and the blueprint picker is visible.

On screen: Use the exact create-project flow from Scenario 1: open create-project state already on screen, switch to the blueprint picker, show **Product and Software Delivery**, then confirm creation.

Continuity: Pull only the visually useful section from Scenario 1 Beats 1.1-1.2, but keep the wording and UI action anchored to Beat 1.2. Cut out on the moment project creation visibly lands.

## Beat 1.2 — Inspect the cast and browse skills

Narration: "Every project comes with its own team, and each agent here is specialized — this one's a backend engineer, that one's QA. Agents work from skills — here's Awesome Copilot in the marketplace, with a few I could pull in. I can also generate a skill from a description, or import one I already have."

Source: Scenario 1 Beat 1.3

DOM cue: Cut in when the **Agents** page is fully loaded and at least one agent card is readable.

On screen: Use the exact Scenario 1 Beat 1.3 sequence: open **Agents**, inspect one agent card, show its role/default model/context window, then go to **Skills** → **Browse marketplaces** → open **Awesome Copilot** → dismiss → open **Generate skill** → dismiss → open **Import skill**.

Continuity: Hard cut from project creation to the already-rendered Agents page. This beat intentionally preserves the full locked Beat 1.3 modal tour, with each internal cut landing on a fully rendered dialog or page state.

## Beat 1.3 — Point it at a live repo

Narration: "I'll point this at the AKS repo. Instead of picking a pre-built blueprint, I'll generate one for what we actually need — managing issues and roadmap work, plus running the blog."

Source: Scenario 2 Beat 1.1

DOM cue: Cut in when the repo-to-project form is visible and `Azure/AKS` is already present or being entered in the target-repo field.

On screen: Lift the strongest portion of Scenario 2 Beat 1.1: `Azure/AKS` target, blueprint-generation path, and the custom brief for issue triage, roadmap work, and blog/content management.

Continuity: Hard cut out of the marketplace tour into the live-repo setup. Stay on the same rendered repo-to-project flow; do not widen into unrelated setup.

## Beat 2.1 — Confirm the OutcomeSpec

Narration: "Before any agent starts working, the coordinator writes up an OutcomeSpec — its understanding of the goal, the assumptions it's making, and what it plans to do. Once it looks right, I confirm it."

Source: Scenario 1 Beat 2.2

DOM cue: Cut in when the generated OutcomeSpec is visible and the confirm action is present or about to become enabled.

On screen: Show the Scenario 1 Beat 2.2 confirmation loop: readable OutcomeSpec, one visible revision if available, then the final confirm action.

Continuity: This beat should feel like a direct compression of Scenario 1 Beat 2.2. Cut out only after the confirmed state is visually acknowledged.

## Beat 2.2 — Watch deterministic execution

Narration: "The coordinator manages the handoffs between them, so tasks run in the right order. Once the subtasks finish, we get the artifacts — spec, plan, UX notes, even marketing copy. Here they are."

Source: Scenario 1 Beat 2.3

DOM cue: Cut in when the run has decomposed into subtasks and the live topology graph has fully rendered.

On screen: Use the exact Scenario 1 Beat 2.3 proof sequence: wait for decomposition into subtasks, approve anything that appears, open the live topology graph, inspect a few nodes, return to the run view, and show the generated artifacts landing in real time.

Continuity: This is the core execution payoff. Preserve the locked Beat 2.3 causality: decomposition first, topology render second, artifacts visible at the end. Accelerate only readable waits; remove dead-time with hard cuts.

## Beat 2.3 — Turn artifacts into board work

Narration: "Let's turn one of those artifacts into work. I'll add a task to the board for a landing page that matches what we just generated, then move it from Backlog to Ready."

Source: Scenario 1 Beat 2.4

DOM cue: Cut in when the **Board** is loaded and the target columns are already visible.

On screen: Lift the exact Scenario 1 Beat 2.4 board action: create the landing-page work item once, then drag it from **Backlog** to **Ready**.

Continuity: Hard cut from generated artifacts to the board. Keep the card move at 1× because the drag explains the state change.

## Beat 2.4 — Open the live preview

Narration: "Once one of them starts implementing, I'll follow along — and when it's done, the agent builds and runs the app right there in its sandbox and registers a live preview. I'll open that preview to see the page running for real, not just a mockup."

Source: Scenario 1 Beat 2.5

DOM cue: Cut in when the implementation subtask is already visible and the preview approval gate or preview affordance is present.

On screen: Use the exact Scenario 1 Beat 2.5 flow: break the board task down, follow one implementation subtask, approve the preview gate when it appears, then open the live preview after it actually renders.

Continuity: Cut in at the implementing-task state, not earlier board setup. Hold on the fully rendered preview long enough to prove it is real.

## Beat 2.5 — Trace it, don't rush it

Narration: "Let's look at how this actually ran. Every step is traced — here's the sequence of calls, the timing, and where the agent made decisions along the way."

Source: Scenario 1 Beat 2.7

DOM cue: Cut in when **Observability → Traces** is fully rendered and the span tree is populated.

On screen: Lift the Scenario 1 Beat 2.7 trace shot exactly as locked: open the transaction trace for the run you just completed and stay on the span tree long enough to read the agent, model, and tool activity.

Continuity: Keep this full-frame and readable. Per the locked source beat, do not rush past the trace and do not zoom through it.

## Beat 3.1 — Wire a scheduled workflow

Narration: "Not everything should run on a schedule — but some things should."

Source: Scenario 1 Beat 3.1

DOM cue: Cut in when the **Workflows** page is loaded and the dependency-sweep workflow is visible.

On screen: Show the Scenario 1 Beat 3.1 weekly schedule setup: open the dependency-sweep workflow, add a weekly schedule, and save it.

Continuity: This beat sets up the contrast for the next trigger beat. Keep only the schedule-editing proof, not extra navigation.

## Beat 3.2 — Wire GitHub to kick off triage

Narration: "Here's a workflow that should only run when something happens on GitHub. I want this one to fire on issues, specifically when it gets labeled `agentweaver:triage` — I'll build that condition instead of typing it as text. I'll create an issue, add the label, and watch triage kick off."

Source: Scenario 1 Beat 3.2

DOM cue: Cut in when the bug-triage workflow trigger editor is rendered and the GitHub event controls are visible.

On screen: Lift the exact Scenario 1 Beat 3.2 sequence: open the bug-triage workflow, switch trigger from schedule to GitHub event, choose **Issues** with label `agentweaver:triage` in the condition builder, show webhook creation UI, then demonstrate the label being added and the run appearing.

Continuity: This is a direct highlight of the locked webhook-trigger beat. Use hard cuts between the workflow editor, GitHub action, and the run appearing, each on a DOM-confirmed rendered state.

## Beat 4.1 — Triage from chat instead

Narration: "There's more than one way to work with Agentweaver. I'll open a new issue, this time without a label, and just ask for it directly. I'll start a chat and tell the assistant to triage issue #x. The assistant reviews the GitHub issue, outlines a minimal fix with a focused test plan, and kicks off a Bug Fix workflow."

Source: Scenario 1 Beats 4.1-4.2

DOM cue: Cut in when the assistant session is open and the composer is ready, or when the issue and the chat entry point are both already visible.

On screen: Combine the locked Scenario 1 Beats 4.1-4.2 flow: create or open the next issue without the trigger label, start an assistant session, ask it to triage that issue directly, then hold on the assistant response / Bug Fix workflow kickoff long enough to prove chat is another control surface.

Continuity: Hard cut from the webhook-triggered run to the assistant-session kickoff. Keep the typed request readable at the start and end, then cut out once the workflow kickoff is visibly established.

## Beat 4.2 — Show the PR in the run, then on GitHub

Narration: "The result shows up right in the run — here's the pull request as part of the topology. And here it is on GitHub, ready for review."

Source: Scenario 1 Beat 4.7

DOM cue: Cut in when the run topology containing the PR is visible; cut out only after the actual PR page is open on GitHub.

On screen: Use the full Scenario 1 Beat 4.7 close-the-loop shot: end on the run topology that contains the resulting pull request, then open that same pull request on GitHub.

Continuity: This is the payoff for the webhook/chat triage sequence. Keep the hard cut from in-product PR evidence to the GitHub PR opening aligned to the DOM-visible PR surface, not a timed dissolve.

## Beat 5.1 — End on the MCP surface

Narration: "You can drive the exact same workflows from your own tools. In Settings, grab the MCP server URL, then connect clients like Claude Desktop, VS Code, or Copilot CLI."

Source: Scenario 1 Beat 5.1 and Scenario 2 Beat 5.1

DOM cue: Cut in when **Settings** is fully rendered and the MCP server URL area is visible.

On screen: End on the shared locked outro used by both demos: show the Settings page, the MCP server URL, and the cross-tool control-surface claim.

Continuity: Close on the rendered MCP settings surface and allow only the final fade-to-black after the last readable frame, since that ends the reel rather than blending two product states.
