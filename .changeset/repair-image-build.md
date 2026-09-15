---
"agentweaver": patch
---

Repair the container image build. The frontend image build runs `tsc` across test files, and a type error in a topology test blocked it. The MCP Dockerfile did not copy `packages/`, so its project reference to `Agentweaver.AspNetCore` was not in the build context.
