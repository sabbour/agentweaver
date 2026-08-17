# Blueprint Demo — Master Beat Plan

This is the **single committed source of truth** for the Agentweaver "Trailhead" demo
narrative, parsed by `lib/beats.mjs`'s `loadBeatPlan`. It replaces the informal,
never-committed script that only ever existed as rendered narration `.txt` output under
`recordings/blueprint-demo-final/`.

Each `## Beat X.Y — Title` heading starts a beat. `Narration: "..."` is the voiceover
script for that beat. Optional `Fresh navigation: true` and `Start URL: ...` metadata
lines let a beat explicitly force a scene-cut reload or declare the route it expects when
captured as the first beat in a session.

Every beat below is intended to be captured **live** against the deployed staging app.
**Critical continuity rule:** each beat continues from the live UI state left by the
previous beat unless its `On screen:` line explicitly calls for a navigation. Do not
restart the same flow from scratch just to reach a later screen.

An optional `On screen:` line after a beat's `Narration:` records the concrete on-screen
actions the capture plan must perform (it is ignored by the narration parser, which only
reads the `Narration:` line).

## Clean-run capture readiness

This is a 21-beat **live-product** narrative.  A closed GitHub issue, a seeded UI
state, or a successful HTTP delivery alone is not capture evidence.  Before recording,
the capture owner records the following redacted evidence; no cookie, bearer token, or
webhook secret belongs in the plan, narration, screenshots, or capture artifacts.

1. **Staging build:** verify the deployed immutable build/release contains the fixes from
   #721 (task-linked run authorization), #722 (automatic webhook provisioning), #723
   (Assistant/MCP Entra identity propagation), and #724 (issue-label event actions).
   Those PRs being merged into `dev` is not enough; the running build must be shown to
   contain them.  Beats 2.4–2.7, 3.2, and 4.1–4.7 are not clean-run eligible otherwise.
2. **Run and trace:** use one fresh project and retain the project-local run URL/ID for
   the board, topology, review, and trace beats.  Evidence is an owner opening the
   task-linked run and its transaction trace without a 401/403.
3. **Webhook (Beat 3.2):** the project's effective linked GitHub identity needs
   repository-hook administration for the automatic path.  Evidence is the Settings
   action reporting **Created** or **Updated**, followed by a redacted GitHub
   `issues.labeled` delivery and the resulting `github.issues.labeled` run in the same
   project.  If that permission is unavailable, do not click or describe the automatic
   action as successful.  Use this exact fallback narration instead: “The workflow is
   ready for an `issues.labeled` event. A repository administrator can use these
   project-specific details to finish the signed GitHub hook; Agentweaver starts triage
   when that delivery arrives.”  Showing that fallback is truthful, but does **not**
   satisfy the live-trigger portion of Beat 3.2; an administrator must complete the
   manual setup before the full 21-beat clean run.
4. **Assistant/MCP (Beats 4.1–4.3):** evidence is a newly opened Assistant session
   reading the current linked GitHub identity and project, then presenting an approval
   before its state-changing workflow start.  Browser sign-in by itself is not evidence.
   If this fails, do not substitute a UI-started workflow while narrating assistant
   parity.  The only safe fallback narration is: “The project UI can start this workflow;
   the Assistant path will be shown after its authenticated connection is available.”
   Resequence later beats only after that evidence exists; this does not qualify as a
   complete 21-beat Assistant capture.
5. **Preview and PR (Beats 2.5, 4.5, and 4.7):** evidence is an actually rendered,
   interactive preview URL after approval and a real pull-request URL linked from the
   completed run/topology.  If preview infrastructure or repository write/PR permission
   is unavailable, show neither a mock preview nor a placeholder PR.  Narrate the
   completed review/diff only, and mark the affected beat pending rather than claiming
   shipment.  A full clean run requires both real artifacts.

## Beat 1.1 — Create the project

Narration: "First, I'll create a new project. I'll give it a name and a short description of what we're building."

Fresh navigation: true

On screen: Open **New Project**. Fill in the project name and short description, but do **not** finish creation yet. Leave the same create-project flow open for beat 1.2.

## Beat 1.2 — Choose a blueprint

Narration: "Before I create it, I want to start from a blueprint. A blueprint sets up the team roster, the workflows, the review policy, and the sandbox — so I'm not starting from a blank slate. I'll pick one that fits, then create the project."

On screen: In the **same open create-project flow** from 1.1, switch to the blueprint picker, choose **Product and Software Delivery**, show the casting result, then confirm project creation. Do not re-enter the repo URL or project name.

## Beat 1.3 — Inspect the team

Narration: "Every project comes with its own team, and each agent here is specialized — this one's a backend engineer, that one's QA. They're not all running the same model, either — each role has its own default, so the coordinator can put a heavier model on complex work and a lighter one on routine tasks. And they're not chatting with each other directly — the coordinator hands off work and they share context through the team's memory. Now let's look at what they build with. Agents work from skills — here's Awesome Copilot in the marketplace, with a few I could pull in. I can also generate a skill from a description, or import one I already have."

On screen: Open **Agents**, inspect one agent card, and show its role, default model, and context window. From there go to **Skills** and continue the same flow: open **Browse marketplaces**, open **Awesome Copilot**, show some of its contents, dismiss it, open **Generate skill**, dismiss it, then open **Import skill**. Do not restart the project flow.

## Beat 2.1 — Frame the product

Narration: "This time I'll describe the product we're building."

Fresh navigation: true

On screen: Starting from the existing project context, open the task/assistant entry point, choose the **Product** workflow or prompt path, enter the Trailhead brief, and submit it.

## Beat 2.2 — Review and confirm the plan

Narration: "Before any agent starts working, the coordinator writes up an OutcomeSpec — its understanding of the goal, the assumptions it's making, and what it plans to do. I'll ask for one change here, and it updates the spec. Once it looks right, I confirm it."

On screen: Stay on the generated OutcomeSpec view from 2.1, provide one clarification, wait for the revised plan to appear, then confirm it. Do not jump away and come back.

## Beat 2.3 — Watch the work plan run

Narration: "With the spec confirmed, the coordinator breaks the work into subtasks and builds a WorkPlan. This is the topology view — a live graph of every agent involved and how they connect. Each agent runs its own subtask inside an isolated sandbox — its own worktree, cut off from everything else — so nothing collides. The coordinator manages the handoffs between them, so tasks run in the right order. Once the subtasks finish, we get the artifacts — spec, plan, UX notes, even marketing copy. Here they are."

On screen: Stay on the same run. Wait for decomposition into subtasks, approve anything that appears, open the live topology graph once it renders, inspect a few nodes, return to the run view, and show the generated artifacts landing in real time.

## Beat 2.4 — Review the board

Narration: "Let's turn one of those artifacts into work. I'll add a task to the board for a landing page that matches what we just generated, then move it from Backlog to Ready."

On screen: Navigate from the finished run into the **Board**, create the landing-page work item exactly once, then drag it from **Backlog** to **Ready**.

## Beat 2.5 — Ship it

Narration: "I'll break this task down further and watch it split into subtasks. Once one of them starts implementing, I'll follow along — and when it's done, the agent builds and runs the app right there in its sandbox and registers a live preview. I'll open that preview to see the page running for real, not just a mockup."

On screen: From the board item created in 2.4, choose the breakdown path, follow one implementation subtask, approve the preview gate when it appears, and open the live preview after it actually renders.

## Beat 2.6 — Review the diff and approve the merge

Narration: "When the work is ready, a notification prompts you to review and approve the merge. Before approving, open the file diff to see exactly what changed — every modified file, side by side. This is the single collective review gate: a RAI safety check plus your human approval, run once over the assembled output of every agent on this run, not one separate approval per agent. Confirm the result and approve the change so the run can complete."

On screen: Continue from the same run that produced the preview. Open the review gate, inspect the real file diff, then approve the change so the run completes.

## Beat 2.7 — Review the trace

Narration: "Let's look at how this actually ran. Every step is traced — here's the sequence of calls, the timing, and where the agent made decisions along the way."

On screen: Open **Observability** for this same project, switch to **Traces**, open the transaction trace for the run you just completed, and stay on the span tree long enough to read the agent, model, and tool activity. Do not rush past the trace.

## Beat 2.8 — Review team memory and decisions

Narration: "Here in the Decisions tab, the team's accepted decisions are carried forward automatically — these are one of four memory layers compiled into every agent's context: active decisions, core project context, learnings and patterns from prior runs, and the current open session. Agents propose new entries to a decision inbox, and after each run a Scribe pass merges the inbox into this shared ledger. That shared memory is what you just watched get created moments ago — it's how future runs pick up the same context without starting from scratch."

On screen: From the same project context, open **Decisions** and show a real, non-empty accepted decision.

## Beat 3.1 — Schedule recurring dependency sweeps

Narration: "Not everything should run on a schedule — but some things should. I'll set up a weekly dependency sweep, since checking for outdated or vulnerable dependencies is naturally recurring work, regardless of what we're shipping."

Fresh navigation: true

On screen: Open **Workflows**, choose the dependency-sweep workflow, add a weekly schedule, and save it. This beat starts from the project you already created; do not recreate or reclone anything.

## Beat 3.2 — Trigger bug triage from GitHub

Narration: "Here's a workflow that should only run when something happens on GitHub. I'll add a bug triage workflow and switch its trigger from schedule to event — GitHub gives us dozens of event types, so Agentweaver only shows the ones that make sense here. I want this one to fire on issues, specifically when it gets labeled `agentweaver:triage` — I'll build that condition instead of typing it as text. For the webhook itself, I can either copy these details into GitHub myself, or click here and let Agentweaver create it — it'll ask for the one extra permission it needs, only now, not upfront. I'll create an issue, add the label, and watch triage kick off."

On screen: Stay in **Workflows**, open the bug-triage workflow, change its trigger to an event-based GitHub trigger, choose **Issues** with label `agentweaver:triage` in the condition builder, show the webhook creation UI, then demonstrate the label being added and the run appearing.

## Beat 4.1 — Triage the next issue from chat

Narration: "There's more than one way to work with Agentweaver. I'll open a new issue, this time without a label, and just ask for it directly. I'll start a chat and tell the assistant to triage issue #x."

Fresh navigation: true

On screen: Starting from the same project, create or open the next issue without the trigger label, open an assistant session, and ask it to triage that issue directly.

## Beat 4.2 — Ask the assistant to triage

Narration: "The assistant reviews the GitHub issue, outlines a minimal fix with a focused test plan, and kicks off a Bug Fix workflow. Any action that would change state still waits for approval, so the team stays in control while the investigation gets underway."

## Beat 4.3 — Read and scope the bug

Narration: "Before changing any code, the workflow narrows the problem down. It captures the current behavior, defines what the app should do instead, and scopes the smallest safe fix so the change stays focused and easy to verify."

## Beat 4.4 — Implement and test the repair

Narration: "The team traces the issue to the underlying implementation, applies the repair, and reruns the relevant tests to confirm the behavior is now stable. The follow-up validation shows the fix holding under the same conditions that previously triggered the problem."

On screen: Click each work-plan step to expand or select it so the UI visibly responds as the narration moves through the repair.

## Beat 4.5 — Preview the repaired behavior

Narration: "Preview the repaired layout the same way you previewed the feature. On a narrow tablet, the banner and the button now stay in their own space instead of colliding."

On screen: When the preview approval gate appears, approve it, wait for the repaired preview to render, then show the fixed layout.

## Beat 4.6 — Approve the bug fix

Narration: "The bug fix reaches the final review gate, where the verified 'Approve & merge' action confirms the change and moves it forward into the merge flow."

## Beat 4.7 — Close the loop on the issue

Narration: "The result shows up right in the run — here's the pull request as part of the topology. And here it is on GitHub, ready for review."

On screen: End on the run topology that contains the resulting pull request, then open that same pull request on GitHub.

## Beat 5.1 — Drive it from your own tools

Narration: "You can drive the exact same workflows from your own tools. In Settings, grab the MCP server URL, then connect clients like Claude Desktop, VS Code, or Copilot CLI. Every capability you saw in the UI today — projects, runs, the board, workflows, blueprints, casting, memory, decisions, sandboxes, diagnostics — is available through that same MCP server, in the same workspace and team context you've been using throughout this demo."

Fresh navigation: true
