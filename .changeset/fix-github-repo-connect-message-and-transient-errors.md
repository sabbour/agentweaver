---
"agentweaver": patch
---

Fix the "Create project from GitHub" dialog and its owner/repo pickers so they no longer tell an
already-connected user to "reconnect GitHub" when repository listing fails.

- Clean up the message shown whenever a "Connect GitHub" action is offered: it now always reads
  "Connect GitHub to see your repositories." instead of the previous copy that contradictorily
  told users to "Retry or reconnect GitHub" while showing only a Connect button.
- The backend's GitHub repository-listing broker previously collapsed a transient failure calling
  the live GitHub API (network error, timeout) into the same `github_capability_unavailable` code
  used for "you're not connected" — so a user who just finished the Connect GitHub flow could still
  see the same "reconnect" prompt even though reconnecting would not help. It now returns a
  distinct `github_capability_transient` outcome/error code for that case, surfaced in the UI as
  "GitHub is temporarily unavailable. Try again in a moment." with the existing retry affordance,
  not another Connect GitHub prompt.
