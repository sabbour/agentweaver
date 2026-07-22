# Blueprint to shipped fix — recording script

## Recording status

Record against staging with a single authenticated browser session. The project is
**blueprint-demo** and the seeded bug is
<https://github.com/sabbour/agentweaver-demo-dryrun/issues/1>.

The PM discovery run `b3bda0e2-2a6b-4e29-9a88-0566178f681e` completed. A second
live run verified the Outcome-plan confirmation flow. Do not represent a later
unverified flow as completed: each such beat below is explicitly marked **NOT YET
VERIFIED — needs follow-up run**.

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

## Beat 1 — Create the project

**video-chapter**

```bash
playwright-cli video-chapter "Create project from an empty repo" --description="Connect the seeded GitHub repository and create the project." --duration=14000
playwright-cli click "getByRole('button', { name: 'Create from GitHub' })"
playwright-cli click "getByRole('textbox', { name: 'Or paste any repository' })"
playwright-cli type "https://github.com/sabbour/agentweaver-demo-dryrun"
playwright-cli click "getByRole('button', { name: 'Go →' })"
playwright-cli click "getByRole('textbox', { name: 'Project name' })"
playwright-cli type "blueprint-demo"
playwright-cli snapshot
```

Narration: “We begin with a deliberately empty GitHub repository and turn it into a
fresh Agentweaver project.”

Pacing: leave action callouts on while typing; pause briefly on the repository URL.

---

## Beat 2 — Cast the product and software delivery team

**video-chapter**

```bash
playwright-cli video-chapter "Cast a cross-functional team" --description="Select the Product & Software Delivery template." --duration=12000
playwright-cli click "getByRole('button', { name: 'Templates' })"
playwright-cli click "getByRole('radio', { name: 'Product & Software Delivery' })"
playwright-cli snapshot
playwright-cli click "getByRole('button', { name: 'Create' })"
playwright-cli snapshot
```

Narration: “This template brings together product management, customer research,
marketing, design, engineering, QA, and delivery.”

Pacing: linger on the template preview before Create.

Note: verified staging behavior casts the template immediately on project creation;
there is no separate cast-confirmation gate.

---

## Beat 2a — Inspect agents, skills, and memory

**video-chapter**

```bash
playwright-cli video-chapter "Inspect the team behind the blueprint" --description="Show the cast, reusable skills, and durable team memory." --duration=14000
playwright-cli click "getByRole('link', { name: 'Agents', exact: true })"
playwright-cli hover "getByRole('list', { name: 'Project agents' })"
playwright-cli hover "getByRole('button', { name: 'Active Ripley Lead PM' })"
playwright-cli hover "getByRole('button', { name: 'Active Dallas Customer Researcher' })"
playwright-cli click "getByRole('link', { name: 'Skills', exact: true })"
playwright-cli click "getByRole('tab', { name: 'Assignments', exact: true })"
playwright-cli snapshot
playwright-cli click "getByRole('link', { name: 'Memories', exact: true })"
playwright-cli snapshot
```

Narration: “The blueprint is an operating team, not a generic prompt: its members
have named roles, reusable skills, and a shared record of decisions.”

Pacing: hold on the agents list, then on assigned skills.

**NOT YET VERIFIED — needs follow-up run:** the current Team memory page has
Decisions, Agent memory, and Session history tabs, but no recorded entries. Show a
real captured decision only after a later workflow creates one.

---

## Beat 3 — Frame the feature with PM discovery

**video-chapter**

```bash
playwright-cli video-chapter "Frame one small feature" --description="Ask PM to define a tiny, testable first slice." --duration=18000
playwright-cli click "getByTestId('start-task-topbar-action')"
playwright-cli click "getByLabel('Workflow', { exact: true })"
playwright-cli click "getByRole('textbox', { name: 'Goal' })"
playwright-cli type "Frame a tiny first feature for this empty repo. Define the problem, target user, success criteria, and keep scope to one very small MVP slice only."
playwright-cli hover "getByRole('button', { name: 'Define Outcome', exact: true })"
playwright-cli click "getByRole('button', { name: 'Define Outcome', exact: true })"
playwright-cli snapshot
```

Narration: “PM starts by defining one user problem, one small outcome, and clear
success criteria.”

Pacing: type naturally; pause before Define Outcome. Select Product Management
Discovery in the Workflow control before recording this take.

---

## Beat 3c — Confirm the outcome plan and choose task promotion

**video-chapter**

```bash
playwright-cli video-chapter "Confirm the outcome plan" --description="Review scope, opt into independent task promotion, and dispatch deliberately." --duration=14000
playwright-cli hover "getByRole('checkbox', { name: 'Independent task promotion Allow standalone backlog tasks for independent deliverables' })"
playwright-cli click "getByRole('checkbox', { name: 'Independent task promotion Allow standalone backlog tasks for independent deliverables' })"
playwright-cli hover "getByRole('button', { name: 'Clarify plan', exact: true })"
playwright-cli hover "getByRole('button', { name: 'Confirm plan', exact: true })"
# [PAUSE 700ms]
playwright-cli click "getByRole('button', { name: 'Confirm plan', exact: true })"
playwright-cli snapshot
```

Narration: “Before work dispatches, we inspect the Outcome plan. We allow genuinely
independent deliverables to become standalone backlog tasks, then explicitly confirm
the plan.”

Pacing: do not click Clarify plan in this take; it is the alternative path for
revision. Pause before Confirm plan so the human decision is legible.

Human-review automation: Confirm plan is the verified dispatch gate. Its live result
is “Outcome plan confirmed … Dispatch is unblocked,” followed by Break into tasks.

---

## Beat 3a — Check notifications while work runs

**video-chapter**

```bash
playwright-cli video-chapter "Check live notifications" --description="Show the notification feed without leaving the run." --duration=8000
playwright-cli click "getByTestId('notification-bell')"
playwright-cli hover "getByRole('switch', { name: 'Sound on' })"
playwright-cli snapshot
playwright-cli click "getByTestId('notification-bell')"
```

Narration: “While agents work asynchronously, notifications keep attention items
visible without interrupting the run.”

Pacing: use a real incoming notification if available; the verified empty state is
“Nothing needs your attention right now.”

---

## Beat 3b — Follow the topology graph

**video-chapter**

```bash
playwright-cli video-chapter "Follow work through the topology graph" --description="Focus coordinator, plan, and research nodes." --duration=12000
playwright-cli click "getByTestId('open-topology-minimap')"
playwright-cli click "getByRole('button', { name: 'Coordinator: In Progress' })"
playwright-cli snapshot
playwright-cli click "getByRole('button', { name: 'Work plan: Complete' })"
playwright-cli click "getByRole('button', { name: /Research the problem space/ })"
playwright-cli click "getByRole('button', { name: 'Zoom in' })"
playwright-cli snapshot
playwright-cli click "getByRole('button', { name: 'Fit to view' })"
playwright-cli click "getByRole('button', { name: 'Close panel' })"
```

Narration: “The graph is active evidence, not a status list: selecting a node focuses
its part of the workflow, and zoom makes the active path readable.”

Pacing: let each selected-node label settle. The verified graph focused Coordinator
and Work plan at 130%; Zoom in on Research reached 156%.

---

## Beat 4 — Review customer research

**video-chapter**

```bash
playwright-cli video-chapter "Review customer research" --description="Inspect the PM workflow's research subtask." --duration=10000
playwright-cli snapshot
```

Narration: “Customer research grounds the feature in a problem space before the team
names or builds anything.”

**NOT YET VERIFIED — needs follow-up run:** the completed PM graph showed the research
task Ready for assembly, but no standalone output panel was exposed. Capture its real
output selector before recording this chapter.

---

## Beat 5 — Name and position the idea

**video-chapter**

```bash
playwright-cli video-chapter "Name and position the idea" --description="Turn validated evidence into a concise market position." --duration=10000
# NOT YET VERIFIED — needs follow-up run.
```

Narration: “With evidence in hand, the team turns the idea into a name, a tagline,
and a credible positioning statement.”

**NOT YET VERIFIED — needs follow-up run:** no live positioning-output surface was
reached; do not invent its task or output selectors.

---

## Beat 6 — Generate launch messaging

**video-chapter**

```bash
playwright-cli video-chapter "Generate launch messaging" --description="Create concise launch copy from the chosen position." --duration=10000
# NOT YET VERIFIED — needs follow-up run.
```

Narration: “Marketing turns the positioned idea into launch-ready copy and a concise
announcement.”

**NOT YET VERIFIED — needs follow-up run:** no marketing run or output surface was
verified.

---

## Beat 7 — Preview the proposed backlog

**video-chapter**

```bash
playwright-cli video-chapter "Break the plan into tasks" --description="Preview the proposed backlog before creating task cards." --duration=12000
playwright-cli click "getByRole('button', { name: 'Break into tasks', exact: true })"
playwright-cli snapshot
```

Narration: “After outcome confirmation, we preview the compact backlog before
creating any task cards.”

Pacing: hold on the dialog titled “Preview proposed backlog items” and read one
proposed title and its state.

---

## Beat 7a — Create tasks and show the board

**video-chapter**

```bash
playwright-cli video-chapter "Show the imported task board" --description="Create the reviewed backlog and inspect the resulting cards." --duration=12000
playwright-cli click "getByRole('button', { name: 'Create tasks', exact: true })"
playwright-cli click "getByRole('link', { name: 'Board', exact: true })"
playwright-cli hover "getByRole('region', { name: 'Backlog column' })"
playwright-cli hover "getByRole('region', { name: 'Ready column' })"
playwright-cli snapshot
```

Narration: “Once the compact backlog is accepted, its cards should appear on the
board, ready for the smallest shippable slice.”

Pacing: linger on the imported card title before moving it.

**NOT YET VERIFIED — needs follow-up run:** Create tasks was observed in the preview,
but the completed Board check still showed zero Backlog and Ready cards. Do not claim
the card-import result until it is visible.

---

## Beat 8 — Implement the first task

**video-chapter**

```bash
playwright-cli video-chapter "Implement the first slice" --description="Show engineering execution, code, and tests." --duration=16000
# NOT YET VERIFIED — needs follow-up run.
```

Narration: “The first ready task enters an engineering workflow that combines design,
implementation, and test evidence.”

**NOT YET VERIFIED — needs follow-up run:** no task-card-to-engineering execution
path was validated.

---

## Beat 9 — Review and merge the feature

**video-chapter**

```bash
playwright-cli video-chapter "Review and merge the first slice" --description="Show human review before merge." --duration=14000
# Only when a live review gate is present:
playwright-cli hover "getByRole('button', { name: 'Approve & merge', exact: true })"
# [PAUSE 700ms]
playwright-cli click "getByRole('button', { name: 'Approve & merge', exact: true })"
playwright-cli snapshot
```

Narration: “Nothing merges silently. A person reviews the assembled result and makes
the explicit merge decision.”

Human-review automation: **Approve & merge** is verified on a live review gate; use
the pause so the automated click still reads as intentional.

**NOT YET VERIFIED — needs follow-up run:** feature-slice review and merge were not
completed in the primary PM scenario.

---

## Beat 9a — Inspect shipped files and workspace

**video-chapter**

```bash
playwright-cli video-chapter "Inspect the shipped files" --description="Move from merged PR diff to Agentweaver workspace." --duration=12000
playwright-cli click "getByRole('link', { name: 'Workspace' })"
playwright-cli click "getByRole('combobox', { name: 'Branch or worktree' })"
playwright-cli snapshot
```

Narration: “After merge, we inspect precisely what changed, then return to the
project workspace and its merged branch.”

**NOT YET VERIFIED — needs follow-up run:** Workspace and its branch/worktree picker
are verified, but a generated merged PR and its Files changed tab are not. Capture the
PR-diff selector from the real PR rather than inventing one.

---

## Beat 9b — Review project health and observability

**video-chapter**

```bash
playwright-cli video-chapter "Review post-ship health" --description="Show throughput, quality, cost, traces, and agent telemetry." --duration=14000
playwright-cli click "getByRole('link', { name: 'Dashboard', exact: true })"
playwright-cli hover "getByRole('heading', { name: 'Operational signals' })"
playwright-cli hover "getByRole('table', { name: 'Agent leaderboard' })"
playwright-cli click "getByRole('link', { name: 'Observability', exact: true })"
playwright-cli click "getByRole('tab', { name: 'Traces', exact: true })"
playwright-cli click "getByRole('tab', { name: 'Agents', exact: true })"
playwright-cli snapshot
```

Narration: “The project’s result is visible beyond code: Dashboard summarizes
throughput and quality, while Observability shows model use, AIC, latency, traces, and
agent-level telemetry.”

Pacing: do not hard-code changing counts. The live controls verified here are Dashboard
Refresh and Time range, Observability time range and Refresh, and Overview, Traces,
and Agents tabs.

---

## Beat 10 — Pivot to the seeded bug

**video-chapter**

```bash
playwright-cli video-chapter "Pivot to the seeded bug" --description="Use the existing GitHub issue as the repair starting point." --duration=8000
# NOT YET VERIFIED — needs follow-up run.
```

Narration: “With the feature story complete, we pivot to the pre-seeded narrow-tablet
welcome-banner bug.”

**NOT YET VERIFIED — needs follow-up run:** no Agentweaver issue-list or linked-issue
surface was validated. Keep the GitHub issue as pre-recording setup.

---

## Beat 10a — Ask the browser assistant to triage the bug

**video-chapter**

```bash
playwright-cli video-chapter "Launch assisted bug triage" --description="Ask the operator assistant to inspect the seeded issue and start a governed repair." --duration=14000
playwright-cli click "getByRole('button', { name: 'New session', exact: true })"
playwright-cli click "getByRole('textbox', { name: 'Message the assistant...' })"
playwright-cli type "Triage https://github.com/sabbour/agentweaver-demo-dryrun/issues/1. Investigate the narrow-tablet welcome-banner overlap, propose a minimal fix and test plan, then use the Bug Fix workflow."
playwright-cli snapshot
playwright-cli click "getByRole('button', { name: 'Send', exact: true })"
```

Narration: “The operator assistant can inspect the issue, propose the smallest safe
fix and test plan, and start a governed Bug Fix workflow. State-changing actions still
require approval.”

Pacing: pause after typing, then allow the first streamed reply to appear.

**NOT YET VERIFIED — needs follow-up run:** the console, textbox, and Send action were
verified, but this issue-specific prompt was not sent and its output was not recorded.

---

## Beat 11 — Scope the bug

**video-chapter**

```bash
playwright-cli video-chapter "Read and scope the bug" --description="Show diagnosis, expected behavior, and the smallest safe repair." --duration=10000
# NOT YET VERIFIED — needs follow-up run.
```

Narration: “Before code changes, the bug workflow makes the failing behavior,
expectation, and safe scope explicit.”

**NOT YET VERIFIED — needs follow-up run:** capture real bug-output selectors from
the assistant-created run.

---

## Beat 12 — Implement and test the repair

**video-chapter**

```bash
playwright-cli video-chapter "Implement and test the repair" --description="Show the fix and its test evidence." --duration=14000
# NOT YET VERIFIED — needs follow-up run.
```

Narration: “Engineering diagnoses, repairs, and proves the fix with tests.”

**NOT YET VERIFIED — needs follow-up run:** no issue-specific implementation run was
validated.

---

## Beat 13 — Preview the repaired behavior

**video-chapter**

```bash
playwright-cli video-chapter "Preview the repaired behavior" --description="Show the narrow-tablet fix before merge." --duration=10000
# NOT YET VERIFIED — needs follow-up run.
```

Narration: “The repair gets the same live preview discipline as the feature.”

**NOT YET VERIFIED — needs follow-up run:** no bug-preview surface was reached.

---

## Beat 14 — Approve the bug-fix review

**video-chapter**

```bash
playwright-cli video-chapter "Approve the bug fix" --description="Make the final human merge decision." --duration=10000
# Only when a live review gate is present:
playwright-cli hover "getByRole('button', { name: 'Approve & merge', exact: true })"
# [PAUSE 700ms]
playwright-cli click "getByRole('button', { name: 'Approve & merge', exact: true })"
playwright-cli snapshot
```

Narration: “The repair remains at a human review gate until someone explicitly
approves merge.”

Human-review automation: use the verified **Approve & merge** locator only for the
review gate belonging to the bug-fix run.

**NOT YET VERIFIED — needs follow-up run:** no bug-fix review gate was exercised.

---

## Beat 15 — Close on the issue-linked PR

**video-chapter**

```bash
playwright-cli video-chapter "Close the issue-to-fix loop" --description="Show the merged PR and its link to the original issue." --duration=10000
playwright-cli video-stop
```

Narration: “We close the loop from product idea to shipped feature to a repaired,
issue-linked fix.”

**NOT YET VERIFIED — needs follow-up run:** no bug-fix merge or issue-linked PR was
generated. Record this final image only once those real artifacts exist.
