# Blueprint to shipped fix — recording script

## Recording status

Record against staging with a single authenticated browser session. The project is
**blueprint-demo** and the seeded bug is
<https://github.com/sabbour/agentweaver-demo-dryrun/issues/1>.

The PM discovery run `b3bda0e2-2a6b-4e29-9a88-0566178f681e` completed. A second
live run verified the Outcome-plan confirmation flow. Do not represent a later
unverified flow as completed: each such beat below is explicitly marked **NOT YET
VERIFIED — needs follow-up run**.

## Recording order and verification status

Record the beats in order, top to bottom: **1.1, 1.2, 1.3, 2.1, 2.2, 2.3, 2.4,
2.5, 2.6, 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7**.

| Beats | Status | Recording use |
| --- | --- | --- |
| 1.1, 2.1, 2.2 | Verified | Record with the stated live selectors. |
| 1.2, 1.3, 2.4, 2.5, 2.6 | Nav-only / partial | Record only the verified surface; respect each cut condition and the "verify in mapping pass" notes. |
| 2.3, 3.1–3.7 | Unverified | Do not include in a finished cut until a follow-up run supplies the missing artifact. |

New UI surfaces added in this pass — the **Generate a Blueprint** option, the **Import
Skill** dialog, and the **Clarify** refinement input — have no confirmed selectors yet.
Each is annotated **verify in mapping pass**: Trinity maps the real refs live before
recording, and the placeholder locators here must not be recorded as-is.

### Wait handling for every take

Never record real-time idle polling. Fresh orchestrations can remain **Pending** for
about two minutes, and the verified PM workflow took more than 16 minutes end to end.
For every kickoff-to-result transition, either use a pre-warmed run that is already at
the next verified state or insert a time-lapse/speed-ramp and resume the take there.

## Preflight

```bash
playwright-cli open --browser=chrome
playwright-cli resize 1920 1080
playwright-cli goto https://agentweaver.6a5efff1a270d8000126291b.westus2.staging.aksapp.io
playwright-cli snapshot
playwright-cli video-start blueprint-to-shipped-fix.webm
playwright-cli video-show-actions --duration=900 --position=top-right
```

Pacing: authenticate before the take; pause one second after navigation. Keep this
browser alive for the whole recording.

---

# Act 1 — Cast the team

## Beat 1.1 — Create the project

**video-chapter**

```bash
playwright-cli video-chapter "Create the project" --description="Point Agentweaver at an empty GitHub repo and name the project." --duration=14000
playwright-cli click "getByRole('button', { name: 'Create from GitHub' })"
playwright-cli click "getByRole('textbox', { name: 'Or paste any repository' })"
playwright-cli type "https://github.com/sabbour/agentweaver-demo-dryrun"
playwright-cli click "getByRole('button', { name: 'Go →' })"
playwright-cli click "getByRole('textbox', { name: 'Project name' })"
playwright-cli type "blueprint-demo"
playwright-cli snapshot
```

Narration: “Here’s an empty GitHub repo. I paste the URL and give the project a
name.”

Pacing: leave action callouts on while typing; pause briefly on the repository URL.
Transition to 1.2: once the name is set, move straight to picking the team.

---

## Beat 1.2 — Choose a blueprint

**video-chapter**

```bash
playwright-cli video-chapter "Choose a blueprint" --description="Show the Generate-a-Blueprint option, then cast the Product & Software Delivery team." --duration=16000
playwright-cli click "getByRole('button', { name: 'Templates' })"
# Generate-a-Blueprint option — selector unknown, verify in mapping pass.
playwright-cli hover "TODO(mapping): 'Generate a Blueprint' option on the template picker"
playwright-cli click "getByRole('radio', { name: 'Product & Software Delivery' })"
playwright-cli snapshot
playwright-cli click "getByRole('button', { name: 'Create' })"
playwright-cli snapshot
```

Narration: “A blueprint is a reusable team — the roles, the skills they carry, and the
workflows they run. I can generate a custom one for my goal, or start from a preset; I’ll
cast Product & Software Delivery.”

Pacing: hover the Generate option long enough to read it, then linger on the Product &
Software Delivery card before Create.

Note: verified staging behavior casts the template immediately on project creation; there
is no separate cast-confirmation gate.

**Verify in mapping pass:** the **Generate a Blueprint** control sits alongside the preset
cards on the template picker, but its live selector is not captured. Trinity maps it
before recording; do not record the placeholder locator.

---

## Beat 1.3 — Inspect the team

**video-chapter**

```bash
playwright-cli video-chapter "Inspect the team" --description="Show the agents, the skills page, importing shared skills, and per-agent assignments." --duration=16000
playwright-cli click "getByRole('link', { name: 'Agents', exact: true })"
playwright-cli hover "getByRole('list', { name: 'Project agents' })"
playwright-cli hover "getByRole('button', { name: 'Active Ripley Lead PM' })"
playwright-cli hover "getByRole('button', { name: 'Active Dallas Customer Researcher' })"
playwright-cli click "getByRole('link', { name: 'Skills', exact: true })"
# Import Skill dialog — open to show pulling shared skills from GitHub; selector unverified, verify in mapping pass.
playwright-cli click "getByRole('button', { name: 'Import Skill' })"
playwright-cli snapshot
# Close the dialog before showing assignments — close control unverified, verify in mapping pass.
playwright-cli click "getByRole('tab', { name: 'Assignments', exact: true })"
playwright-cli snapshot
```

Narration: “Each agent has a name, a role, and a set of skills. On the Skills page I can
import shared skills straight from a GitHub repo and assign each one to the agents that
need it.”

Pacing: hold on the agents list, then on the Skills catalog, then on the Assignments grid
so the per-agent checkboxes are legible.

Note: Team memory is intentionally saved for the end (Beat 2.6), after a run has had a
chance to write a decision.

**Verify in mapping pass:** skills are shared by importing them from a GitHub source
(`owner/repo` or a github.com URL) — there is no separate marketplace page. The **Import
Skill** button, the import dialog, and its close control are shown in the docs but not yet
mapped on staging. Capture the real selectors before recording, and do not record the
placeholder locators.

**NOT YET VERIFIED — needs follow-up run:** the Import Skill dialog and a real imported
skill were not exercised on staging. Record the import surface only once its selectors and
a real result are captured.

---

# Act 2 — Frame and ship a feature

## Beat 2.1 — Frame the product

**video-chapter**

```bash
playwright-cli video-chapter "Frame the product" --description="Pick the product workflow and describe one small feature to build." --duration=18000
playwright-cli click "getByTestId('start-task-topbar-action')"
playwright-cli select "getByLabel('Workflow', { exact: true })" "pm-discovery"
playwright-cli click "getByRole('textbox', { name: 'Goal' })"
playwright-cli type "Build the first tiny slice of Standup Scribe: a single-page web app where someone pastes a raw meeting transcript and gets back a clean list of action items, each with an owner and a due date when one is mentioned. The landing screen has a short welcome banner explaining the tool and one primary 'Paste transcript' button. Keep scope to this one MVP slice — paste text, extract action items, render the list. Define the problem, the target user, and the success criteria."
playwright-cli hover "getByRole('button', { name: 'Define Outcome', exact: true })"
playwright-cli click "getByRole('button', { name: 'Define Outcome', exact: true })"
playwright-cli snapshot
```

Narration: “I start the product workflow and describe one small feature: paste a meeting
transcript, get back the action items. One MVP slice, with clear success criteria.”

Pacing: type the goal naturally; pause before Define Outcome. Select the product
discovery workflow with the verified `pm-discovery` value before entering the goal.

Transition to Beat 2.2: the Outcome plan can remain Pending for roughly two minutes.
Time-lapse that wait or cut to a pre-warmed run when the confirmation panel is ready;
do not record idle polling.

---

## Beat 2.2 — Review and confirm the plan

**video-chapter**

```bash
playwright-cli video-chapter "Review and confirm the plan" --description="Read the OutcomeSpec, use Clarify to refine it, allow independent tasks, and confirm." --duration=16000
playwright-cli hover "getByRole('button', { name: 'Clarify plan', exact: true })"
playwright-cli click "getByRole('button', { name: 'Clarify plan', exact: true })"
# Clarify refinement input — selector unknown, verify in mapping pass.
playwright-cli type "Keep this first slice to paste-and-extract only: no accounts and no saved history yet."
# Submit the clarification — control unverified, verify in mapping pass.
playwright-cli snapshot
playwright-cli hover "getByRole('checkbox', { name: 'Independent task promotion Allow standalone backlog tasks for independent deliverables' })"
playwright-cli click "getByRole('checkbox', { name: 'Independent task promotion Allow standalone backlog tasks for independent deliverables' })"
playwright-cli hover "getByRole('button', { name: 'Confirm plan', exact: true })"
# [PAUSE 700ms]
playwright-cli click "getByRole('button', { name: 'Confirm plan', exact: true })"
playwright-cli snapshot
```

Narration: “I read the OutcomeSpec first, then use Clarify to tighten the scope. I let the
independent pieces become their own tasks, and confirm.”

Pacing: show the Clarify exchange, then pause before Confirm plan so the human decision is
legible.

Human-review automation: Confirm plan is the verified dispatch gate. Its live result is
“Outcome plan confirmed … Dispatch is unblocked,” followed by the work plan.

**Verify in mapping pass:** the Clarify button is verified, but the refinement input and
its submit control are new to this take and not yet mapped. Capture them live before
recording; do not record the placeholder locators.

---

## Beat 2.3 — Watch the work plan run

**video-chapter**

```bash
playwright-cli video-chapter "Watch the work plan run" --description="Open the topology graph, step through the new nodes, then watch execution and the artifacts it produces." --duration=16000
playwright-cli click "getByTestId('open-topology-minimap')"
playwright-cli click "getByRole('button', { name: /Coordinator/ })"
playwright-cli snapshot
playwright-cli click "getByRole('button', { name: /Work plan/ })"
playwright-cli click "getByRole('button', { name: /Research the problem space/ })"
playwright-cli click "getByRole('button', { name: 'Zoom in' })"
playwright-cli snapshot
playwright-cli click "getByRole('button', { name: 'Fit to view' })"
playwright-cli click "getByRole('button', { name: 'Close panel' })"
playwright-cli snapshot
```

Narration: “The coordinator turns the plan into a graph of tasks. I click through a few
nodes to see what each agent is doing, close it, and watch the work land.”

Pacing: let each selected-node label settle. The verified graph focused Coordinator and
Work plan at 130%; Zoom in on Research reached 156%. After closing the graph, hold on the
run view while real output appears — use a pre-warmed run or a speed-ramp, never idle
polling.

**NOT YET VERIFIED — needs follow-up run:** the topology graph controls are verified, but
the live execution artifacts (the files and outputs the nodes produce) were not captured.
Get the real artifact selectors before recording the “watch the work land” portion, and do
not stage an empty run view as if work were flowing.

---

## Beat 2.4 — Approve the merge

**video-chapter**

```bash
playwright-cli video-chapter "Approve the merge" --description="Wait for the approval notification, review the result, and approve the merge." --duration=14000
playwright-cli click "getByTestId('notification-bell')"
playwright-cli snapshot
playwright-cli click "getByTestId('notification-bell')"
# Only when a live review gate is present:
playwright-cli hover "getByRole('button', { name: 'Approve & merge', exact: true })"
# [PAUSE 700ms]
playwright-cli click "getByRole('button', { name: 'Approve & merge', exact: true })"
playwright-cli snapshot
```

Narration: “When the work is ready, a notification asks for my approval. I review the
result and approve the merge.”

Pacing: wait for the approval notification to arrive, then open it. Use the pause on
Approve & merge so the automated click still reads as an intentional human decision.

Human-review automation: the notification bell is verified; its empty state reads
“Nothing needs your attention right now.” Approve & merge is verified on a live review
gate.

**NOT YET VERIFIED — needs follow-up run:** the approval-request notification and the
feature-slice review gate were not exercised end to end in the primary PM scenario.
Pre-warm a run that has reached the gate before recording this beat.

---

## Beat 2.5 — Check health

**video-chapter**

```bash
playwright-cli video-chapter "Check project health" --description="Look at throughput, quality, cost, and traces per agent." --duration=14000
playwright-cli click "getByRole('link', { name: 'Dashboard', exact: true })"
playwright-cli hover "getByRole('heading', { name: 'Operational signals' })"
playwright-cli hover "getByRole('table', { name: 'Agent leaderboard' })"
playwright-cli click "getByRole('link', { name: 'Observability', exact: true })"
playwright-cli click "getByRole('tab', { name: 'Traces', exact: true })"
playwright-cli click "getByRole('tab', { name: 'Agents', exact: true })"
playwright-cli snapshot
```

Narration: “The Dashboard shows throughput and quality. Observability shows model use,
cost, latency, and traces down to each agent.”

Pacing: do not hard-code changing counts. The live controls verified here are Dashboard
Refresh and Time range, Observability time range and Refresh, and Overview, Traces,
and Agents tabs.

---

## Beat 2.6 — Review team memory

**video-chapter**

```bash
playwright-cli video-chapter "Review team memory" --description="Show the decisions the run wrote down." --duration=8000
playwright-cli click "getByRole('link', { name: 'Memories', exact: true })"
playwright-cli click "getByRole('tab', { name: 'Decisions', exact: true })"
playwright-cli snapshot
```

Narration: “Now the decisions the run made are saved. The next piece of work starts with
that context instead of a blank page.”

Reason for placement: this pays off the team we met in Beat 1.3, after a workflow has had a
chance to write a decision, instead of showing an empty memory page early.

**NOT YET VERIFIED — needs follow-up run:** no decision card was present in this project;
cut this beat until a real decision is visible.

---

# Act 3 — Triage the seeded bug

With the feature shipped and the decisions saved, I turn to a bug that was already filed
against this repo.

## Beat 3.1 — Pivot to the seeded bug

**video-chapter**

```bash
playwright-cli video-chapter "Pivot to the seeded bug" --description="Start the repair from the existing GitHub issue." --duration=8000
# NOT YET VERIFIED — needs follow-up run.
```

**DRAFT VO — only record once verified:** “Same project, a filed bug: on a narrow tablet
the welcome banner overlaps the primary button, so people can’t start.”

**NOT YET VERIFIED — needs follow-up run:** no Agentweaver issue-list or linked-issue
surface was validated. Keep the GitHub issue as pre-recording setup.

---

## Beat 3.2 — Ask the assistant to triage

**video-chapter**

```bash
playwright-cli video-chapter "Ask the assistant to triage" --description="Have the assistant read the issue and kick off a Bug Fix workflow." --duration=14000
playwright-cli click "getByRole('button', { name: 'New session', exact: true })"
playwright-cli click "getByRole('textbox', { name: 'Message the assistant...' })"
playwright-cli type "Triage https://github.com/sabbour/agentweaver-demo-dryrun/issues/1. Investigate the narrow-tablet welcome-banner overlap, propose a minimal fix and test plan, then use the Bug Fix workflow."
playwright-cli snapshot
playwright-cli click "getByRole('button', { name: 'Send', exact: true })"
```

Narration: “The assistant reads the issue, proposes the smallest safe fix and a test plan,
and starts a Bug Fix workflow. Anything that changes state still waits for my approval.”

Pacing: pause after typing, then allow the first streamed reply to appear.

Transition to Beat 3.3: assistant-created orchestration may sit Pending for about two
minutes, and a full workflow can take 16 or more minutes. Use a speed-ramp or resume a
pre-warmed bug run at its first real output; never record the idle wait.

**NOT YET VERIFIED — needs follow-up run:** the console, textbox, and Send action were
verified, but this issue-specific prompt was not sent and its output was not recorded.

---

## Beat 3.3 — Read and scope the bug

**video-chapter**

```bash
playwright-cli video-chapter "Read and scope the bug" --description="Show the diagnosis, the expected behavior, and the smallest safe fix." --duration=10000
# NOT YET VERIFIED — needs follow-up run.
```

Narration: “Before touching code, the workflow spells out what’s broken, what should
happen, and how small the fix can stay.”

**NOT YET VERIFIED — needs follow-up run:** capture real bug-output selectors from the
assistant-created run.

---

## Beat 3.4 — Implement and test the repair

**video-chapter**

```bash
playwright-cli video-chapter "Implement and test the repair" --description="Show the fix and the tests that prove it." --duration=14000
# NOT YET VERIFIED — needs follow-up run.
```

Narration: “Engineering finds the cause, fixes it, and proves it with tests.”

**NOT YET VERIFIED — needs follow-up run:** no issue-specific implementation run was
validated.

---

## Beat 3.5 — Preview the repaired behavior

**video-chapter**

```bash
playwright-cli video-chapter "Preview the repaired behavior" --description="Show the narrow-tablet layout working before merge." --duration=10000
# NOT YET VERIFIED — needs follow-up run.
```

Narration: “We preview the fix the same way we previewed the feature — the banner and the
button no longer collide on a narrow tablet.”

**NOT YET VERIFIED — needs follow-up run:** no bug-preview surface was reached.

---

## Beat 3.6 — Approve the bug fix

**video-chapter**

```bash
playwright-cli video-chapter "Approve the bug fix" --description="A person makes the final merge decision." --duration=10000
# Only when a live review gate is present:
playwright-cli hover "getByRole('button', { name: 'Approve & merge', exact: true })"
# [PAUSE 700ms]
playwright-cli click "getByRole('button', { name: 'Approve & merge', exact: true })"
playwright-cli snapshot
```

Narration: “The fix waits at a review gate until someone approves the merge.”

Human-review automation: use the verified **Approve & merge** locator only for the review
gate belonging to the bug-fix run.

**NOT YET VERIFIED — needs follow-up run:** no bug-fix review gate was exercised.

---

## Beat 3.7 — Close the loop on the issue

**video-chapter**

```bash
playwright-cli video-chapter "Close the loop on the issue" --description="Show the merged PR linked back to the original issue." --duration=10000
playwright-cli video-stop
```

Narration: “That closes the loop: from an idea, to a shipped feature, to a fixed bug
linked back to its issue.”

**NOT YET VERIFIED — needs follow-up run:** no bug-fix merge or issue-linked PR was
generated. Record this final image only once those real artifacts exist.

External-surface requirement: show the issue-linked PR in a deliberate second
**github.com** tab, with separately captured selectors and cursor/action-callout
behavior. Do not imply that this evidence is an in-app Agentweaver page.
