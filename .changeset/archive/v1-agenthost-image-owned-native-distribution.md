---
"Agentweaver.AgentHost": patch
"Agentweaver.AgentRuntime": patch
---

Load the pinned native CLI distribution from its read-only image directory instead of extracting executable addons into private state.
Require the exact image-owned distribution path and complete native package.
Verify startup under both supported user identities with non-executable private mounts, without changing SDK session recovery or credential boundaries.
