# Blueprint to shipped fix

## Dry-run findings (Beats 1-9, 2026-07-22)

- **Authentication:** passed. A human completed GitHub OAuth in a headed Edge session; the refreshed authenticated state was saved to `C:\Users\asabbour\.copilot\session-state\15b0c7c4-9d50-4b25-a48b-e530292d7f98\files\staging-auth.json`.
- **Scenario setup:** the dry-run repo is `https://github.com/sabbour/agentweaver-demo-dryrun`; its seeded bug is [issue #1](https://github.com/sabbour/agentweaver-demo-dryrun/issues/1).

### Beat results

1. **PASS — pick repo and create project.** From the authenticated `Projects` page, use `getByRole('button', { name: 'Create from GitHub' })`, `getByRole('textbox', { name: 'Or paste any repository' })`, `getByRole('button', { name: 'Go →' })`, `getByRole('textbox', { name: 'Project name' })`, and `getByRole('button', { name: 'Create' })`. This created **blueprint-demo** at `https://agentweaver.6a5efff1a270d8000126291b.westus2.staging.aksapp.io/projects/8b4ba3ca-b7b0-4e40-b8d3-3d64d3502610`.
2. **BLOCKED — the planned cast-confirmation gate is absent.** `getByRole('button', { name: 'Templates' })` exposes `getByRole('radio', { name: 'Product & Software Delivery' })`; its live preview contains Lead PM, Customer Researcher, Product Marketing Manager, and engineering roles. Selecting it and creating the project immediately cast 12 project agents (plus system agents); no human confirmation dialog/button appeared. The resulting team is verifiable through the `Agents` project-nav link and includes the required roles, but the recording cannot show the described approval gate.
3. **PARTIAL — PM workflow is executing and its graph is verified.** The actual start surface is `getByTestId('start-task-topbar-action')`, with `getByLabel('Workflow', { exact: true })` value `pm-discovery`, `getByRole('textbox', { name: 'Goal' })`, and `getByRole('button', { name: 'Direct' })`. It created [orchestration b3bda0e2](https://agentweaver.6a5efff1a270d8000126291b.westus2.staging.aksapp.io/projects/8b4ba3ca-b7b0-4e40-b8d3-3d64d3502610/orchestrations/b3bda0e2-2a6b-4e29-9a88-0566178f681e). `getByTestId('open-topology-minimap')` opens the **Topology** dialog with graph controls (`getByRole('button', { name: 'Fit to view' })`, `getByRole('button', { name: 'Tidy' })`) and the graph region. At 6m50s the graph showed Work plan, Review Gate, and Merge completed; research and PM synthesis ready for assembly; Coordinator and Scribe running. At 8m37s, it remained in Scribing with 7 tasks, 0 pending, 0 waiting and 26.38 AIC consumed. The initial `Coordinator: Pending`/`0.0000 AIC` snapshot was the normal early startup state, not a credit failure.
4. **PARTIAL — customer research is a generated PM-workflow subtask.** The graph exposes `getByRole('treeitem', { name: /Research the problem space and target user/ })`; the task reached **Ready for assembly**. Its output has not yet been surfaced as a standalone results panel, so no output selector is claimed.
5. **PASS — notifications affordance verified.** `getByTestId('notification-bell')` opens the **Notifications** panel (`getByRole('button', { name: 'Notifications' })`). This run displayed the empty-state text “Nothing needs your attention right now” and the `getByRole('switch', { name: 'Sound on' })` control.
5. **BLOCKED — depends on the unavailable research output.**
6. **BLOCKED — depends on the unavailable positioning output.**
7. **BLOCKED — the planned ranked backlog cannot be produced while the orchestration is pending.** The observed run surface does expose `getByRole('button', { name: 'Break into tasks' })`, but invoking it would not satisfy the planned output without a completed PM run.
8. **BLOCKED — depends on a generated backlog task and executable workflow.**
9. **BLOCKED — depends on completed implementation and a reachable human-review/preview state.**

### Recording-plan corrections required

- Replace the landing-page/create-project placeholders with the Beat 1 locators above.
- Use the **Product & Software Delivery** template; it is the verified template containing PM, research, marketing, and engineering roles.
- Remove or rewrite the Beat 2 human cast-confirmation scene: staging casts the selected template during project creation without that gate.
- Keep one authenticated browser session open for the full recording dry run; save storage state only as a backup checkpoint, not as continuity between beats.
- Allow the PM orchestration to finish before rerunning the later backlog, implementation, preview, and merge beats; do not claim output, backlog, preview, approval, merge, PR, or selector validation beyond the observed surfaces above.

## Status: planning only

This document is a **PLAN**, not a recording script already validated against the live app. Run a dry-run pass against **staging** first, replace every `[VERIFY SELECTOR]` placeholder with the real ref/test id/locator discovered from live `snapshot` output, and confirm the seeded empty repo + seeded bug issue are present before recording.

## Scenario narrative

Create a brand-new Agentweaver project from an empty GitHub repository, cast a full cross-functional Blueprint team, and take one small product idea from framing through customer research, naming, positioning, marketing, backlog breakdown, implementation, preview, review, and merge. Then pivot to a pre-seeded bug issue on that same repository, triage it through the bug workflow, implement and test the fix, preview the repaired behavior, and end by opening the fix PR linked back to the original issue.

## Recording assumptions

- Use staging build URL `https://agentweaver.6a5efff1a270d8000126291b.westus2.staging.aksapp.io`.
- Sign in ahead of time with a demo account that can create projects, approve reviews, and merge PRs.
- Prepare:
  - `https://github.com/sabbour/agentweaver-demo-dryrun` — brand-new GitHub repo with no app code yet.
  - `<IDEA_PROMPT>` — one small feature idea suitable for a first slice.
  - `https://github.com/sabbour/agentweaver-demo-dryrun/issues/1` / `Bug: welcome banner overlaps primary action on narrow tablet width` — bug issue already present on the repo.
- Record at 1920x1080.
- Keep **video action callouts enabled** during mouse/typing moments; hide them only during static reading or when you want the review-gate framing to breathe on screen.
- Insert short manual pauses in the recording runner between commands where noted so cursor travel and typing are visible on camera.

## Global recording preflight

```bash
playwright-cli open --browser=chrome
playwright-cli resize 1920 1080
playwright-cli goto https://agentweaver.6a5efff1a270d8000126291b.westus2.staging.aksapp.io
playwright-cli snapshot
playwright-cli video-start blueprint-to-shipped-fix.webm
playwright-cli video-show-actions --duration=900 --position=top-right
```

Narration: “We’re starting from a clean Agentweaver staging environment and recording the full journey from blank repo to shipped feature to shipped bug fix.”

Recording notes:

- Pause ~1s after `goto` so the landing page settles before the first cursor movement.
- If login is required, handle it in a separate prep take rather than inside the polished recording.

---

## Beat 1 — Pick the empty repo and create the project

**video-chapter**

```bash
playwright-cli video-chapter "Create project from empty repo" --description="Connect a brand-new GitHub repository and create the Agentweaver project shell." --duration=12000
playwright-cli snapshot
# Slow cursor sweep across the primary CTA before clicking.
# [PAUSE 800ms]
playwright-cli mousemove <X_LANDING_PRIMARY_CTA [VERIFY SELECTOR]>
playwright-cli click <REF_CREATE_PROJECT_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_REPO_URL_INPUT [VERIFY SELECTOR]>
# [PAUSE 500ms]
playwright-cli click <REF_REPO_URL_INPUT [VERIFY SELECTOR]>
playwright-cli type "https://github.com/sabbour/agentweaver-demo-dryrun"
playwright-cli snapshot
playwright-cli hover <REF_PROJECT_NAME_INPUT [VERIFY SELECTOR]>
playwright-cli click <REF_PROJECT_NAME_INPUT [VERIFY SELECTOR]>
playwright-cli type "blueprint-demo"
playwright-cli snapshot
playwright-cli hover <REF_CONFIRM_CREATE_PROJECT [VERIFY SELECTOR]>
# [PAUSE 700ms]
playwright-cli click <REF_CONFIRM_CREATE_PROJECT [VERIFY SELECTOR]>
playwright-cli snapshot
```

Narration: “First, we point Agentweaver at a completely empty GitHub repo and create a fresh project around it.”

Recording notes:

- Keep `video-show-actions` on so the URL typing is visible.
- During the dry run, confirm whether project creation lives on the landing page, projects screen, or a modal; replace placeholders accordingly.

---

## Beat 2 — Select the PM + Marketing + Research + Engineering Blueprint team

**video-chapter**

```bash
playwright-cli video-chapter "Select a cross-functional Blueprint team" --description="Choose the Product & Software Delivery template and show the resulting cast." --duration=18000
playwright-cli snapshot
playwright-cli click "getByRole('button', { name: 'Templates' })"
playwright-cli snapshot
playwright-cli click "getByRole('radio', { name: 'Product & Software Delivery' })"
playwright-cli snapshot
playwright-cli click "getByRole('button', { name: 'Create' })"
playwright-cli snapshot
```

Narration: “Next, we select the Product & Software Delivery template: it includes PM, customer research, marketing, and engineering. Creating the project casts this template immediately.”

Recording notes:

- Staging does not expose a separate human cast-confirmation gate; do not narrate or record one.

---

## Beat 3 — Frame the idea with the PM agent

**video-chapter**

```bash
playwright-cli video-chapter "Frame one small feature" --description="Use the PM agent to define problem, target user, and success criteria for a tightly scoped first slice." --duration=20000
playwright-cli snapshot
playwright-cli hover <REF_NEW_TASK_OR_PROMPT_BUTTON [VERIFY SELECTOR]>
playwright-cli click <REF_NEW_TASK_OR_PROMPT_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_WORKFLOW_OR_ROLE_PICKER [VERIFY SELECTOR]>
playwright-cli click <REF_WORKFLOW_OR_ROLE_PICKER [VERIFY SELECTOR]>
playwright-cli click <REF_PM_DISCOVERY_OPTION [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli click <REF_TASK_PROMPT_EDITOR [VERIFY SELECTOR]>
# [PAUSE 600ms]
playwright-cli type "Frame a tiny first feature for this empty repo. Define the problem, target user, success criteria, and keep scope to one very small MVP slice only."
playwright-cli snapshot
playwright-cli hover <REF_SUBMIT_TASK_BUTTON [VERIFY SELECTOR]>
playwright-cli click <REF_SUBMIT_TASK_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_PM_RUN_CARD [VERIFY SELECTOR]>
```

Narration: “We start with product framing. The PM agent keeps us disciplined: one user problem, one small MVP slice, and clear success criteria.”

Recording notes:

- Favor `type` instead of `fill` so the prompt appears naturally in the video.
- If the product framing appears in chat rather than cards, swap the target refs during the dry run.

---

## Beat 3a — Check notifications while work runs

**video-chapter**

```bash
playwright-cli video-chapter "Check live notifications" --description="Show the notification feed while the PM workflow progresses asynchronously." --duration=8000
playwright-cli snapshot
playwright-cli click "getByTestId('notification-bell')"
playwright-cli snapshot
playwright-cli hover "getByRole('switch', { name: 'Sound on' })"
playwright-cli click "getByTestId('notification-bell')"
```

Narration: “While the PM work continues in the background, the notification feed keeps attention items visible without leaving the run.”

Recording notes:

- The verified empty state is “Nothing needs your attention right now”; use a real notification only if one arrives during the recorded run.

---

## Beat 4 — Run a quick customer research pass

**video-chapter**

```bash
playwright-cli video-chapter "Run fast customer research" --description="Collect a few demand signals and comparables before committing to the feature." --duration=16000
playwright-cli snapshot
playwright-cli hover <REF_FOLLOW_UP_OR_NEW_TASK_BUTTON [VERIFY SELECTOR]>
playwright-cli click <REF_FOLLOW_UP_OR_NEW_TASK_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli click <REF_ROLE_PICKER [VERIFY SELECTOR]>
playwright-cli click <REF_CUSTOMER_RESEARCH_OPTION [VERIFY SELECTOR]>
playwright-cli click <REF_TASK_PROMPT_EDITOR [VERIFY SELECTOR]>
# [PAUSE 600ms]
playwright-cli type "Do a fast customer research pass for this feature. Bring back a few demand signals, adjacent comparables, and what would make this idea obviously useful."
playwright-cli snapshot
playwright-cli click <REF_SUBMIT_TASK_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_RESEARCH_RESULTS_PANEL [VERIFY SELECTOR]>
```

Narration: “Before naming or building anything, we ask customer research for quick demand signals and comparable products.”

Recording notes:

- Allow a brief visual linger on the returned findings so the audience can see evidence, not just motion.

---

## Beat 5 — Name and position the product

**video-chapter**

```bash
playwright-cli video-chapter "Name and position the idea" --description="Turn the validated idea into a name, tagline, and one-line positioning statement." --duration=16000
playwright-cli snapshot
playwright-cli click <REF_FOLLOW_UP_OR_NEW_TASK_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli click <REF_PM_OR_POSITIONING_ROLE_OPTION [VERIFY SELECTOR]>
playwright-cli click <REF_TASK_PROMPT_EDITOR [VERIFY SELECTOR]>
# [PAUSE 600ms]
playwright-cli type "Name this feature or product, give it a short tagline, and write a one-line positioning statement grounded in the research we just reviewed."
playwright-cli snapshot
playwright-cli click <REF_SUBMIT_TASK_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_NAMING_OUTPUT_SECTION [VERIFY SELECTOR]>
```

Narration: “With the user problem clearer, the PM can tighten the message into a name, a tagline, and a one-line position in the market.”

Recording notes:

- Leave action callouts on.
- During the dry run, identify whether positioning is a new run, a follow-up message, or a board task.

---

## Beat 6 — Run the marketing pass

**video-chapter**

```bash
playwright-cli video-chapter "Generate launch messaging" --description="Ask marketing for launch copy and a blog-post announcement for the same feature." --duration=18000
playwright-cli snapshot
playwright-cli click <REF_FOLLOW_UP_OR_NEW_TASK_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli click <REF_MARKETING_ROLE_OPTION [VERIFY SELECTOR]>
playwright-cli click <REF_TASK_PROMPT_EDITOR [VERIFY SELECTOR]>
# [PAUSE 600ms]
playwright-cli type "Create short launch copy for this feature and draft a concise blog post announcing it, aligned to the positioning we just chose."
playwright-cli snapshot
playwright-cli click <REF_SUBMIT_TASK_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_MARKETING_OUTPUT_SECTION [VERIFY SELECTOR]>
playwright-cli mousemove <X_MARKETING_OUTPUT_SECTION [VERIFY SELECTOR]>
```

Narration: “Now marketing turns the concept into something launchable: short copy for the release moment and a blog-post draft for the story.”

Recording notes:

- Pause ~1s on the marketing output so viewers can see that the work product exists.

---

## Beat 7 — Break the idea into a tiny ranked backlog

**video-chapter**

```bash
playwright-cli video-chapter "Break work into 2 to 3 tasks" --description="Create a ranked backlog with only the smallest shippable first slice." --duration=18000
playwright-cli snapshot
playwright-cli hover <REF_CREATE_BACKLOG_TASK_BUTTON [VERIFY SELECTOR]>
playwright-cli click <REF_CREATE_BACKLOG_TASK_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli click <REF_TASK_PROMPT_EDITOR [VERIFY SELECTOR]>
# [PAUSE 600ms]
playwright-cli type "Break this project into a ranked backlog of at most three tasks. Keep task one as the smallest shippable slice and make the acceptance criteria explicit."
playwright-cli snapshot
playwright-cli click <REF_SUBMIT_TASK_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_BACKLOG_COLUMN [VERIFY SELECTOR]>
playwright-cli mousemove <X_BACKLOG_TOP_TASK [VERIFY SELECTOR]>
playwright-cli snapshot
```

Narration: “Instead of generating a giant plan, we ask for a tiny ranked backlog: two or three tasks, with task one small enough to ship quickly.”

Recording notes:

- If the board requires drag-and-drop from Backlog to Ready, capture that in the next beat rather than here.

---

## Beat 8 — Implement the first task with contract, build, and tests

**video-chapter**

```bash
playwright-cli video-chapter "Implement the first slice" --description="Move the top task into execution and show the engineering workflow producing code and tests." --duration=24000
playwright-cli snapshot
playwright-cli hover <REF_TOP_BACKLOG_TASK [VERIFY SELECTOR]>
playwright-cli mousemove <X_TOP_BACKLOG_TASK [VERIFY SELECTOR]>
playwright-cli drag <REF_TOP_BACKLOG_TASK [VERIFY SELECTOR]> <REF_READY_COLUMN_OR_START_ZONE [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_START_RUN_BUTTON [VERIFY SELECTOR]>
playwright-cli click <REF_START_RUN_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_ENGINEERING_RUN_CARD [VERIFY SELECTOR]>
playwright-cli mousemove <X_ENGINEERING_RUN_CARD [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli click <REF_ENGINEERING_RUN_CARD [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_CONTRACT_BUILD_TEST_TIMELINE [VERIFY SELECTOR]>
```

Narration: “Once the first task is ready, Agentweaver routes it into the engineering workflow. The audience should see that the run is not just code generation — it includes design contract thinking, implementation, and tests.”

Recording notes:

- Prefer a visible drag if the UI supports it; otherwise replace with the exact ready/start clicks found in staging.
- Linger on the run timeline, status graph, or file/test evidence returned by the run.

---

## Beat 9 — Preview, human validate, and merge the first slice

**video-chapter**

```bash
playwright-cli video-chapter "Preview and ship the first slice" --description="Open the generated preview, pause for human approval, then merge so the PR opens against the new repository." --duration=24000
playwright-cli snapshot
playwright-cli hover <REF_HUMAN_REVIEW_COLUMN_OR_RUN [VERIFY SELECTOR]>
playwright-cli click <REF_HUMAN_REVIEW_COLUMN_OR_RUN [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_PREVIEW_BUTTON [VERIFY SELECTOR]>
playwright-cli click <REF_PREVIEW_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli mousemove <X_PREVIEW_SURFACE [VERIFY SELECTOR]>
playwright-cli hover <REF_PREVIEW_SURFACE [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_APPROVE_OR_MERGE_BUTTON [VERIFY SELECTOR]>
# [PAUSE 700ms]
playwright-cli mousemove <X_APPROVE_OR_MERGE_BUTTON [VERIFY SELECTOR]>
playwright-cli click <REF_APPROVE_OR_MERGE_BUTTON [VERIFY SELECTOR]>
playwright-cli video-hide-actions
```

Narration: “When the first slice is ready, we open the live preview and arrive at the human review gate — for this recording we’re clicking approve ourselves, but in real use a person makes this merge decision.”

Recording notes:

- Keep the pre-click pause and slow mousemove so the approval feels intentional on camera.
- After merge completes, show the resulting PR or merge confirmation.

Resume after approval/merge:

```bash
playwright-cli video-show-actions --duration=900 --position=top-right
playwright-cli snapshot
playwright-cli hover <REF_PR_LINK_OR_MERGE_CONFIRMATION [VERIFY SELECTOR]>
playwright-cli click <REF_PR_LINK_OR_MERGE_CONFIRMATION [VERIFY SELECTOR]>
playwright-cli snapshot
```

---

## Beat 10 — Seeded bug issue already exists; create a triage task

**video-chapter**

```bash
playwright-cli video-chapter "Pivot from shipped feature to bug triage" --description="Show the already-open GitHub issue and create a task that kicks off the bug workflow." --duration=18000
playwright-cli snapshot
playwright-cli hover <REF_REPO_OR_ISSUES_TAB [VERIFY SELECTOR]>
playwright-cli click <REF_REPO_OR_ISSUES_TAB [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_SEEDED_BUG_ISSUE_ROW [VERIFY SELECTOR]>
playwright-cli click <REF_SEEDED_BUG_ISSUE_ROW [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_CREATE_TASK_FROM_ISSUE_BUTTON [VERIFY SELECTOR]>
playwright-cli click <REF_CREATE_TASK_FROM_ISSUE_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_BUG_WORKFLOW_OPTION [VERIFY SELECTOR]>
playwright-cli click <REF_BUG_WORKFLOW_OPTION [VERIFY SELECTOR]>
playwright-cli snapshot
```

Narration: “The repo already contains a seeded bug issue. From the issue itself, we create a new task and let Agentweaver kick off the bug workflow.”

Recording notes:

- If issue creation is outside Agentweaver in GitHub proper, treat that as pre-seeded setup and only show the linked issue inside Agentweaver.

---

## Beat 11 — Read and scope the bug

**video-chapter**

```bash
playwright-cli video-chapter "Read and scope the bug" --description="Open the bug task and show the triage prompt or scoping summary." --duration=16000
playwright-cli snapshot
playwright-cli hover <REF_BUG_TASK_CARD [VERIFY SELECTOR]>
playwright-cli click <REF_BUG_TASK_CARD [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_BUG_DESCRIPTION_PANEL [VERIFY SELECTOR]>
playwright-cli mousemove <X_BUG_DESCRIPTION_PANEL [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli click <REF_BEGIN_TRIAGE_OR_SUBMIT_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_TRIAGE_SUMMARY_PANEL [VERIFY SELECTOR]>
```

Narration: “Before changing code, the bug workflow scopes the issue: what’s broken, what’s expected, and what we believe the smallest safe fix should be.”

Recording notes:

- If a human needs to tweak the bug prompt before start, show that typing with `type` in the same pattern as earlier beats.

---

## Beat 12 — Implement and test the bug fix

**video-chapter**

```bash
playwright-cli video-chapter "Implement and test the fix" --description="Let engineering run the bug-fix workflow and surface the repair plus test evidence." --duration=22000
playwright-cli snapshot
playwright-cli hover <REF_START_BUG_RUN_BUTTON [VERIFY SELECTOR]>
playwright-cli click <REF_START_BUG_RUN_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_BUG_RUN_TIMELINE [VERIFY SELECTOR]>
playwright-cli mousemove <X_BUG_RUN_TIMELINE [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli click <REF_BUG_RUN_TIMELINE_OR_DETAILS [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_TEST_EVIDENCE_SECTION [VERIFY SELECTOR]>
```

Narration: “Agentweaver now runs the bug workflow end to end: diagnose, implement, and prove the fix with tests.”

Recording notes:

- Hold briefly on explicit test evidence or changed files.

---

## Beat 13 — Preview the fix

**video-chapter**

```bash
playwright-cli video-chapter "Preview the repaired behavior" --description="Open the preview for the bug-fix run and show that the broken flow now works." --duration=18000
playwright-cli snapshot
playwright-cli hover <REF_BUG_HUMAN_REVIEW_RUN [VERIFY SELECTOR]>
playwright-cli click <REF_BUG_HUMAN_REVIEW_RUN [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_PREVIEW_BUTTON [VERIFY SELECTOR]>
playwright-cli click <REF_PREVIEW_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli mousemove <X_REPAIRED_PREVIEW_SURFACE [VERIFY SELECTOR]>
playwright-cli hover <REF_REPAIRED_PREVIEW_SURFACE [VERIFY SELECTOR]>
playwright-cli snapshot
```

Narration: “Just like the feature slice, the bug fix gets a live preview before any merge is allowed.”

Recording notes:

- If the repaired behavior needs one or two visible interactions in the preview, add those during the dry run with real refs.

---

## Beat 14 — Human validates the fix

**video-chapter**

```bash
playwright-cli video-chapter "Pass through the human review gate" --description="Show the final bug-fix review decision and click through it for the recording." --duration=12000
playwright-cli snapshot
playwright-cli hover <REF_BUG_APPROVE_BUTTON [VERIFY SELECTOR]>
# [PAUSE 700ms]
playwright-cli mousemove <X_BUG_APPROVE_BUTTON [VERIFY SELECTOR]>
playwright-cli click <REF_BUG_APPROVE_BUTTON [VERIFY SELECTOR]>
playwright-cli video-hide-actions
```

Narration: “Nothing ships automatically. Here’s the human review gate — for this recording we’re clicking approve ourselves, but in real use a person decides whether the fix ships.”

Recording notes:

- Keep the approval click slow and obvious so viewers register the decision point.

Resume after approval:

```bash
playwright-cli video-show-actions --duration=900 --position=top-right
playwright-cli snapshot
```

---

## Beat 15 — Merge and show the PR linked back to the issue

**video-chapter**

```bash
playwright-cli video-chapter "Merge and open the issue-linked PR" --description="Complete the bug-fix merge and show the PR connected back to the original source issue." --duration=18000
playwright-cli hover <REF_FINAL_MERGE_BUTTON [VERIFY SELECTOR]>
# [PAUSE 700ms]
playwright-cli mousemove <X_FINAL_MERGE_BUTTON [VERIFY SELECTOR]>
playwright-cli click <REF_FINAL_MERGE_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_MERGE_CONFIRMATION_OR_PR_LINK [VERIFY SELECTOR]>
playwright-cli click <REF_MERGE_CONFIRMATION_OR_PR_LINK [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_LINKED_ISSUE_SECTION [VERIFY SELECTOR]>
playwright-cli mousemove <X_LINKED_ISSUE_SECTION [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli video-stop
playwright-cli close
```

Narration: “We finish by showing the bug-fix PR opened from the workflow and linked back to the original issue — a complete loop from idea to shipped feature to shipped repair.”

Recording notes:

- End on the PR + issue linkage or merged-state confirmation, whichever reads more clearly in staging.

## Dry-run checklist before recording

- Replace every `[VERIFY SELECTOR]` placeholder with a real ref/test id/locator from staging snapshots.
- Confirm chapter durations against actual load times; lengthen where live compute takes longer.
- Confirm whether any steps are chat-based, board-based, modal-based, or tab-based in the current UI.
- Rehearse the three recorded review-gate clicks:
  - cast confirmation
  - first-slice approval + merge
  - bug-fix approval + merge
- Confirm the seeded repo and bug issue are still in the expected state right before capture.
