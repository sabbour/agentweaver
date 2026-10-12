---
"Agentweaver.Abstractions": minor
"Agentweaver.Providers.Sandbox.AgentSandbox": minor
"Agentweaver.SourceControl": minor
"Agentweaver.Orchestrator.Core": minor
"Agentweaver.Orchestrator": minor
---

The source adds version-2 repository input contracts for BuildTest.
The execution checkpoint stores bounded binary input separately from ordinary checkpoint JSON.
Git export excludes credentials.
Native workspace transport validates the Pod identity and request fingerprint.

These foundations preserve version-1 behavior.
The full source-to-PVC preparation and output-capture flow remains separate work.
