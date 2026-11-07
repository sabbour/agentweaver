---
"Agentweaver.Abstractions": minor
"Agentweaver.AgentRuntime": patch
"Agentweaver.Identity": minor
"Agentweaver.Providers.Sandbox.AgentSandbox": patch
"Agentweaver.Environment": patch
"Agentweaver.Identity.Broker": patch
"Agentweaver.Orchestrator": minor
"Agentweaver.Projects.Config": patch
"Agentweaver.Web": patch
---

Start the coordinator AgentHost after run admission. Environment creates a
private retained workspace and first Sandbox placement. Orchestrator registers
the runtime, requests Broker bootstrap, and commits readiness only after the
real activation route returns current Host evidence. Cancellation releases the
exact saved placement and keeps workspace data.
