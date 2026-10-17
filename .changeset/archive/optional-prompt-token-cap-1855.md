---
"Agentweaver.Abstractions": patch
"Agentweaver.Knowledge": patch
"Agentweaver.Projects.Config": patch
---

Allow new run selections to omit an unenforced prompt-token cap while preserving configured limits. Knowledge continues to apply its own context-token ceiling when a run selection has no cap.
