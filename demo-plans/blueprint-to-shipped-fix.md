# Blueprint to shipped fix

## Status: planning only

This document is a **PLAN**, not a recording script already validated against the live app. Run a dry-run pass against **staging** first, replace every `[VERIFY SELECTOR]` placeholder with the real ref/test id/locator discovered from live `snapshot` output, and confirm the seeded empty repo + seeded bug issue are present before recording.

## Scenario narrative

Create a brand-new Agentweaver project from an empty GitHub repository, cast a full cross-functional Blueprint team, and take one small product idea from framing through customer research, naming, positioning, marketing, backlog breakdown, implementation, preview, review, and merge. Then pivot to a pre-seeded bug issue on that same repository, triage it through the bug workflow, implement and test the fix, preview the repaired behavior, and end by opening the fix PR linked back to the original issue.

## Recording assumptions

- Use a staging build URL such as `<STAGING_BASE_URL>`.
- Sign in ahead of time with a user that can create projects, approve reviews, and merge PRs.
- Prepare:
  - `<EMPTY_REPO_URL>` — brand-new GitHub repo with no app code yet.
  - `<IDEA_PROMPT>` — one small feature idea suitable for a first slice.
  - `<SEEDED_BUG_ISSUE_URL>` / `<SEEDED_BUG_ISSUE_TITLE>` — bug issue already present on the repo.
- Record at 1920x1080.
- Keep **video action callouts enabled** during mouse/typing moments; hide them only during static reading or live human gates.
- Insert short manual pauses in the recording runner between commands where noted so cursor travel and typing are visible on camera.

## Global recording preflight

```bash
playwright-cli open --browser=chrome
playwright-cli resize 1920 1080
playwright-cli goto <STAGING_BASE_URL>
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
playwright-cli type "<EMPTY_REPO_URL>"
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

## Beat 2 — Cast the PM + Marketing + Research + Engineering Blueprint team

**video-chapter**

```bash
playwright-cli video-chapter "Cast a cross-functional Blueprint team" --description="Choose the Blueprint-based team setup and show the cast before confirming." --duration=18000
playwright-cli snapshot
playwright-cli hover <REF_BLUEPRINTS_TAB_OR_CARD [VERIFY SELECTOR]>
playwright-cli click <REF_BLUEPRINTS_TAB_OR_CARD [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli mousemove <X_BLUEPRINT_CARD [VERIFY SELECTOR]>
playwright-cli hover <REF_PM_MARKETING_RESEARCH_ENGINEERING_BLUEPRINT [VERIFY SELECTOR]>
playwright-cli click <REF_PM_MARKETING_RESEARCH_ENGINEERING_BLUEPRINT [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_CAST_TEAM_BUTTON [VERIFY SELECTOR]>
playwright-cli click <REF_CAST_TEAM_BUTTON [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_SKILL_CATALOG_PANEL [VERIFY SELECTOR]>
playwright-cli mousemove <X_SKILL_CATALOG_PANEL [VERIFY SELECTOR]>
playwright-cli snapshot
playwright-cli hover <REF_HUMAN_CONFIRM_CAST_BUTTON [VERIFY SELECTOR]>
playwright-cli video-hide-actions
```

Narration: “Next, we instantiate a full Blueprint team: PM, customer research, marketing, and engineering. Before work begins, Agentweaver shows the proposed cast and each agent’s skill surface so a human can confirm the lineup.”

Recording notes:

- End automation at the confirmation edge.
- **LIVE HUMAN ACTION REQUIRED — do not automate this click:** confirm the cast after Ahmed verbally acknowledges the team composition.
- Once the human confirmation is complete, resume with `video-show-actions`.

Resume after live confirmation:

```bash
playwright-cli video-show-actions --duration=900 --position=top-right
playwright-cli snapshot
```

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
playwright-cli video-hide-actions
```

Narration: “When the first slice is ready, we open the live preview, inspect the result, and stop at the mandatory human gate before anything merges.”

Recording notes:

- **LIVE HUMAN ACTION REQUIRED — do not automate this click:** Ahmed should validate the preview and approve the run live.
- **LIVE HUMAN ACTION REQUIRED — do not automate this click:** Ahmed should trigger the merge/approve action live.
- After merge completes, show the resulting PR or merge confirmation.

Resume after live approval/merge:

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
playwright-cli video-chapter "Pause at the human review gate" --description="Stop for live validation of the bug fix before merge." --duration=12000
playwright-cli snapshot
playwright-cli video-hide-actions
```

Narration: “Nothing ships automatically. The human reviewer inspects the repaired behavior and makes the final decision.”

Recording notes:

- **LIVE HUMAN ACTION REQUIRED — do not automate this click:** Ahmed validates the bug-fix preview.
- **LIVE HUMAN ACTION REQUIRED — do not automate this click:** Ahmed approves the run for merge.

Resume after live approval:

```bash
playwright-cli video-show-actions --duration=900 --position=top-right
playwright-cli snapshot
```

---

## Beat 15 — Merge and show the PR linked back to the issue

**video-chapter**

```bash
playwright-cli video-chapter "Merge and open the issue-linked PR" --description="Complete the bug-fix merge and show the PR connected back to the original source issue." --duration=18000
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
- Rehearse the three live human gates:
  - cast confirmation
  - first-slice review + merge
  - bug-fix review + merge
- Confirm the seeded repo and bug issue are still in the expected state right before capture.
