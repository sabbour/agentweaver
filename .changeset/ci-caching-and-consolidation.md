---
"agentweaver": patch
---

Cache Playwright browsers and bubblewrap in CI to avoid re-downloading on every run. Draft-gate expensive jobs so stacked PRs don't burn full CI on upper frames. Remove the redundant `web-lint` echo job and the `diagrams-in-sync` job.
