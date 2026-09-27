---
"agentweaver": patch
---

Keep in-progress workflow branches and their parent run intact when an API replica restarts while the parent is waiting for its fan join. Reconnect to the saved work plan instead of treating one closed watch connection as a failed run. Repeated unexpected stream completions fail explicitly after three durable closures, and stale watchers cannot publish terminal outcomes after a lease handoff.
