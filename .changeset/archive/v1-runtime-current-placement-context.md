---
"Agentweaver.Abstractions": minor
"Agentweaver.Identity": minor
"Agentweaver.Environment": minor
"Agentweaver.Orchestrator": minor
---

Add the current Sandbox lease and registered-profile context for authenticated
runtime enrollment. Keep Environment lifecycle, lease revision, provider fencing,
placement generation, accepted model reference, and Core execution fence separate.

Persist registrations only after fresh current-owner checks, including after
database waits. Preserve public Environment write gates and explicit authorization
denials. This source does not complete configure delivery or native SDK accounting.
