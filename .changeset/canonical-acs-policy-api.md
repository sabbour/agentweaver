---
"Agentweaver.Abstractions": minor
"Agentweaver.Policy.Acs": patch
"Agentweaver.Projects.Config": minor
"Agentweaver.Gateway": minor
"Agentweaver.AgentHost": patch
---

Expose the Platform ACS Policy source through read and validate routes, and a compare-and-swap write route. Head writes are off by default and return 503 until a reviewed change turns them on; a retry of a saved request still returns its receipt. Keep the Policy ID of an existing head. Enforce one policy head per scope; the migration stops, without changes, when a scope has more than one head. Report validator timeouts, unproven OPA failures, and native validator timeout, OPA process, staging, and internal diagnostics as 503 dependency failures. Report other validator diagnostics as 422 invalid artifacts. Projects & Config starts without ACS_OPA_PATH and reports Policy validation as unavailable. Source image builds add the pinned ACS assets to Projects & Config, and the AgentHost build context now includes the shared ACS validator for its image probe. The source remains inactive; this does not migrate or replace the v4 Policy path.