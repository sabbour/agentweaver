---
"agentweaver": minor
---

Wire the existing `open_pull_request` workflow node into the built-in default
workflow (`merge` → `push-pr` → `scribe`) and into the `RunWorkflowGraphBinder`
so any code-producing workflow with a platform-appended merge/scribe step now
publishes or updates a GitHub pull request automatically. `GitHubPullRequestClient`
is now idempotent: if GitHub reports the pull request already exists (422), the
existing open PR is looked up and returned as success instead of failing the run.
