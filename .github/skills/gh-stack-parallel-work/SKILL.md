---
name: "gh-stack-parallel-work"
description: "Agentweaver coordinator policy for parallel implementation in gh-stack PRs"
domain: "version-control"
confidence: "high"
source: "team-decision"
---

## Scope

Use this overlay whenever work may use `gh stack`, dependent PR layers, or a large
feature spanning auth, backend, frontend, and tests. It overrides generic agent
ownership guidance, not the official `gh stack` operational guidance.

An actual stacked PR has **two or more logical PR layers**. A local one-branch tracking
setup or an ordinary one-branch PR is not a stack. Use the normal Agentweaver PR workflow
for ordinary PRs.

`gh stack` v0.1.0 is optional and only fits a genuinely dependent, strictly linear
cohort. It is never the admission queue for independent issues. Independent changes use
the temporary integration branch workflow below.

## Admission findings ledger

Ponytail remains one required reviewer source, but its candidate-branch JSON is not
admission authority. The authoritative record is exactly one GitHub PR comment with the
marker `<!-- agentweaver.admission-findings-ledger/v1 -->`, written by an admission owner
independent from the candidate author. It binds the repository, PR number, current head
SHA, owner, ordered cohort identity, every required reviewer source, every finding, and
its complete transition history.

Each reviewer submits an independent GitHub review at the exact current SHA containing
an `agentweaver.findings-source/v1` payload. Required findings must transition exactly
`recorded -> owned -> (corrective-pr | waived) -> revalidated -> resolved`; every
transition records actor, timestamp, SHA, and evidence. The source payload controls a
finding's severity and policy: the ledger cannot lower either. A corrective PR must be
merged and included in the candidate. A waiver needs a rationale plus an authorized
approver independent from both candidate author and ledger author. Revalidation is by a
third independent actor against the current SHA. Any new head invalidates all evidence.
The trusted workflow reads repository-controlled reviewer-source and admission-owner
allowlists from GitHub repository variables; source labels in a review payload are never
authority, and a candidate author or unlisted secondary account cannot satisfy them.
Each reviewer source must also attest the SHA-256 hash of the immutable, non-empty ordered
cohort snapshot, which binds the candidate PR, SHA, and order exactly once.

The trusted `Admission findings` workflow uses `pull_request_target`, checks out only
`dev`, and queries GitHub PR/review/comment metadata; it never checks out or executes
candidate code. Run the same preflight before marking ready and immediately before
merging:

```bash
gh workflow run "Admission findings" --ref dev -f pr-number=<pr>
gh run watch --exit-status "$(gh run list --workflow 'Admission findings' --limit 1 --json databaseId --jq '.[0].databaseId')"
```

Missing, duplicate, unknown, reordered, skipped, stale, self-approved, or incomplete
evidence blocks admission.

### One-time bootstrap

The check must never be required before this workflow is present on `origin/dev`. For the
single bootstrap PR that adds it, complete independent review and existing CI, then use
`gh pr merge 1490 --squash`. Immediately configure the repository-variable allowlists
with an independently authorized principal, add `Admission findings` to the `dev` ruleset,
and retain a passing disposable test-PR run as evidence. No later PR may use this
exception; the full sequence is maintained in
[`dev-branch-protection.md`](../../dev-branch-protection.md).

## Ponytail implementation evidence

Coding agents use `ponytail` by default. After implementation they must:

1. run the exact affected validation from `CONTRIBUTING.md`;
2. rubber-duck the tested result, naming load-bearing assumptions, reused or deleted
   complexity, and any flaw found and fixed; then
3. stop and hand the exact branch tip to a different agent for `ponytail-review`.

The reviewer writes `.squad/.scratch/ponytail-gates/<head-sha>.json` using this contract:

```json
{
  "kind": "agentweaver.ponytail-review/v1",
  "head_sha": "40-character commit SHA",
  "implementer": "agent or contributor",
  "reviewer": "different agent or contributor",
  "implementer_validation": {
    "commands": ["exact command"]
  },
  "rubber_duck": {
    "assumptions": ["load-bearing assumption"],
    "simplifications": ["what was deleted or reused"],
    "flaw": "what was fixed, or an explicit statement that none was found"
  },
  "findings": [
    {
      "id": "PT-001",
      "confidence": "high",
      "summary": "unnecessary complexity",
      "location": "repo/path:line",
      "waiver": {
        "justification": "why this complexity is necessary",
        "approved_by": "accountable approver"
      }
    }
  ]
}
```

Omit `waiver` when none exists. Admission is derived: every high-confidence finding must
have an explicit waiver with a non-empty justification and accountable approver, or the
gate blocks. Lower-confidence findings remain review feedback. Git and PR history are
the authoritative timestamps; the gate does not self-attest ceremony times or a
redundant verdict. Validate the file against the exact candidate tip:

```bash
npm run workflow:ponytail-gate -- \
  .squad/.scratch/ponytail-gates/<head-sha>.json \
  --expect-tip "$(git rev-parse HEAD)"
```

The command failing blocks admission. Persist the validated JSON verbatim in the
candidate PR as a comment (a raw JSON body is intentionally machine-readable and
auditable), for example `gh pr comment <number> --body-file <gate.json>`. A waiver is an
explicit accountable decision, not reviewer silence.

## Temporary integration branch queue

GitHub Merge Queue is not available for this repository, so the user owns one published,
short-lived integration branch for each admission cohort:

1. Create the branch from current `origin/dev` and publish it before admission begins.
2. Admit candidates one at a time in declared order. Before each admission, update from
   the latest published integration tip, verify the candidate's validated Ponytail gate
   matches its exact head SHA, integrate it, run its affected validation, then publish
   the new tip. A stale gate or failed validation stops the queue.
3. After the last candidate, run `npm run validate:full` plus required live proofs on the
   aggregate. Run `ponytail-debt` against that exact aggregate tip, capture its output
   under `.squad/.scratch/`, and attach it with the aggregate SHA to the promotion PR.
4. Promote only from that exact reviewed integration tip into `dev`; do not rebuild the
   aggregate during promotion. Immediately before merge, verify the promotion PR head is
   still the recorded integration SHA. After the repository's squash-only promotion,
   record the resulting `dev` SHA and verify its tree hash equals the integration tip's
   tree hash.
5. Delete the remote and local temporary integration branch only after promotion is
   confirmed. Preserve the PR comments and aggregate evidence as the audit trail.

Never reuse a release branch as this queue, and never integrate work by mutating another
agent's worktree.

## Authoritative operational reference

Before deciding whether work belongs in a stack, or choosing its layers' count, order,
or content, coordinators must read [the official stack-design reference](../../../.agents/skills/gh-stack/references/stack-design.md).
Read [the official gh-stack skill](../../../.agents/skills/gh-stack/SKILL.md) before
running any stack command. It is authoritative for command syntax and flags, command
behavior and side effects, errors and recovery, non-interactive forms, remote selection,
and stack-design references.

In particular, coordinators must use the official safe non-interactive forms: JSON view
output, automatic PR submission, and explicit branch names for initialization and added
layers. The official skill and command-specific `gh stack <command> --help` define
supported flags and remote selection. Its multi-remote `--remote <name>` rule applies
only to supported `push`, `submit`, `sync`, `rebase`, and `link` operations; `init`
does not accept `--remote`. Do not invent flags or rely on interactive prompts.

## Coordinator-owned stack lifecycle

**Only the coordinator owns feature stack topology and lifecycle.** The coordinator:

- analyzes dependencies and chooses atomic, ordered review layers;
- centrally initializes the stack once, with exact ordered branch names, and adds later
  layers centrally;
- gives every agent an isolated worktree, assigned branch, lower-layer dependency, and
  full stack context;
- serializes stack commands. A stack state can be locked, so agents and coordinators do
  not run competing operations against the same stack;
- submits, synchronizes, rebases, pushes, and links the stack; and
- coordinates review, merge readiness, and recovery.

Arrange the initial stack before distributing per-layer worktrees. Branches checked out
in other worktrees can prevent local stack adoption or initialization. Worktree isolation
remains mandatory for concurrent implementation after that initial setup.

When upstream or a lower layer changes, the coordinator follows the official sync and
rebase workflow. A fix belongs in the lowest layer that owns it, never in an upper PR for
convenience. The coordinator uses `gh stack push`, not a raw push, after stack rebases.

`gh stack modify` is TUI-only; it is not a non-interactive recovery path. When
restructuring is necessary in an automated or agent workflow, the coordinator follows the
official recovery guidance, including coordinator-directed unstack/re-initialize when
appropriate, then re-submits or syncs.

## Agent boundaries

Assigned agents work only in their coordinator-provided worktree and branch. They do not
initialize, add, submit, sync, rebase, push, modify, or merge a stack; change a stack PR
base; or create competing stack branches.

Agents use pathspec-scoped `git add <files>` only. They must not use all-files staging
shortcuts, including `gh stack add -Am`, in shared or agent worktrees. They commit only
their assigned layer and report the commit SHA, validation, readiness, dependencies, and
blockers to the coordinator.

## Merge policy

The only ordinary admission command is `gh pr merge <pr> --squash`. Do not use
`--auto`, `--rebase`, `--merge`, or `gh stack merge`. Confirm `MERGED`, `mergedAt`, the
merge SHA, and refreshed `origin/dev` state before cleanup.

## Agent assignment prompt

```text
You own one assigned layer of a coordinator-managed gh stack.

WORKTREE_PATH: <absolute isolated worktree path>
ASSIGNED_STACK_BRANCH: <exact branch name>
BASE_BRANCH: <immediate lower stack branch, or dev for the bottom layer>
LOWER_LAYER_DEPENDENCY: <branch/PR and what it provides, or none>
STACK_CONTEXT: <ordered bottom-to-top branch list and this layer's scope>

Work only in WORKTREE_PATH and on ASSIGNED_STACK_BRANCH. Do not run gh stack commands,
change PR bases, rebase/merge/push the stack, or create competing branches. Use
pathspec-only `git add <files>`. Commit only your layer and report commit SHA, validation,
readiness, and any dependency/blocker to the coordinator.
```
