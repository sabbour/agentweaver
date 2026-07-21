---
"agentweaver": patch
---

Rename the misleading `azure:dev` npm script to `dev:open` (opens a browser after `npm run dev` starts). It made zero Azure calls, so the `azure:` prefix implied a nonexistent cloud dependency.
