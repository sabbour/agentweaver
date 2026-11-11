# Plan issues and releases

Use this guide to set a target milestone, apply the repository's existing
labels, and reconcile changes that actually shipped. Milestones and labels
describe plans; they do not show that work was implemented, published, or
deployed.

This is the same planning mechanism used for the 0.x product line. The
component manifest and changesets remain the versioning mechanism; issue
metadata does not publish packages or deploy workloads.

## Before you begin

You need access to the repository's issues, pull requests, milestones, and
labels. Authenticate the GitHub CLI before using the commands below. This
guide changes repository planning metadata; it does not authorize package
publication, cloud access, migrations, or deployment.

## Choose a target release

Give every scheduled issue and its pull request the same version milestone,
such as `v1.0.7`. Use the next unreleased patch for repairs. Group later
capabilities into ordered, coherent version milestones rather than putting
all remaining work under the initial release.

There is no fixed issue quota. A small repair release can contain one issue;
a larger capability release can contain several related issues. Choose a
batch that can be implemented, reviewed and tested together. Describe the
outcome and prerequisite order in the milestone. Do not set a due date unless
one has been agreed.

Use the earliest suitable **unreleased** milestone when the target is not
yet clear. Do not choose an old open milestone just because its version is
lower: publication records and unfinished validation work can remain open
after shipping.
If the work is genuinely deferred or not scheduled, leave its milestone
unset and apply `release:backlog`.

```powershell
gh api --paginate "repos/sabbour/agentweaver/milestones?state=all&per_page=100"
gh issue edit <issue-number> --repo sabbour/agentweaver --milestone <version>
gh pr edit <pr-number> --repo sabbour/agentweaver --milestone <version>
```

A milestone is a target, not an instruction to start work. Keep dependency
order and existing implementation ownership. A partly implemented
prerequisite is not complete because it has a target version. Finish the
required prerequisite before dependent work can ship. Move the target if the
prerequisite misses the cut.

## Apply the existing labels

Use the existing repository labels. Do not create labels for each version
or use a programme label instead of a milestone.

| Label family | Meaning |
| --- | --- |
| `type:*` | Use one applicable type: `feature`, `bug`, `spike`, `chore`, `docs` or `epic`. |
| `area:*` | Name the affected product surface, such as `api`, `web`, `workflows`, `backlog`, `runtime-resilience`, `observability`, `infra` or `testing`. Several areas are allowed. |
| `priority:p0` | A release blocker. |
| `priority:p1` | Work in the current implementation batch. |
| `priority:p2` | Work queued after the current batch. |
| `go:yes` | The scope is ready for implementation, subject to its prerequisites. |
| `go:needs-research` | Investigation is still needed. |
| `go:no` | The work is not being pursued. |
| `status:in-progress` | Implementation is actually in progress. |
| `status:testing` | Validation is actually in progress. |
| `release:blocking` | The work blocks the release or its required checks. |
| `release:backlog` | The work has no scheduled release target. |

Use at most one label within each of `type:`, `priority:`, `go:` and
`release:`. Use the status that is true now; a planned issue is not
in progress. Remove stale status labels when the activity ends. Closure,
not a new shipped-status label, records completed issue work.

The older `release:v*` version labels are not maintained release targets.
Remove them when replanning an issue. Keep historical labels on unrelated
issues unchanged. The old `bug`, `enhancement` and `workstream:*` labels
are also legacy names; use the corresponding `type:*` and `area:*` names
for new work.

## Understand the shared automation

GitHub issues and labels belong to the repository, not to a branch. Issue
forms and issue-event workflows run from the repository's default branch.
The existing `Squad Label Enforce` workflow keeps the `go:`, `release:`,
`type:` and `priority:` families mutually exclusive. It adds
`release:backlog` when `go:yes` is applied without a release label, and
removes release labels when `go:no` is applied.

That workflow does not use the milestone to decide whether work is
scheduled. After applying `go:yes` to a scheduled issue, remove any
automatically added `release:backlog`. Do not remove a genuine
`release:blocking` label. A person must review area and status labels,
milestone assignments, and release reconciliation. The label
sync workflow manages shared labels; there is no milestone-enforcement
workflow. Do not copy branch-specific issue workflows or assume CI checks
the release plan.

## Reconcile the release that actually shipped

The release manager creates the next version milestone at the release cut.
Future capability milestones may be created earlier to make the sequence
visible. Keep the descriptions focused on release outcomes; creating one
does not authorize publication or deployment.

After a package or CLI release, compare its published versions and consumed
changesets with the merged pull requests. Assign each shipped PR to the
first published version containing its changes, and assign the issues that
PR actually completed to that version. Do not move an unfinished parent
issue just because one partial contribution shipped.

Check whether a merge commit is in the release tag's history:

```powershell
git merge-base --is-ancestor <merge-commit> <release-tag>
```

Exit code zero means the commit is an ancestor. It does not show that the
feature was enabled or deployed. Check the package or CLI release itself as
well. A PR merged after the latest release tag
belongs to the next planned milestone, not the last published one.

Move work that missed the cut to the next suitable milestone. Do not leave
unshipped work on a closed milestone. Close the implementation milestone
only after publication and reconciliation are complete and no required
validation work remains. A published release with unfinished checks can
retain an open milestone; explain the remaining work in its description.

## Track release acceptance

Create one GitHub issue named `Release acceptance: vX.Y.Z` for each version
milestone, and assign it to that milestone. Use this issue to track release
acceptance, not implementation work. The milestone remains a target; reconcile
actual shipment against published versions and source history.

At the release cut, compare merged pull requests in `v1` history, component
changesets and published versions, and the tracker. Include the representative
integration project, focused API and/or UI scenarios for every new feature
(including features whose implementation issues are closed), risk-based
regression scenarios, and applicable archive, operating-system, installation,
documentation, cleanup, and carried-forward checks. Do not infer coverage from
open issues or pending changesets alone. Use the
[release acceptance checklist](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md#release-acceptance)
for the tracker fields and sign-off criteria.

### Close implementation issues after merge

An implementation issue tracks source delivery. After its work is complete:

1. Confirm that the pull request is merged and that its commit is in the
   target `v1` history.
2. Confirm that the merged pull request or pull request set completes the
   issue's full source scope. Keep any real implementation gap in a focused
   issue; do not close a partially completed issue.
3. Move any remaining live-acceptance task to the matching release tracker.
   Keep the scenario and its required evidence there.
4. Comment on the implementation issue with links to the merged pull request
   and tracker item.
5. Close the implementation issue as completed manually, then verify that
   GitHub shows it as closed.

Pull requests target `v1`, while `dev` is the default branch. GitHub closing
keywords do not close an issue when its pull request merges into `v1`; do not
rely on `Closes #…` or similar keywords. Keep later acceptance updates on the
release tracker and any focused repair issue. Do not keep a completed
implementation issue open only for acceptance work.

<a id="complete-acceptance-and-track-repairs"></a>

## Follow up on release gaps

Follow the [component release reference](../reference/releases.md) and
[release operations guide](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md)
for versioning, NuGet package publishing, direct GHCR image builds, and
Kustomize deployment. Keep package publication, image publication, workload
deployment, and release acceptance as separate records.

If a required acceptance check finds a defect, create or reuse a focused repair
issue in the next unreleased patch milestone. Link the failed tracker item and
its evidence to the repair issue. Keep the original implementation issue
closed unless its closure was factually wrong. Record a corrected-source test
as a new attempt; a merged repair or release tag alone does not show that a
corrected workload is running. Use the approved release and deployment
procedures for any corrected publication or deployment.

## Verify the plan

Check that each scheduled issue and its pull request use the same target
milestone, the labels match the current work, and no unfinished prerequisite
is treated as complete. After a release, reconcile the published package or
CLI version with the changesets and merged changes before closing the
milestone.
