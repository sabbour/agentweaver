---
"Agentweaver.Identity": patch
"Agentweaver.EventsAndSessions": patch
"Agentweaver.Orchestrator": patch
---

Verify native observed accounting against Core's persisted turn before and after the Events cost transaction.
Keep raw SDK usage without a proven platform message ID unchanged.
Allow its observed join only with exact authenticated native evidence and immutable source and ledger receipts.
Release Core owner locks for the Events request and recheck authority, checkpoint, source, report, and revision before completion.
Reject wrong non-null message IDs, late events, altered accounting, and native usage reused by another dispatch.
Retain explicit unpriced accounting and partial source reports; this change does not claim source finality.
