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

Use this execution order; the lettered labels preserve the narrative grouping from
earlier planning but are not chronological: **1, 2, 2a, 3, 3c, 3a, 3b, 4, 5, 6,
7, 7a, 8, 9, 9a, 9b, 9c, 10, 10a, 11, 12, 13, 14, 15**.

| Beats | Status | Recording use |
| --- | --- | --- |
| 1–3, 3c, 3b, 7 | Verified | Record with the stated live selectors. |
| 2a, 3a, 9a, 9b, 9c, 10a | Nav-only / partial | Record only the verified surface; respect the stated cut condition. |
| 4–6, 7a–8, 9–10, 11–15 | Unverified | Do not include in a finished cut until a follow-up run supplies the missing artifact. |

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

## Beat 1 — Create the project

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

---

## Beat 2 — Cast the product and software delivery team

**video-chapter**

```bash
playwright-cli video-chapter "Cast the team" --description="Pick the Product & Software Delivery template." --duration=12000
playwright-cli click "getByRole('button', { name: 'Templates' })"
playwright-cli click "getByRole('radio', { name: 'Product & Software Delivery' })"
playwright-cli snapshot
playwright-cli click "getByRole('button', { name: 'Create' })"
playwright-cli snapshot
```

Narration: “This template comes with a product manager, a researcher, marketing,
design, engineering, QA, and delivery.”

Pacing: linger on the template preview before Create.

Note: verified staging behavior casts the template immediately on project creation;
there is no separate cast-confirmation gate.

---

## Beat 2a — Inspect agents, skills, and memory

**video-chapter**

```bash
playwright-cli video-chapter "See who's on the team" --description="Look at the agents, the skills they share, and the team's memory." --duration=14000
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

**DRAFT VO — only record once verified:** “Each agent has a name and a role, a set of
skills, and a shared record of the decisions they make.”

Pacing: hold on the agents list, then on assigned skills.

**NOT YET VERIFIED — needs follow-up run:** the current Team memory page has
Decisions, Agent memory, and Session history tabs, but no recorded entries. Show a
real captured decision only after a later workflow creates one.

---

## Beat 3 — Frame the feature with PM discovery

**video-chapter**

```bash
playwright-cli video-chapter "Frame the feature" --description="Ask the PM to define a small, testable first slice." --duration=18000
playwright-cli click "getByTestId('start-task-topbar-action')"
playwright-cli select "getByLabel('Workflow', { exact: true })" "pm-discovery"
playwright-cli click "getByRole('textbox', { name: 'Goal' })"
playwright-cli type "Frame a tiny first feature for this empty repo. Define the problem, target user, success criteria, and keep scope to one very small MVP slice only."
playwright-cli hover "getByRole('button', { name: 'Define Outcome', exact: true })"
playwright-cli click "getByRole('button', { name: 'Define Outcome', exact: true })"
playwright-cli snapshot
```

Narration: “The PM defines one user problem, one small outcome, and how we’ll know it
worked.”

Pacing: type naturally; pause before Define Outcome. Select Product Management
Discovery with the verified `pm-discovery` value before entering the goal.

Transition to Beat 3c: the Outcome plan can remain Pending for roughly two minutes.
Time-lapse that wait or cut to a pre-warmed run when the confirmation panel is ready;
do not record idle polling.

---

## Beat 3c — Confirm the outcome plan and choose task promotion

**video-chapter**

```bash
playwright-cli video-chapter "Confirm the plan" --description="Check the scope, allow independent tasks, and confirm the plan." --duration=14000
playwright-cli hover "getByRole('checkbox', { name: 'Independent task promotion Allow standalone backlog tasks for independent deliverables' })"
playwright-cli click "getByRole('checkbox', { name: 'Independent task promotion Allow standalone backlog tasks for independent deliverables' })"
playwright-cli hover "getByRole('button', { name: 'Clarify plan', exact: true })"
playwright-cli hover "getByRole('button', { name: 'Confirm plan', exact: true })"
# [PAUSE 700ms]
playwright-cli click "getByRole('button', { name: 'Confirm plan', exact: true })"
playwright-cli snapshot
```

Narration: “I read the plan first. I let the independent pieces become their own
backlog tasks, then confirm.”

Pacing: do not click Clarify plan in this take; it is the alternative path for
revision. Pause before Confirm plan so the human decision is legible.

Human-review automation: Confirm plan is the verified dispatch gate. Its live result
is “Outcome plan confirmed … Dispatch is unblocked,” followed by Break into tasks.

---

## Beat 3a — Check notifications while work runs

**video-chapter**

```bash
playwright-cli video-chapter "Check live notifications" --description="Open the notification feed without leaving the run." --duration=8000
playwright-cli click "getByTestId('notification-bell')"
playwright-cli hover "getByRole('switch', { name: 'Sound on' })"
playwright-cli snapshot
playwright-cli click "getByTestId('notification-bell')"
```

Narration: “The agents work in the background. Anything that needs me shows up here.”

Pacing: use a real incoming notification if available; the verified empty state is
“Nothing needs your attention right now.”

Cut candidate: include this beat only when a notification can be deliberately
triggered before the take. An empty notification tray does not earn a chapter.

---

## Beat 3b — Follow the topology graph

**video-chapter**

```bash
playwright-cli video-chapter "Follow the graph" --description="Focus the coordinator, plan, and research nodes." --duration=12000
playwright-cli click "getByTestId('open-topology-minimap')"
playwright-cli click "getByRole('button', { name: /Coordinator/ })"
playwright-cli snapshot
playwright-cli click "getByRole('button', { name: /Work plan/ })"
playwright-cli click "getByRole('button', { name: /Research the problem space/ })"
playwright-cli click "getByRole('button', { name: 'Zoom in' })"
playwright-cli snapshot
playwright-cli click "getByRole('button', { name: 'Fit to view' })"
playwright-cli click "getByRole('button', { name: 'Close panel' })"
```

Narration: “Click a node and the graph focuses that part of the run. Zoom in to read
the path the agents are taking.”

Pacing: let each selected-node label settle. The verified graph focused Coordinator
and Work plan at 130%; Zoom in on Research reached 156%.

---

## Beat 4 — Review customer research

**video-chapter**

```bash
playwright-cli video-chapter "Review customer research" --description="Open the research subtask from the PM run." --duration=10000
playwright-cli snapshot
```

**DRAFT VO — only record once verified:** “The researcher looks at the problem before
anyone names or builds the feature.”

**NOT YET VERIFIED — needs follow-up run:** the completed PM graph showed the research
task Ready for assembly, but no standalone output panel was exposed. Capture its real
output selector before recording this chapter.

---

## Beat 5 — Name and position the idea

**video-chapter**

```bash
playwright-cli video-chapter "Name and position the idea" --description="Turn the research into a name and a market position." --duration=10000
# NOT YET VERIFIED — needs follow-up run.
```

**DRAFT VO — only record once verified:** “Now the team gives the idea a name, a
tagline, and a place in the market.”

**NOT YET VERIFIED — needs follow-up run:** no live positioning-output surface was
reached; do not invent its task or output selectors.

---

## Beat 6 — Generate launch messaging

**video-chapter**

```bash
playwright-cli video-chapter "Write the launch copy" --description="Write the launch copy from that position." --duration=10000
# NOT YET VERIFIED — needs follow-up run.
```

**DRAFT VO — only record once verified:** “Marketing writes the announcement and the
copy that goes with the launch.”

**NOT YET VERIFIED — needs follow-up run:** no marketing run or output surface was
verified.

---

## Beat 7 — Preview the proposed backlog

**video-chapter**

```bash
playwright-cli video-chapter "Break work into tasks" --description="Preview the backlog before any task cards get created." --duration=12000
playwright-cli click "getByRole('button', { name: 'Break into tasks', exact: true })"
playwright-cli snapshot
```

Narration: “With the plan confirmed, I preview the backlog before creating any
cards.”

Pacing: hold on the dialog titled “Preview proposed backlog items” and read one
proposed title and its state.

Transition to Beat 7a: this is a result boundary, not a wait screen. If the preview
is slow, speed-ramp to it or resume from a pre-warmed confirmed plan. Do not substitute
an empty Board for the missing import result.

---

## Beat 7a — Create tasks and show the board

**video-chapter**

```bash
playwright-cli video-chapter "Show the task board" --description="Create the backlog and look at the cards it makes." --duration=12000
playwright-cli click "getByRole('button', { name: 'Create tasks', exact: true })"
playwright-cli click "getByRole('link', { name: 'Board', exact: true })"
playwright-cli hover "getByRole('region', { name: 'Backlog column' })"
playwright-cli hover "getByRole('region', { name: 'Ready column' })"
playwright-cli snapshot
```

**DRAFT VO — only record once verified:** “Accept the backlog and the cards land on
the board, starting with the smallest slice we can ship.”

Pacing: do not linger or move a card until a real imported card is visible.

**NOT YET VERIFIED — needs follow-up run:** Create tasks was observed in the preview,
but the completed Board check still showed zero Backlog and Ready cards. Do not claim
the card-import result until it is visible.

---

## Beat 8 — Implement the first task

**video-chapter**

```bash
playwright-cli video-chapter "Implement the first slice" --description="Watch engineering write the code and the tests." --duration=16000
# NOT YET VERIFIED — needs follow-up run.
```

Narration: “The first task goes to engineering, which designs it, builds it, and backs
it with tests.”

**NOT YET VERIFIED — needs follow-up run:** no task-card-to-engineering execution
path was validated.

---

## Beat 9 — Review and merge the feature

**video-chapter**

```bash
playwright-cli video-chapter "Review and merge the first slice" --description="A person reviews the work before it merges." --duration=14000
# Only when a live review gate is present:
playwright-cli hover "getByRole('button', { name: 'Approve & merge', exact: true })"
# [PAUSE 700ms]
playwright-cli click "getByRole('button', { name: 'Approve & merge', exact: true })"
playwright-cli snapshot
```

Narration: “Nothing merges on its own. Someone reviews the result and makes the call
to merge.”

Human-review automation: **Approve & merge** is verified on a live review gate; use
the pause so the automated click still reads as intentional.

**NOT YET VERIFIED — needs follow-up run:** feature-slice review and merge were not
completed in the primary PM scenario.

---

## Beat 9a — Inspect shipped files and workspace

**video-chapter**

```bash
playwright-cli video-chapter "Inspect the shipped files" --description="Go from the merged PR diff back to the workspace." --duration=12000
playwright-cli click "getByRole('link', { name: 'Workspace' })"
playwright-cli click "getByRole('combobox', { name: 'Branch or worktree' })"
playwright-cli snapshot
```

Narration: “After the merge, I check exactly what changed, then come back to the
workspace on the merged branch.”

**NOT YET VERIFIED — needs follow-up run:** Workspace and its branch/worktree picker
are verified, but a generated merged PR and its Files changed tab are not. Capture the
PR-diff selector from the real PR rather than inventing one.

External-surface requirement: the merged PR diff is expected on **github.com**, in a
deliberately opened second browser tab. Capture its selectors separately; do not imply
that Agentweaver’s Workspace is the PR-diff page, and re-enable cursor/action callouts
for that tab if the recorder does not carry them across.

---

## Beat 9b — Review project health and observability

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

## Beat 9c — Return to captured team memory

**video-chapter**

```bash
playwright-cli video-chapter "Return to team memory" --description="Show the decision the run wrote down." --duration=8000
playwright-cli click "getByRole('link', { name: 'Memories', exact: true })"
playwright-cli click "getByRole('tab', { name: 'Decisions', exact: true })"
playwright-cli snapshot
```

**DRAFT VO — only record once verified:** “That decision is now saved for the next
piece of work.”

Reason for placement: this pays off Beat 2a after a workflow has had an opportunity to
write a decision, instead of presenting the currently empty memory page as evidence.

**NOT YET VERIFIED — needs follow-up run:** no decision card was present in this
project; cut this chapter until one is visible.

---

## Beat 10 — Pivot to the seeded bug

**video-chapter**

```bash
playwright-cli video-chapter "Pivot to the seeded bug" --description="Start the repair from the existing GitHub issue." --duration=8000
# NOT YET VERIFIED — needs follow-up run.
```

**DRAFT VO — only record once verified:** “With the feature shipped, I turn to a filed
bug: the welcome banner overlaps on a narrow tablet.”

**NOT YET VERIFIED — needs follow-up run:** no Agentweaver issue-list or linked-issue
surface was validated. Keep the GitHub issue as pre-recording setup.

---

## Beat 10a — Ask the browser assistant to triage the bug

**video-chapter**

```bash
playwright-cli video-chapter "Ask the assistant to triage" --description="Have the assistant read the issue and kick off a Bug Fix workflow." --duration=14000
playwright-cli click "getByRole('button', { name: 'New session', exact: true })"
playwright-cli click "getByRole('textbox', { name: 'Message the assistant...' })"
playwright-cli type "Triage https://github.com/sabbour/agentweaver-demo-dryrun/issues/1. Investigate the narrow-tablet welcome-banner overlap, propose a minimal fix and test plan, then use the Bug Fix workflow."
playwright-cli snapshot
playwright-cli click "getByRole('button', { name: 'Send', exact: true })"
```

Narration: “The assistant reads the issue, proposes the smallest safe fix and a test
plan, and starts a Bug Fix workflow. Anything that changes state still waits for my
approval.”

Pacing: pause after typing, then allow the first streamed reply to appear.

Transition to Beat 11: assistant-created orchestration may sit Pending for about two
minutes, and a full PM-style workflow can take 16 or more minutes. Use a speed-ramp or
resume a pre-warmed bug run at its first real output; never record the idle wait.

**NOT YET VERIFIED — needs follow-up run:** the console, textbox, and Send action were
verified, but this issue-specific prompt was not sent and its output was not recorded.

---

## Beat 11 — Scope the bug

**video-chapter**

```bash
playwright-cli video-chapter "Read and scope the bug" --description="Show the diagnosis, the expected behavior, and the smallest safe fix." --duration=10000
# NOT YET VERIFIED — needs follow-up run.
```

Narration: “Before touching code, the workflow spells out what’s broken, what should
happen, and how small the fix can stay.”

**NOT YET VERIFIED — needs follow-up run:** capture real bug-output selectors from
the assistant-created run.

---

## Beat 12 — Implement and test the repair

**video-chapter**

```bash
playwright-cli video-chapter "Implement and test the repair" --description="Show the fix and the tests that prove it." --duration=14000
# NOT YET VERIFIED — needs follow-up run.
```

Narration: “Engineering finds the cause, fixes it, and proves it with tests.”

**NOT YET VERIFIED — needs follow-up run:** no issue-specific implementation run was
validated.

---

## Beat 13 — Preview the repaired behavior

**video-chapter**

```bash
playwright-cli video-chapter "Preview the repaired behavior" --description="Show the narrow-tablet layout working before merge." --duration=10000
# NOT YET VERIFIED — needs follow-up run.
```

Narration: “We preview the fix the same way we previewed the feature.”

**NOT YET VERIFIED — needs follow-up run:** no bug-preview surface was reached.

---

## Beat 14 — Approve the bug-fix review

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

Human-review automation: use the verified **Approve & merge** locator only for the
review gate belonging to the bug-fix run.

**NOT YET VERIFIED — needs follow-up run:** no bug-fix review gate was exercised.

---

## Beat 15 — Close on the issue-linked PR

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
