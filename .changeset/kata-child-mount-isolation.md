---
"agentweaver": patch
---

Confine Kata AgentHost shell and preview child processes to run-scoped mount namespaces, preventing absolute, obfuscated, traversal, and symlink paths from reaching sibling projects on the shared workspace volume.
