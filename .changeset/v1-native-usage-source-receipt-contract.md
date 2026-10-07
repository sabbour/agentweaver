---
"Agentweaver.Abstractions": minor
"Agentweaver.Identity": minor
"Agentweaver.AgentRuntime": patch
"Agentweaver.EventsAndSessions": minor
---

Define the immutable native usage source receipt with exact runtime, owner, SDK,
model, catalog, event, and turn pins. Preserve cache-write measurements and unknown
native request counts without a second model multiplier.

Extend the usage ledger with nullable request counts and cache-write totals.
Retain legacy canonical hashes and support one transaction for the source inbox,
usage entry, rate card, and accounting receipt. These source primitives do not
complete authenticated native observation delivery or Events accounting.
