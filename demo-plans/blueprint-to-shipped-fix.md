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

Record the beats in order, top to bottom: **1.1, 1.2, 1.3, 2.1, 2.2, 2.3, 2.4, 2.5,
2.6, 2.7, 2.8, 3.1, 3.2, 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 4.7, 5.1**.

| Beats | Status | Recording use |
| --- | --- | --- |
| 1.1, 2.1, 2.2 | Verified | Record with the stated live selectors. |
| 1.2, 1.3, 2.4, 2.6, 2.7, 2.8, 3.1, 3.2, 5.1 | Nav-only / partial | Record only the verified surface; respect each cut condition and the "verify in mapping pass" notes. |
| 2.3, 2.5, 4.1–4.7 | Unverified | Do not include in a finished cut until a follow-up run supplies the missing artifact. |

New UI surfaces added in this pass — the **Generate a Blueprint** option, the **Import
Skill** dialog, the **Clarify** refinement input, the Backlog-to-Ready **board drag** and
task card, and the **Open preview** control — have no confirmed selectors yet. Each is
annotated **verify in mapping pass**: Trinity maps the real refs live before recording, and
the placeholder locators here must not be recorded as-is.

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
playwright-cli video-chapter "Create the project" --description="Point Agentweaver at an empty GitHub repo and name the project" --duration=14000
playwright-cli click "getByRole('button', { name: 'Create from GitHub' })"
playwright-cli click "getByRole('textbox', { name: 'Or paste any repository' })"
playwright-cli type "https://github.com/sabbour/agentweaver-demo-dryrun"
playwright-cli click "getByRole('button', { name: 'Go →' })"
playwright-cli click "getByRole('textbox', { name: 'Project name' })"
playwright-cli type "blueprint-demo"
playwright-cli snapshot
```

Narration: “Here’s an empty GitHub repo. Paste the URL, name the project, and you’re
in.”

Pacing: leave action callouts on while typing; pause briefly on the repository URL.
Transition to 1.2: once the name is set, move straight to picking the team.

---

## Beat 1.2 — Choose a blueprint

**video-chapter**

```bash
playwright-cli video-chapter "Choose a blueprint" --description="Show the Generate-a-Blueprint option, then cast the Product & Software Delivery team" --duration=16000
playwright-cli click "getByRole('button', { name: 'Templates' })"
# Generate-a-Blueprint option — selector unknown, verify in mapping pass.
playwright-cli hover "TODO(mapping): 'Generate a Blueprint' option on the template picker"
playwright-cli click "getByRole('radio', { name: 'Product & Software Delivery' })"
playwright-cli snapshot
playwright-cli click "getByRole('button', { name: 'Create' })"
playwright-cli snapshot
```

Narration: “A blueprint is a reusable team: the roles, the skills they carry, and the
workflows they run. Generate a custom one for your goal, or start from a preset. Here you’ll
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
playwright-cli video-chapter "Inspect the team" --description="Show the agents, browse the curated skills marketplace or import from any GitHub repo, then assign each skill" --duration=16000
playwright-cli click "getByRole('link', { name: 'Agents', exact: true })"
playwright-cli hover "getByRole('list', { name: 'Project agents' })"
playwright-cli hover "getByRole('button', { name: 'Active Ripley Lead PM' })"
playwright-cli hover "getByRole('button', { name: 'Active Dallas Customer Researcher' })"
playwright-cli click "getByRole('link', { name: 'Skills', exact: true })"
# Browse marketplaces — puzzle-piece button that opens the curated marketplace dialog; selector unmapped, verify in mapping pass.
playwright-cli click "TODO(mapping): 'Browse marketplaces' button on the Skills page"
playwright-cli snapshot
# Close the marketplace dialog before the plain import path — close control unmapped, verify in mapping pass.
playwright-cli click "TODO(mapping): close control on the 'Browse curated marketplaces' dialog"
# Import Skill dialog — open to show pulling a skill straight from a GitHub repo URL; selector unverified, verify in mapping pass.
playwright-cli click "getByRole('button', { name: 'Import Skill' })"
playwright-cli snapshot
# Close the dialog before showing assignments — close control unverified, verify in mapping pass.
playwright-cli click "getByRole('tab', { name: 'Assignments', exact: true })"
playwright-cli snapshot
```

Narration: “Every agent has a name, a role, and a set of skills. Browse the curated
marketplaces — like GitHub Awesome Copilot and Azure Skills — or paste any GitHub repo to
import a skill, then assign each one to the agents that need it.”

Pacing: hold on the agents list, then on the Skills catalog, then on the Assignments grid
so the per-agent checkboxes are legible.

Note: Team memory is intentionally saved for the end (Beat 2.8), after a run has had a
chance to write a decision.

**Verify in mapping pass:** the Skills page has two import paths — **Browse marketplaces**
(a puzzle-piece button that opens the **Browse curated marketplaces** dialog, with GitHub
Awesome Copilot and Azure Skills configured on staging) and **Import Skill** (paste an
`owner/repo` or github.com URL). The marketplace button and dialog, the Import Skill button
and dialog, and their close controls are not mapped on staging yet. Capture the real
selectors before recording, and do not record the placeholder locators.

**NOT YET VERIFIED — needs follow-up run:** the Browse marketplaces dialog, the Import
Skill dialog, and a real imported skill were not exercised on staging. Record these
surfaces only once their selectors and a real result are captured.

---

# Act 2 — Frame and ship a feature

## Beat 2.1 — Frame the product

**video-chapter**

```bash
playwright-cli video-chapter "Frame the product" --description="Pick the product workflow and hand the team a real problem to solve" --duration=18000
playwright-cli click "getByTestId('start-task-topbar-action')"
playwright-cli select "getByLabel('Workflow', { exact: true })" "pm-discovery"
playwright-cli click "getByRole('textbox', { name: 'Goal' })"
playwright-cli type "People come out of standup with a messy transcript and no clear record of what was decided or who owns what. I want to launch Standup Scribe, a tool that turns that raw transcript into a clean summary the whole team can share. Work out who this is really for, what they need, and how we'd position and message it. Then figure out the first experience that gets someone from a pile of text to a summary they trust. As the first thing we can put in front of a real user, stand up a landing page that presents the value props as placeholder content, with real structure and stand-in copy, so we can see the story before we build the product behind it."
playwright-cli hover "getByRole('button', { name: 'Define Outcome', exact: true })"
playwright-cli click "getByRole('button', { name: 'Define Outcome', exact: true })"
playwright-cli snapshot
```

Narration: “Start the product workflow and hand the team a real problem: standups end in a
messy transcript with no shared record. Ask them to shape Standup Scribe from there —
product, marketing, research, and design work out who it’s for and what the first
experience should be.”

Pacing: type the goal naturally; pause before Define Outcome. Select the product
discovery workflow with the verified `pm-discovery` value before entering the goal.

Transition to Beat 2.2: the Outcome plan can remain Pending for roughly two minutes.
Time-lapse that wait or cut to a pre-warmed run when the confirmation panel is ready;
do not record idle polling.

---

## Beat 2.2 — Review and confirm the plan

**video-chapter**

```bash
playwright-cli video-chapter "Review and confirm the plan" --description="Read the OutcomeSpec, use Clarify to refine it, allow independent tasks, and confirm" --duration=16000
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

Narration: “Read the OutcomeSpec the team came back with — its first slice is a value-prop
landing page: a welcome banner, the value props as placeholders, and one ‘Paste
transcript’ button. Use Clarify to tighten the scope, let the independent pieces run as
their own tasks, and confirm.”

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
playwright-cli video-chapter "Watch the work plan run" --description="Open the topology graph, step through the nodes, then watch the run produce artifacts" --duration=16000
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

Narration: “The coordinator turns the plan into a graph of tasks. Click through a few
nodes to see what each agent’s doing, close it, and watch the work land.”

Pacing: let each selected-node label settle. The verified graph focused Coordinator and
Work plan at 130%; Zoom in on Research reached 156%. After closing the graph, hold on the
run view while real output appears — use a pre-warmed run or a speed-ramp, never idle
polling.

**NOT YET VERIFIED — needs follow-up run:** the topology graph controls are verified, but
the live execution artifacts (the files and outputs the nodes produce) were not captured.
Get the real artifact selectors before recording the “watch the work land” portion, and do
not stage an empty run view as if work were flowing.

Transition to Beat 2.4: once the tasks exist, move to the board to see them and queue
one up.

---

## Beat 2.4 — Review the board

**video-chapter**

```bash
playwright-cli video-chapter "Review the board" --description="See the promoted tasks, move the landing-page task from Backlog to Ready, and watch it get picked up" --duration=16000
playwright-cli click "getByRole('link', { name: 'Board', exact: true })"
playwright-cli hover "getByRole('region', { name: 'Backlog column' })"
playwright-cli hover "getByRole('region', { name: 'Ready column' })"
playwright-cli snapshot
# Human move: drag the landing-page card from Backlog to Ready — verb, card, and drop target unverified, verify in mapping pass.
# playwright-cli drag "TODO(mapping): landing-page card in Backlog" "TODO(mapping): Ready column drop target"
playwright-cli snapshot
```

Narration: “Independent task promotion split the plan into separate tasks. Drag the
landing-page task from Backlog to Ready, and the coordinator picks it up.”

Pacing: hold on Backlog so the split tasks are readable. You can only drag between Backlog
and Ready; after the move, wait for the heartbeat to pull the card into Active. Pre-warm a
Ready task or speed-ramp the pickup; never record idle polling.

**Verify in mapping pass:** the landing-page card selector and the Backlog-to-Ready drag
targets are not mapped, and the heartbeat pickup into Active was not captured. Trinity maps
the card and the drop target; pre-warm a run so the pickup is visible.

---

## Beat 2.5 — Ship it

**video-chapter**

```bash
playwright-cli video-chapter "Ship it" --description="Approve gates as they appear, wait for the preview environment in Build and Test, and open the live landing page" --duration=18000
# Approve each gate as it appears (tool, permission, and preview-approval cards):
playwright-cli click "getByTestId('notification-bell')"
playwright-cli snapshot
playwright-cli click "getByTestId('notification-bell')"
# Preview environment: after Build & Test, an "Open preview" control exposes the Gateway preview URL — selector unverified, verify in mapping pass.
playwright-cli click "TODO(mapping): 'Open preview' control on the Build & Test row"
playwright-cli snapshot
```

Narration: “As the work runs, approve each gate that comes up. After Build and Test, a
preview environment spins up, and you open it to see the landing page running live.”

Pacing: there may be more than one approval gate; approve each as it appears. Wait for the
preview to reach “Open preview” before you launch it, using a pre-warmed run or a
speed-ramp.

Human-review automation: the preview step runs after Build & Test. Its states are **Open
preview** (a reachable Gateway URL), **Preview pending approval** (approve the tool-approval
card), and **Preview unavailable** (non-blocking). The preview URL appears on the Build &
Test row and in the human-review artifacts panel.

**Verify in mapping pass:** the **Open preview** control and the preview URL are not mapped.
The preview can self-skip when there is no reachable Gateway preview, so pre-warm a run that
produced a live preview, and let Trinity map the control before recording.

**NOT YET VERIFIED — needs follow-up run:** no live preview environment or rendered landing
page was captured on staging.

---

## Beat 2.6 — Approve the merge

**video-chapter**

```bash
playwright-cli video-chapter "Approve the merge" --description="Open the final approval notification, approve the merge, and watch the run finish" --duration=14000
playwright-cli click "getByTestId('notification-bell')"
playwright-cli snapshot
playwright-cli click "getByTestId('notification-bell')"
# Only when a live review gate is present:
playwright-cli hover "getByRole('button', { name: 'Approve & merge', exact: true })"
# [PAUSE 700ms]
playwright-cli click "getByRole('button', { name: 'Approve & merge', exact: true })"
playwright-cli snapshot
```

Narration: “When the work’s ready, a notification asks you to approve the merge. Review
the result, approve, and the run finishes.”

Pacing: wait for the approval notification to arrive, then open it. Use the pause on
Approve & merge so the automated click still reads as an intentional human decision.

Human-review automation: the notification bell is verified; its empty state reads
“Nothing needs your attention right now.” Approve & merge is verified on a live review
gate.

**NOT YET VERIFIED — needs follow-up run:** the approval-request notification and the
feature-slice review gate were not exercised end to end in the primary PM scenario.
Pre-warm a run that has reached the gate before recording this beat.

---

## Beat 2.7 — Check project health

**video-chapter**

```bash
playwright-cli video-chapter "Check project health" --description="See throughput, quality, cost, and traces for each agent" --duration=14000
playwright-cli click "getByRole('link', { name: 'Dashboard', exact: true })"
playwright-cli hover "getByRole('heading', { name: 'Operational signals' })"
playwright-cli hover "getByRole('table', { name: 'Agent leaderboard' })"
playwright-cli click "getByRole('link', { name: 'Observability', exact: true })"
playwright-cli click "getByRole('tab', { name: 'Traces', exact: true })"
playwright-cli click "getByRole('tab', { name: 'Agents', exact: true })"
playwright-cli snapshot
```

Narration: “The Dashboard shows throughput and quality. Observability shows model use,
cost, latency, and per-agent traces.”

Pacing: do not hard-code changing counts. The live controls verified here are Dashboard
Refresh and Time range, Observability time range and Refresh, and Overview, Traces,
and Agents tabs.

---

## Beat 2.8 — Review team memory

**video-chapter**

```bash
playwright-cli video-chapter "Review team memory" --description="Show the decisions the run wrote down" --duration=8000
playwright-cli click "getByRole('link', { name: 'Memories', exact: true })"
playwright-cli click "getByRole('tab', { name: 'Decisions', exact: true })"
playwright-cli snapshot
```

Narration: “The run saved its decisions, so your next piece of work starts with that
context already in hand.”

Reason for placement: this pays off the team we met in Beat 1.3, after a workflow has had a
chance to write a decision, instead of showing an empty memory page early.

**NOT YET VERIFIED — needs follow-up run:** no decision card was present in this project;
cut this beat until a real decision is visible.

---

# Act 3 — Put it on autopilot

The feature shipped once. Now make it run again without you: on a clock, or whenever
something happens in GitHub.

## Beat 3.1 — Put it on a schedule

**video-chapter**

```bash
playwright-cli video-chapter "Put it on a schedule" --description="Open the workflow that just ran and set it to run on a recurring cadence" --duration=12000
# Workflows page and the just-run workflow row — selectors unknown, verify in mapping pass.
playwright-cli click "TODO(mapping): 'Workflows' nav link"
playwright-cli click "TODO(mapping): row for the workflow that just ran"
# 'Add schedule' control and cadence selectors — verify in mapping pass.
playwright-cli click "TODO(mapping): 'Add schedule' button"
playwright-cli click "TODO(mapping): cadence selector (daily, weekly, or monthly, in UTC)"
playwright-cli snapshot
```

Narration: “Open Workflows and pick the one that just delivered. Add a schedule, choose a
daily, weekly, or monthly cadence in UTC, and it runs on its own from here on.”

Pacing: hover the cadence options long enough to read them before picking one.

**Verify in mapping pass:** the **Add schedule** control and the cadence selectors are
not mapped yet; let Trinity confirm the real refs before recording.

---

## Beat 3.2 — Trigger it from GitHub

**video-chapter**

```bash
playwright-cli video-chapter "Trigger it from GitHub" --description="Generate a webhook secret in Project Settings and wire it into a real GitHub repo webhook" --duration=14000
playwright-cli click "getByRole('link', { name: 'Project Settings', exact: true })"
# Webhooks tab, generate-secret control, and payload URL — selectors unknown, verify in mapping pass.
playwright-cli click "TODO(mapping): 'Webhooks' tab"
playwright-cli click "TODO(mapping): generate-secret control (reveal-once)"
playwright-cli click "TODO(mapping): payload URL field"
playwright-cli snapshot
```

Narration: “A schedule covers time; webhooks cover events. In Project Settings, Webhooks,
generate a secret, copy the payload URL, and wire it into a real GitHub repo webhook. Now
a push or a merge kicks off the same run.”

Pacing: reveal the secret once, and let the callout capture that reveal-once state
before you move on to the payload URL.

**Verify in mapping pass:** the Webhooks tab path, the generate-secret control, and the
payload URL selectors are not mapped yet; let Trinity confirm the real refs before
recording.

---

# Act 4 — Triage the seeded bug

The feature ships and reruns on its own. Now turn to a bug that was already filed against
this repo.

## Beat 4.1 — Pivot to the seeded bug

**video-chapter**

```bash
playwright-cli video-chapter "Pivot to the seeded bug" --description="Start the repair from the existing GitHub issue" --duration=8000
# NOT YET VERIFIED — needs follow-up run.
```

**DRAFT VO — only record once verified:** “Same project, a real bug: on a narrow tablet,
the welcome banner overlaps the primary button, so people can’t get started.”

**NOT YET VERIFIED — needs follow-up run:** no Agentweaver issue-list or linked-issue
surface was validated. Keep the GitHub issue as pre-recording setup.

---

## Beat 4.2 — Ask the assistant to triage

**video-chapter**

```bash
playwright-cli video-chapter "Ask the assistant to triage" --description="Have the assistant read the issue and start a Bug Fix workflow" --duration=14000
playwright-cli click "getByRole('button', { name: 'New session', exact: true })"
playwright-cli click "getByRole('textbox', { name: 'Message the assistant...' })"
playwright-cli type "Triage https://github.com/sabbour/agentweaver-demo-dryrun/issues/1. Investigate the narrow-tablet welcome-banner overlap, propose a minimal fix and test plan, then use the Bug Fix workflow."
playwright-cli snapshot
playwright-cli click "getByRole('button', { name: 'Send', exact: true })"
```

Narration: “The assistant reads the issue, proposes the smallest safe fix and a test plan,
and starts a Bug Fix workflow. Anything that changes state still waits for your approval.”

Pacing: pause after typing, then allow the first streamed reply to appear.

Transition to Beat 4.3: assistant-created orchestration may sit Pending for about two
minutes, and a full workflow can take 16 or more minutes. Use a speed-ramp or resume a
pre-warmed bug run at its first real output; never record the idle wait.

**NOT YET VERIFIED — needs follow-up run:** the console, textbox, and Send action were
verified, but this issue-specific prompt was not sent and its output was not recorded.

---

## Beat 4.3 — Read and scope the bug

**video-chapter**

```bash
playwright-cli video-chapter "Read and scope the bug" --description="Show the diagnosis, the expected behavior, and the smallest safe fix" --duration=10000
# NOT YET VERIFIED — needs follow-up run.
```

Narration: “Before touching code, the workflow spells out what’s broken, what should
happen, and how small the fix can stay.”

**NOT YET VERIFIED — needs follow-up run:** capture real bug-output selectors from the
assistant-created run.

---

## Beat 4.4 — Implement and test the repair

**video-chapter**

```bash
playwright-cli video-chapter "Implement and test the repair" --description="Show the fix and the tests that prove it" --duration=14000
# NOT YET VERIFIED — needs follow-up run.
```

Narration: “Engineering finds the cause, fixes it, and proves it with tests.”

**NOT YET VERIFIED — needs follow-up run:** no issue-specific implementation run was
validated.

---

## Beat 4.5 — Preview the repaired behavior

**video-chapter**

```bash
playwright-cli video-chapter "Preview the repaired behavior" --description="Show the narrow-tablet layout working before merge" --duration=10000
# NOT YET VERIFIED — needs follow-up run.
```

Narration: “Preview the fix the same way you previewed the feature. Now the banner and
the button don’t collide on a narrow tablet.”

**NOT YET VERIFIED — needs follow-up run:** no bug-preview surface was reached.

---

## Beat 4.6 — Approve the bug fix

**video-chapter**

```bash
playwright-cli video-chapter "Approve the bug fix" --description="Make the final merge decision" --duration=10000
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

## Beat 4.7 — Close the loop on the issue

**video-chapter**

```bash
playwright-cli video-chapter "Close the loop on the issue" --description="Show the merged PR linked back to the original issue" --duration=10000
```

Narration: “That closes the loop: from an idea, to a shipped feature, to a fixed bug
linked back to its issue.”

**NOT YET VERIFIED — needs follow-up run:** no bug-fix merge or issue-linked PR was
generated. Record this final image only once those real artifacts exist.

External-surface requirement: show the issue-linked PR in a deliberate second
**github.com** tab, with separately captured selectors and cursor/action-callout
behavior. Do not imply that this evidence is an in-app Agentweaver page.

---

# Coda — Bring your own tools

## Beat 5.1 — Drive it from your own tools

**video-chapter**

```bash
playwright-cli video-chapter "Drive it from your own tools" --description="Copy the MCP server URL and ready-to-paste client configs from Account settings, and confirm the bearer token stays masked" --duration=12000
playwright-cli click "getByRole('link', { name: 'Account settings', exact: true })"
# 'MCP clients' nav path and 'Copy config' buttons — selectors unknown, verify in mapping pass.
playwright-cli click "TODO(mapping): 'MCP clients' nav item"
playwright-cli hover "TODO(mapping): masked bearer token"
playwright-cli click "TODO(mapping): 'Copy config' button (Claude Desktop)"
playwright-cli click "TODO(mapping): 'Copy config' button (VS Code)"
playwright-cli click "TODO(mapping): 'Copy config' button (Copilot CLI)"
playwright-cli snapshot
playwright-cli video-stop
```

Narration: “None of this needs a browser. Connect Agentweaver to Claude Desktop, VS
Code, or Copilot CLI over MCP, copy the config, and drive the same team and workflows
from your own tools.”

Pacing: keep the bearer token masked in every frame; copy each client config in turn and
let the “Copied” state register before you move to the next one.

Reason for placement: this closes the demo on its widest surface, the team and workflows
you cast now reach past the browser into whatever tool you already use.

**Verify in mapping pass:** the Account settings → MCP clients nav path and the **Copy
config** buttons are not mapped yet; let Trinity confirm the real refs before recording.
