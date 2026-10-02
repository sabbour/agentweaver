# Milestone delivery tracker

Open the project canvas `agentweaver-release-tracker` and select a milestone version
from the dropdown (or open with `{"version":"0.35.0"}`). The seeded default is
v0.34.1. The dashboard shows each tracked milestone issue, its linked PR and status,
the current priority, the combined integration RC, direct API and UI behavior gates,
the release, and AKS deployment. The **Refresh status** button rereads the project
artifact; agents can also invoke the canvas `refresh` action.

The source of truth is one project-owned file per version under
`.github/extensions/release-tracker/milestones/<version>.json`, committed with the
extension. To start tracking a new release, invoke the canvas `create_milestone`
action with `{"version":"0.35.0"}`, then invoke `add_issue` for each milestone
issue (with `version`, `number`, and `title`; optional status, priority, linked
PR number/status, PR head, and note). Refresh the canvas to select the new version. Existing
milestone files cannot be overwritten by creation, and only release-number versions
such as `0.35.0` or `v0.35.0` are accepted.

It is not synchronized automatically with GitHub or between unmerged worktrees.
An agent updates it with the canvas `update_status` action and commits the changed
artifact so other project sessions receive the update after their branch incorporates
it. For example:

```json
{
  "version": "0.34.1",
  "section": "issue",
  "number": 1707,
  "changes": {
    "status": "closed",
    "prNumber": 1730,
    "prStatus": "merged",
    "note": "Merged and verified at the recorded head."
  }
}
```

Each issue row displays rubber-duck, code-review, and issue-specific API, UI,
and GitHub gate statuses. Set the
issue's `head` to the exact current linked PR commit through `update_status`,
then invoke `update_review` with `version`, `number`, `kind` (`rubberDuck` or
`codeReview`, `api`, `ui`, or `github`), `status`, `head`, and `evidence`.
A passing check requires a matching full commit SHA and nonempty evidence.
Changing the linked PR or head resets all five checks to **not run**; an older
head cannot be used to pass them.
Unreviewed work is displayed as **not run**, not implicitly approved.

The **Agents and subagents** graph fetches the app's current session activity
every 15 seconds while visible. Edges connect milestone project sessions
to their coordinator and each session's actual CLI subagents; issue-linked
nodes open the issue. Subagent lifecycle comes from each selected local
session's read-only Copilot event journal (`subagent.started`, `completed`,
and `failed`); only those lifecycle events are parsed. The canvas session's
task registry supplies its current task state directly. Peer statuses reflect
the latest durable lifecycle event, with its timestamp shown in the graph;
an unreadable journal is marked unavailable rather than shown as empty. The
canvas `live_activity` action reads the same sources on demand. If the live
feed fails, the graph shows an error rather than displaying stored reports
as live activity. Active branches appear first with blue outlines while all
recorded subagents remain visible. Solid edges represent observed session and
subagent relationships; dotted edges lead from active, issue-linked sessions
to planned PR/check/merge steps derived from the recorded issue status, not
from a future agent or a live GitHub check. Unlike recorded release gates,
activity is not committed to a milestone artifact.

For gates, use `section` values `priority`, `integrationRc`, `directApi`,
`directUi`, `liveGate`, `release`, or `aksDeployment` without `number`. Changes
may include `status` and `note`; direct-test and delivery gates also accept
`evidence`, `revision`, and `digest`. Record the exact RC revision/digest and
evidence before marking direct behavior tests as passed. The canvas rejects a
passing direct test unless its revision/digest matches the deployed combined RC;
the live gate additionally requires both direct API and UI tests to pass. Do not
treat platform integrity checks as behavior proof. Changing the RC pin resets
direct tests and the live gate to **not run** and release/deployment to
**pending**. Direct tests require the combined RC to be **deployed**; marking
release **passed** requires the live gate to have passed, and marking AKS
**deployed** requires the release to have passed.

For every milestone, each PR needs a final local rubber-duck and code-review pair
on its current head before readiness. After issue-specific API/UI and GitHub
gates pass, authorize `gh pr merge --rebase --auto` one PR at a time and monitor
to actual merge before admitting the next; do not squash or assume auto-merge
has already completed. For v0.34.1, assemble
and deploy one combined RC, then perform direct API and UI behavior tests against
that pinned RC. The shared Oracle run must cover #1709/#1713 via direct API start,
current revision/hashes, original/corrected browser interactions, request-changes,
approval, and terminal UI state. #1719 needs an exact-RC deterministic regression
and a real Generate Blueprint → exact job completion/result → generated-artifact UI
roundtrip. Only after those gates pass should release and AKS deployment proceed.

The initial live gate is **not run**: authentication is ready, but the public
`/api/version` reports `a5768a5` / `0.33.2` while deployments carry tag `61dc441`.
Pin the final digest and revision before accepting behavior evidence. The canvas
does not merge PRs, run tests, release, or deploy.
