---
"Agentweaver.Abstractions": minor
"Agentweaver.Identity": minor
"Agentweaver.Environment": minor
---

Add the run-bound runtime Workspace metadata read under one retained Environment
owner lock. Require the exact current Sandbox lease and attached Workspace
generation, data generation, transition revision, and provider attachment.
The response does not claim Storage flush, a content checkpoint, or suspension.
