---
"agentweaver": patch
---

Avoid starting the optional writable system root for ordinary sandboxed `run_command` calls so trivial shell commands no longer inherit the helper's 120-second failure wait.
