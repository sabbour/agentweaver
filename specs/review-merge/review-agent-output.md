# Review agent output before merge

**Issue:** [#17](https://github.com/sabbour/agentweaver/issues/17)  
**Area:** Review & merge

## User story

As a human reviewer, I want to inspect proposed changes, context, and run history before deciding, so that repository history changes only after informed human approval.

## Context / problem

Agent work is untrusted until reviewed. The review surface brings together changed files, workspace context, timeline, and explicit decisions.

## Scope

### In
- changed-file inventory
- diff, source, and Markdown preview views
- full run workspace browsing during review
- timeline context while reviewing
- no-change and historical artifact states

### Out
- remote PR authoring as the required review path
- editing files directly in the review viewer
- approving without a run reaching review

## Acceptance criteria

- [ ] Runs awaiting review expose changed files and review controls together.
- [ ] Reviewers can open changed files as diffs and readable previews where applicable.
- [ ] Reviewers can browse workspace context read-only.
- [ ] Binary, large, and no-change cases are clearly identified.
- [ ] Historical runs remain inspectable after terminal states.

## Notable edge cases

- Large or binary files do not break review.
- No-change runs explain that there is nothing to merge.
- Artifact access respects run ownership.
