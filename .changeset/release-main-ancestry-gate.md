---
"agentweaver": patch
---

`release:prepare` adds the `main` ancestry merge before it changes release files. Promotion PRs from `release/*` to `main` fail when that merge is missing.