# Plan issues and releases

Use GitHub version milestones to show what each Agentweaver release is
expected to contain. Labels describe the kind of work, product area,
priority and current state. Neither a milestone nor a label proves that
work is implemented, published, deployed or accepted.

This is the same planning mechanism used for the 0.x product line. The v1
component manifest and changesets remain the versioning mechanism; issue
metadata does not publish packages or deploy workloads.

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
lower: publication and acceptance records can remain open after shipping.
If the work is genuinely deferred or not scheduled, leave its milestone
unset and apply `release:backlog`.

```powershell
gh api --paginate "repos/sabbour/agentweaver/milestones?state=all&per_page=100"
gh issue edit <issue-number> --repo sabbour/agentweaver --milestone <version>
gh pr edit <pr-number> --repo sabbour/agentweaver --milestone <version>
```

A milestone is a target, not an instruction to start work. Keep dependency
order and existing implementation ownership. A partly implemented
prerequisite is not complete because it has a target version. Resolve its
actual required slice before the dependent work can ship; move the target
if that prerequisite misses the cut.

## Apply the existing labels

Use the existing repository labels. Do not create labels for each version
or use a programme label instead of a milestone.

| Label family | Meaning |
| --- | --- |
| `type:*` | Use one applicable type: `feature`, `bug`, `spike`, `chore`, `docs` or `epic`. |
| `area:*` | Name the affected product surface, such as `api`, `web`, `workflows`, `mcp`, `backlog`, `runtime-resilience`, `observability`, `infra` or `testing`. Several areas are allowed. |
| `priority:p0` | A release blocker. |
| `priority:p1` | Work in the current implementation batch. |
| `priority:p2` | Work queued after the current batch. |
| `go:yes` | The scope is ready for implementation, subject to its prerequisites. |
| `go:needs-research` | Investigation is still needed. |
| `go:no` | The work is not being pursued. |
| `status:in-progress` | Implementation is actually in progress. |
| `status:testing` | Validation is actually in progress. |
| `release:blocking` | The work blocks release or release acceptance. |
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
`release:blocking` label. Area and status labels, milestone assignments and
release reconciliation still need human or coordinator review. The label
sync workflow manages shared labels; there is no v1 milestone-enforcement
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

Tag ancestry is useful supporting evidence:

```powershell
git merge-base --is-ancestor <merge-commit> <release-tag>
```

Exit code zero means the commit is an ancestor. It does not prove that the
feature was enabled, deployed or accepted. Check the package or CLI release
itself as well. A PR merged after the latest release tag
belongs to the next planned milestone, not the last published one.

Move work that missed the cut to the next suitable milestone. Do not leave
unshipped work on a closed milestone. Close the implementation milestone
only after publication and reconciliation are verified and its outstanding
acceptance work is complete. A published release with unfinished acceptance
can retain an open milestone; explain the outstanding work in its
description.

## Complete acceptance and track repairs

Follow the [component release reference](../reference/releases.md) and
[release operations guide](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md)
for versioning, NuGet package publishing, direct GHCR image builds, and
Kustomize deployment. Planning
does not authorize credentials, cloud access, migrations or deployment.

For separately approved deployed acceptance, run the representative
integration project and focused API, UI or MCP scenarios for each new
feature. Record the result and any blocked checks. A local test or successful
rollout does not replace those
journeys.

File each abnormal result as a narrow repair issue in the next patch
milestone for that release. Implement and review the fix, publish and deploy
the approved corrected source when required, then rerun the affected
scenarios. Keep the original acceptance issue open until every required
criterion has evidence. Never close it because the repair merged, a tag
exists or the remaining checks are blocked.
