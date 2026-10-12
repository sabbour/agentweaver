---
"Agentweaver.AgentRuntime": patch
"Agentweaver.Identity": patch
---

Expose authenticated native suspend evidence after admitted turns finish and the actual Events cache and journal write completes.
Require Core's exact current-operation echo and phase version before stopping new turns and around cache capture, persistence, and replay.
Keep Core suspend and resume incomplete when checkpoint or Workspace content evidence is missing.
