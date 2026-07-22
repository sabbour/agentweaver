---
"agentweaver": minor
---

Projects with no connected GitHub repository can now create and connect one instead of
being stuck. A new "Connect a GitHub repository" flow (Project Settings, and a dismissible
banner on the project dashboard) lets you pick an owner (yourself or an org), choose a
repo name and visibility, and creates the repo then pushes the project's existing local
history to it.

The push-PR execution step's "no connected repository" case now emits a `skipped` step
event (with a message pointing to Project Settings) instead of `failed`, since a missing
GitHub connection is not a run failure.
