---
"Agentweaver.Orchestrator": minor
"Agentweaver.Environment": minor
"Agentweaver.Abstractions": minor
"Agentweaver.Identity": minor
"Agentweaver.Identity.Broker": minor
---

Add separate, default-off TLS profiles for Environment's current-call verifier and Identity owner clients.
Keep the existing Events listener and peer pins separate, and reject incorrect service roles, shared listener ports, and ordinary HTTPS before reading a proof.
These source profiles do not enable remote MCP dispatch, credential delivery, or applied network enforcement.
Add exact current-call and gate contracts, an authenticated owner client, and a non-wire native call context.
An in-process gate receipt is not proof of forced network enforcement.
