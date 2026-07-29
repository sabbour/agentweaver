# Blueprint Demo — Master Beat Plan

This is the **single committed source of truth** for the Agentweaver "Trailhead" demo
narrative, parsed by `lib/beats.mjs`'s `loadBeatPlan`. It replaces the informal,
never-committed script that only ever existed as rendered narration `.txt` output under
`recordings/blueprint-demo-final/` — that gap is exactly why the last recording only
ever captured 5 of these 21+ beats.

Each `## Beat X.Y — Title` heading starts a beat. `Narration: "..."` is the voiceover
script for that beat. `BLOCKED(reason)` marks a beat that must stay narration-only for a
stated reason (rather than silently dropping it, as happened before).

Every beat below is intended to be captured **live** against the deployed staging app
unless marked `BLOCKED`.

An optional `On screen:` line after a beat's `Narration:` records the concrete on-screen
actions the capture plan must perform (it is ignored by the narration parser, which only
reads the `Narration:` line). These specs are the reproducible source of truth for the
behavior fixes applied after the PR #613 review — the per-beat capture JSON under
`recordings/_scratch/plans/` is generated/environment-specific and intentionally
gitignored. Two pipeline-wide fixes also apply to every beat: the synthetic cursor now
tracks the real post-zoom click coordinates, and camera zoom/pan is only used when a
target genuinely needs magnifying (scale ≤ 1.02 = no zoom), to cut gratuitous motion.

## Beat 1.1 — Create the project

Narration: "Start with an empty GitHub repository, paste in the repo URL, and create a new project from it. Once the repository is connected, give the project a name and move straight into setting up the team."

On screen: This beat OWNS repo-connect + naming only — paste the repo URL, connect it, set the project name, then advance to blueprint selection. It must NOT re-run blueprint selection (that belongs to 1.2), so 1.1 and 1.2 are never captured as near-duplicate segments (fixes the "synced-1-1 and 1-2 are the same video" defect).

## Beat 1.2 — Choose a blueprint

Narration: "Blueprints package the roles, skills, and workflows your team will use. You can generate a custom setup for a specific goal or start from a proven preset. Here, we'll choose the Product and Software Delivery blueprint. Behind the scenes, a casting algorithm assigns each role a named agent from a themed universe — so the team you're about to meet isn't just 'Agent 1, Agent 2' — it has personality and continuity across every run in this project."

On screen: This beat OWNS blueprint selection only — pick the "Product and Software Delivery" blueprint and show the casting result. Do NOT re-enter the repo URL or project name (already done in 1.1) and reuse the existing project rather than creating a second one, so 1.2 is not a duplicate of 1.1.

## Beat 1.3 — Inspect the team

Narration: "Here's the project team in action, with each agent's role and assigned skills visible at a glance. Every agent also shows its own default model and context window right here in the roster — so you can see at a glance which agent is running a fast, low-cost model for routine work versus a larger-context model for deep reasoning. From the Skills section, you can browse curated marketplaces or import skills directly from a GitHub repository, then assign those capabilities to the right agent for the workflow."

On screen: Click an agent (e.g. Deckard) to open its card showing the default model (claude-opus-4.8) and context window. Then go to the Skills page and actually exercise the UI the narration describes: open the "Browse marketplaces" dialog, the "Generate skill" dialog, and the "Import skill" dialog (each opened then dismissed with Escape), then switch to the "Assignments" tab to show a skill assigned to an agent. Use no-zoom clicks — do not magnify unless a detail needs it.

## Beat 2.1 — Frame the product

Narration: "We start by choosing the software delivery workflow and giving the team a concrete product challenge to solve. Notice the coordinator doesn't just take the request at face value — it drafts an OutcomeSpec: the goal, the desired outcome, the scope, and its assumptions, then picks the best-fit workflow for the job using an LLM pass over all the available workflows and roles, and tells you why it chose this one. You can always override that choice from the Start Task dialog, or just type 'use' and the workflow id. The idea here is Trailhead, a tool that helps groups turn scattered weekend trip planning into one shared plan everyone can agree on. From there, the brief lays out the core positioning, value props, and a simple first landing page experience centered around a 'Plan my first trip' call to action."

On screen: Choose the software-delivery workflow, enter the Trailhead brief, and show the drafted OutcomeSpec (goal / outcome / scope / assumptions and the workflow-choice rationale). Keep the camera still on the OutcomeSpec content — remove the earlier zoom onto a meaningless empty area (the "zooms on a meaningless area around 00:27" defect); only zoom if it lands on real OutcomeSpec text.

## Beat 2.2 — Review and confirm the plan

Narration: "Before any work starts, the coordinator asks for your confirmation on the OutcomeSpec — this is a mandatory gate, not an optional courtesy. The plan is refined with a tighter first slice focused on the landing page experience: the welcome banner, key value props, and the 'Plan my first trip' call to action. Independent task promotion is enabled so standalone deliverables can move forward in parallel, and the updated plan is confirmed before execution begins."

## Beat 2.3 — Watch the work plan run

Narration: "Once confirmed, the coordinator decomposes the spec into a WorkPlan — a dependency graph of subtasks — and dispatches child agents in parallel, each working in its own isolated sandbox so nothing they do can collide with another agent's work. This live topology graph shows every agent and its status as it runs. You're not just a spectator here: you can steer mid-run — send a directive, redirect a child agent, or amend the plan — without stopping the whole run. Watch: we'll send a quick redirect to one of the agents right now, then step through a few nodes to inspect the work plan before returning to the run view as outputs start landing in real time."

On screen: Perform REAL steering and topology actions (both were previously narrated but never shown). (1) Send an actual steering directive to a running child agent via the run's steering/message box and confirm a new event/message lands on the run timeline before moving on. (2) Open the live topology graph view and `waitFor` a graph node to render before narrating over it — do not narrate over a blank/broken graph. If the current run's graph won't render, target a run that has an active multi-agent topology first.

## Beat 2.4 — Review the board

Narration: "The board now shows the promoted tasks broken out into separate work items. Move the landing-page task from Backlog into Ready, and the coordinator immediately picks it up for execution."

On screen: Create the landing-page work item exactly ONCE, then drag it from Backlog to Ready. Do not create it twice and delete the duplicate on camera (fixes the "duplicate workitem being entered into backlog then you delete it" defect). If a stray duplicate already exists from a prior session, remove it off-camera (eval-step cleanup) before recording so only a single create is shown.

## Beat 2.5 — Ship it

Narration: "As the workflow progresses, approve each gate as it appears so the run can continue. Once Build and Test finishes, the preview environment comes online, and you open the preview to check the landing page running live."

On screen: The preview step is gated by a human-in-the-loop approval (`start_preview` posts a `ToolApprovalRequired` card to the run timeline with a 5-minute timeout — this is a safety gate, NOT a product bug). When that card appears, promptly click "Approve" ("here's the human-in-the-loop safety gate — approving the preview now"), then `waitFor` the live preview to actually render before narrating over it. Must not sit on "Preview Unavailable: approval timed out".

## Beat 2.6 — Review the diff and approve the merge

Narration: "When the work is ready, a notification prompts you to review and approve the merge. Before approving, open the file diff to see exactly what changed — every modified file, side by side. This is the single collective review gate: a RAI safety check plus your human approval, run once over the assembled output of every agent on this run, not one separate approval per agent. Confirm the result and approve the change so the run can complete."

## Beat 2.7 — Check project health

Narration: "The Dashboard gives a quick view of operational health, including throughput and quality across agents. In Observability, you can drill into traces, compare agent activity, and monitor usage, latency, and cost over time. Opening a run's transaction trace reveals the full distributed tree of agent, model, and tool spans behind a single operation. And on the Cluster page, you can see the AKS health behind the project itself — quota headroom, warm pool readiness, sandbox claims, and a live auto-refresh loop keeping that view current."

On screen: Open the Dashboard and `waitFor` a real chart/tile element to render (not a fixed short timeout) before narrating over it — the dashboard takes time to load. Only then move on to Observability. Then switch to the **Traces** tab (`/observability/traces`, `ObservabilityTracesPage.tsx`) and open one run's transaction trace by clicking its **Preview trace** button (targeted by run id, not a positional/first-item selector) so the `TransactionTracePanel` expands and renders the real agent/LLM/tool span tree — showing an actual transaction trace, not just the dashboard. Finish by navigating to **Cluster** (`/projects/:projectId/cluster`), `waitFor` real rendered diagnostics like `agent_pod_quota` and `Warm pool ready`, and hold long enough for the auto-refresh countdown/last-updated indicator to visibly tick while quota headroom, warm pool status, and sandbox-claim data are on screen.

## Beat 2.8 — Review team memory and decisions

Narration: "Here in the Decisions tab, the team's accepted decisions are carried forward automatically — these are one of four memory layers compiled into every agent's context: active decisions, core project context, learnings and patterns from prior runs, and the current open session. Agents propose new entries to a decision inbox, and after each run a Scribe pass merges the inbox into this shared ledger. That shared memory is what you just watched get created moments ago — it's how future runs pick up the same context without starting from scratch."

On screen: Show the Decisions tab with a real, NON-EMPTY accepted decision. A decision must exist before this beat is captured — either let an earlier beat's run complete a Scribe pass that records one, or trigger one deliberately earlier in the sequence. Verify `GET /api/projects/{proj}/decisions` returns `total_count > 0` before rolling; do not show an empty tab (fixes "no decisions have been recorded by Scribe").

## Beat 2.9 — When something goes wrong

Narration: "Not every run finishes cleanly — that's expected. Runs that fail land in the board's Problems column, with the failure reason surfaced right on the card, so a Tech Lead can see exactly what broke and retry or redirect the work without digging through logs."

## Beat 3.1 — Put it on a schedule

Narration: "Open Workflows and choose the workflow that just completed. From there, add a schedule and pick a daily, weekly, or monthly cadence in UTC so the workflow keeps running automatically on its own. A separate heartbeat also runs continuously in the background, promoting Ready tasks and starting runs up to a configurable concurrency limit — so the board keeps moving even when nobody's watching it."

On screen: Operate on a legitimate schedulable delivery workflow selected explicitly by name — the capture targets the `software-delivery-copy` workflow (a clone of the completed Software Delivery workflow) via a name-based selector, NOT a positional/first-item selector, so the stray "Copy of Bug Fix" (`bug-fix-copy`) duplicate is never picked up by accident. There is currently no workflow-delete API/UI (see decisions inbox), so the stray `bug-fix-copy` remains visible-but-unused in the list rather than being removed; the schedule dialog is opened and configured (e.g. Weekly / Monday / 09:00 UTC) on the intended `software-delivery-copy` row. Then show the **Heartbeat** page's live service-status UI briefly, navigate to the Board, open **Pickup settings**, and hold on the real `Max Ready items per heartbeat` control so the narration's configurable background pickup/concurrency claim is backed by the actual UI.

## Beat 3.2 — Trigger it from GitHub

Narration: "Schedules handle time-based runs, but webhooks handle real events. In Project Settings under Webhooks, generate a secret, copy the payload URL, and connect it to a GitHub repository webhook. Now watch it happen for real: we push a commit to the repository right now, and the webhook fires, triggering the same workflow automatically."

On screen: After connecting the webhook and pushing the commit, show visible on-screen evidence that the webhook fired — navigate to the Runs/Orchestrations list (or run timeline) and show the newly triggered run appearing in near-real-time. If a live wait is impractical, cut to the resulting run with a short "watch for this" callout. Do not narrate the webhook firing with nothing visible on screen.

## Beat 4.1 — Pivot to the seeded bug

Narration: "We switch to a real issue already tracked in GitHub. On narrower tablet layouts, the welcome banner slides over the 'Plan my first trip' button, making the main action hard to use. Now the repair starts from that existing bug report."

## Beat 4.2 — Ask the assistant to triage

Narration: "The assistant reviews the GitHub issue, outlines a minimal fix with a focused test plan, and kicks off a Bug Fix workflow. Any action that would change state still waits for approval, so the team stays in control while the investigation gets underway."

## Beat 4.3 — Read and scope the bug

Narration: "Before changing any code, the workflow narrows the problem down. It captures the current behavior, defines what the app should do instead, and scopes the smallest safe fix so the change stays focused and easy to verify."

## Beat 4.4 — Implement and test the repair

Narration: "The team traces the issue to the underlying implementation, applies the repair, and reruns the relevant tests to confirm the behavior is now stable. The follow-up validation shows the fix holding under the same conditions that previously triggered the problem."

On screen: CLICK each work-plan step to expand/select/navigate it so the UI actually responds as narrated — do not merely hover over the steps (fixes "hovers over them without clicking"). Each click should produce a visible expand/detail change.

## Beat 4.5 — Preview the repaired behavior

Narration: "Preview the repaired layout the same way you previewed the feature. On a narrow tablet, the banner and the button now stay in their own space instead of colliding."

On screen: Same human-in-the-loop preview gate as beat 2.5 — when the `ToolApprovalRequired` card appears for `start_preview`, click "Approve", then `waitFor` the repaired preview to render before narrating. Must not show "Preview Unavailable: approval timed out".

## Beat 4.6 — Approve the bug fix

Narration: "The bug fix reaches the final review gate, where the verified 'Approve & merge' action confirms the change and moves it forward into the merge flow."

## Beat 4.7 — Close the loop on the issue

Narration: "That closes the loop: from an idea, to a shipped feature, to a merged fix linked back to the original GitHub issue. The final PR ties the work together in GitHub, showing the full path from planning through resolution."

## Beat 5.1 — Drive it from your own tools

Narration: "You can drive the exact same workflows from your own tools. In Settings, grab the MCP server URL, then connect clients like Claude Desktop, VS Code, or Copilot CLI. Every capability you saw in the UI today — projects, runs, the board, workflows, blueprints, casting, memory, decisions, sandboxes, diagnostics — is available through that same MCP server, in the same workspace and team context you've been using throughout this demo."
