---
"Agentweaver.Abstractions": minor
"Agentweaver.Identity": minor
"Agentweaver.Environment": patch
"Agentweaver.Identity.Broker": patch
"Agentweaver.Orchestrator": minor
"Agentweaver.Web": patch
"Agentweaver.AgentRuntime": patch
"Agentweaver.Providers.Sandbox.AgentSandbox": patch
"Agentweaver.Projects.Config": patch
"Agentweaver.Persistence.Postgres": patch
---

Start the coordinator AgentHost after run admission. Environment creates a
private retained workspace and first Sandbox placement. Orchestrator registers
the runtime, requests Broker bootstrap, and commits readiness only after the
real activation route returns current Host evidence. Cancellation releases the
exact saved placement and keeps workspace data.

Include the run-start intent table in the reviewed PostgreSQL grant plan.
Readiness checks its complete schema through catalog metadata. Keep P1 setup
routes in the production host, preserve completed resume effects, and retain
the first-start authority check.
Record a returned first-start resource even when owner authority is lost, so
the exact resource remains available for cleanup.
