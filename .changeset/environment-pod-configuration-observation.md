---
"Agentweaver.Abstractions": minor
"Agentweaver.Providers.Sandbox.AgentSandbox": minor
"Agentweaver.Environment": minor
---

Expose typed current Pod identity and configuration observations through the
authenticated, run-bound Environment owner read. Recheck the exact Pod UID and
resource version after provider reads. Missing metadata stays incomplete; this
is not invocation ownership or proof of a successful mount inside AgentHost.
New AgentHost templates expose their own Pod UID through a validated Downward
API field, without treating legacy missing self-identity as evidence.
