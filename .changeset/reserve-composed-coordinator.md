---
"agentweaver": patch
---

Enable one coordinator-composed workflow stage for runtime-derived dependent work plans. The parent now suspends durably while child tasks execute, receives a typed assembled result, and installs the verified tree into its isolated run branch with restart-safe, fail-closed recovery.
