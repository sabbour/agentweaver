---
"Agentweaver.Projects.Config": patch
---

Add a source-only one-shot P1 project setup command with immutable operation receipts and guarded cleanup for projects that have not been admitted to a run. Cleanup archives the project and revokes only the setup-owned Orchestrator assignment; it does not stop runs or remove Environment resources.
