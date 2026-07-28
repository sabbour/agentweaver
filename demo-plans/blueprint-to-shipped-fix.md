# Blueprint to shipped fix — recording script

## Recording status

Record against staging with a single authenticated browser session. The project is
**blueprint-demo** and the seeded bug is
<https://github.com/sabbour/agentweaver-demo-dryrun/issues/1>.

The PM discovery run `b3bda0e2-2a6b-4e29-9a88-0566178f681e` completed. A second
live run verified the Outcome-plan confirmation flow. Do not represent a later
unverified flow as completed: each such beat below is explicitly marked **NOT YET
VERIFIED — needs follow-up run**.

**2026-07-25 dry-run status:** live staging auth replay still works with the reviewed
harness/browser boundary at
`https://agentweaver.6a63b4fb256d5a00017339af.westus2.staging.aksapp.io`, and the staging
UI reports **Alpha v0.11.1**. Reusing the saved storage state plus companion
`sessionStorage` seed, `/projects` loads authenticated chrome with no sign-in buttons.
The transient `GET /runs failed (403)` org-membership window cleared: run pages now load
normally again. Two fresh resumed live runs then re-proved the active blocker is still
AgentHost readiness, not auth:

- `2b44685b-d169-4315-b021-fb2a40171812`: reached **awaiting confirmation** in about
  **11s**, Clarify round-trip back to **awaiting confirmation** in about **13s**,
  confirmation returned HTTP 200, and child run `81b78ae0-a006-4bb4-9663-087a51b99191`
  dispatched after about **2m 1s**. Child pod `agentweaver-agent-host-6786n` bound at
  `2026-07-25T08:34:57Z` but never became ready on `/healthz` within **90s**; the parent
  returned to **assembly_blocked** at `2026-07-25T08:36:40Z`.
- `fa5b4990-2abd-4538-b658-bacf389d5751`: reached **awaiting confirmation** in about
  **16s**, Clarify round-trip back to **awaiting confirmation** in about **13s**,
  confirmation returned HTTP 200, and child run `45b48b1e-c7f3-47e0-a970-bab9ef65b4fd`
  dispatched by `2026-07-25T08:46:55.968Z`. This child failed at
  `2026-07-25T08:48:21.0157730+00:00`; exact pod:
  `agentweaver-agent-host-fnq6t` (`http://10.244.3.77:8088/healthz` readiness timeout).

Because both fresh retries still died before any work artifacts appeared, actual
board-heartbeat and preview elapsed times remain **uncaptured in this pass**.

After the live `Sandbox__AgentHost__RequireMtls=true` env fix on api/worker, another fresh
run (`47ec68dc-4655-44d9-b9af-9ed04273b7a7`) still failed before producing artifacts. It
reached **awaiting confirmation** in about **30s**, the Clarify revise round-trip
returned to **awaiting confirmation** in about **15s**, and confirmation returned HTTP
200 at `2026-07-25T09:07:23.933Z`. Child run
`65ec67a8-e1c7-4311-bb00-d725725060e3` then failed at
`2026-07-25T09:10:49.3797010+00:00`; exact pod:
`agentweaver-agent-host-6s74j`. Crucially, the failure target is now
`https://10.244.3.167:8088/healthz` — proving the mTLS caller-side env fix is active —
but readiness still times out within **90s**. During the same run, `GET /api/runs/...`
also briefly returned the earlier org-membership 403 at
`2026-07-25T09:08:36.315Z`, `09:09:07.046Z`, `09:09:19.264Z`, and `09:09:31.200Z`
before succeeding again on adjacent polls.

After the later `72781ce` redeploy with the AgentHost mTLS client-handler wiring, a fresh
run (`8b047bef-f048-4618-ac84-4f711ae84316`) changed failure mode again: it reached
**awaiting confirmation** in about **13s**, the Clarify round-trip returned to
**awaiting confirmation** in about **13s**, and confirmation returned HTTP 200 at
`2026-07-25T09:58:52.619Z`. But this run never emitted `coordinator.work_plan` or
`subtask.dispatched`, so there is **no child run ID** to inspect. Instead, the event log
showed `tool.error` / `run.degraded` at `2026-07-25T09:59:36.1041910+00:00`,
`2026-07-25T09:59:36.1148590+00:00`, and `2026-07-25T09:59:47.9045510+00:00` with the
message: `Native Copilot shell is disabled; use the sandboxed run_command tool (routed
through the sandbox executor).` The run then stayed **in_progress** with
`coordinator_status=confirmed`, UI **Work plan Pending** / **Workflow Executing**, and no
board/preview/review artifacts. As of this pass, the active blocker is no longer auth or
the earlier child readiness timeout alone; it is now this work-planning stall before any
truthful downstream demo surfaces appear.

Follow-up retry `407ed7e3-ea88-4c2e-bf99-fcf075b5cb82` disproved that as a hard blocker.
Reusing the same staging auth, a fresh define-outcome run hit **awaiting confirmation** in
about **23.1s**, the Clarify revise round-trip returned in about **24.6s**, and confirm
returned at `2026-07-25T13:36:29.944+03:00`. Crucially, after waiting longer:

- `coordinator.work_plan` appeared at `2026-07-25T13:38:05.2677647+03:00`
  (**~95s after confirm**)
- first `subtask.dispatched` appeared at `2026-07-25T13:38:18.4993053+03:00`
  (**~109s after confirm**)
- child run `dc2c2970-74c1-4ba1-a5a3-ab25aa6bb1de` (Pris) dispatched successfully and
  later reached `assemble_ready`
- sibling child run `363aa24d-7abe-4e8c-ba22-c293802ce062` (Roy) also reached
  `assemble_ready`
- the parent later dispatched subtask 10 as child run
  `96c42417-c008-4edb-ba48-6756622e4cfd`

So the `Native Copilot shell is disabled` event should currently be treated as
**degraded/slow planning behavior, not a proven no-dispatch dead-end by itself**. The
remaining blocker is no longer “no work plan ever appears”; it is that the downstream
artifact-mapping pass still has not been completed from this now-progressing live run.

Continuing that same live run uncovered two new downstream blockers:

- review did surface truthfully, but approving the live human-review gate on parent run
  `407ed7e3-ea88-4c2e-bf99-fcf075b5cb82` ended in
  `merge_failed / assembly_failed` with result
  `assembly_merge_failed: the working tree cannot be safely reconciled with the merge result because uncommitted content diverges from the merge result and cannot be safely reconciled; commit, stash, or discard the local changes and retry`
- the execution child `8e748e2a-1968-4611-9037-afaa488d029f` successfully implemented the
  landing page and reached `assemble_ready`, but its preview never surfaced in the UI
  because repeated `start_preview(port)` calls failed with the generic error
  **Tool execution failed** even after `observe_bound_port` and `health_check` proved a
  healthy forwarded port (`3639 -> 3000`, HTTP 200)

So the current hard blockers for a truthful final recording are now:
**(1)** no live promoted backlog cards on the board to drag,
**(2)** no working preview surface despite a successful execution child,
and **(3)** merge currently failing after approval on the live orchestration.

Fresh-project recording attempt update (`blueprint-demo-final`, project
`01b1a87d-6d2c-497c-8030-d50888c5bfdb`): the actual screencast pass started and captured
segment files `seg1a-create-define.webm` and `seg1b-confirm-plan.webm`, but the fresh
parent run `4030fa60-3afd-45c7-beda-60112d81563e` then stalled again after confirmation.
At `2026-07-25T14:48:24+03:00`, `/api/runs/4030fa60-3afd-45c7-beda-60112d81563e`
still reported `status=in_progress`, `coordinator_status=confirmed`, `step_count=3`, and
`/api/runs/4030fa60-3afd-45c7-beda-60112d81563e/children` still returned `[]` — more
than 20 minutes after run start (`2026-07-25T11:26:08.649916+00:00`). No work-plan,
child-dispatch, board, preview, or review artifacts surfaced from this fresh project, so
the actual recording could not progress past the opening two segments.

Workflow-selection correction + fresh retry (`blueprint-demo-final`, recreated as project
`7d926daf-61f8-4770-ae1f-65cc0c67f095`): the earlier stall was indeed caused by the wrong
explicit workflow override. The replacement recording segment now selects
`software-delivery`, and the new parent run `d908837c-d8d8-493b-a0f8-05a2e9afb8d6`
confirmed the correct workflow selection:
`Selected 'Software Delivery' from an explicit workflow override.` However, this fresh
run uncovered a new blocker. After confirm, the persisted run state remained
`status=in_progress`, `coordinator_status=confirmed`, `step_count=2`, and both
`/api/runs/d908837c-d8d8-493b-a0f8-05a2e9afb8d6/work-plan` (404
`work_plan_not_found`) and `/api/runs/d908837c-d8d8-493b-a0f8-05a2e9afb8d6/children`
(`[]`) showed that no work plan was ever persisted or dispatched. Yet the event log did
contain the coordinator/model turn emitting a five-subtask JSON-style work-plan response
and ending cleanly at `2026-07-25T11:57:52.1014810+00:00`. As of
`2026-07-25T15:05:50+03:00`, no `coordinator.work_plan` or `subtask.dispatched` event
had followed, so the actual recording is blocked again — this time with the correct
workflow selected.

Prompt correction retry (`blueprint-demo-final`, same project `7d926daf-61f8-4770-ae1f-65cc0c67f095`):
the canonical seeded-issue prompt from Beat 4.2 was then submitted verbatim:
`Triage https://github.com/sabbour/agentweaver-demo-dryrun/issues/1. Investigate the narrow-tablet welcome-banner overlap, propose a minimal fix and test plan, then use the Bug Fix workflow.`
This produced fresh run `bca73169-7a85-4227-b188-08fdfbfefc52`, whose outcome is now
correctly code-oriented:
`Issue #1 is triaged with a documented reproduction and root cause for the narrow-tablet welcome-banner overlap, a minimal targeted fix and regression test plan are proposed, and the confirmed fix is implemented and validated through the Bug Fix workflow.`
After confirmation, the run also recorded the correct workflow-selection event:
`Selected 'Bug Fix' from an explicit workflow override.`
However, this retry hit a new blocker before any promotion-classifier / work-plan event:
after initial project inspection the run requested approval for
`web_fetch https://github.com/sabbour/contoso-air/issues/1` at
`2026-07-25T12:20:07.8783330+00:00` (note the unexpected `contoso-air` issue URL rather
than `agentweaver-demo-dryrun/issues/1`). Attempting to approve via the run API returned
`503 {"error":"AgentHost approval endpoint is unreachable.","state":"agenthost_unreachable"}`,
and the event log later recorded `URL fetch was denied by the operator.` With that fetch
denied, the run remained `status=in_progress`, `coordinator_status=confirmed`, with no
persisted `/work-plan` and no children as of `2026-07-25T15:25:38+03:00`.

Clean bug-fix retry (`caf60f12-09a2-49f2-b6f3-dc693bec57c2`) disproved that same
workspace-bleed path as the immediate blocker for the next attempt. This retry reused the
same exact seeded-issue prompt and the same `bug-fix` workflow override, but showed:
no `contoso-air` anywhere in events, no `/workspace` root-browse pattern, and no approval
requests at all. The outcome spec remained correctly scoped to
`agentweaver-demo-dryrun/issues/1`, and the post-confirm model turn emitted a plausible
three-subtask bug-fix JSON plan (`bug-1-root-cause-analysis`,
`minimal-fix-proposal`, `regression-test-plan`) before ending cleanly at
`2026-07-25T12:40:20.1196090+00:00`. However, after about **231s** of post-confirm
polling, persisted state was still stuck at `status=in_progress`,
`coordinator_status=confirmed`, `/work-plan` was still **404**, `/children` was still
`[]`, and no `coordinator.work_plan` / `subtask.dispatched` events had appeared. So the
demo is still blocked — now by a clean-scope work-plan persistence/dispatch failure rather
than by the earlier cross-workspace contamination.

Post-PR505 fresh retry (`blueprint-demo-fkfix`, project
`9db14262-4ff0-4d80-a7fd-b1260c357e15`, run
`9a04162a-2ea4-455d-b27b-2d5656f6b230`) did pick up the new staging image
**Alpha v0.11.0-dev+5d2b3b9**, but it could not even finish grounding the outcome spec:
the very first approval gate for
`web_fetch https://github.com/sabbour/agentweaver-demo-dryrun/issues/1` failed both from
the UI and via direct POST to `/api/runs/.../tool-approvals` with the same response:
`503 {"error":"AgentHost approval endpoint is unreachable.","state":"agenthost_unreachable"}`.
The UI now surfaces that inline as
`Approval failed: API error 503: {"error":"AgentHost approval endpoint is unreachable.","state":"agenthost_unreachable"}`.
Because that approval cannot be granted, this run never reaches Outcome-plan confirmation,
so PR #505's persistence fix could not yet be exercised end-to-end on a fresh bug-fix run.

After the single-api-replica workaround, the next fresh project/run finally proved the FK
ordering fix itself works: project `blueprint-demo-fkfix-2`
(`a42e1692-4136-4aab-97b3-d086e1bd3f38`) created parent run
`ebbd7a17-0f1b-42b1-93ac-1b2dd487c76a`, `/work-plan` returned **200** with
`workPlanId: 9`, and child run `398d212a-72a8-47f3-b509-40bee65a19ba` dispatched to
agent **Kirk**. But the flow is still not end-to-end green: that first child bound
pod `agentweaver-agent-host-76d7c` at `2026-07-25T13:32:26.5562289+00:00` and then
failed at `2026-07-25T13:33:57.5819470+00:00` with
`agenthost_launch_failed` because the pod never became ready at
`http://10.244.4.118:8088/healthz` within **90s**. The parent consequently moved to
`assembly_blocked`, the board shows the run under **Problems**, and no preview/review/
merge/recording path could yet be exercised from this run.

Latest live retry on staging build **Alpha v0.11.0-dev+f2e7983** clarified two more
critical truths:

- The earlier `delegated_to_backlog` branch was caused by confirming with
  **Allow standalone backlog tasks for independent deliverables** enabled. A board
  autopilot pickup of the promoted backlog task then exposed a new blocker: active run
  `ea20292c-e034-4f2d-94a8-2e53a0415eee` stayed in
  `coordinator_status=drafting` for **more than 6 minutes** with only three persisted
  events (`run.options_set`, `coordinator.started`,
  `coordinator.outcome_spec.drafting`), empty drafted fields, and no approval/work-plan
  surface. So the delegated-backlog continuation is currently **BLOCKED**.
- A fresh direct bug-fix run in the same project, with that checkbox left **off**, did
  progress again: run `c6a6eb31-00dc-4898-aac3-e41964cfe3da` reached
  **awaiting confirmation** about **12s** after start
  (`2026-07-25T15:07:41.099Z` → `15:07:53.669Z`), persisted
  `workPlanId: 12`, and dispatched child `7ab82034-20fd-4fb6-917f-f09ec32d473d` by
  `2026-07-25T15:10:55.547Z`. Child 15 reached `assemble_ready` at
  `15:12:50.564Z`; child 16 dispatched at `15:12:54.854Z`; all three children were
  `assemble_ready` by `15:16:15.030Z`; RAI passed green by `15:16:33.912Z`.
- That same run still did **not** reach a recordable preview/review/merge finish. Its
  confirmed work plan regressed to three planning/proposal/QA document subtasks rather
  than an implementation + preview path, and assembly then died at Build & Test:
  `coordinator.assembly_failed` at `2026-07-25T15:16:49.499Z` with
  `build_test_infra_agenthost_configure_failed` /
  `AgentHost /configure for run 'c6a6eb31-00dc-4898-aac3-e41964cfe3da' failed: HTTP 500`.
  Preview applicability had just reported `preview_required`, so no preview URL, no
  human review gate, and no merge surface appeared.

Fresh retry `d2ad3035-afef-4e74-93e9-a7c45bfb60ee` in the real `blueprint-demo` project
went farther but still failed before a recordable finish. It persisted `workPlanId: 15`
and drove all three subtasks to `assemble_ready` by
`2026-07-25T20:05:02.2016618+00:00` (`23`: `85b9527b-fb35-4aa2-a6ef-050d307dd42d`,
`24`: `2c947196-00d2-4a7b-b34a-563a6b173294`, `25`:
`fd964824-0baf-40fe-8299-57fcf2278efd`). Assembly then reached `children_complete`,
started at `2026-07-25T20:05:10.5720220+00:00`, and passed RAI green at
`2026-07-25T20:06:10.1479930+00:00`. The first preview-applicability pass unexpectedly
reported `preview_skipped_not_applicable` / `llm_docs_or_non_runtime` at
`2026-07-25T20:06:33.5459856+00:00`, then Build & Test completed and approved at
`2026-07-25T20:07:19.6814246+00:00`. Immediately afterward the coordinator restarted
assembly, emitted a human-review request internally, flipped preview applicability to
`preview_required` at `2026-07-25T20:07:40.5777836+00:00`, bound assembly pod
`agentweaver-agent-host-zwvh9`, and then failed at
`2026-07-25T20:07:56.2285446+00:00` with
`build_test_infra_agenthost_configure_failed` /
`AgentHost /configure for run 'd2ad3035-afef-4e74-93e9-a7c45bfb60ee' failed: HTTP 500`.
So the real seeded-repo path now reproducibly reaches full child completion plus RAI,
but is still **BLOCKED** at assembly Build & Test before any truthful preview/review/
merge recording can be captured.

Fresh seeded-project retry (`blueprint-demo-live-2325`, project
`b3608f95-e4f9-4fcf-93bb-5245e1f69ed9`) finally proved the cloned repo now contains the
real app inputs (`index.html`, `styles.css`, `package.json`, `build.js`, `test.js`), but
the end-to-end recording path is still blocked by two later-stage product/runtime bugs:

- Attempt 1 on this fresh project, run `b875731d-7c48-4619-bb3d-21edd71a06b1`, still hit
  the narrow assembly race at Build & Test:
  `build_test_infra_agenthost_configure_failed` / `AgentHost /configure ... HTTP 500`.
- Per Ahmed's instruction, this was retried once immediately. Attempt 2, run
  `f9e7867c-48f7-40af-8236-5cd0c9f9e53f`, progressed farther:
  - subtask 36 `assemble_ready`
  - subtask 37 stalled twice and was redispatched twice
    (`4ccc5790-eaff-4dd7-9030-7f6bdf0d8bc7` stalled at `2026-07-25T21:23:47Z`,
    `760ca8ba-6e40-4a92-aace-1076b3022ece` stalled at `2026-07-25T21:30:01Z`), then the
    third child `6f952975-f3b5-4259-b76b-ab172b004a55` finally reached `assemble_ready`
    at `2026-07-25T21:38:17.2057359+00:00`
  - subtask 38 later reached `assemble_ready` (via child
    `481c10c3-7354-49ba-85a1-4cae9aac6c9b`)
  - RAI passed green
  - Build & Test completed successfully
  - preview was **required** (`sandbox.preview_applicability state=preview_required
    reason=llm_preview_required`) but then failed with
    `sandbox.preview_failed` / `Could not determine how to run the app from the worktree
    (Phase-1 heuristics).`
  - after human review approval returned **200**, merge still failed on this truly fresh
    project with the same result as before:
    `assembly_merge_failed: the working tree cannot be safely reconciled with the merge result because uncommitted content diverges from the merge result and cannot be safely reconciled; commit, stash, or discard the local changes and retry`

That means the final recording is still **BLOCKED** even on a fresh seeded project:
attempt frequency for the narrow `/configure` race is **1 of 2** fresh-project attempts,
and when that race is dodged the next blockers are now **preview startup heuristics** and
**merge failure on a fresh project**, not just stale shared-project contamination.

Latest retry after Ahmed manually cleaned and committed the sandbox-state dirt on `main`
(commit `9966644`) reached the requested pre-merge pause point but still failed before a
manual review approval could be posted. Fresh project `blueprint-demo-live-2157`
(`3ea4f234-5ade-4354-ae71-8174acfd3c41`) and run
`3a4f3eeb-98d8-4b58-9b19-7f41d3cac2e3` progressed through all three subtasks, RAI green,
Build & Test complete, and emitted the human-review gate at
`2026-07-25T22:11:23.4791990+00:00`. But before the approval POST landed, the
coordinator re-entered assembly, replayed RAI + build-test setup, rebound
`agentweaver-agent-host-gjnw9`, and failed again at
`2026-07-25T22:11:53.3825565+00:00` with
`build_test_infra_agenthost_configure_failed` / `AgentHost /configure ... HTTP 500`.
So even with merge dirt proactively cleaned, the narrow post-review re-arm race can still
erase the paused human-review checkpoint before merge is clicked. The actual recording
remains **BLOCKED**.

Post-PR513 retry on staging commit `6d7d9aa8` still did **not** clear the recording path.
Fresh project `blueprint-demo-live-232803` (`95174ba2-affc-4329-b020-eb357da6282c`) and
fresh run `b552be51-602d-4095-9073-1cb0ca04507e` progressed cleanly through all three
subtasks (`42`, `43`, `44` all `assemble_ready`) and RAI green at
`2026-07-25T23:44:12.4948850+00:00`. But the run then failed at the **first** assembly
Build & Test / preview-required pass — before any human-review gate surfaced — with the
same `build_test_infra_agenthost_configure_failed` result:
`AgentHost /configure for run 'b552be51-602d-4095-9073-1cb0ca04507e' failed: HTTP 500`
at `2026-07-25T23:44:30.3814169+00:00`. This suggests PR #513 may have fixed the earlier
post-review re-arm ordering bug, but it did **not** eliminate all initial Build & Test
configure failures on fresh seeded projects. No merge or recording could occur from this
attempt, so the actual recording remains **BLOCKED**.

One more fresh retry was run immediately per Ahmed's request to catch the same signature
while live AgentHost logs were already being tailed. Fresh project
`blueprint-demo-live-235836` (`fdccf146-0dd8-406f-bbd3-f60f81e72f44`) created fresh run
`b01d9f00-7330-4956-af50-4be65efd9f8e`. This run again reached full child completion, but
with one extra clue:

- subtask `45` first dispatched child `67368511-7ab3-4db2-9a9c-e2b4154cf046`, which
  stalled and was redispatched once at `2026-07-26T00:07:16.7557307+00:00`; replacement
  child `b0a2be44-e028-43a7-9af5-673b4a84c00a` reached `assemble_ready` at
  `2026-07-26T00:10:03.5589141+00:00`
- subtasks `46` and `47` also reached `assemble_ready` at
  `2026-07-26T00:11:51.1958667+00:00` and `2026-07-26T00:16:28.6047942+00:00`
- assembly then bound AgentHost pod `agentweaver-agent-host-whrxq` at
  `2026-07-26T00:16:35.5580070+00:00`, passed RAI green at
  `2026-07-26T00:16:41.6108430+00:00`, and marked preview applicability
  `preview_required`
- before Build & Test completed, the same parent run rebound to a **different**
  AgentHost pod `agentweaver-agent-host-wxkvj` at
  `2026-07-26T00:16:50.0234134+00:00`
- the run then failed **7.6s later** with the same result:
  `build_test_infra_agenthost_configure_failed` /
  `AgentHost /configure for run 'b01d9f00-7330-4956-af50-4be65efd9f8e' failed: HTTP 500`
  at `2026-07-26T00:16:57.6441121+00:00`

So the latest evidence now matches the suspected **claim recreate / rebound** path
directly: even after PR #513, a fresh seeded bug-fix run can pass RAI on one AgentHost
pod and then immediately switch to a different pod before `/configure` 500s. This was
the requested live-log repro point; recording is still **BLOCKED** until that path is
stable enough to reach preview/review/merge.

## Recording order and verification status

Record the beats in order, top to bottom: **1.1, 1.2, 1.3, 2.1, 2.2, 2.3, 2.4, 2.5,
2.6, 2.7, 2.8, 3.1, 3.2, 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 4.7, 5.1**.

| Beats | Status | Recording use |
| --- | --- | --- |
| 1.1, 1.2, 1.3, 2.1, 2.2, 2.7, 3.2, 5.1 | Live selector-verified | Record with the stated selectors/labels, keeping the caveats noted below. |
| 2.3, 2.4, 2.5, 2.6, 2.8, 3.1 | Partially verified / blocked downstream | Record only the verified surface; the artifact-producing part is blocked in this dry-run by renewed AgentHost launch failure after child dispatch or by workflow-row preconditions. |
| 4.1–4.7 | Unverified | Do not include in a finished cut until a follow-up run supplies the missing artifact. |

New UI surfaces added in this pass — the **Generate** tab / **Generate Blueprint** action,
the **Import Skill** dialog, the **Clarify** refinement input, the Backlog-to-Ready
**board drag** and task card, and the **Open preview** control — are now partially mapped
from frontend source where possible. Every source-only locator below is marked
**(resolved via source — not live-verified)** so it is not mistaken for a live DOM pass.

### Wait handling for every take

Never record real-time idle polling. Fresh orchestrations can remain **Pending** for
about two minutes, and the verified PM workflow took more than 16 minutes end to end.
For every kickoff-to-result transition, either use a pre-warmed run that is already at
the next verified state or insert a time-lapse/speed-ramp and resume the take there.

**New verified timing rule:** coordinator decomposition can take up to about **2 minutes
end-to-end** after Confirm plan. The occasional shell-tool-denial retry adds latency, so
the recording script must poll/wait **at least 150s after confirm** before treating
decomposition as stuck.

## Preflight

```bash
playwright-cli open --browser=chrome
playwright-cli resize 1920 1080
playwright-cli goto https://agentweaver.6a63b4fb256d5a00017339af.westus2.staging.aksapp.io
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
# Source-resolved (not live-verified): the picker exposes a Generate tab button, while the action button inside that tab is named "Generate Blueprint".
playwright-cli hover "getByRole('button', { name: 'Generate', exact: true })"
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

**Live-verified:** the create-project dialog shows blueprint tab buttons **Suggested**,
**Templates**, and **Generate**, and the preset card selector remains the radio
**Product & Software Delivery**. Source also confirms a primary **Generate Blueprint**
button inside the Generate tab, though this beat only needs to surface the tab itself.

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
playwright-cli click "getByRole('button', { name: 'Browse marketplaces', exact: true })" # (resolved via source — not live-verified)
playwright-cli snapshot
# Source-resolved (not live-verified): dialog title is "Browse skill marketplaces"; tests close it with Escape and there is no dedicated close button in source.
playwright-cli run-code "async page => { await page.keyboard.press('Escape'); }"
# Import Skill dialog — source-resolved (not live-verified).
playwright-cli click "getByRole('button', { name: 'Import skill', exact: true })"
playwright-cli snapshot
# Source-resolved (not live-verified): tests close the Import skill dialog with Escape and there is no explicit close button in source.
playwright-cli run-code "async page => { await page.keyboard.press('Escape'); }"
playwright-cli click "getByRole('tab', { name: 'Assignments', exact: true })" # (resolved via source — not live-verified)
playwright-cli snapshot
```

Narration: “Every agent has a name, a role, and a set of skills. Browse the curated
marketplaces — like GitHub Awesome Copilot and Azure Skills — or paste any GitHub repo to
import a skill, then assign each one to the agents that need it.”

Pacing: hold on the agents list, then on the Skills catalog, then on the Assignments grid
so the per-agent checkboxes are legible.

Note: Team memory is intentionally saved for the end (Beat 2.8), after a run has had a
chance to write a decision.

**Live-verified:** the Skills page exposes **Browse marketplaces**, **Import skill**, and
the **Assignments** tab. The Import dialog opens with drop zones for **Drop .md skill
files here** and **Drop a skill folder here**, a URL field labeled
**Paste raw SKILL.md URL or GitHub repo/folder URL***, and buttons
**Preview candidates** / **Import**. Source still indicates dialog dismissal via
**Escape** rather than a dedicated close button.

**NOT YET VERIFIED — needs follow-up run:** the Browse marketplaces dialog contents and a
real imported skill were not exercised on staging. Record those results only once their
live content is captured.

---

# Act 2 — Frame and ship a feature

## Beat 2.1 — Frame the product

**video-chapter**

```bash
playwright-cli video-chapter "Frame the product" --description="Pick the product workflow and hand the team a real problem to solve" --duration=18000
playwright-cli click "getByTestId('start-task-topbar-action')"
playwright-cli select "getByLabel('Workflow', { exact: true })" "software-delivery"
playwright-cli click "getByRole('textbox', { name: 'Goal' })"
playwright-cli type "Planning a weekend trip with friends turns into a mess of group chats, links, and half-made plans, and everyone ends up on a slightly different page. I want to launch Trailhead, which turns any free weekend into an outdoor trip the whole group actually agrees on. Work out who this is really for, what they need, and how we'd position it — the promise and the value props that make someone want to try it. Then shape the first experience that gets a group from 'we should go somewhere' to a plan they all share. As the simplest thing we can put in front of a real user, stand up a landing page that tells that story with placeholder content: a welcome banner, the three value props as stand-in blurbs, and one primary 'Plan my first trip' button to start."
playwright-cli hover "getByRole('button', { name: 'Define Outcome', exact: true })"
playwright-cli click "getByRole('button', { name: 'Define Outcome', exact: true })"
playwright-cli snapshot
```

Narration: “Start the delivery workflow and hand the team a real problem: planning a group
trip scatters across chats and links, and everyone ends up on a different page. Ask them
to shape Trailhead from there — and ship the first landing-page slice all the way through
implementation, preview, and review.”

Pacing: type the goal naturally; pause before Define Outcome. Select the delivery
workflow with the verified `software-delivery` value before entering the goal.

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
playwright-cli click "getByRole('textbox', { name: /^(Feedback|Additional feedback)$/ })" # (resolved via source — not live-verified; label depends on whether the plan surfaced open questions)
playwright-cli type "Keep this first slice to the landing page only: the welcome banner, the value props, and the 'Plan my first trip' button. No accounts and no saved trips yet."
playwright-cli click "getByRole('button', { name: 'Send', exact: true })" # (resolved via source — not live-verified)
playwright-cli snapshot
playwright-cli hover "getByRole('checkbox', { name: 'Independent task promotion Allow standalone backlog tasks for independent deliverables' })"
playwright-cli click "getByRole('checkbox', { name: 'Independent task promotion Allow standalone backlog tasks for independent deliverables' })"
playwright-cli hover "getByRole('button', { name: 'Confirm plan', exact: true })"
# [PAUSE 700ms]
playwright-cli click "getByRole('button', { name: 'Confirm plan', exact: true })"
playwright-cli snapshot
```

Narration: “Read the OutcomeSpec the team came back with — its first slice is a value-prop
landing page: a welcome banner, the three value props as placeholders, and one ‘Plan my
first trip’ button. Use Clarify to tighten the scope, let the independent pieces run as
their own tasks, and confirm.”

Pacing: show the Clarify exchange, then pause before Confirm plan so the human decision is
legible.

Human-review automation: Confirm plan is the verified dispatch gate. Its live result is
“Outcome plan confirmed … Dispatch is unblocked,” followed by the work plan.

**Source-resolved — not live-verified:** the Clarify dialog title is **Clarify plan** and
its submit button is **Send**. The main freeform textarea is labeled **Feedback** when no
open questions are present, or **Additional feedback** when questions are also rendered.

---

## Beat 2.3 — Watch the work plan run

**video-chapter**

```bash
playwright-cli video-chapter "Watch the work plan run" --description="Open the topology graph, step through the nodes, then watch the run produce artifacts" --duration=16000
playwright-cli click "getByTestId('open-topology-minimap')"
playwright-cli click "getByRole('button', { name: /Coordinator/ })"
playwright-cli snapshot
playwright-cli click "getByRole('button', { name: /Work plan/ })"
playwright-cli click "getByRole('button', { name: /Implement the confirmed outcome/ })"
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

**Partially live-verified:** the topology graph controls are verified, and live Software
Delivery runs expose the node label **Implement the confirmed outcome**. After the
v0.11.1 deploy, the temporary run-route 403 and earlier `agenthost_launch_failed` are no
longer the current blockers in this stage. Real work plans do appear, but slowly: on live
run `407ed7e3-ea88-4c2e-bf99-fcf075b5cb82`, `coordinator.work_plan` appeared about
**95s after confirm** and the first `subtask.dispatched` event appeared about **109s
after confirm**. Do not stage an “empty” run as broken until at least **150s** have
elapsed after Confirm plan.

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
# Human move: drag the landing-page card from Backlog to Ready — still blocked because the live board currently surfaces no promoted backlog cards to drag.
# playwright-cli drag "BLOCKED(live-behavior-mismatch-no-promoted-backlog-cards): blueprint-demo board showed Backlog=0 and Ready=0 with no task-card-* elements even while the orchestration had active subtasks and later reached human review" "getByTestId('column-ready')"  # drop target resolved via source; no observable live draggable card yet
playwright-cli snapshot
```

Narration: “Independent task promotion split the plan into separate tasks. Drag the
landing-page task from Backlog to Ready, and the coordinator picks it up.”

Pacing: hold on Backlog so the split tasks are readable. You can only drag between Backlog
and Ready; after the move, wait for the heartbeat to pull the card into Active. Pre-warm a
Ready task or speed-ramp the pickup; never record idle polling.

**Partially live-verified:** the board route and intake-column labels are stable
(**Backlog column** / **Ready column**; source test ids `column-backlog` /
`column-ready`). However, the latest live board pass on `blueprint-demo` showed
**0 queued tasks** in both columns and **no `task-card-*` elements**, even while the
orchestration had active subtasks and later reached human review. The specific
landing-page card title and runtime `task-card-<task_id>` remain
`BLOCKED(live-behavior-mismatch-no-promoted-backlog-cards)`.

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
playwright-cli click "getByRole('button', { name: 'Open preview', exact: true })" # (resolved via source — not live-verified)
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

**Partially live-verified:** the preview CTA is a **button** named **Open preview** in
source and in the run-page control surface, but the latest live execution child still did
not surface one. Child run `8e748e2a-1968-4611-9037-afaa488d029f` successfully built the
landing page and reached `assemble_ready`; `observe_bound_port` and `health_check` proved
a healthy forwarded port (`3639 -> 3000`, HTTP 200). But repeated
`start_preview(port)` calls then failed with the generic error **Tool execution failed**,
so no preview session or **Open preview** control appeared in the UI. The preview URL and
preview-generation timing remain
`BLOCKED(start_preview-tool-fails-after-successful-health-check)`.

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

**Live-verified with downstream failure:** the orchestration page now surfaces a real
approval accordion entry **Human review · Run-level · Needs input** under
**Approvals and gates**, with buttons **Approve & merge** and **Decline**. Approving that
gate on live run `407ed7e3-ea88-4c2e-bf99-fcf075b5cb82` advanced review, but the run then
ended `merge_failed / assembly_failed` with result
`assembly_merge_failed: the working tree cannot be safely reconciled with the merge result because uncommitted content diverges from the merge result and cannot be safely reconciled; commit, stash, or discard the local changes and retry`.
So the control is mapped, but the current merge outcome remains
`BLOCKED(live-merge-fails-after-approve)`.

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

**BLOCKED(live-merge-fails-after-approve):** no downstream scribe/decision artifact was
captured from this run because the orchestration failed at merge immediately after review
approval.

---

# Act 3 — Put it on autopilot

The feature shipped once. Now make it run again without you: on a clock, or whenever
something happens in GitHub.

## Beat 3.1 — Put it on a schedule

**video-chapter**

```bash
playwright-cli video-chapter "Put it on a schedule" --description="Open the workflow that just ran and set it to run on a recurring cadence" --duration=12000
playwright-cli click "getByRole('link', { name: 'Workflows', exact: true })"
# Live caveat: built-in workflow rows on staging exposed "Duplicate to project" instead of "Add schedule" in this failed run because no project workflow artifact was produced.
playwright-cli click "getByRole('button', { name: 'Add schedule', exact: true })" # BLOCKED(no-project-workflow-row-yet)
playwright-cli click "getByRole('combobox', { name: 'Cadence', exact: true })" # BLOCKED(no-schedule-dialog-without-project-workflow-row)
playwright-cli snapshot
```

Narration: “Open Workflows and pick the one that just delivered. Add a schedule, choose a
daily, weekly, or monthly cadence in UTC, and it runs on its own from here on.”

Pacing: hover the cadence options long enough to read them before picking one.

**Partially live-verified:** the Workflows nav item is **Workflows**, and the page-level
buttons **New workflow**, **Generate workflow**, **Set as default**, and **Sync** were
confirmed live. In this dry-run the list only exposed built-in rows with
**Duplicate to project**, so the inline **Add schedule** / **Edit schedule** action and
the **Cadence** combobox remain `BLOCKED(no-project-workflow-row-yet)`.

---

## Beat 3.2 — Trigger it from GitHub

**video-chapter**

```bash
playwright-cli video-chapter "Trigger it from GitHub" --description="Generate a webhook secret in Project Settings and wire it into a real GitHub repo webhook" --duration=14000
playwright-cli click "getByRole('link', { name: 'Settings', exact: true })" # (resolved via source — not live-verified; project nav label is "Settings")
# Webhooks tab, generate-secret control, and payload URL — selectors unknown, verify in mapping pass.
playwright-cli click "getByRole('button', { name: 'Webhooks', exact: true })" # (resolved via source — not live-verified)
playwright-cli click "getByRole('button', { name: /^(Generate|Rotate) secret$/ })" # (resolved via source — not live-verified)
playwright-cli click "getByRole('textbox', { name: 'Payload URL', exact: true })" # (resolved via source — not live-verified)
playwright-cli snapshot
```

Narration: “A schedule covers time; webhooks cover events. In Project Settings, Webhooks,
generate a secret, copy the payload URL, and wire it into a real GitHub repo webhook. Now
a push or a merge kicks off the same run.”

Pacing: reveal the secret once, and let the callout capture that reveal-once state
before you move on to the payload URL.

**Live-verified:** the project nav label is **Settings**; inside the Settings rail the
subsection button is **Webhooks**. Source still indicates the secret action will be
**Generate secret** on first use and **Rotate secret** afterward, and the payload URL
field is labeled **Payload URL**.

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
the welcome banner overlaps the primary ‘Plan my first trip’ button, so people can’t get
started.”

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
playwright-cli video-chapter "Drive it from your own tools" --description="Copy the read-only MCP server URL from Account settings and confirm the bearer token stays masked" --duration=12000
playwright-cli click "getByRole('link', { name: 'Settings', exact: true })" # live-verified page label is "Settings"
# Live-verified: the Settings page renders an "MCP clients" section directly; there is no separate nav item.
playwright-cli hover "getByRole('textbox', { name: 'MCP server URL', exact: true })" # live-verified
# BLOCKED(live-ui-mismatch): staging exposes no bearer-token field and no copy control for the MCP server URL.
playwright-cli snapshot
playwright-cli video-stop
```

Narration: “None of this needs a browser. Open Settings, copy your MCP server URL, and
point Claude Desktop, VS Code, or Copilot CLI at it. The same bearer token signs you in,
so you drive the same team and workflows from your own tools.”

Pacing: keep the bearer token masked in every frame; copy the server URL and let the
“Copied” state register before you move on.

Reason for placement: this closes the demo on its widest surface, the team and workflows
you cast now reach past the browser into whatever tool you already use.

**Live-verified:** the page label is **Settings**, and the MCP area is a direct section
titled **MCP clients** with a read-only textbox labeled **MCP server URL**.
**Confirmed demo-script mismatch, not a product bug:** `apps/web/src/pages/SettingsPage.tsx`
renders only that read-only MCP server URL field in the MCP clients section. There is no
bearer-token field and no copy button in the shipped UI, so the current beat script must
be adjusted rather than filing this as a Settings-page defect.
