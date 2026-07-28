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

## Beat 1.1 — Create the project

Narration: "Start with an empty GitHub repository, paste in the repo URL, and create a new project from it. Once the repository is connected, give the project a name and move straight into setting up the team."

## Beat 1.2 — Choose a blueprint

Narration: "Blueprints package the roles, skills, and workflows your team will use. You can generate a custom setup for a specific goal or start from a proven preset. Here, we'll choose the Product and Software Delivery blueprint. Behind the scenes, a casting algorithm assigns each role a named agent from a themed universe — so the team you're about to meet isn't just 'Agent 1, Agent 2' — it has personality and continuity across every run in this project."

## Beat 1.3 — Inspect the team

Narration: "Here's the project team in action, with each agent's role and assigned skills visible at a glance. Every agent also shows its own default model and context window right here in the roster — so you can see at a glance which agent is running a fast, low-cost model for routine work versus a larger-context model for deep reasoning. From the Skills section, you can browse curated marketplaces or import skills directly from a GitHub repository, then assign those capabilities to the right agent for the workflow."

## Beat 2.1 — Frame the product

Narration: "We start by choosing the software delivery workflow and giving the team a concrete product challenge to solve. Notice the coordinator doesn't just take the request at face value — it drafts an OutcomeSpec: the goal, the desired outcome, the scope, and its assumptions, then picks the best-fit workflow for the job using an LLM pass over all the available workflows and roles, and tells you why it chose this one. You can always override that choice from the Start Task dialog, or just type 'use' and the workflow id. The idea here is Trailhead, a tool that helps groups turn scattered weekend trip planning into one shared plan everyone can agree on. From there, the brief lays out the core positioning, value props, and a simple first landing page experience centered around a 'Plan my first trip' call to action."

## Beat 2.2 — Review and confirm the plan

Narration: "Before any work starts, the coordinator asks for your confirmation on the OutcomeSpec — this is a mandatory gate, not an optional courtesy. The plan is refined with a tighter first slice focused on the landing page experience: the welcome banner, key value props, and the 'Plan my first trip' call to action. Independent task promotion is enabled so standalone deliverables can move forward in parallel, and the updated plan is confirmed before execution begins."

## Beat 2.3 — Watch the work plan run

Narration: "Once confirmed, the coordinator decomposes the spec into a WorkPlan — a dependency graph of subtasks — and dispatches child agents in parallel, each working in its own isolated sandbox so nothing they do can collide with another agent's work. This live topology graph shows every agent and its status as it runs. You're not just a spectator here: you can steer mid-run — send a directive, redirect a child agent, or amend the plan — without stopping the whole run. Watch: we'll send a quick redirect to one of the agents right now, then step through a few nodes to inspect the work plan before returning to the run view as outputs start landing in real time."

## Beat 2.4 — Review the board

Narration: "The board now shows the promoted tasks broken out into separate work items. Move the landing-page task from Backlog into Ready, and the coordinator immediately picks it up for execution."

## Beat 2.5 — Ship it

Narration: "As the workflow progresses, approve each gate as it appears so the run can continue. Once Build and Test finishes, the preview environment comes online, and you open the preview to check the landing page running live."

## Beat 2.6 — Review the diff and approve the merge

Narration: "When the work is ready, a notification prompts you to review and approve the merge. Before approving, open the file diff to see exactly what changed — every modified file, side by side. This is the single collective review gate: a RAI safety check plus your human approval, run once over the assembled output of every agent on this run, not one separate approval per agent. Confirm the result and approve the change so the run can complete."

## Beat 2.7 — Check project health

Narration: "The Dashboard gives a quick view of operational health, including throughput and quality across agents. In Observability, you can drill into traces, compare agent activity, and monitor usage, latency, and cost over time."

## Beat 2.8 — Review team memory and decisions

Narration: "Here in the Decisions tab, the team's accepted decisions are carried forward automatically — these are one of four memory layers compiled into every agent's context: active decisions, core project context, learnings and patterns from prior runs, and the current open session. Agents propose new entries to a decision inbox, and after each run a Scribe pass merges the inbox into this shared ledger. That shared memory is what you just watched get created moments ago — it's how future runs pick up the same context without starting from scratch."

## Beat 2.9 — When something goes wrong

Narration: "Not every run finishes cleanly — that's expected. Runs that fail land in the board's Problems column, with the failure reason surfaced right on the card, so a Tech Lead can see exactly what broke and retry or redirect the work without digging through logs."

## Beat 3.1 — Put it on a schedule

Narration: "Open Workflows and choose the workflow that just completed. From there, add a schedule and pick a daily, weekly, or monthly cadence in UTC so the workflow keeps running automatically on its own. A separate heartbeat also runs continuously in the background, promoting Ready tasks and starting runs up to a configurable concurrency limit — so the board keeps moving even when nobody's watching it."

## Beat 3.2 — Trigger it from GitHub

Narration: "Schedules handle time-based runs, but webhooks handle real events. In Project Settings under Webhooks, generate a secret, copy the payload URL, and connect it to a GitHub repository webhook. Now watch it happen for real: we push a commit to the repository right now, and the webhook fires, triggering the same workflow automatically."

## Beat 4.1 — Pivot to the seeded bug

Narration: "We switch to a real issue already tracked in GitHub. On narrower tablet layouts, the welcome banner slides over the 'Plan my first trip' button, making the main action hard to use. Now the repair starts from that existing bug report."

## Beat 4.2 — Ask the assistant to triage

Narration: "The assistant reviews the GitHub issue, outlines a minimal fix with a focused test plan, and kicks off a Bug Fix workflow. Any action that would change state still waits for approval, so the team stays in control while the investigation gets underway."

## Beat 4.3 — Read and scope the bug

Narration: "Before changing any code, the workflow narrows the problem down. It captures the current behavior, defines what the app should do instead, and scopes the smallest safe fix so the change stays focused and easy to verify."

## Beat 4.4 — Implement and test the repair

Narration: "The team traces the issue to the underlying implementation, applies the repair, and reruns the relevant tests to confirm the behavior is now stable. The follow-up validation shows the fix holding under the same conditions that previously triggered the problem."

## Beat 4.5 — Preview the repaired behavior

Narration: "Preview the repaired layout the same way you previewed the feature. On a narrow tablet, the banner and the button now stay in their own space instead of colliding."

## Beat 4.6 — Approve the bug fix

Narration: "The bug fix reaches the final review gate, where the verified 'Approve & merge' action confirms the change and moves it forward into the merge flow."

## Beat 4.7 — Close the loop on the issue

Narration: "That closes the loop: from an idea, to a shipped feature, to a merged fix linked back to the original GitHub issue. The final PR ties the work together in GitHub, showing the full path from planning through resolution."

## Beat 5.1 — Drive it from your own tools

Narration: "You can drive the exact same workflows from your own tools. In Settings, grab the MCP server URL, then connect clients like Claude Desktop, VS Code, or Copilot CLI. Every capability you saw in the UI today — projects, runs, the board, workflows, blueprints, casting, memory, decisions, sandboxes, diagnostics — is available through that same MCP server, in the same workspace and team context you've been using throughout this demo."
