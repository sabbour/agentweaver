---
"agentweaver": patch
---

Keep in-progress workflow branches and their parent run intact when an API replica restarts while the parent is waiting for its fan join. Reconnect to the saved work plan instead of treating a closed watch connection as a failed run.
