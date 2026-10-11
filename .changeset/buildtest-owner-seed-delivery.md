---
"Agentweaver.Abstractions": minor
"Agentweaver.Identity": minor
"Agentweaver.Environment": patch
"Agentweaver.Orchestrator": patch
---

Core returns BuildTest command metadata separately from the original bounded binary seed.
Environment reads the seed from its fixed Core owner and verifies its exact length and digest.
The client deadline covers headers and body.
Current owner checks repeat after retrieval, before reservation, and before each provider effect without another seed transfer.
Stored operation updates and Core terminal checkpoint recovery validate metadata without seed bytes in JSON.
Reconciliation restores the exact verified seed only after the stored command metadata matches.

Version 1 behavior and the final version-2 actual-seed execution check remain in place.
These source changes do not prove provider preparation, live TLS authority, or complete BuildTest acceptance.
