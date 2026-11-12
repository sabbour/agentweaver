# Agentweaver project workflow routing

Read the repository README and its linked v1 architecture and testing guides for
canonical commands. `AGENTS.md` and `CONTRIBUTING.md` are not present on this branch.

## Issue delivery

Use this manual coordinator process for issue-backed code changes. Do not invoke
or recreate a dynamic issue-to-merge workflow.

1. **Confirm the scope.** Read the issue, its comments, and the request. Read the
   relevant README, architecture, and testing guides. Write down the deliverable,
   acceptance checks, and exclusions. Verify the existing `type:` and `area:`
   labels and milestone. Do not change issue metadata unless the task needs it.
2. **Review the design before code.** Check matching v0.x code and tests for prior
   art. Record the source-backed baseline, proposed change, failure and recovery
   behavior, tests, documentation, and release impact. Get a focused design review
   and post the reviewed design on the issue before implementation.
3. **Create an isolated worktree.** Use a fresh feature worktree and branch from
   the target branch (`v1` by default). Verify the origin, branch, ancestry, and
   clean worktree. Do not reuse a worktree that belongs to another pull request.
4. **Implement the approved scope.** Add focused regression tests. Update affected
   documentation, diagrams, and changesets in the same change. Do not make manual
   version edits when the changeset workflow owns them.
5. **Validate the candidate.** Run the focused tests and the required build,
   documentation, link, diagram, and release checks for the change. Record the
   exact commands and results. Keep test, live-acceptance, publication, and
   deployment evidence separate. Do not claim a check that did not pass.
6. **Review the exact diff before a pull request.** Run one design review and one
   code review on the completed local change. Fix real defects and plan mismatches.
   Do not expand the task to follow unrelated suggestions. Resolve blocking
   findings before continuing.
7. **Open and monitor the pull request.** Commit and push only the reviewed change.
   Link the issue and include validation evidence. Monitor checks to completion.
   Fix failures caused by the change; rerun unrelated transient failures when
   possible. Follow required approvals and serialize merge admission when needed.
   Do not treat a queued merge as a completed merge.
8. **Verify and hand off.** Confirm the final merge state. Clean up only the
   worktree and branch owned by this change after preserving required evidence.
   Report the commit, pull request, checks, and any remaining limits.

Release and deployment work are separate. Follow the [release runbook](../releases/README.md)
when the user requests those operations.

For Agentweaver 1.x API acceptance, use the checked-in case scripts. The
current read-only Gateway case runs `auth status`, `api operations`,
`api describe`, and `rest` through the exact native CLI apphost. Set
`AGENTWEAVER_CLI_PATH` to the approved apphost, sign in with the normal
`agentweaver auth login` command when authorized, and use only an explicitly
approved non-production target. The script does not open a browser, accept
tokens, mutate resources, or send model requests. Its successful `--dry-run`
checks local inputs only and creates no evidence. Do not present this case as
full API or P1 acceptance. See the [API case guide](../docs/guide/api-acceptance-migration.md),
[testing guide](../docs/guide/testing.md), and [CLI guide](../docs/guide/cli.md).

## Release and deployment

For v1 package, image, and Kubernetes release operations, follow the
[release runbook](../releases/README.md). Keep NuGet package
publication, container image publication, and workload deployment distinct;
do not revive image archives, publication receipts, source-claim gates, or
manual migration-token handoffs.

## Documentation as you go

Before writing or reviewing documentation, use the shared
[authoring guide and templates](../docs/authoring/README.md). Follow its
reader-goal, source-fidelity, safety, and arc42 guidance; keep contributor
evidence out of published user instructions.

Update affected v1 documentation and diagrams in the same PR as each behavior,
interface, provider, model-support, or configuration change. Run
`npm run docs:build`, `npm run docs:check-links`, and the diagram checks.
